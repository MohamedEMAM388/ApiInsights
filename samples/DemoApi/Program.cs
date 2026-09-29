using ApiInsights;

var builder = WebApplication.CreateBuilder(args);

// Show the Correlation ID in console logs
builder.Logging.AddSimpleConsole(o => o.IncludeScopes = true);

// DEMO ONLY: this policy lets everyone in. In a real app use e.g. p.RequireRole("Admin").
builder.Services.AddAuthorization(o => o.AddPolicy("AdminOnly", p => p.RequireAssertion(_ => true)));

builder.Services.AddApiInsights(o =>
{
    o.Analytics.SkipPaths = new[] { "/health" };
    o.Dashboard.AuthorizationPolicy = "AdminOnly";
});

// Outgoing calls forward the Correlation ID
builder.Services.AddHttpClient("self").AddCorrelationIdPropagation();

var app = builder.Build();

app.UseApiInsights(); // FIRST, so 500s are measured
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsync("Something went wrong.");
}));

app.MapGet("/health", () => "ok");

app.MapGet("/users/{id:int}", (int id, ILogger<Program> log, ICorrelationIdAccessor cid) =>
{
    log.LogInformation("Loading user {Id}", id);
    return new { id, name = $"User {id}", correlationId = cid.CorrelationId };
});

app.MapGet("/slow", async () =>
{
    await Task.Delay(Random.Shared.Next(100, 1500));
    return "finally";
});

app.MapGet("/fail", () => Results.StatusCode(500));
app.MapGet("/crash", () => { throw new InvalidOperationException("boom"); });

app.Run();
