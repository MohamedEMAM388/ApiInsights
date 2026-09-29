using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiInsights.Tests;

public class MiddlewareTests
{
    [Fact]
    public async Task Generates_a_correlation_id_when_client_sends_none()
    {
        using var host = await TestApp.StartAsync();
        var res = await host.GetTestClient().GetAsync("/users/1");

        Assert.True(res.Headers.TryGetValues("X-Correlation-Id", out var values));
        Assert.Matches("^[a-f0-9]{32}$", values!.Single());
    }

    [Fact]
    public async Task Reuses_a_valid_incoming_correlation_id()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "abc-123");

        var res = await client.GetAsync("/users/1");
        Assert.Equal("abc-123", res.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Theory]
    [InlineData("bad id;\tinjected")]
    [InlineData("with_underscore")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 72 chars
    public async Task Replaces_an_invalid_incoming_id_to_prevent_log_injection(string incoming)
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-Id", incoming);

        var res = await client.GetAsync("/users/1");
        var id = res.Headers.GetValues("X-Correlation-Id").Single();

        Assert.NotEqual(incoming, id);
        Assert.Matches("^[a-f0-9]{32}$", id);
    }

    [Fact]
    public async Task Records_hits_grouped_by_route_pattern_with_the_correlation_id()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();
        await client.GetAsync("/users/1");
        await client.GetAsync("/users/2");
        await TestApp.WaitForRequestsAsync(host, 2);

        var snap = TestApp.Snapshot(host);
        var e = Assert.Single(snap.Endpoints);
        Assert.Equal("/users/{id}", e.Route);
        Assert.Equal(2, e.Calls);
        Assert.All(snap.SlowestRequests, h => Assert.False(string.IsNullOrEmpty(h.CorrelationId)));
    }

    [Fact]
    public async Task Unknown_paths_are_recorded_as_unmatched_and_500s_appear_in_errors()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();
        await client.GetAsync("/nope/123");
        await client.GetAsync("/boom");
        await TestApp.WaitForRequestsAsync(host, 2);

        var snap = TestApp.Snapshot(host);
        Assert.Contains(snap.Endpoints, e => e.Route == "unmatched");
        Assert.Single(snap.RecentErrors);
    }

    [Fact]
    public async Task Unhandled_exceptions_are_recorded_as_500()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/crash"));
        await TestApp.WaitForRequestsAsync(host, 1);

        var error = Assert.Single(TestApp.Snapshot(host).RecentErrors);
        Assert.Equal(500, error.StatusCode);
        Assert.Equal("/crash", error.Path);
        Assert.False(string.IsNullOrEmpty(error.CorrelationId));
    }

    [Fact]
    public async Task Skipped_paths_are_not_recorded_but_still_get_a_correlation_id()
    {
        using var host = await TestApp.StartAsync(o => o.Analytics.SkipPaths = new[] { "/users" });
        var res = await host.GetTestClient().GetAsync("/users/1");
        await Task.Delay(300);

        Assert.True(res.Headers.Contains("X-Correlation-Id"));
        Assert.Equal(0, TestApp.Snapshot(host).TotalRequests);
    }

    [Fact]
    public async Task Analytics_disabled_records_nothing_but_correlation_id_still_works()
    {
        using var host = await TestApp.StartAsync(o => o.Analytics.Enabled = false);
        var res = await host.GetTestClient().GetAsync("/users/1");
        await Task.Delay(300);

        Assert.True(res.Headers.Contains("X-Correlation-Id"));
        Assert.Equal(0, TestApp.Snapshot(host).TotalRequests);
    }
}
