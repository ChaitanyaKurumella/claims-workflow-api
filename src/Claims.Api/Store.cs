using static Claims.Api.SqliteDatabase;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claims.Api;
// Each mutation holds an immediate SQLite transaction. Claim, audit and outbox commit together.
public sealed class Store
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
    readonly SqliteDatabase database;
    public OutboxStore Deliveries { get; }

    public Store(string path)
    {
        database = new(path);
        using var db = database.Open();
        SchemaMigrator.Apply(db);
        Deliveries = new(database);
    }

    SqliteConnection Open() => database.Open();
    static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;
    static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
    const string Columns = "id,reference,amount_cents,status,category,description,priority,assignee,version,created_at,due_at,escalated";
    static Claim ReadClaim(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt64(2) / 100m, Enum.Parse<ClaimStatus>(r.GetString(3)), r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt32(8), DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture), r.GetBoolean(11));
    static Claim? Find(SqliteConnection db, SqliteTransaction? tx, Guid id)
    {
        using var c = Cmd(db, tx, $"SELECT {Columns} FROM claims WHERE id=$p0", id.ToString());
        using var r = c.ExecuteReader();
        return r.Read() ? ReadClaim(r) : null;
    }

    static void Save(SqliteConnection db, SqliteTransaction tx, Claim c, bool insert = false) => Exec(db, tx, insert ? $"INSERT INTO claims({Columns}) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11)" : "UPDATE claims SET reference=$p1,amount_cents=$p2,status=$p3,category=$p4,description=$p5,priority=$p6,assignee=$p7,version=$p8,created_at=$p9,due_at=$p10,escalated=$p11 WHERE id=$p0", c.Id.ToString(), c.Reference, (long)(c.Amount * 100), c.Status.ToString(), c.Category, c.Description, c.Priority, c.Assignee, c.Version, c.CreatedAt.ToUniversalTime().ToString("O"), c.DueAt.ToUniversalTime().ToString("O"), c.Escalated);
    // Bounded API queries use keyset pagination; List is retained for seed/test compatibility.
    public Claim[] List() => QueryClaims(null, null, 100000);
    public Claim[] QueryClaims(ClaimStatus? status, string? after, int limit = 50)
    {
        if (limit is < 1 or > 100000)
            throw new RuleException(400, "Invalid page size.");
        if (after is not null && !Guid.TryParse(after, out _))
            throw new RuleException(400, "Invalid cursor.");
        using var db = Open();
        var where = new List<string>();
        var values = new List<object?>();
        if (status is not null)
        {
            where.Add("status=$p" + values.Count);
            values.Add(status.ToString());
        }

        if (after is not null)
        {
            where.Add("id>$p" + values.Count);
            values.Add(Guid.Parse(after).ToString());
        }

        var sql = $"SELECT {Columns} FROM claims" + (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) + " ORDER BY id LIMIT $p" + values.Count;
        values.Add(limit);
        using var c = Cmd(db, null, sql, values.ToArray());
        using var r = c.ExecuteReader();
        var list = new List<Claim>();
        while (r.Read())
            list.Add(ReadClaim(r));
        return list.ToArray();
    }

    public object Summary()
    {
        using var db = Open();
        using var c = Cmd(db, null, "SELECT COUNT(*),COALESCE(SUM(CASE WHEN status NOT IN ('Approved','Rejected') THEN amount_cents ELSE 0 END),0),COUNT(CASE WHEN status NOT IN ('Approved','Rejected') AND due_at<$p0 THEN 1 END) FROM claims", DateTimeOffset.UtcNow.ToString("O"));
        using var r = c.ExecuteReader();
        r.Read();
        return new
        {
            total = r.GetInt64(0),
            exposure = r.GetInt64(1) / 100m,
            overdue = r.GetInt64(2)
        };
    }

    public void CheckHealth()
    {
        using var db = Open();
        using var c = Cmd(db, null, "SELECT version FROM schema_version ORDER BY version DESC LIMIT 1");
        c.ExecuteScalar();
    }

    public StoredClaim? Get(Guid id)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var claim = Find(db, tx, id);
        if (claim is null)
            return null;
        using var c = Cmd(db, tx, "SELECT sequence,document FROM audit WHERE claim_id=$p0 ORDER BY sequence", id.ToString());
        using var r = c.ExecuteReader();
        var events = new List<AuditEvent>();
        while (r.Read())
            events.Add(Read<AuditEvent>(r.GetString(1))with { Sequence = r.GetInt64(0) });
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
        if (ClaimRules.Validate(input)is { } error)
            throw new RuleException(400, error);
        if (key.Length is < 8 or > 128)
            throw new RuleException(400, "Idempotency-Key must contain 8–128 characters.");
        var fingerprint = Write(input with { Reference = input.Reference!.Trim().ToUpperInvariant() });
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var c = Cmd(db, tx, "SELECT fingerprint,response FROM idempotency WHERE key=$p0", actor.Id + ":" + key))
        using (var r = c.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != fingerprint)
                    throw new RuleException(409, "Idempotency key was already used with a different request.");
                return (Read<Claim>(r.GetString(1)), true);
            }

        var now = DateTimeOffset.UtcNow;
        var claim = new Claim(Guid.NewGuid(), input.Reference!.Trim(), input.Amount, ClaimStatus.Submitted, input.Category, input.Description, input.Priority, null, 1, now, now.AddHours(input.Priority == "Urgent" ? 4 : 24), false);
        try
        {
            Save(db, tx, claim, true);
        }
        catch (SqliteException e)when (e.SqliteErrorCode == 19)
        {
            throw new RuleException(409, "Reference already exists.");
        }

        Record(db, tx, claim, "Submitted", actor, "Claim entered intake");
        Exec(db, tx, "INSERT INTO idempotency VALUES($p0,$p1,$p2)", actor.Id + ":" + key, fingerprint, Write(claim));
        tx.Commit();
        return (claim, false);
    }

    public Claim Apply(Guid id, string action, Command command, Actor actor)
    {
        if (command.Note?.Length > 1000)
            throw new RuleException(400, "Note must be at most 1000 characters.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        var claim = Find(db, tx, id) ?? throw new RuleException(404, "Claim not found.");
        if (claim.Version != command.Version)
            throw new RuleException(409, "This claim changed. Refresh and review the latest version.");
        var next = WorkflowPolicy.Decide(claim, action, command, actor);
        Save(db, tx, next);
        Record(db, tx, next, action == "approve" && next.Status == ClaimStatus.PendingSupervisor ? "SupervisorRequested" : action, actor, command.Note ?? "");
        tx.Commit();
        return next;
    }

    public int Escalate(DateTimeOffset now)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var due = new List<Claim>();
        using (var c = Cmd(db, tx, $"SELECT {Columns} FROM claims WHERE escalated=0 AND status NOT IN ('Approved','Rejected') AND due_at<$p0 ORDER BY due_at LIMIT 100", now.ToUniversalTime().ToString("O")))
        using (var r = c.ExecuteReader())
            while (r.Read())
                due.Add(ReadClaim(r));
        foreach (var x in due)
        {
            var n = x with
            {
                Escalated = true,
                Version = x.Version + 1
            };
            Save(db, tx, n);
            Record(db, tx, n, "SlaEscalated", new("scheduler", "System"), "Review deadline exceeded");
        }

        tx.Commit();
        return due.Count;
    }

    // Compatibility facade; delivery persistence is isolated from claim persistence.
    public (int Pending, int DeadLetters) DeliveryCounts() => Deliveries.DeliveryCounts();
    public OutboxEvent[] Outbox() => Deliveries.Outbox();
    public bool DispatchOne(DateTimeOffset now, bool simulateFailure = false) => Deliveries.DispatchOne(now, simulateFailure);
    public DeliveryLease? Lease(DateTimeOffset now) => Deliveries.Lease(now);
    public bool Complete(DeliveryLease lease, DateTimeOffset now, string? error) => Deliveries.Complete(lease, now, error);
    public void Retry(long id) => Deliveries.Retry(id);
}
