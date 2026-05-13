namespace TourkitAiProxy.Services;

/// In-memory usage tracker. Counters reset khi process restart.
/// Cost estimate hardcode theo DeepSeek V4 Pro retail ($0.27/$1.10 per Mtok) — không
/// chính xác cho model khác, nhưng đủ làm tín hiệu order-of-magnitude.
public class UsageTracker
{
    private readonly object _lock = new();
    private long _calls, _inTok, _outTok, _totalMs;
    private readonly Dictionary<string, long> _byModel = new();

    public void Track(string model, int inTok, int outTok, long ms)
    {
        lock (_lock)
        {
            _calls++;
            _inTok  += inTok;
            _outTok += outTok;
            _totalMs += ms;
            _byModel[model] = _byModel.GetValueOrDefault(model) + 1;
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            var costUsd = (_inTok * 0.27 + _outTok * 1.10) / 1_000_000.0;
            return new
            {
                calls            = _calls,
                inputTokens      = _inTok,
                outputTokens     = _outTok,
                avgLatencyMs     = _calls == 0 ? 0 : _totalMs / _calls,
                estimatedCostUsd = Math.Round(costUsd, 4),
                byModel          = _byModel
            };
        }
    }
}
