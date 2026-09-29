namespace ApiInsights;

/// <summary>Read access to collected analytics.</summary>
public interface IApiStatsProvider
{
    ApiStatsSnapshot GetSnapshot();
}

/// <summary>Storage abstraction. In-memory by default; an EF Core provider can replace it later.</summary>
public interface IApiInsightsStore : IApiStatsProvider
{
    void Record(IReadOnlyList<ApiHit> hits);
}

/// <summary>Aggregated numbers for one endpoint (grouped by route pattern). Error rates are percentages (0-100).</summary>
public sealed record EndpointStats(
    string Method,
    string Route,
    long Calls,
    double AvgMs,
    double P95Ms,
    double MaxMs,
    double ClientErrorRate,
    double ServerErrorRate,
    DateTime LastCallUtc);

public sealed record ApiStatsSnapshot(
    DateTime StartedUtc,
    long TotalRequests,
    IReadOnlyList<EndpointStats> Endpoints,
    IReadOnlyList<ApiHit> SlowestRequests,
    IReadOnlyList<ApiHit> RecentErrors);
