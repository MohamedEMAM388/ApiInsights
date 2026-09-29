using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ApiInsights;

/// <summary>Serves the built-in dashboard page and its JSON data. Disabled until an authorization policy is set.</summary>
internal sealed class DashboardHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiInsightsOptions _options;
    private readonly IApiStatsProvider _stats;
    private readonly PathString _basePath;
    private readonly string _html;

    public DashboardHandler(ApiInsightsOptions options, IApiStatsProvider stats)
    {
        _options = options;
        _stats = stats;

        var path = "/" + (options.Dashboard.Path ?? "/_api-stats").Trim('/');
        _basePath = new PathString(path);
        _html = LoadHtml();
    }

    private bool IsEnabled => !string.IsNullOrWhiteSpace(_options.Dashboard.AuthorizationPolicy);

    /// <summary>True when the request targets the dashboard path (used to also exclude it from analytics).</summary>
    public bool Matches(PathString path) => path.StartsWithSegments(_basePath, StringComparison.OrdinalIgnoreCase);

    /// <returns>true if the request was handled (response written), false to continue the pipeline.</returns>
    public async Task<bool> TryHandleAsync(HttpContext ctx)
    {
        if (!IsEnabled) return false; // locked by default -> falls through (usually 404)

        ctx.Request.Path.StartsWithSegments(_basePath, StringComparison.OrdinalIgnoreCase, out var remaining);
        var isData = remaining.Equals("/data", StringComparison.OrdinalIgnoreCase);
        var isPage = !remaining.HasValue || remaining == "/";
        if (!isData && !isPage) return false;

        if (!await AuthorizeAsync(ctx)) return true; // challenge / forbid already written

        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

        if (isData)
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(_stats.GetSnapshot(), Json));
        }
        else
        {
            var dataUrl = ctx.Request.PathBase + _basePath + "/data";
            var page = _html
                .Replace("{{DATA_URL}}", WebUtility.HtmlEncode(dataUrl))
                .Replace("{{REFRESH_SECONDS}}", Math.Max(1, _options.Dashboard.RefreshIntervalSeconds).ToString());

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(page);
        }

        return true;
    }

    // Authentication runs AFTER UseApiInsights in the pipeline, so we authenticate + authorize here ourselves,
    // exactly like the built-in authorization middleware would.
    private async Task<bool> AuthorizeAsync(HttpContext ctx)
    {
        var sp = ctx.RequestServices;
        var policyName = _options.Dashboard.AuthorizationPolicy!;

        var evaluator = sp.GetService<IPolicyEvaluator>();
        var provider = sp.GetService<IAuthorizationPolicyProvider>();
        if (evaluator is null || provider is null)
        {
            await Fail(ctx, "ApiInsights: call builder.Services.AddAuthorization() to use the dashboard.");
            return false;
        }

        var policy = await provider.GetPolicyAsync(policyName);
        if (policy is null)
        {
            await Fail(ctx, $"ApiInsights: authorization policy '{policyName}' was not found.");
            return false;
        }

        var authn = await evaluator.AuthenticateAsync(policy, ctx);

        // UseApiInsights() runs BEFORE UseAuthentication(), so HttpContext.User is still anonymous here.
        // When the policy names no schemes, the evaluator only looks at HttpContext.User, so we authenticate
        // with the default scheme ourselves (exactly what UseAuthentication would have done).
        if (!authn.Succeeded && policy.AuthenticationSchemes.Count == 0)
        {
            var result = await TryDefaultAuthenticateAsync(ctx);
            if (result is { Succeeded: true, Principal: not null })
            {
                ctx.User = result.Principal;
                authn = result;
            }
        }

        var authz = await evaluator.AuthorizeAsync(policy, authn, ctx, resource: null);
        if (authz.Succeeded) return true;

        var schemes = policy.AuthenticationSchemes;
        if (authz.Challenged)
        {
            if (schemes.Count > 0) foreach (var s in schemes) await ctx.ChallengeAsync(s);
            else await ctx.ChallengeAsync();
        }
        else
        {
            if (schemes.Count > 0) foreach (var s in schemes) await ctx.ForbidAsync(s);
            else await ctx.ForbidAsync();
        }
        return false;
    }

    private static async Task<AuthenticateResult?> TryDefaultAuthenticateAsync(HttpContext ctx)
    {
        try
        {
            return await ctx.AuthenticateAsync();
        }
        catch (InvalidOperationException)
        {
            return null; // authentication isn't configured / no default scheme: treat as anonymous
        }
    }

    private static Task Fail(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return ctx.Response.WriteAsync(message);
    }

    private static string LoadHtml()
    {
        using var stream = typeof(DashboardHandler).Assembly.GetManifestResourceStream("dashboard.html")
            ?? throw new InvalidOperationException("ApiInsights dashboard resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
