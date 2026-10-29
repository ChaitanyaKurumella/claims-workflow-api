using Claims.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class ReliabilityTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    string PathFor(string file = "claims.db") => Path.Combine(folder, file);
    Store Open() => new(PathFor());
    static readonly Actor Reviewer = new("person-a", "Reviewer");
    Claim Create(Store s, string reference) => s.Create(new(reference, 12.34m), Reviewer, "create-" + reference).Claim;
    [Fact]
    public void Cursor_pages_cover_each_claim_once_and_status_filter_is_applied_before_limit()
    {
        var s = Open();
        var all = Enumerable.Range(0, 113).Select(i => Create(s, "PAGE-" + i)).ToArray();
        var assigned = s.Apply(all[37].Id, "assign", new(1), Reviewer);
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var rows = s.QueryClaims(null, cursor, 17);
            if (rows.Length == 0)
                break;
            seen.AddRange(rows.Select(x => x.Id));
            cursor = rows.Last().Id.ToString();
        }
        while (true);
        Assert.Equal(113, seen.Count);
        Assert.Equal(113, seen.Distinct().Count());
        Assert.Equal(assigned, Assert.Single(s.QueryClaims(ClaimStatus.InReview, null, 1)));
        Assert.Throws<RuleException>(() => s.QueryClaims(null, "invalid", 10));
    }

    [Fact]
    public void Expired_lease_replays_same_event_key_and_fences_late_completion()
    {
        var s = Open();
        Create(s, "CRASH");
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var first = s.Lease(now)!;
        Assert.NotNull(first);
        Assert.Null(Open().Lease(now.AddSeconds(1)));
        // Consumer can have committed here while the dispatcher died before acknowledgement.
        var recovered = Open().Lease(now.AddSeconds(31))!;
        Assert.Equal(first.EventKey, recovered.EventKey);
        Assert.NotEqual(first.Token, recovered.Token);
        Assert.Equal(2, recovered.Attempts);
        Assert.False(s.Complete(first, now.AddSeconds(32), null));
        Assert.True(s.Complete(recovered, now.AddSeconds(32), null));
        Assert.Null(s.Lease(now.AddDays(1)));
        Assert.Equal("Delivered", Assert.Single(s.Outbox()).Status);
    }

    [Fact]
    public void Repeated_worker_crashes_reach_dead_letter_and_can_be_retried()
    {
        var s = Open();
        Create(s, "CRASH-MAX");
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        for (int i = 0; i < 5; i++)
            Assert.NotNull(s.Lease(now.AddSeconds(i * 31)));
        Assert.Null(s.Lease(now.AddSeconds(155)));
        var e = Assert.Single(s.Outbox());
        Assert.Equal("DeadLetter", e.Status);
        s.Retry(e.Id);
        Assert.NotNull(s.Lease(now.AddDays(1)));
    }

    sealed class Receiver : IEventDelivery
    {
        public HashSet<string> Receipts { get; } = [];
        public int Calls { get; private set; }

        public Task Deliver(DeliveryLease lease, CancellationToken ct)
        {
            Calls++;
            Receipts.Add(lease.EventKey);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Consumer_deduplication_handles_crash_after_success_before_acknowledgement()
    {
        var s = Open();
        Create(s, "ACK-GAP");
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var receiver = new Receiver();
        await receiver.Deliver(s.Lease(now)!, default); // simulated process termination: no Complete
        var dispatcher = new OutboxDispatcher(Open().Deliveries, receiver, NullLogger<OutboxDispatcher>.Instance);
        Assert.True(await dispatcher.Dispatch(now.AddSeconds(31), default));
        Assert.Equal(2, receiver.Calls);
        Assert.Single(receiver.Receipts);
        Assert.Equal("Delivered", Assert.Single(s.Outbox()).Status);
    }

    sealed class FailureHandler : HttpMessageHandler
    {
        public string? Key;
        public string? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Key = request.Headers.GetValues("Idempotency-Key").Single();
            Payload = await request.Content!.ReadAsStringAsync(ct);
            return new(System.Net.HttpStatusCode.ServiceUnavailable);
        }
    }

    [Fact]
    public async Task Http_failure_retries_without_losing_payload_and_sends_stable_key()
    {
        var s = Open();
        Create(s, "HTTP-FAIL");
        var handler = new FailureHandler();
        using var http = new HttpClient(handler);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "Claims:DeliveryUrl", "https://receiver.example/events" } }).Build();
        var dispatcher = new OutboxDispatcher(s.Deliveries, new HttpEventDelivery(http, config), NullLogger<OutboxDispatcher>.Instance);
        Assert.True(await dispatcher.Dispatch(DateTimeOffset.UtcNow.AddSeconds(1), default));
        var e = Assert.Single(s.Outbox());
        Assert.Equal("Pending", e.Status);
        Assert.Equal(1, e.Attempts);
        Assert.Equal(e.Payload, handler.Payload);
        Assert.EndsWith(":" + e.Id, handler.Key);
    }

    sealed class DiagnosticReceiver(string outcome, Store store) : IEventDelivery
    {
        public Task Deliver(DeliveryLease lease, CancellationToken ct)
        {
            if (outcome == "failed") throw new HttpRequestException("sensitive receiver detail");
            if (outcome == "cancelled") throw new OperationCanceledException(ct);
            if (outcome == "lease_rejected")
                store.Complete(lease, DateTimeOffset.UtcNow, null);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("failed")]
    [InlineData("lease_rejected")]
    [InlineData("cancelled")]
    public async Task Delivery_spans_report_outcome_without_payload_or_error_details(string outcome)
    {
        using var parent = new System.Diagnostics.Activity("test").Start();
        System.Diagnostics.Activity? observed = null;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == OperationsTelemetry.Name,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == parent.TraceId && activity.OperationName == "claims.delivery") observed = activity;
            }
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var store = Open();
        Create(store, "PRIVATE-PAYLOAD");
        var dispatcher = new OutboxDispatcher(store.Deliveries, new DiagnosticReceiver(outcome, store), NullLogger<OutboxDispatcher>.Instance);
        using var cancellation = new CancellationTokenSource();
        if (outcome == "cancelled") cancellation.Cancel();
        if (outcome == "cancelled")
            await Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.Dispatch(DateTimeOffset.UtcNow.AddSeconds(1), cancellation.Token));
        else Assert.True(await dispatcher.Dispatch(DateTimeOffset.UtcNow.AddSeconds(1), cancellation.Token));
        Assert.NotNull(observed);
        Assert.Equal(outcome, observed.GetTagItem("outcome"));
        Assert.Single(observed.TagObjects);
        Assert.Equal(outcome is "failed" or "lease_rejected" ? System.Diagnostics.ActivityStatusCode.Error : System.Diagnostics.ActivityStatusCode.Unset, observed.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }
}
