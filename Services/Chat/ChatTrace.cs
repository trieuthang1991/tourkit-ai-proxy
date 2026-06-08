// Services/Chat/ChatTrace.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// 1 bước quan sát được trong workflow Chat Agent.
/// Dùng để hiển thị cho team theo dõi AI "nghĩ" / "lấy số" / "phân tích" thế nào.
/// </summary>
public record ChatTraceStep(
    [property: JsonPropertyName("name")]       string Name,         // vd "planner_call", "tool_dispatch", "analysis"
    [property: JsonPropertyName("status")]     string Status,       // "ok" | "skip" | "fail" | "fallback"
    [property: JsonPropertyName("durationMs")] long   DurationMs,
    [property: JsonPropertyName("summary")]    string Summary,      // 1 dòng "Planner chọn cashflow, params=..."
    [property: JsonPropertyName("meta")]       Dictionary<string, object?>? Meta = null  // raw payload nếu cần đào sâu
);

/// <summary>
/// Toàn bộ trace của 1 lượt chat. Trả ra khi request có debug=true.
/// </summary>
public record ChatTrace(
    [property: JsonPropertyName("runId")]    string RunId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")]    string? Model,
    [property: JsonPropertyName("agent")]    string Agent,         // "JsonPlannerAgent" | "NativeToolUseAgent"
    [property: JsonPropertyName("totalMs")]  long   TotalMs,
    [property: JsonPropertyName("steps")]    List<ChatTraceStep> Steps
);

/// <summary>
/// Thu thập step trong 1 lần chat. KHÔNG thread-safe per-collector (gọi từ 1 thread workflow),
/// nhưng dùng ConcurrentQueue để parallel tool exec push step đồng thời được.
/// </summary>
public sealed class TraceCollector
{
    private readonly ConcurrentQueue<ChatTraceStep> _steps = new();
    private readonly Stopwatch _totalSw = Stopwatch.StartNew();
    private readonly string _runId;
    private string _agent = "";

    public bool Enabled { get; }
    public string RunId => _runId;

    public TraceCollector(bool enabled)
    {
        Enabled = enabled;
        _runId = $"r_{Guid.NewGuid():N}"[..10];
    }

    public void SetAgent(string agent) => _agent = agent;

    /// <summary>
    /// Ghi 1 step. No-op nếu trace chưa bật.
    /// </summary>
    public void Step(string name, string status, long durationMs, string summary,
        Dictionary<string, object?>? meta = null)
    {
        if (!Enabled) return;
        _steps.Enqueue(new ChatTraceStep(name, status, durationMs, summary, meta));
    }

    /// <summary>
    /// Đóng gói trace cuối cùng để trả ra response/SSE.
    /// </summary>
    public ChatTrace Build(string provider, string? model)
    {
        _totalSw.Stop();
        return new ChatTrace(
            RunId:    _runId,
            Provider: provider,
            Model:    model,
            Agent:    _agent,
            TotalMs:  _totalSw.ElapsedMilliseconds,
            Steps:    _steps.ToList()
        );
    }

    /// <summary>
    /// Helper dùng kiểu using-block để đo step tự động.
    /// var t = trace.Begin("planner_call");
    /// ... do work ...
    /// t.Done("ok", "Planner chọn cashflow");
    /// </summary>
    public StepTimer Begin(string name) => new StepTimer(this, name);

    public sealed class StepTimer
    {
        private readonly TraceCollector _c;
        private readonly string _name;
        private readonly Stopwatch _sw;
        private bool _done;

        internal StepTimer(TraceCollector c, string name)
        {
            _c = c; _name = name; _sw = Stopwatch.StartNew();
        }

        public void Done(string status, string summary, Dictionary<string, object?>? meta = null)
        {
            if (_done) return;
            _done = true;
            _sw.Stop();
            _c.Step(_name, status, _sw.ElapsedMilliseconds, summary, meta);
        }
    }
}
