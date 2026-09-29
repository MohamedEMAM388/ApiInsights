# 📡 ApiInsights

> Request tracing and a built-in performance dashboard for ASP.NET Core APIs — **two lines in `Program.cs`, zero infrastructure.**

[![NuGet](https://img.shields.io/nuget/v/ApiInsights.svg)](https://www.nuget.org/packages/ApiInsights)
[![Downloads](https://img.shields.io/nuget/dt/ApiInsights.svg)](https://www.nuget.org/packages/ApiInsights)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

> ⚠️ **Preview release (`0.x`).** The API may change before `1.0`. Pin the exact version if you depend on it.

---

## 🎯 Why ApiInsights?

Every API team eventually asks the same questions:

- *"Which endpoints are slow?"*
- *"Which endpoints fail the most?"*
- *"A user reported an error — where are the logs for **that exact request**?"*

Answering them usually means setting up Prometheus, Grafana, or a full OpenTelemetry pipeline. That is the right call for large systems, but it is heavy for small and medium projects.

**ApiInsights gives you the essentials out of the box:**

| Feature | What you get |
|---------|--------------|
| 🔗 **Correlation ID** | A unique ID per request, attached to every log line, returned to the client, and forwarded to outgoing `HttpClient` calls |
| 📊 **Usage Analytics** | Per-endpoint call count, average latency, p95, and 4xx/5xx rates |
| 🖥️ **Built-in Dashboard** | A ready-made page with slow requests and recent errors, each linked to its Correlation ID |
| ⚡ **Non-blocking** | Metrics are recorded through an in-memory channel and processed in the background |
| 🔐 **Secure by default** | The dashboard is locked until you configure an authorization policy |

---

## 🚀 Quick Start

### 1️⃣ Install

```bash
dotnet add package ApiInsights --prerelease
```

### 2️⃣ Register

```csharp
// Program.cs
builder.Services.AddApiInsights();

var app = builder.Build();

app.UseApiInsights();   // register early in the pipeline (see "Pipeline order")
```

### 3️⃣ Run your API and open the dashboard

```
https://localhost:5001/_api-stats
```

That's it. Every request now has a Correlation ID and is counted in the analytics.

---

## ⚙️ Configuration

```csharp
builder.Services.AddApiInsights(options =>
{
    // 🔗 Correlation ID
    options.CorrelationId.Enabled = true;
    options.CorrelationId.HeaderName = "X-Correlation-Id";

    // 📊 Analytics
    options.Analytics.Enabled = true;
    options.Analytics.SkipPaths = new[] { "/health", "/_api-stats" };
    options.Analytics.MaxTrackedEndpoints = 500;
    options.Analytics.LatencyBucketsMs = new[] { 10, 25, 50, 100, 250, 500, 1000, 2500 };

    // 🖥️ Dashboard
    options.Dashboard.Path = "/_api-stats";
    options.Dashboard.AuthorizationPolicy = "AdminOnly";   // required to enable the dashboard
});
```

| Option | Default | Description |
|--------|---------|-------------|
| `CorrelationId.Enabled` | `true` | Generate/propagate a Correlation ID |
| `CorrelationId.HeaderName` | `X-Correlation-Id` | Header used for incoming and outgoing requests |
| `Analytics.Enabled` | `true` | Collect per-endpoint metrics |
| `Analytics.SkipPaths` | `[]` | Paths excluded from analytics |
| `Analytics.MaxTrackedEndpoints` | `500` | Upper bound on tracked routes; extra routes are grouped as `unmatched` |
| `Analytics.LatencyBucketsMs` | see above | Histogram buckets used to estimate percentiles |
| `Dashboard.Path` | `/_api-stats` | Dashboard URL |
| `Dashboard.AuthorizationPolicy` | `null` | Authorization policy name. **Dashboard stays disabled while `null`** |

---

## 🔗 Correlation ID

### How it works

1. If the client sends `X-Correlation-Id`, it is validated and reused. Otherwise a new one is generated.
2. The ID is added to the `ILogger` scope, so **every log written during the request includes it**.
3. The ID is returned in the response header, so a client can report *"my request `abc123` failed"*.
4. Outgoing `HttpClient` calls forward the same ID to downstream services.

Incoming IDs are restricted to 64 characters of letters, digits, and dashes to prevent log injection.

### Propagate to outgoing calls

```csharp
builder.Services
    .AddHttpClient("payments", c => c.BaseAddress = new Uri("https://payments.example.com"))
    .AddCorrelationIdPropagation();
```

### Read the ID anywhere

```csharp
public class OrderService
{
    private readonly ICorrelationIdAccessor _correlation;
    public OrderService(ICorrelationIdAccessor correlation) => _correlation = correlation;

    public void DoWork() => Console.WriteLine(_correlation.CorrelationId);
}
```

### Example log output

```
[abc123] info: OrderService  Creating order for customer 42
[abc123] warn: PaymentClient Payment provider responded slowly (1830 ms)
[abc123] fail: OrderService  Order creation failed
```

---

## 📊 Usage Analytics

For every endpoint (grouped by **route pattern**, e.g. `/users/{id}` — not `/users/1`, `/users/2`, …):

| Metric | Description |
|--------|-------------|
| Calls | Total number of requests |
| Avg latency | Mean response time |
| p95 latency | Estimated from histogram buckets |
| 4xx / 5xx rate | Client and server error percentages |
| Last call | Timestamp of the latest request |

### Programmatic access

```csharp
app.MapGet("/internal/stats", (IApiStatsProvider stats) => stats.GetSnapshot());
```

---

## 🖥️ Dashboard

The dashboard is a single page served by the library — no extra frontend build, no external services.

- **Endpoints table** — sortable by calls, latency, or error rate
- **Slowest requests** — with Correlation IDs for quick log lookup
- **Recent errors** — latest 5xx responses
- **Auto refresh** — configurable interval

> 🔐 The dashboard exposes your API structure, so it is **disabled until you set `Dashboard.AuthorizationPolicy`**.

```csharp
builder.Services.AddAuthorization(o =>
    o.AddPolicy("AdminOnly", p => p.RequireRole("Admin")));
```

The dashboard authenticates the request itself (using the policy's schemes, or your default authentication scheme), so it works even though `UseApiInsights()` runs before `UseAuthentication()`. An unauthenticated request gets `401`/a challenge and an unauthorized one gets `403`.

---

## 🏗️ Architecture

```
Incoming Request
      ↓
ApiInsightsMiddleware
  ├─ Resolve / generate Correlation ID
  ├─ Push ID to ILogger scope + response header
  └─ Measure duration
      ↓
Channel<ApiHit>   ← non-blocking write
      ↓
Background collector (batches hits)
      ↓
IApiInsightsStore  (In-Memory by default)
      ↓
Dashboard / IApiStatsProvider
```

### Project layout

| Package | Purpose |
|---------|---------|
| `ApiInsights` | Middleware, Correlation ID, collector, in-memory store, dashboard |
| `ApiInsights.EntityFrameworkCore` *(planned)* | Persistent storage with EF Core |

---

## 📌 Pipeline order

Register `UseApiInsights()` **before** exception handling and authentication, so failures like `500` and `401` are measured correctly:

```csharp
app.UseApiInsights();          // 1️⃣ first
app.UseExceptionHandler("/error");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

---

## ⚠️ Limitations

Being upfront about what the default setup does **not** do:

- **In-memory storage resets on restart.** Metrics are lost when the app restarts.
- **No aggregation across instances.** If you run multiple instances, each one shows its own numbers.
- **Percentiles are estimates** based on fixed buckets, not exact values.
- **Not a full observability stack.** No distributed tracing spans, no alerting.

If you need any of these, see the next section.

---

## 🤔 ApiInsights vs. OpenTelemetry

| | ApiInsights | OpenTelemetry + backend |
|---|---|---|
| Setup time | Minutes | Hours to days |
| Infrastructure | None | Collector + storage + UI |
| Distributed tracing | Correlation ID only | Full trace/span model |
| Multi-instance aggregation | ❌ (v1) | ✅ |
| Alerting | ❌ | ✅ |
| Best for | Small/medium APIs, internal tools, prototypes | Large or distributed systems |

They are not mutually exclusive — ApiInsights is a good starting point, and you can move to OpenTelemetry as your system grows.

---

## 🗺️ Roadmap

- [x] Correlation ID middleware and `HttpClient` propagation
- [x] In-memory analytics and dashboard
- [ ] EF Core storage provider
- [ ] Export endpoint (JSON / CSV)
- [ ] Optional integration with [UserActivityLogger](https://www.nuget.org/packages/UserActivityLogger) (`CorrelationId` on activity records)
- [ ] Configurable alerts (slow endpoint / error-rate thresholds)
- [ ] Minimal API and gRPC support

---

## 🤝 Contributing

Contributions are welcome! Please read [CONTRIBUTING.md](CONTRIBUTING.md) first.

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Add tests for your change
4. Open a pull request

Look for issues labeled `good first issue` if you are looking for a place to start.

---

## 📄 License

Released under the [MIT License](LICENSE).
