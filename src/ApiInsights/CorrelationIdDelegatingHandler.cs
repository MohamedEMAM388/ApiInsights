using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ApiInsights;

/// <summary>Forwards the current Correlation ID on outgoing HttpClient calls.</summary>
internal sealed class CorrelationIdDelegatingHandler : DelegatingHandler
{
    private readonly ICorrelationIdAccessor _accessor;
    private readonly string _headerName;

    public CorrelationIdDelegatingHandler(ICorrelationIdAccessor accessor, IOptions<ApiInsightsOptions> options)
    {
        _accessor = accessor;
        _headerName = options.Value.CorrelationId.HeaderName;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = _accessor.CorrelationId;
        if (id is not null && !request.Headers.Contains(_headerName))
            request.Headers.TryAddWithoutValidation(_headerName, id);

        return base.SendAsync(request, cancellationToken);
    }
}

public static class HttpClientBuilderExtensions
{
    /// <summary>Forward the current request's Correlation ID to downstream services.</summary>
    public static IHttpClientBuilder AddCorrelationIdPropagation(this IHttpClientBuilder builder)
    {
        builder.Services.TryAddTransient<CorrelationIdDelegatingHandler>();
        return builder.AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
    }
}
