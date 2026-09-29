namespace ApiInsights;

/// <summary>A single measured request.</summary>
public sealed record ApiHit(
    string Method,
    string Route,
    string Path,
    int StatusCode,
    double DurationMs,
    DateTime TimestampUtc,
    string? CorrelationId);
