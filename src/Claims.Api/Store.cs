using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Claims.Api;

// Each mutation holds an immediate SQLite transaction. Claim, audit and outbox commit together.
public sealed class Store
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    readonly string connectionString;
    public Store(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = Open();
        using var tx = db.BeginTransaction();
        Exec(db, tx, """
            CREATE TABLE IF NOT EXISTS schema_version(version INTEGER PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS claims(id TEXT PRIMARY KEY, reference TEXT NOT NULL COLLATE NOCASE UNIQUE, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT, claim_id TEXT NOT NULL REFERENCES claims(id), document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS idempotency(key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, response TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, claim_id TEXT NOT NULL REFERENCES claims(id), type TEXT NOT NULL, payload TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'Pending', next_attempt TEXT NOT NULL, last_error TEXT);
            CREATE TABLE IF NOT EXISTS integration_receipts(event_id INTEGER PRIMARY KEY, received_at TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_outbox_due ON outbox(status,next_attempt);
            CREATE INDEX IF NOT EXISTS ix_audit_claim ON audit(claim_id,sequence);
            INSERT OR IGNORE INTO schema_version VALUES(1);
            """);
        tx.Commit();
    }
    SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;"; cmd.ExecuteNonQuery(); return db; }
    static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params object?[] values)
    { var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = sql; for (int i = 0; i < values.Length; i++) c.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value); return c; }
    static void Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params object?[] values) { using var c = Cmd(db, tx, sql, values); c.ExecuteNonQuery(); }
    static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;
    static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
    static Claim? Find(SqliteConnection db, SqliteTransaction? tx, Guid id) { using var c = Cmd(db, tx, "SELECT document FROM claims WHERE id=$p0", id.ToString()); return c.ExecuteScalar() is string s ? Read<Claim>(s) : null; }
    public Claim[] List() { using var db = Open(); using var c = Cmd(db, null, "SELECT document FROM claims ORDER BY rowid DESC"); using var r = c.ExecuteReader(); var list = new List<Claim>(); while (r.Read()) list.Add(Read<Claim>(r.GetString(0))); return list.ToArray(); }
    public StoredClaim? Get(Guid id)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var claim = Find(db, tx, id); if (claim is null) return null;
        using var c = Cmd(db, tx, "SELECT sequence,document FROM audit WHERE claim_id=$p0 ORDER BY sequence", id.ToString());
        using var r = c.ExecuteReader(); var events = new List<AuditEvent>(); while (r.Read()) events.Add(Read<AuditEvent>(r.GetString(1)) with { Sequence = r.GetInt64(0) });
        return new(claim, events.ToArray());
    }
    static void Record(SqliteConnection db, SqliteTransaction tx, Claim claim, string action, Actor actor, string note)
    {
        var e = new AuditEvent(0, DateTimeOffset.UtcNow, action, actor.Id, note, claim.Version);
        Exec(db, tx, "INSERT INTO audit(claim_id,document) VALUES($p0,$p1)", claim.Id.ToString(), Write(e));
        Exec(db, tx, "INSERT INTO outbox(claim_id,type,payload,next_attempt) VALUES($p0,$p1,$p2,$p3)", claim.Id.ToString(), action, Write(new { claim, e }), DateTimeOffset.UtcNow.ToString("O"));
    }
    public (Claim Claim, bool Replayed) Create(CreateClaim input, Actor actor, string key)
    {
        if (ClaimRules.Validate(input) is { } error) throw new RuleException(400, error);
        if (key.Length is < 8 or > 128) throw new RuleException(400, "Idempotency-Key must contain 8–128 characters.");
        var fingerprint = Write(input with { Reference = input.Reference!.Trim().ToUpperInvariant() });
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var c = Cmd(db, tx, "SELECT fingerprint,response FROM idempotency WHERE key=$p0", actor.Id + ":" + key))
        using (var r = c.ExecuteReader()) if (r.Read()) { if (r.GetString(0) != fingerprint) throw new RuleException(409, "Idempotency key was already used with a different request."); return (Read<Claim>(r.GetString(1)), true); }
        var now = DateTimeOffset.UtcNow;
        var claim = new Claim(Guid.NewGuid(), input.Reference!.Trim(), input.Amount, ClaimStatus.Submitted, input.Category, input.Description, input.Priority, null, 1, now, now.AddHours(input.Priority == "Urgent" ? 4 : 24), false);
        try { Exec(db, tx, "INSERT INTO claims VALUES($p0,$p1,$p2)", claim.Id.ToString(), claim.Reference, Write(claim)); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new RuleException(409, "Reference already exists."); }
        Record(db, tx, claim, "Submitted", actor, "Claim entered intake");
        Exec(db, tx, "INSERT INTO idempotency VALUES($p0,$p1,$p2)", actor.Id + ":" + key, fingerprint, Write(claim));
        tx.Commit(); return (claim, false);
    }
    public Claim Apply(Guid id, string action, Command command, Actor actor)
    {
        if (command.Note?.Length > 1000) throw new RuleException(400, "Note must be at most 1000 characters.");
        using var db = Open(); using var tx = db.BeginTransaction(); var claim = Find(db, tx, id) ?? throw new RuleException(404, "Claim not found.");
        if (claim.Version != command.Version) throw new RuleException(409, "This claim changed. Refresh and review the latest version.");
        bool supervisor = actor.Role is "Supervisor" or "Administrator";
        if (claim.Status is ClaimStatus.Approved or ClaimStatus.Rejected) throw new RuleException(409, "Final decisions cannot be changed.");
        var next = claim;
        switch (action)
        {
            case "assign":
                if (claim.Status != ClaimStatus.Submitted) throw new RuleException(409, "Only unassigned intake claims can be claimed.");
                next = claim with { Status = ClaimStatus.InReview, Assignee = actor.Id }; break;
            case "release":
                if (claim.Status != ClaimStatus.InReview) throw new RuleException(409, "Claim is not in review.");
                if (claim.Assignee != actor.Id && !supervisor) throw new RuleException(403, "Only the assignee or a supervisor can release this claim.");
                next = claim with { Status = ClaimStatus.Submitted, Assignee = null }; break;
            case "approve":
            case "reject":
                if (string.IsNullOrWhiteSpace(command.Note)) throw new RuleException(400, "A decision rationale is required.");
                if (claim.Status == ClaimStatus.PendingSupervisor)
                {
                    if (!supervisor) throw new RuleException(403, "Supervisor authorization is required.");
                    if (claim.Assignee == actor.Id) throw new RuleException(403, "A different person must perform the second approval.");
                    next = claim with { Status = action == "approve" ? ClaimStatus.Approved : ClaimStatus.Rejected };
                }
                else
                {
                    if (claim.Status != ClaimStatus.InReview) throw new RuleException(409, "Assign the claim before deciding.");
                    if (claim.Assignee != actor.Id) throw new RuleException(403, "Only the assigned reviewer can decide this claim.");
                    next = claim with { Status = action == "reject" ? ClaimStatus.Rejected : claim.Amount >= 10000 ? ClaimStatus.PendingSupervisor : ClaimStatus.Approved };
                }
                break;
            default: throw new RuleException(404, "Unknown workflow action.");
        }
        next = next with { Version = claim.Version + 1 };
        Exec(db, tx, "UPDATE claims SET document=$p0 WHERE id=$p1", Write(next), id.ToString());
        Record(db, tx, next, action == "approve" && next.Status == ClaimStatus.PendingSupervisor ? "SupervisorRequested" : action, actor, command.Note ?? "");
        tx.Commit(); return next;
    }
    public int Escalate(DateTimeOffset now)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var due = new List<Claim>();
        using (var c = Cmd(db, tx, "SELECT document FROM claims")) using (var r = c.ExecuteReader()) while (r.Read()) { var x = Read<Claim>(r.GetString(0)); if (!x.Escalated && x.DueAt < now && x.Status is not (ClaimStatus.Approved or ClaimStatus.Rejected)) due.Add(x); }
        foreach (var x in due) { var n = x with { Escalated = true, Version = x.Version + 1 }; Exec(db, tx, "UPDATE claims SET document=$p0 WHERE id=$p1", Write(n), n.Id.ToString()); Record(db, tx, n, "SlaEscalated", new("scheduler", "System"), "Review deadline exceeded"); }
        tx.Commit(); return due.Count;
    }
    public (int Pending, int DeadLetters) DeliveryCounts()
    {
        using var db = Open(); using var c = Cmd(db, null, "SELECT COUNT(CASE WHEN status='Pending' THEN 1 END),COUNT(CASE WHEN status='DeadLetter' THEN 1 END) FROM outbox");
        using var r = c.ExecuteReader(); r.Read(); return (r.GetInt32(0), r.GetInt32(1));
    }
    public OutboxEvent[] Outbox()
    {
        using var db = Open(); using var c = Cmd(db, null, "SELECT id,claim_id,type,payload,attempts,status,next_attempt,last_error FROM outbox ORDER BY id DESC LIMIT 200"); using var r = c.ExecuteReader(); var list = new List<OutboxEvent>();
        while (r.Read()) list.Add(new(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), r.IsDBNull(7) ? null : r.GetString(7))); return list.ToArray();
    }
    // The local integration sink deduplicates by event ID. External consumers need the same guarantee.
    public bool DispatchOne(DateTimeOffset now, bool simulateFailure = false)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        long id; int attempts; string payload;
        using (var c = Cmd(db, tx, "SELECT id,attempts,payload FROM outbox WHERE status='Pending' AND next_attempt <= $p0 ORDER BY id LIMIT 1", now.ToString("O")))
        using (var r = c.ExecuteReader()) { if (!r.Read()) return false; id = r.GetInt64(0); attempts = r.GetInt32(1) + 1; payload = r.GetString(2); }
        if (simulateFailure) Exec(db, tx, "UPDATE outbox SET attempts=$p0,status=$p1,next_attempt=$p2,last_error=$p3 WHERE id=$p4", attempts, attempts >= 5 ? "DeadLetter" : "Pending", now.AddSeconds(Math.Pow(2, attempts)).ToString("O"), "Simulated integration outage", id);
        else { Exec(db, tx, "INSERT OR IGNORE INTO integration_receipts VALUES($p0,$p1,$p2)", id, now.ToString("O"), payload); Exec(db, tx, "UPDATE outbox SET attempts=$p0,status='Delivered',last_error=NULL WHERE id=$p1", attempts, id); }
        tx.Commit(); return true;
    }
    public void Retry(long id)
    { using var db = Open(); using var c = Cmd(db, null, "UPDATE outbox SET status='Pending',attempts=0,next_attempt=$p0,last_error=NULL WHERE id=$p1 AND status='DeadLetter'", DateTimeOffset.UtcNow.ToString("O"), id); if (c.ExecuteNonQuery() == 0) throw new RuleException(409, "Only dead letters can be retried."); }
}
