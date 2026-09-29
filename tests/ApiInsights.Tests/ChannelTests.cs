using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ApiInsights.Tests;

public class ChannelTests
{
    private static ApiHit Hit(int i) => new("GET", "/x", "/x", 200, i, DateTime.UtcNow, null);

    private static ApiHitChannel CreateChannel(int capacity)
    {
        var o = new ApiInsightsOptions();
        o.Analytics.ChannelCapacity = capacity;
        return new ApiHitChannel(Options.Create(o));
    }

    [Fact]
    public void When_full_new_hits_are_dropped_and_older_ones_are_kept()
    {
        var channel = CreateChannel(capacity: 2);

        var results = Enumerable.Range(1, 5).Select(i => channel.TryWrite(Hit(i))).ToArray();

        Assert.Equal(new[] { true, true, false, false, false }, results);
        Assert.Equal(2, channel.Reader.Count);
        Assert.True(channel.Reader.TryRead(out var first));
        Assert.Equal(1, first!.DurationMs);
    }

    [Fact]
    public void Capacity_below_one_is_clamped_to_one()
    {
        var channel = CreateChannel(capacity: 0);

        Assert.True(channel.TryWrite(Hit(1)));
        Assert.False(channel.TryWrite(Hit(2)));
    }

    [Fact]
    public async Task A_full_channel_never_blocks_or_fails_requests()
    {
        // No collector running + capacity 1 => the channel is full after the first request.
        using var host = await TestApp.StartAsync(
            o => o.Analytics.ChannelCapacity = 1,
            runCollector: false);
        var client = host.GetTestClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => client.GetAsync($"/users/{i}")));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode));
        Assert.All(responses, r => Assert.True(r.Headers.Contains("X-Correlation-Id")));

        // Give the tail of the pipeline a moment, then verify only one hit could be queued.
        await Task.Delay(200);
        Assert.Equal(1, host.Services.GetRequiredService<ApiHitChannel>().Reader.Count);
    }

    [Fact]
    public async Task Pending_hits_are_flushed_to_the_store_on_shutdown()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();
        var channel = host.Services.GetRequiredService<ApiHitChannel>();
        var store = host.Services.GetRequiredService<IApiStatsProvider>();
        const int count = 50;

        for (var i = 0; i < count; i++) await client.GetAsync($"/users/{i}");

        // wait until every hit has left the request pipeline (either queued or already stored)
        for (var i = 0; i < 60 && store.GetSnapshot().TotalRequests + channel.Reader.Count < count; i++)
            await Task.Delay(50);

        await host.StopAsync();

        Assert.Equal(count, store.GetSnapshot().TotalRequests);
    }
}
