using Microsoft.Extensions.Options;

namespace ApiInsights.Tests;

public class StoreTests
{
    private static InMemoryApiInsightsStore CreateStore(Action<ApiInsightsOptions>? configure = null)
    {
        var o = new ApiInsightsOptions();
        configure?.Invoke(o);
        return new InMemoryApiInsightsStore(Options.Create(o));
    }

    private static ApiHit Hit(string route = "/users/{id}", int status = 200, double ms = 10, string method = "GET")
        => new(method, route, route, status, ms, DateTime.UtcNow, "cid");

    [Fact]
    public void Groups_by_route_pattern_and_computes_average_and_rates()
    {
        var store = CreateStore();
        store.Record(new[] { Hit(ms: 10), Hit(ms: 30), Hit(status: 404, ms: 20), Hit(status: 500, ms: 40) });

        var e = Assert.Single(store.GetSnapshot().Endpoints);
        Assert.Equal(4, e.Calls);
        Assert.Equal(25, e.AvgMs);
        Assert.Equal(25, e.ClientErrorRate);
        Assert.Equal(25, e.ServerErrorRate);
    }

    [Fact]
    public void P95_is_estimated_from_buckets_and_never_exceeds_max()
    {
        var store = CreateStore();
        var hits = Enumerable.Range(0, 100).Select(_ => Hit(ms: 5)).ToList();
        hits[0] = Hit(ms: 900); // one slow outlier should not affect p95 of 100 calls
        store.Record(hits);

        var e = store.GetSnapshot().Endpoints.Single();
        Assert.True(e.P95Ms <= 10);
        Assert.Equal(900, e.MaxMs);
    }

    [Fact]
    public void Extra_routes_beyond_the_limit_are_grouped_as_unmatched()
    {
        var store = CreateStore(o => o.Analytics.MaxTrackedEndpoints = 2);
        store.Record(new[] { Hit("/a"), Hit("/b"), Hit("/c"), Hit("/d") });

        var routes = store.GetSnapshot().Endpoints.Select(e => e.Route).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "/a", "/b", "unmatched" }, routes);
        Assert.Equal(2, store.GetSnapshot().Endpoints.Single(e => e.Route == "unmatched").Calls);
    }

    [Fact]
    public void Keeps_only_the_slowest_n_requests_sorted_descending()
    {
        var store = CreateStore(o => o.Analytics.SlowRequestsCount = 3);
        store.Record(Enumerable.Range(1, 10).Select(i => Hit(ms: i)).ToList());

        var slow = store.GetSnapshot().SlowestRequests.Select(h => h.DurationMs).ToList();
        Assert.Equal(new[] { 10.0, 9.0, 8.0 }, slow);
    }

    [Fact]
    public void Recent_errors_only_contain_5xx_newest_first_and_are_capped()
    {
        var store = CreateStore(o => o.Analytics.RecentErrorsCount = 2);
        store.Record(new[]
        {
            Hit(status: 500, ms: 1), Hit(status: 404), Hit(status: 502, ms: 2), Hit(status: 503, ms: 3)
        });

        var errors = store.GetSnapshot().RecentErrors;
        Assert.Equal(new[] { 503, 502 }, errors.Select(e => e.StatusCode));
    }
}
