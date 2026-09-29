using Microsoft.Extensions.Hosting;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ApiInsights.Tests;

public class PropagationTests
{
    /// <summary>Fake downstream service: records the headers of every outgoing request.</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<Dictionary<string, string[]>> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static Task<IHost> StartWithDownstreamAsync(CaptureHandler capture, Action<ApiInsightsOptions>? configure = null)
        => TestApp.StartAsync(
            configure,
            services: s =>
            {
                s.AddSingleton(capture);
                s.AddHttpClient("down")
                    .AddCorrelationIdPropagation()
                    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<CaptureHandler>());
            },
            endpoints: e =>
            {
                e.MapGet("/call", async (IHttpClientFactory f) =>
                {
                    using var r = await f.CreateClient("down").GetAsync("http://downstream.test/x");
                    return Results.Ok();
                });
                e.MapGet("/call-manual", async (IHttpClientFactory f) =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/x");
                    req.Headers.TryAddWithoutValidation("X-Correlation-Id", "manual");
                    using var r = await f.CreateClient("down").SendAsync(req);
                    return Results.Ok();
                });
            });

    [Fact]
    public async Task Outgoing_calls_carry_the_current_request_correlation_id()
    {
        var capture = new CaptureHandler();
        using var host = await StartWithDownstreamAsync(capture);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "trace-777");

        var res = await client.GetAsync("/call");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var sent = Assert.Single(capture.Requests);
        Assert.Equal("trace-777", Assert.Single(sent["X-Correlation-Id"]));
    }

    [Fact]
    public async Task Outgoing_calls_carry_a_generated_id_that_matches_the_response_header()
    {
        var capture = new CaptureHandler();
        using var host = await StartWithDownstreamAsync(capture);

        var res = await host.GetTestClient().GetAsync("/call");

        var responseId = res.Headers.GetValues("X-Correlation-Id").Single();
        Assert.Equal(responseId, Assert.Single(capture.Requests)["X-Correlation-Id"].Single());
    }

    [Fact]
    public async Task Outgoing_calls_use_the_custom_header_name()
    {
        var capture = new CaptureHandler();
        using var host = await StartWithDownstreamAsync(capture, o => o.CorrelationId.HeaderName = "X-Request-Id");

        await host.GetTestClient().GetAsync("/call");

        var sent = Assert.Single(capture.Requests);
        Assert.True(sent.ContainsKey("X-Request-Id"));
        Assert.False(sent.ContainsKey("X-Correlation-Id"));
    }

    [Fact]
    public async Task A_header_set_explicitly_on_the_outgoing_request_is_not_overwritten()
    {
        var capture = new CaptureHandler();
        using var host = await StartWithDownstreamAsync(capture);

        await host.GetTestClient().GetAsync("/call-manual");

        Assert.Equal("manual", Assert.Single(Assert.Single(capture.Requests)["X-Correlation-Id"]));
    }

    [Fact]
    public async Task Nothing_is_forwarded_when_correlation_id_is_disabled()
    {
        var capture = new CaptureHandler();
        using var host = await StartWithDownstreamAsync(capture, o => o.CorrelationId.Enabled = false);

        await host.GetTestClient().GetAsync("/call");

        Assert.False(Assert.Single(capture.Requests).ContainsKey("X-Correlation-Id"));
    }

    [Fact]
    public async Task No_header_is_added_when_there_is_no_current_request()
    {
        var capture = new CaptureHandler();
        var handler = new CorrelationIdDelegatingHandler(new CorrelationIdAccessor(), Options.Create(new ApiInsightsOptions()))
        {
            InnerHandler = capture
        };

        await new HttpMessageInvoker(handler).SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/x"), CancellationToken.None);

        Assert.False(Assert.Single(capture.Requests).ContainsKey("X-Correlation-Id"));
    }
}
