using Microsoft.Data.Sqlite;

namespace Claims.Api;
// Ordered migrations execute in one transaction, including version recording.
public static class SchemaMigrator
{
    public const int CurrentVersion = 3;
    public static void Apply(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        void Run(string sql)
        {
            using var c = db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        Run("CREATE TABLE IF NOT EXISTS schema_version(version INTEGER PRIMARY KEY)");
        using var check = db.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_version";
        var version = Convert.ToInt32(check.ExecuteScalar());
        if (version > CurrentVersion)
            throw new InvalidOperationException("Database schema is newer than this application.");
        if (version < 1)
            Run("""
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
        if (version < 2)
            Run("""
            CREATE TABLE claims_v2(
                id TEXT PRIMARY KEY, reference TEXT NOT NULL COLLATE NOCASE UNIQUE,
                amount_cents INTEGER NOT NULL CHECK(amount_cents BETWEEN 1 AND 100000000),
                status TEXT NOT NULL CHECK(status IN ('Submitted','InReview','PendingSupervisor','Approved','Rejected')),
                category TEXT NOT NULL, description TEXT NOT NULL, priority TEXT NOT NULL,
                assignee TEXT, version INTEGER NOT NULL CHECK(version>0),
                created_at TEXT NOT NULL, due_at TEXT NOT NULL, escalated INTEGER NOT NULL CHECK(escalated IN (0,1)));
            INSERT INTO claims_v2 SELECT id,reference,
                CAST(ROUND(json_extract(document,'$.amount')*100) AS INTEGER),
                json_extract(document,'$.status'),json_extract(document,'$.category'),
                json_extract(document,'$.description'),json_extract(document,'$.priority'),
                json_extract(document,'$.assignee'),json_extract(document,'$.version'),
                json_extract(document,'$.createdAt'),json_extract(document,'$.dueAt'),json_extract(document,'$.escalated') FROM claims;
            -- Preserve parent table identity: rebuilding children avoids dangling foreign keys.
            CREATE TEMP TABLE saved_audit AS SELECT * FROM audit;
            CREATE TEMP TABLE saved_outbox AS SELECT * FROM outbox;
            DROP TABLE audit;
            DROP TABLE outbox;
            DROP TABLE claims;
            ALTER TABLE claims_v2 RENAME TO claims;
            CREATE TABLE audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT, claim_id TEXT NOT NULL REFERENCES claims(id), document TEXT NOT NULL);
            INSERT INTO audit SELECT * FROM saved_audit;
            CREATE TABLE outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, claim_id TEXT NOT NULL REFERENCES claims(id), type TEXT NOT NULL, payload TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'Pending', next_attempt TEXT NOT NULL, last_error TEXT);
            INSERT INTO outbox SELECT * FROM saved_outbox;
            DROP TABLE saved_audit;
            DROP TABLE saved_outbox;
            CREATE INDEX ix_audit_claim ON audit(claim_id,sequence);
            CREATE INDEX ix_outbox_due ON outbox(status,next_attempt);
            CREATE INDEX ix_claims_status_id ON claims(status,id);
            CREATE INDEX ix_claims_due ON claims(due_at) WHERE escalated=0 AND status NOT IN ('Approved','Rejected');
            INSERT INTO schema_version VALUES(2);
            """);
        if (version < 3)
            Run("""
            ALTER TABLE outbox ADD COLUMN lease_token TEXT;
            ALTER TABLE outbox ADD COLUMN lease_until TEXT;
            CREATE TABLE database_identity(id TEXT PRIMARY KEY);
            INSERT INTO database_identity VALUES(lower(hex(randomblob(16))));
            INSERT INTO schema_version VALUES(3);
            """);
        tx.Commit();
    }
}
