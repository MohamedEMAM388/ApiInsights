using Microsoft.Extensions.Options;

namespace ApiInsights;

internal sealed class InMemoryApiInsightsStore : IApiInsightsStore
{
    internal const string UnmatchedRoute = "unmatched";

    private readonly object _lock = new();
    private readonly Dictionary<string, Accumulator> _endpoints = new(StringComparer.Ordinal);
    private readonly List<ApiHit> _slowest = new();
    private readonly Queue<ApiHit> _errors = new();
    private readonly AnalyticsOptions _options;
    private readonly int[] _bounds;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private long _total;

    public InMemoryApiInsightsStore(IOptions<ApiInsightsOptions> options)
    {
        _options = options.Value.Analytics;
        _bounds = _options.LatencyBucketsMs.OrderBy(x => x).ToArray();
    }

    public void Record(IReadOnlyList<ApiHit> hits)
    {
        lock (_lock)
        {
            foreach (var hit in hits)
            {
                _total++;
                GetAccumulator(hit).Add(hit, _bounds);
                TrackSlowest(hit);
                if (hit.StatusCode >= 500) TrackError(hit);
            }
        }
    }

    public ApiStatsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var endpoints = _endpoints.Values
                .Select(a => a.ToStats(_bounds))
                .OrderByDescending(e => e.Calls)
                .ToList();

            return new ApiStatsSnapshot(
                _startedUtc,
                _total,
                endpoints,
                _slowest.ToList(),
                _errors.Reverse().ToList()); // newest first
        }
    }

    private Accumulator GetAccumulator(ApiHit hit)
    {
        var isUnmatched = hit.Route == UnmatchedRoute;
        var key = isUnmatched ? UnmatchedRoute : $"{hit.Method} {hit.Route}";

        if (_endpoints.TryGetValue(key, out var acc)) return acc;

        // Cap the number of tracked endpoints so random paths can't grow memory forever.
        if (!isUnmatched && _endpoints.Count >= _options.MaxTrackedEndpoints)
        {
            key = UnmatchedRoute;
            if (_endpoints.TryGetValue(key, out acc)) return acc;
            isUnmatched = true;
        }

        acc = new Accumulator(isUnmatched ? "*" : hit.Method, isUnmatched ? UnmatchedRoute : hit.Route, _bounds.Length + 1);
        _endpoints[key] = acc;
        return acc;
    }

    private void TrackSlowest(ApiHit hit)
    {
        var max = _options.SlowRequestsCount;
        if (max <= 0) return;
        if (_slowest.Count >= max && hit.DurationMs <= _slowest[^1].DurationMs) return;

        _slowest.Add(hit);
        _slowest.Sort((a, b) => b.DurationMs.CompareTo(a.DurationMs));
        if (_slowest.Count > max) _slowest.RemoveAt(_slowest.Count - 1);
    }

    private void TrackError(ApiHit hit)
    {
        var max = _options.RecentErrorsCount;
        if (max <= 0) return;
        _errors.Enqueue(hit);
        while (_errors.Count > max) _errors.Dequeue();
    }

    private sealed class Accumulator
    {
        private readonly int[] _buckets;
        private long _calls, _client, _server;
        private double _totalMs, _maxMs;
        private DateTime _last;

        public Accumulator(string method, string route, int bucketCount)
        {
            Method = method;
            Route = route;
            _buckets = new int[bucketCount];
        }

        public string Method { get; }
        public string Route { get; }

        public void Add(ApiHit hit, int[] bounds)
        {
            _calls++;
            _totalMs += hit.DurationMs;
            if (hit.DurationMs > _maxMs) _maxMs = hit.DurationMs;
            _last = hit.TimestampUtc;

            if (hit.StatusCode is >= 400 and < 500) _client++;
            else if (hit.StatusCode >= 500) _server++;

            var i = 0;
            while (i < bounds.Length && hit.DurationMs > bounds[i]) i++;
            _buckets[i]++; // last bucket = overflow
        }

        public EndpointStats ToStats(int[] bounds) => new(
            Method, Route, _calls,
            _calls == 0 ? 0 : _totalMs / _calls,
            EstimateP95(bounds),
            _maxMs,
            _calls == 0 ? 0 : _client * 100.0 / _calls,
            _calls == 0 ? 0 : _server * 100.0 / _calls,
            _last);

        // Percentile estimate from histogram: upper bound of the bucket containing the 95th percentile,
        // clamped to the real max so it never over-reports.
        private double EstimateP95(int[] bounds)
        {
            if (_calls == 0) return 0;
            var target = Math.Ceiling(_calls * 0.95);
            long cumulative = 0;
            for (var i = 0; i < _buckets.Length; i++)
            {
                cumulative += _buckets[i];
                if (cumulative >= target)
                    return i < bounds.Length ? Math.Min(bounds[i], _maxMs) : _maxMs;
            }
            return _maxMs;
        }
    }
}
