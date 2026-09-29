using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiInsights.Tests;

public class CorrelationIdTests
{
    [Fact]
    public async Task When_disabled_no_header_is_returned_and_accessor_is_null_but_analytics_still_work()
    {
        using var host = await TestApp.StartAsync(o => o.CorrelationId.Enabled = false);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "incoming-1"); // must be ignored

        var res = await client.GetAsync("/whoami");

        Assert.False(res.Headers.Contains("X-Correlation-Id"));
        Assert.Equal("none", await res.Content.ReadAsStringAsync());

        await TestApp.WaitForRequestsAsync(host, 1);
        var hit = Assert.Single(TestApp.Snapshot(host).SlowestRequests);
        Assert.Null(hit.CorrelationId);
    }

    [Fact]
    public async Task Custom_header_name_is_used_for_reading_and_writing()
    {
        using var host = await TestApp.StartAsync(o => o.CorrelationId.HeaderName = "X-Request-Id");
        var client = host.GetTestClient();

        // generated
        var generated = await client.GetAsync("/users/1");
        Assert.True(generated.Headers.Contains("X-Request-Id"));
        Assert.False(generated.Headers.Contains("X-Correlation-Id"));

        // reused
        client.DefaultRequestHeaders.Add("X-Request-Id", "req-42");
        var reused = await client.GetAsync("/users/1");
        Assert.Equal("req-42", reused.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task The_default_header_is_ignored_when_a_custom_header_name_is_configured()
    {
        using var host = await TestApp.StartAsync(o => o.CorrelationId.HeaderName = "X-Request-Id");
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "should-not-be-used");

        var res = await client.GetAsync("/users/1");
        Assert.NotEqual("should-not-be-used", res.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task Concurrent_requests_each_see_their_own_id()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();
        const int count = 200;

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(async _ =>
        {
            var res = await client.GetAsync("/whoami");
            return (Header: res.Headers.GetValues("X-Correlation-Id").Single(),
                    Body: await res.Content.ReadAsStringAsync());
        }));

        // the accessor inside the endpoint (after an await) returned the same id that went out in the header
        Assert.All(results, r => Assert.Equal(r.Header, r.Body));
        // and no two requests shared an id
        Assert.Equal(count, results.Select(r => r.Header).Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_requests_are_all_counted()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();
        const int count = 200;

        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => client.GetAsync("/whoami")));
        await TestApp.WaitForRequestsAsync(host, count);

        var snap = TestApp.Snapshot(host);
        Assert.Equal(count, snap.TotalRequests);
        Assert.Equal(count, Assert.Single(snap.Endpoints).Calls);
    }

    [Fact]
    public async Task The_id_does_not_leak_outside_the_request()
    {
        using var host = await TestApp.StartAsync();
        await host.GetTestClient().GetAsync("/users/1");

        Assert.Null(host.Services.GetRequiredService<ICorrelationIdAccessor>().CorrelationId);
    }
}
