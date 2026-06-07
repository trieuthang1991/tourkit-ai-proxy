// Services/Chat/UnresolvedQuestionsLog.cs
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Append-only log cau hoi AI khong suy luan duoc (data/chat-unresolved.jsonl).
/// Tham chieu cho dev: 9 trigger tag (planner_none, both_fail, tool_empty, ...).
/// Auto-rotate khi file >50MB; auto-purge entry >30 ngay qua daily cleanup.
/// </summary>
public class UnresolvedQuestionsLog
{
    private readonly string _filePath;
    private readonly ILogger<UnresolvedQuestionsLog> _log;
    private readonly object _lock = new();
    private const long MaxBytes = 50L * 1024 * 1024;

    public UnresolvedQuestionsLog(IWebHostEnvironment env, ILogger<UnresolvedQuestionsLog> log)
    {
        _log = log;
        var dataDir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "chat-unresolved.jsonl");
    }

    /// <summary>
    /// Tag la 1 trong 9 trigger o spec section 8.6:
    ///   planner_none_but_data_intent  -- planner tra tool=none nhung cau hoi ro rang can so lieu
    ///   both_planner_and_heuristic_fail -- ca planner JSON fail + HeuristicRoute null
    ///   tool_returned_empty           -- BuildChatData tra ve khong co noi dung
    ///   upstream_persistent_error     -- TourKitApiException sau retry
    ///   ai_hallucinated_numbers       -- ValidateNumbers tra warning != null
    ///   iteration_limit_reached       -- iteration >= 3 trong NativeToolUseAgent
    ///   response_too_short_after_retry -- IsTooShort sau ca 2 lan retry
    ///   input_truncated               -- wasTruncated = true trong ChatAgentService
    ///   injection_blocked             -- (de lai G3-2, chua implement)
    /// </summary>
    public void Append(
        string tag, string sessionId, string tenantId, string question,
        IReadOnlyList<ChatTurn>? history,
        string? plannerRaw, string? toolChosen, string? aiReplyPreview,
        string? provider, string? model, int? iterations,
        long latencyMs, int tokensIn, int tokensOut)
    {
        try
        {
            lock (_lock)
            {
                // Rotate neu vuot size cap -- giu file chinh gon
                if (File.Exists(_filePath))
                {
                    var size = new FileInfo(_filePath).Length;
                    if (size > MaxBytes)
                    {
                        var rotated = _filePath + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                        File.Move(_filePath, rotated);
                    }
                }

                var entry = new
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    tag,
                    sessionId,
                    tenantId,
                    question = (question ?? "").Length > 500
                        ? question![..500] + "…"
                        : question,
                    history = history?.TakeLast(3).Select(h => new
                    {
                        role    = h.Role,
                        content = (h.Content ?? "").Length > 300
                            ? h.Content![..300] + "…"
                            : h.Content
                    }),
                    plannerRaw = plannerRaw is { Length: > 400 }
                        ? plannerRaw[..400] + "…"
                        : plannerRaw,
                    toolChosen,
                    aiReplyPreview = aiReplyPreview is { Length: > 300 }
                        ? aiReplyPreview[..300] + "…"
                        : aiReplyPreview,
                    provider, model, iterations,
                    latencyMs, tokensIn, tokensOut
                };

                var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                File.AppendAllText(_filePath, json + "\n");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[unresolved-log] write that bai");
        }
    }

    /// <summary>
    /// Doc N entry moi nhat, filter theo tag neu co.
    /// Tra danh sach sap xep tu moi nhat -> cu nhat (reverse order).
    /// </summary>
    public List<JsonElement> Read(int days = 7, string? tag = null, int maxEntries = 200)
    {
        var result = new List<JsonElement>();
        if (!File.Exists(_filePath)) return result;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        try
        {
            // Doc nguoc tu cuoi file de lay entry moi nhat truoc
            var lines = File.ReadAllLines(_filePath);
            for (int i = lines.Length - 1; i >= 0 && result.Count < maxEntries; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    var doc = JsonDocument.Parse(lines[i]);
                    var ts = doc.RootElement.GetProperty("ts").GetString();
                    if (DateTime.Parse(ts!).ToUniversalTime() < cutoff) break;
                    if (tag != null && doc.RootElement.GetProperty("tag").GetString() != tag) continue;
                    result.Add(doc.RootElement.Clone());
                }
                catch { /* bo qua dong loi format */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[unresolved-log] read that bai");
        }
        return result;
    }
}
