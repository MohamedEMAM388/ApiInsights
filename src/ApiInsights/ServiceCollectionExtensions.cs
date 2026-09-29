using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ApiInsights;

public static class ApiInsightsExtensions
{
    /// <summary>Registers ApiInsights services (Correlation ID, analytics collector, in-memory store).</summary>
    public static IServiceCollection AddApiInsights(this IServiceCollection services, Action<ApiInsightsOptions>? configure = null)
    {
        var options = services.AddOptions<ApiInsightsOptions>();
        if (configure is not null) options.Configure(configure);

        services.TryAddSingleton<CorrelationIdAccessor>();
        services.TryAddSingleton<ICorrelationIdAccessor>(sp => sp.GetRequiredService<CorrelationIdAccessor>());

        services.TryAddSingleton<ApiHitChannel>();
        services.TryAddSingleton<IApiInsightsStore, InMemoryApiInsightsStore>();
        services.TryAddSingleton<IApiStatsProvider>(sp => sp.GetRequiredService<IApiInsightsStore>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ApiInsightsCollector>());
        return services;
    }

    /// <summary>
    /// Adds the ApiInsights middleware. Register it EARLY: before UseExceptionHandler and authentication,
    /// so 500 and 401 responses are measured correctly.
    /// </summary>
    public static IApplicationBuilder UseApiInsights(this IApplicationBuilder app)
        => app.UseMiddleware<ApiInsightsMiddleware>();
}
