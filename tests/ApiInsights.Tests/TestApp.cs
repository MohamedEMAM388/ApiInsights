using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiInsights.Tests;

internal static class TestApp
{
    /// <summary>Starts an in-memory API with UseApiInsights() registered FIRST (like the README says).</summary>
    public static async Task<IHost> StartAsync(
        Action<ApiInsightsOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        Action<IEndpointRouteBuilder>? endpoints = null,
        bool runCollector = true)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddApiInsights(configure);
                    services?.Invoke(s);

                    if (!runCollector)
                        s.Remove(s.Single(d => d.ImplementationType == typeof(ApiInsightsCollector)));
                })
                .Configure(app =>
                {
                    app.UseApiInsights();
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        e.MapGet("/users/{id}", (string id) => id);
                        e.MapGet("/boom", () => Results.StatusCode(500));
                        e.MapGet("/crash", (Func<IResult>)(() => throw new InvalidOperationException("boom")));
                        e.MapGet("/whoami", async (ICorrelationIdAccessor accessor) =>
                        {
                            await Task.Delay(Random.Shared.Next(1, 25)); // force interleaving between requests
                            return accessor.CorrelationId ?? "none";
                        });
                        endpoints?.Invoke(e);
                    });
                }))
            .StartAsync();
    }

    /// <summary>Hits go through a background channel, so poll until they are stored.</summary>
    public static async Task WaitForRequestsAsync(IHost host, long expected)
    {
        var stats = host.Services.GetRequiredService<IApiStatsProvider>();
        for (var i = 0; i < 100 && stats.GetSnapshot().TotalRequests < expected; i++)
            await Task.Delay(50);
    }

    public static ApiStatsSnapshot Snapshot(IHost host)
        => host.Services.GetRequiredService<IApiStatsProvider>().GetSnapshot();
}
