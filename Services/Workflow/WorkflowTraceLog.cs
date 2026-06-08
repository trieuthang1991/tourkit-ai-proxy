// Services/Workflow/WorkflowTraceLog.cs
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Append-only log workflow trace (data/workflow-traces.jsonl).
/// Lưu MỌI request có debug=on để team xem lại sau (audit, post-mortem, regression check).
/// Auto-rotate file >50MB; Read() trả entry mới nhất trước.
///
/// Mỗi line là 1 JSON entry:
///   { ts, workflow, runId, totalMs, stepCount, path, method, statusCode,
///     sessionId, tenantId, trace: { ...full trace } }
/// </summary>
public class WorkflowTraceLog
{
    private readonly string _filePath;
    private readonly ILogger<WorkflowTraceLog> _log;
    private readonly object _lock = new();
    private const long MaxBytes = 50L * 1024 * 1024;

    public WorkflowTraceLog(IWebHostEnvironment env, ILogger<WorkflowTraceLog> log)
    {
        _log = log;
        var dataDir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "workflow-traces.jsonl");
    }

    /// <summary>Ghi 1 trace entry. Gọi từ WorkflowTraceMiddleware sau khi request xong.</summary>
    public void Append(HttpContext ctx, WorkflowTrace trace)
    {
        if (trace == null || trace.Steps == null || trace.Steps.Count == 0) return;
        try
        {
            lock (_lock)
            {
                // Rotate khi file lớn
                if (File.Exists(_filePath))
                {
                    var size = new FileInfo(_filePath).Length;
                    if (size > MaxBytes)
                    {
                        var rotated = _filePath + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                        File.Move(_filePath, rotated);
                    }
                }

                // Lấy session/tenant từ header nếu có
                var sessionId = ctx.Request.Headers.TryGetValue("X-Session-Id", out var sid)
                    ? sid.ToString() : null;

                var entry = new
                {
                    ts          = DateTime.UtcNow.ToString("o"),
                    workflow    = trace.Workflow,
                    runId       = trace.RunId,
                    totalMs     = trace.TotalMs,
                    stepCount   = trace.Steps.Count,
                    path        = ctx.Request.Path.Value,
                    method      = ctx.Request.Method,
                    statusCode  = ctx.Response.StatusCode,
                    sessionId,
                    trace       = trace   // full trace với steps + meta
                };

                File.AppendAllText(_filePath,
                    JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[workflow-trace-log] write fail");
        }
    }

    /// <summary>Đọc N entry mới nhất, filter theo workflow + days.</summary>
    public List<JsonElement> Read(int days = 7, string? workflow = null, int maxEntries = 100)
    {
        var result = new List<JsonElement>();
        if (!File.Exists(_filePath)) return result;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        try
        {
            var lines = File.ReadAllLines(_filePath);
            for (int i = lines.Length - 1; i >= 0 && result.Count < maxEntries; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    var doc = JsonDocument.Parse(lines[i]);
                    var ts = doc.RootElement.GetProperty("ts").GetString();
                    if (DateTime.Parse(ts!).ToUniversalTime() < cutoff) break;
                    if (workflow != null && doc.RootElement.GetProperty("workflow").GetString() != workflow)
                        continue;
                    result.Add(doc.RootElement.Clone());
                }
                catch { /* bỏ qua line lỗi format */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[workflow-trace-log] read fail");
        }
        return result;
    }

    /// <summary>Liệt kê các workflow đã được log + count + min/max latency (cho tab summary).</summary>
    public Dictionary<string, (int Count, long MaxMs, long MinMs)> Summary(int days = 7)
    {
        var map = new Dictionary<string, (int, long, long)>();
        if (!File.Exists(_filePath)) return map;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        try
        {
            foreach (var line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var ts = doc.RootElement.GetProperty("ts").GetString();
                    if (DateTime.Parse(ts!).ToUniversalTime() < cutoff) continue;
                    var wf = doc.RootElement.GetProperty("workflow").GetString() ?? "(unknown)";
                    var ms = doc.RootElement.GetProperty("totalMs").GetInt64();
                    if (map.TryGetValue(wf, out var cur))
                        map[wf] = (cur.Item1 + 1, Math.Max(cur.Item2, ms), Math.Min(cur.Item3, ms));
                    else
                        map[wf] = (1, ms, ms);
                }
                catch { }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[workflow-trace-log] summary fail"); }
        return map;
    }
}
