using Microsoft.Extensions.Hosting;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiInsights.Tests;

/// <summary>Fake authentication: "X-Test-Role: Admin" logs the caller in with that role.</summary>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Role", out var role))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, role.ToString()) }, "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class DashboardTests
{
    private static void AddAuth(IServiceCollection s, bool policyWithScheme = false)
    {
        s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        s.AddAuthorization(o => o.AddPolicy("AdminOnly", p =>
        {
            if (policyWithScheme) p.AddAuthenticationSchemes("Test");
            p.RequireRole("Admin");
        }));
    }

    private static Task<IHost> StartSecuredAsync(Action<ApiInsightsOptions>? configure = null, bool policyWithScheme = false)
        => TestApp.StartAsync(
            o => { o.Dashboard.AuthorizationPolicy = "AdminOnly"; configure?.Invoke(o); },
            services: s => AddAuth(s, policyWithScheme));

    private static HttpClient ClientAs(IHost host, string? role)
    {
        var c = host.GetTestClient();
        if (role is not null) c.DefaultRequestHeaders.Add("X-Test-Role", role);
        return c;
    }

    [Fact]
    public async Task Locked_by_default_when_no_policy_is_configured()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_api-stats")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_api-stats/data")).StatusCode);
    }

    [Theory]
    [InlineData("/_api-stats")]
    [InlineData("/_api-stats/data")]
    public async Task Anonymous_users_are_challenged(string path)
    {
        using var host = await StartSecuredAsync();
        var res = await ClientAs(host, role: null).GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("/_api-stats")]
    [InlineData("/_api-stats/data")]
    public async Task Authenticated_users_without_the_required_role_are_forbidden(string path)
    {
        using var host = await StartSecuredAsync();
        var res = await ClientAs(host, "User").GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData(false)] // default authentication scheme
    [InlineData(true)]  // scheme named in the policy
    public async Task Authorized_users_get_the_page_and_the_data(bool policyWithScheme)
    {
        using var host = await StartSecuredAsync(policyWithScheme: policyWithScheme);
        var client = ClientAs(host, "Admin");

        var page = await client.GetAsync("/_api-stats");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("ApiInsights", await page.Content.ReadAsStringAsync());

        var data = await client.GetAsync("/_api-stats/data");
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        Assert.Contains("totalRequests", await data.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_policy_that_needs_no_authentication_works_even_without_an_authentication_scheme()
    {
        using var host = await TestApp.StartAsync(
            o => o.Dashboard.AuthorizationPolicy = "Open",
            services: s => s.AddAuthorization(o => o.AddPolicy("Open", p => p.RequireAssertion(_ => true))));

        var res = await host.GetTestClient().GetAsync("/_api-stats");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_policy_name_gives_a_clear_500_instead_of_exposing_the_dashboard()
    {
        using var host = await TestApp.StartAsync(
            o => o.Dashboard.AuthorizationPolicy = "DoesNotExist",
            services: s => AddAuth(s));

        var res = await ClientAs(host, "Admin").GetAsync("/_api-stats");

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Contains("DoesNotExist", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_AddAuthorization_gives_a_clear_500()
    {
        using var host = await TestApp.StartAsync(o => o.Dashboard.AuthorizationPolicy = "AdminOnly");

        var res = await host.GetTestClient().GetAsync("/_api-stats");

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Contains("AddAuthorization", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Custom_dashboard_path_is_used_and_the_old_one_is_gone()
    {
        using var host = await StartSecuredAsync(o => o.Dashboard.Path = "/ops/stats");
        var client = ClientAs(host, "Admin");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ops/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ops/stats/data")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_api-stats")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_responses_are_not_cached_and_not_counted_in_analytics()
    {
        using var host = await StartSecuredAsync();
        var client = ClientAs(host, "Admin");

        var res = await client.GetAsync("/_api-stats/data");
        await client.GetAsync("/_api-stats");
        await Task.Delay(300);

        Assert.True(res.Headers.CacheControl!.NoStore);
        Assert.Equal(0, TestApp.Snapshot(host).TotalRequests);
    }

    [Fact]
    public async Task Data_endpoint_returns_what_was_recorded()
    {
        using var host = await StartSecuredAsync();
        var client = ClientAs(host, "Admin");
        await client.GetAsync("/users/1");
        await TestApp.WaitForRequestsAsync(host, 1);

        var json = await (await client.GetAsync("/_api-stats/data")).Content.ReadAsStringAsync();

        Assert.Contains("/users/{id}", json);
    }
}
