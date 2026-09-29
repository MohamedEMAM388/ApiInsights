using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiInsights;

/// <summary>One middleware for both features: Correlation ID + analytics measurement.</summary>
internal sealed class ApiInsightsMiddleware
{
    internal const string HttpContextItemKey = "ApiInsights.CorrelationId";
    private const int MaxPathLength = 200;

    // Letters, digits and dashes only, up to 64 chars: prevents log injection.
    private static readonly Regex ValidId = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.Compiled);

    private readonly RequestDelegate _next;
    private readonly ApiInsightsOptions _options;
    private readonly ILogger<ApiInsightsMiddleware> _logger;
    private readonly CorrelationIdAccessor _accessor;
    private readonly ApiHitChannel _channel;
    private readonly DashboardHandler _dashboard;

    public ApiInsightsMiddleware(
        RequestDelegate next,
        IOptions<ApiInsightsOptions> options,
        ILogger<ApiInsightsMiddleware> logger,
        CorrelationIdAccessor accessor,
        ApiHitChannel channel,
        IApiStatsProvider stats)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
        _accessor = accessor;
        _channel = channel;
        _dashboard = new DashboardHandler(_options, stats);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        // The dashboard is served by the library itself (only when a policy is configured).
        if (_dashboard.Matches(ctx.Request.Path) && await _dashboard.TryHandleAsync(ctx))
            return;

        string? id = null;
        if (_options.CorrelationId.Enabled)
        {
            var cid = ResolveOrCreateId(ctx);
            id = cid;
            _accessor.Set(cid);
            ctx.Items[HttpContextItemKey] = cid;

            var header = _options.CorrelationId.HeaderName;
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers[header] = cid;
                return Task.CompletedTask;
            });
        }

        using var scope = id is null
            ? null
            : _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id });

        if (!ShouldTrack(ctx.Request.Path))
        {
            await _next(ctx);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await _next(ctx);
        }
        catch
        {
            failed = true; // unhandled exception: the server will answer 500
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var status = failed && ctx.Response.StatusCode < 400 ? 500 : ctx.Response.StatusCode;
            var path = ctx.Request.Path.Value ?? "/";
            if (path.Length > MaxPathLength) path = path[..MaxPathLength];

            // TryWrite: if the channel is full, drop this hit instead of slowing the API down.
            _channel.TryWrite(new ApiHit(
                ctx.Request.Method,
                GetRoutePattern(ctx),
                path,
                status,
                elapsed,
                DateTime.UtcNow,
                id));
        }
    }

    private string ResolveOrCreateId(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers[_options.CorrelationId.HeaderName].FirstOrDefault();
        return incoming is not null && ValidId.IsMatch(incoming)
            ? incoming
            : Guid.NewGuid().ToString("N");
    }

    private bool ShouldTrack(PathString path)
    {
        if (!_options.Analytics.Enabled) return false;
        if (_dashboard.Matches(path)) return false;

        foreach (var skip in _options.Analytics.SkipPaths)
            if (!string.IsNullOrEmpty(skip) && path.StartsWithSegments(skip, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    // The endpoint is set by routing (which runs later in the pipeline) and is still available after _next returns.
    private static string GetRoutePattern(HttpContext ctx)
    {
        var raw = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (string.IsNullOrEmpty(raw)) return InMemoryApiInsightsStore.UnmatchedRoute;
        return raw.StartsWith('/') ? raw : "/" + raw;
    }
}
