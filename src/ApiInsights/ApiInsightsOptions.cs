namespace ApiInsights;

/// <summary>Root options for ApiInsights.</summary>
public sealed class ApiInsightsOptions
{
    public CorrelationIdOptions CorrelationId { get; } = new();
    public AnalyticsOptions Analytics { get; } = new();
    public DashboardOptions Dashboard { get; } = new();
}

public sealed class CorrelationIdOptions
{
    /// <summary>Generate / propagate a Correlation ID.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Header used for incoming and outgoing requests.</summary>
    public string HeaderName { get; set; } = "X-Correlation-Id";
}

public sealed class AnalyticsOptions
{
    /// <summary>Collect per-endpoint metrics.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path prefixes excluded from analytics (the dashboard path is always excluded).</summary>
    public string[] SkipPaths { get; set; } = Array.Empty<string>();

    /// <summary>Upper bound on tracked routes; extra routes are grouped under "unmatched".</summary>
    public int MaxTrackedEndpoints { get; set; } = 500;

    /// <summary>Histogram bucket upper bounds (ms) used to estimate percentiles.</summary>
    public int[] LatencyBucketsMs { get; set; } = { 10, 25, 50, 100, 250, 500, 1000, 2500 };

    /// <summary>How many slowest requests to keep.</summary>
    public int SlowRequestsCount { get; set; } = 20;

    /// <summary>How many recent 5xx responses to keep.</summary>
    public int RecentErrorsCount { get; set; } = 50;

    /// <summary>Capacity of the in-memory channel. When full, new hits are dropped instead of slowing requests.</summary>
    public int ChannelCapacity { get; set; } = 10_000;
}

public sealed class DashboardOptions
{
    /// <summary>Dashboard URL.</summary>
    public string Path { get; set; } = "/_api-stats";

    /// <summary>Authorization policy name. The dashboard stays DISABLED while this is null/empty.</summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>Auto refresh interval in seconds.</summary>
    public int RefreshIntervalSeconds { get; set; } = 5;
}
