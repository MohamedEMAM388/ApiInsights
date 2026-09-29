using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ApiInsights;

/// <summary>Bounded in-memory queue between the middleware (writer) and the collector (reader).</summary>
internal sealed class ApiHitChannel
{
    private readonly Channel<ApiHit> _channel;

    public ApiHitChannel(IOptions<ApiInsightsOptions> options)
    {
        var capacity = Math.Max(1, options.Value.Analytics.ChannelCapacity);
        _channel = Channel.CreateBounded<ApiHit>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite, // never block a request
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool TryWrite(ApiHit hit) => _channel.Writer.TryWrite(hit);
    public ChannelReader<ApiHit> Reader => _channel.Reader;
}
