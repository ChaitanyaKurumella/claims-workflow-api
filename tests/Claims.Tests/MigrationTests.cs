using Claims.Api;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

public sealed class MigrationTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    string FilePath => Path.Combine(folder, "claims.db");

    SqliteConnection Connect()
    {
        Directory.CreateDirectory(folder);
        var db = new SqliteConnection($"Data Source={FilePath};Pooling=False");
        db.Open();
        return db;
    }

    static void Run(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    void Legacy(bool corrupt = false)
    {
        using var db = Connect();
        Run(db, """
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
        var c = new Claim(Guid.NewGuid(), "LEGACY", 123.45m, ClaimStatus.InReview, "Medical", "Preserve me", "Urgent", "reviewer", 7, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(4), false);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO claims VALUES($id,$ref,$doc)";
        insert.Parameters.AddWithValue("$id", c.Id.ToString());
        insert.Parameters.AddWithValue("$ref", c.Reference);
        insert.Parameters.AddWithValue("$doc", corrupt ? "{}" : JsonSerializer.Serialize(c, options));
        insert.ExecuteNonQuery();
        using var child = db.CreateCommand();
        child.CommandText = "INSERT INTO audit(claim_id,document) VALUES($id,'{}'); INSERT INTO outbox(claim_id,type,payload,next_attempt) VALUES($id,'Submitted','{}','2026-01-01T00:00:00.0000000+00:00')";
        child.Parameters.AddWithValue("$id", c.Id.ToString());
        child.ExecuteNonQuery();
    }

    [Fact]
    public void Upgrade_preserves_claim_values_child_rows_and_foreign_keys_and_is_repeatable()
    {
        Legacy();
        var store = new Store(FilePath);
        var c = Assert.Single(store.List());
        Assert.Equal(123.45m, c.Amount);
        Assert.Equal(7, c.Version);
        Assert.Equal("Preserve me", c.Description);
        Assert.Equal(ClaimStatus.InReview, c.Status);
        Assert.Single(store.Outbox());
        Assert.Equal(c, Assert.Single(new Store(FilePath).List()));
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_check";
        using var r = cmd.ExecuteReader();
        Assert.False(r.Read());
    }

    [Fact]
    public void Invalid_legacy_row_rolls_back_entire_migration_and_version()
    {
        Legacy(true);
        Assert.Throws<SqliteException>(() => new Store(FilePath));
        using var db = Connect();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT MAX(version) FROM schema_version";
        Assert.Equal(1L, c.ExecuteScalar());
        c.CommandText = "SELECT document FROM claims";
        Assert.Equal("{}", c.ExecuteScalar());
    }

    [Fact]
    public void Future_schema_fails_closed()
    {
        var store = new Store(FilePath);
        using (var db = Connect())
            Run(db, "INSERT INTO schema_version VALUES(999)");
        Assert.Throws<InvalidOperationException>(() => new Store(FilePath));
    }

    [Fact]
    public void Online_backup_restores_claims_history_outbox_and_identity()
    {
        var store = new Store(FilePath);
        var c = store.Create(new("BACKUP", 88.88m), new("alice", "Reviewer"), "backup-key").Claim;
        var before = store.Lease(DateTimeOffset.UtcNow.AddSeconds(1))!;
        var backup = Path.Combine(folder, "backup.db");
        using (var db = Connect())
        using (var target = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            target.Open();
            db.BackupDatabase(target);
        }

        store.Apply(c.Id, "assign", new(1), new("alice", "Reviewer"));
        var restored = new Store(backup);
        Assert.Equal(1, restored.Get(c.Id)!.Claim.Version);
        Assert.Single(restored.Get(c.Id)!.History);
        Assert.Single(restored.Outbox());
        Assert.Equal(before.EventKey, restored.Lease(DateTimeOffset.UtcNow.AddMinutes(1))!.EventKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }
}
