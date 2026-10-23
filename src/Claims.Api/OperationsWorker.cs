namespace Claims.Api;
public sealed class OperationsWorker(Store store, IConfiguration config, ILogger<OperationsWorker> logger, IServiceProvider services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.GetValue<bool>("Claims:DisableWorker"))
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var escalated = store.Escalate(DateTimeOffset.UtcNow);
                if (escalated > 0)
                    logger.LogWarning("Escalated {Count} overdue claims", escalated);
                for (int i = 0; i < 50 && !stoppingToken.IsCancellationRequested; i++)
                {
                    var dispatcher = services.GetService<OutboxDispatcher>();
                    var sent = dispatcher is null ? store.DispatchOne(DateTimeOffset.UtcNow, config.GetValue<bool>("Claims:SimulateIntegrationFailure")) : await dispatcher.Dispatch(DateTimeOffset.UtcNow, stoppingToken);
                    if (!sent)
                        break;
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Operations worker cycle failed; retrying next cycle");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
