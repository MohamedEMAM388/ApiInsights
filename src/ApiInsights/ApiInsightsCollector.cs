using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiInsights;

/// <summary>Background service that drains the channel in batches and writes to the store.</summary>
internal sealed class ApiInsightsCollector : BackgroundService
{
    private const int BatchSize = 256;

    private readonly ApiHitChannel _channel;
    private readonly IApiInsightsStore _store;
    private readonly ILogger<ApiInsightsCollector> _logger;

    public ApiInsightsCollector(ApiHitChannel channel, IApiInsightsStore store, ILogger<ApiInsightsCollector> logger)
    {
        _channel = channel;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        var batch = new List<ApiHit>(BatchSize);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < BatchSize && reader.TryRead(out var hit))
                    batch.Add(hit);

                Flush(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }

        // Drain whatever is left.
        while (reader.TryRead(out var rest)) batch.Add(rest);
        Flush(batch);
    }

    private void Flush(List<ApiHit> batch)
    {
        if (batch.Count == 0) return;
        try
        {
            _store.Record(batch);
        }
        catch (Exception ex)
        {
            // Analytics must never take the app down.
            _logger.LogWarning(ex, "ApiInsights failed to store {Count} hits", batch.Count);
        }
        finally
        {
            batch.Clear();
        }
    }
}
