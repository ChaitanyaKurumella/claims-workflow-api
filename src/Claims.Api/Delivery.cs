using System.Net.Http.Headers;
using System.Text;

namespace Claims.Api;
public sealed record DeliveryLease(long Id, string Token, string EventKey, string Payload, int Attempts);
public interface IEventDelivery
{
    Task Deliver(DeliveryLease delivery, CancellationToken cancellationToken);
}

// Network I/O occurs outside the SQLite transaction. A consumer must deduplicate EventKey.
public sealed class HttpEventDelivery(HttpClient client, IConfiguration config) : IEventDelivery
{
    public async Task Deliver(DeliveryLease delivery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config["Claims:DeliveryUrl"]);
        request.Headers.Add("Idempotency-Key", delivery.EventKey);
        if (config["Claims:DeliveryToken"] is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class OutboxDispatcher(OutboxStore store, IEventDelivery delivery, ILogger<OutboxDispatcher> logger)
{
    public async Task<bool> Dispatch(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lease = store.Lease(now);
        if (lease is null)
            return false;
        string? error = null;
        try
        {
            await delivery.Deliver(lease, cancellationToken);
        }
        catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            error = "Delivery failed (" + e.GetType().Name + ")";
            logger.LogWarning("Event {EventKey} attempt {Attempt} failed: {ErrorType}", lease.EventKey, lease.Attempts, e.GetType().Name);
        }

        if (!store.Complete(lease, DateTimeOffset.UtcNow, error))
            logger.LogWarning("Completion rejected for expired event lease {EventKey}", lease.EventKey);
        return true;
    }
}
