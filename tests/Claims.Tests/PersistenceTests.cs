using Claims.Api;
using Xunit;

public sealed class PersistenceTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Store NewStore() => new(Path.Combine(folder, "claims.db"));
    static readonly Actor Reviewer = new("reviewer-01", "Reviewer"), Supervisor = new("supervisor-01", "Supervisor");
    static void Fails(int status, Action action) => Assert.Equal(status, Assert.Throws<RuleException>(action).Status);
    [Fact]
    public void Two_person_approval_and_restart_preserve_audit_and_outbox()
    {
        var s = NewStore();
        var c = s.Create(new("BIG", 10000), Reviewer, "test-big-claim").Claim;
        c = s.Apply(c.Id, "assign", new(c.Version), Reviewer);
        c = s.Apply(c.Id, "approve", new(c.Version, "Policy checked"), Reviewer);
        Assert.Equal(ClaimStatus.PendingSupervisor, c.Status);
        Fails(403, () => s.Apply(c.Id, "approve", new(c.Version, "Self approve"), new(Reviewer.Id, "Supervisor")));
        Fails(403, () => s.Apply(c.Id, "approve", new(c.Version, "Other reviewer"), new("reviewer-02", "Reviewer")));
        c = s.Apply(c.Id, "approve", new(c.Version, "Independent review"), Supervisor);
        var reopened = NewStore();
        Assert.Equal(ClaimStatus.Approved, reopened.Get(c.Id)!.Claim.Status);
        Assert.Equal(4, reopened.Get(c.Id)!.History.Length);
        Assert.Equal(4, reopened.Outbox().Length);
        Fails(409, () => reopened.Apply(c.Id, "reject", new(c.Version, "Reversal"), Supervisor));
    }

    [Fact]
    public void Idempotency_replays_original_response_and_rejects_reused_payload_and_duplicate_reference()
    {
        var s = NewStore();
        var input = new CreateClaim("RETRY", 100);
        var first = s.Create(input, Reviewer, "request-001");
        s.Apply(first.Claim.Id, "assign", new(1), Reviewer);
        var replay = s.Create(input, Reviewer, "request-001");
        Assert.True(replay.Replayed);
        Assert.Equal(first.Claim, replay.Claim);
        Fails(409, () => s.Create(input with { Amount = 200 }, Reviewer, "request-001"));
        Fails(409, () => s.Create(input with { Reference = "retry" }, Reviewer, "request-002"));
        Assert.Single(s.List());
    }

    [Fact]
    public void Multiple_store_instances_compete_with_one_winner_and_no_extra_events()
    {
        var s = NewStore();
        var c = s.Create(new("RACE", 100), Reviewer, "race-create").Claim;
        c = s.Apply(c.Id, "assign", new(1), Reviewer);
        var stores = Enumerable.Range(0, 8).Select(_ => NewStore()).ToArray();
        var results = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 8, i =>
        {
            try
            {
                stores[i].Apply(c.Id, i % 2 == 0 ? "approve" : "reject", new(2, "Concurrent review"), Reviewer);
                results.Add(200);
            }
            catch (RuleException e)
            {
                results.Add(e.Status);
            }
        });
        Assert.Single(results, x => x == 200);
        Assert.Equal(7, results.Count(x => x == 409));
        Assert.Equal(3, s.Get(c.Id)!.History.Length);
        Assert.Equal(3, s.Outbox().Length);
    }

    [Fact]
    public void Stale_and_unauthorized_mutations_leave_no_side_effects()
    {
        var s = NewStore();
        var c = s.Create(new("OWNER", 10), Reviewer, "owner-create").Claim;
        c = s.Apply(c.Id, "assign", new(1), Reviewer);
        Fails(403, () => s.Apply(c.Id, "reject", new(2, "Unauthorized"), Supervisor));
        Fails(409, () => s.Apply(c.Id, "release", new(1), Reviewer));
        Assert.Equal(2, s.Get(c.Id)!.History.Length);
        Assert.Equal(2, s.Outbox().Length);
    }

    [Fact]
    public void Sla_escalates_active_claim_once_and_invalidates_stale_review()
    {
        var s = NewStore();
        var c = s.Create(new("LATE", 100), Reviewer, "late-create").Claim;
        Assert.Equal(1, s.Escalate(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Equal(0, s.Escalate(DateTimeOffset.UtcNow.AddDays(3)));
        Assert.True(s.Get(c.Id)!.Claim.Escalated);
        Fails(409, () => s.Apply(c.Id, "assign", new(1), Reviewer));
        Assert.Equal("SlaEscalated", s.Get(c.Id)!.History.Last().Action);
    }

    [Fact]
    public void Failed_delivery_backs_off_dead_letters_and_replays_after_restart()
    {
        var s = NewStore();
        s.Create(new("OUTBOX", 100), Reviewer, "outbox-create");
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.True(s.DispatchOne(now, true));
        Assert.False(s.DispatchOne(now, true));
        for (int i = 0; i < 4; i++)
        {
            now = now.AddMinutes(5);
            Assert.True(s.DispatchOne(now, true));
        }

        var e = Assert.Single(s.Outbox());
        Assert.Equal("DeadLetter", e.Status);
        Assert.Equal(5, e.Attempts);
        var reopened = NewStore();
        reopened.Retry(e.Id);
        Assert.True(reopened.DispatchOne(now.AddDays(1)));
        Assert.Equal("Delivered", Assert.Single(reopened.Outbox()).Status);
        Assert.False(reopened.DispatchOne(now.AddDays(2)));
    }

    [Fact]
    public void Final_claims_are_not_escalated()
    {
        var s = NewStore();
        var c = s.Create(new("CLOSED", 1), Reviewer, "closed-create").Claim;
        c = s.Apply(c.Id, "assign", new(1), Reviewer);
        s.Apply(c.Id, "approve", new(c.Version, "Checked"), Reviewer);
        Assert.Equal(0, s.Escalate(DateTimeOffset.UtcNow.AddDays(5)));
    }

    [Fact]
    public void Outbox_write_failure_rolls_back_claim_and_audit()
    {
        var store = NewStore();
        var claim = store.Create(new("ATOMIC", 50), Reviewer, "atomic-create").Claim;
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "claims.db")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_outbox BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT, 'injected storage failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.Apply(claim.Id, "assign", new(1), Reviewer));
        Assert.Equal(1, store.Get(claim.Id)!.Claim.Version);
        Assert.Equal(ClaimStatus.Submitted, store.Get(claim.Id)!.Claim.Status);
        Assert.Single(store.Get(claim.Id)!.History);
        Assert.Single(store.Outbox());
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }
}
