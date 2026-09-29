namespace ApiInsights;

/// <summary>Gives access to the Correlation ID of the current request.</summary>
public interface ICorrelationIdAccessor
{
    /// <summary>The current Correlation ID, or null outside a request / when the feature is disabled.</summary>
    string? CorrelationId { get; }
}

internal sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? CorrelationId => Current.Value;

    internal void Set(string? id) => Current.Value = id;
}
