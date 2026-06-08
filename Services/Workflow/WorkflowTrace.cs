// Services/Workflow/WorkflowTrace.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// 1 bước quan sát được trong BẤT KỲ workflow AI nào (chat, review, mail, visa, deal, tour-builder).
/// Dùng để hiển thị cho team theo dõi AI "nghĩ" / "lấy số" / "phân tích" thế nào.
/// </summary>
public record WorkflowTraceStep(
    [property: JsonPropertyName("name")]       string Name,         // vd "planner_call", "tool_dispatch", "extract_pdf"
    [property: JsonPropertyName("status")]     string Status,       // "ok" | "skip" | "fail" | "fallback"
    [property: JsonPropertyName("durationMs")] long   DurationMs,
    [property: JsonPropertyName("summary")]    string Summary,      // 1 dòng người đọc hiểu
    [property: JsonPropertyName("meta")]       Dictionary<string, object?>? Meta = null  // raw payload đào sâu
);

/// <summary>
/// Toàn bộ trace của 1 request. Trả ra khi user bật debug (?debug=1 hoặc X-Debug header).
/// Field "workflow" dùng đa năng cho mọi tính năng (vd "ChatAgent", "CustomerReview", "VisaScoring").
/// </summary>
public record WorkflowTrace(
    [property: JsonPropertyName("runId")]    string RunId,
    [property: JsonPropertyName("workflow")] string Workflow,
    [property: JsonPropertyName("totalMs")]  long   TotalMs,
    [property: JsonPropertyName("steps")]    List<WorkflowTraceStep> Steps,
    [property: JsonPropertyName("meta")]     Dictionary<string, object?>? Meta = null  // workflow-wide meta (provider/model/feature)
);

/// <summary>
/// Thu thập step trong 1 request. KHÔNG thread-safe per-collector về writes (gọi từ 1 thread workflow),
/// nhưng dùng ConcurrentQueue để parallel exec push step đồng thời được.
/// </summary>
public sealed class TraceCollector
{
    private readonly ConcurrentQueue<WorkflowTraceStep> _steps = new();
    private readonly Stopwatch _totalSw = Stopwatch.StartNew();
    private readonly string _runId;
    private string _workflow = "";
    private readonly Dictionary<string, object?> _meta = new();

    public bool Enabled { get; }
    public string RunId => _runId;

    public TraceCollector(bool enabled)
    {
        Enabled = enabled;
        _runId = $"r_{Guid.NewGuid():N}"[..10];
    }

    /// Tên workflow đang chạy (vd "ChatAgent", "CustomerReview", "MailClassifier").
    public void SetWorkflow(string workflow) => _workflow = workflow;

    /// Thêm meta-data workflow-wide (provider, model, customerId, mailId...).
    public void SetMeta(string key, object? value) { if (Enabled) _meta[key] = value; }

    /// Ghi 1 step. No-op nếu trace chưa bật → zero overhead production.
    public void Step(string name, string status, long durationMs, string summary,
        Dictionary<string, object?>? meta = null)
    {
        if (!Enabled) return;
        _steps.Enqueue(new WorkflowTraceStep(name, status, durationMs, summary, meta));
    }

    /// Đóng gói trace cuối cùng để trả ra response/SSE.
    public WorkflowTrace Build()
    {
        _totalSw.Stop();
        return new WorkflowTrace(
            RunId:    _runId,
            Workflow: _workflow,
            TotalMs:  _totalSw.ElapsedMilliseconds,
            Steps:    _steps.ToList(),
            Meta:     _meta.Count > 0 ? new Dictionary<string, object?>(_meta) : null
        );
    }

    /// Helper using-block để đo step tự động.
    /// var t = trace.Begin("ai_call"); ... t.Done("ok", "...");
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
