using Microsoft.Data.Sqlite;
using System.Globalization;
using static Claims.Api.SqliteDatabase;

namespace Claims.Api;
public sealed class OutboxStore
{
    readonly SqliteDatabase database;
    internal OutboxStore(SqliteDatabase database)
    {
        this.database = database;
    }

    SqliteConnection Open() => database.Open();
    public (int Pending, int DeadLetters) DeliveryCounts()
    {
        using var db = Open();
        using var c = Cmd(db, null, "SELECT COUNT(CASE WHEN status IN ('Pending','Sending') THEN 1 END),COUNT(CASE WHEN status='DeadLetter' THEN 1 END) FROM outbox");
        using var r = c.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1));
    }

    public OutboxEvent[] Outbox()
    {
        using var db = Open();
        using var c = Cmd(db, null, "SELECT id,claim_id,type,payload,attempts,status,next_attempt,last_error FROM outbox ORDER BY id DESC LIMIT 200");
        using var r = c.ExecuteReader();
        var list = new List<OutboxEvent>();
        while (r.Read())
            list.Add(new(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), r.IsDBNull(7) ? null : r.GetString(7)));
        return list.ToArray();
    }

    // The local integration sink deduplicates by event ID. External consumers need the same guarantee.
    public bool DispatchOne(DateTimeOffset now, bool simulateFailure = false)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        long id;
        int attempts;
        string payload;
        using (var c = Cmd(db, tx, "SELECT id,attempts,payload FROM outbox WHERE status='Pending' AND next_attempt <= $p0 ORDER BY id LIMIT 1", now.ToString("O")))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read())
                return false;
            id = r.GetInt64(0);
            attempts = r.GetInt32(1) + 1;
            payload = r.GetString(2);
        }

        if (simulateFailure)
            Exec(db, tx, "UPDATE outbox SET attempts=$p0,status=$p1,next_attempt=$p2,last_error=$p3 WHERE id=$p4", attempts, attempts >= 5 ? "DeadLetter" : "Pending", now.AddSeconds(Math.Pow(2, attempts)).ToString("O"), "Simulated integration outage", id);
        else
        {
            Exec(db, tx, "INSERT OR IGNORE INTO integration_receipts VALUES($p0,$p1,$p2)", id, now.ToString("O"), payload);
            Exec(db, tx, "UPDATE outbox SET attempts=$p0,status='Delivered',last_error=NULL WHERE id=$p1", attempts, id);
        }

        tx.Commit();
        return true;
    }

    public DeliveryLease? Lease(DateTimeOffset now)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        // A crash on the final attempt must eventually become visible as dead letter.
        Exec(db, tx, "UPDATE outbox SET status='DeadLetter',last_error='Delivery lease expired after final attempt' WHERE status='Sending' AND lease_until<=$p0 AND attempts>=5", now.ToString("O"));
        long id;
        string payload;
        int attempts;
        using (var c = Cmd(db, tx, "SELECT id,payload,attempts FROM outbox WHERE (status='Pending' AND next_attempt<=$p0) OR (status='Sending' AND lease_until<=$p0 AND attempts<5) ORDER BY id LIMIT 1", now.ToString("O")))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read())
            {
                tx.Commit();
                return null;
            }

            id = r.GetInt64(0);
            payload = r.GetString(1);
            attempts = r.GetInt32(2) + 1;
        }

        var token = Guid.NewGuid().ToString();
        Exec(db, tx, "UPDATE outbox SET status='Sending',attempts=$p0,lease_token=$p1,lease_until=$p2 WHERE id=$p3", attempts, token, now.AddSeconds(30).ToString("O"), id);
        using var identity = Cmd(db, tx, "SELECT id FROM database_identity");
        var key = identity.ExecuteScalar() + ":" + id;
        tx.Commit();
        return new(id, token, key, payload, attempts);
    }

    public bool Complete(DeliveryLease lease, DateTimeOffset now, string? error)
    {
        using var db = Open();
        using var c = Cmd(db, null, "UPDATE outbox SET status=$p0,next_attempt=$p1,last_error=$p2,lease_token=NULL,lease_until=NULL WHERE id=$p3 AND status='Sending' AND lease_token=$p4", error is null ? "Delivered" : lease.Attempts >= 5 ? "DeadLetter" : "Pending", now.AddSeconds(Math.Pow(2, lease.Attempts)).ToString("O"), error, lease.Id, lease.Token);
        return c.ExecuteNonQuery() == 1;
    }

    public void Retry(long id)
    {
        using var db = Open();
        using var c = Cmd(db, null, "UPDATE outbox SET status='Pending',attempts=0,next_attempt=$p0,last_error=NULL WHERE id=$p1 AND status='DeadLetter'", DateTimeOffset.UtcNow.ToString("O"), id);
        if (c.ExecuteNonQuery() == 0)
            throw new RuleException(409, "Only dead letters can be retried.");
    }
}
