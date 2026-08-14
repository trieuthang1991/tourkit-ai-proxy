using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Đọc TÊN trạng thái do từng công ty tự đặt rồi suy ra: cái nào còn phải làm, cái nào đã kết thúc.
///
/// Vì sao phải nhờ AI: CRM trả về danh sách trạng thái chỉ gồm mã + tên + màu — KHÔNG có cờ nào nói
/// trạng thái nào là "đã đóng". Mà tên thì mỗi công ty một kiểu: "Kết thúc", "Win", "Đã bàn giao",
/// "Chốt xong", "Ngưng theo". Dò từ khoá bắt được vài kiểu quen thuộc rồi trượt phần còn lại — và
/// trượt ở đây nghĩa là bản tin sáng bảo nhân viên gọi lại đơn đã hủy (đã xảy ra thật).
///
/// KHÔNG có bảng riêng — CỐ Ý. Đây chỉ là GỢI Ý cho lần mở form đầu tiên; lựa chọn thật của người
/// dùng nằm ở <c>dbo.UserWorkflows.OptionsJson</c> sau khi họ bấm Lưu. Mất gợi ý không mất dữ liệu
/// nào, chỉ tốn thêm một lượt AI rẻ → để trong <see cref="ChatCache"/> (Redis khi có cấu hình, nên
/// nhiều instance dùng chung và sống qua restart) là đủ, khỏi thêm schema để bảo trì.
///
/// Chỉ hỏi lại khi danh sách trạng thái đổi: khoá cache có kèm vân tay của (mã + tên).
///
/// Hỏng thì trả null — nơi gọi tự rơi về cách đoán theo từ khoá ở client. Cấu hình trạng thái là
/// việc người dùng vẫn sửa tay được, nên AI hỏng KHÔNG được phép chặn họ mở form.
/// </summary>
public class StatusSemanticsService
{
    private readonly ChatCache _cache;
    private readonly ProviderRegistry _registry;
    private readonly AiModelRegistry _models;
    private readonly AiCallContext _ctx;
    private readonly ILogger<StatusSemanticsService> _log;

    /// Giữ lâu vì danh sách trạng thái gần như không đổi; đổi thì vân tay trong khoá tự khác.
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    public StatusSemanticsService(ChatCache cache, ProviderRegistry registry,
        AiModelRegistry models, AiCallContext ctx, ILogger<StatusSemanticsService> log)
    {
        _cache = cache; _registry = registry; _models = models; _ctx = ctx; _log = log;
    }

    public record StatusOption(int Value, string Label);
    public record StatusHint(List<int> Open, List<int> Closed);

    private const string SystemPrompt =
        "Bạn phân loại trạng thái trong phần mềm CRM du lịch Việt Nam. " +
        "Với mỗi trạng thái, quyết định nó là CÒN PHẢI LÀM (công việc/cơ hội vẫn đang chạy, nhân " +
        "viên còn phải động vào) hay ĐÃ KẾT THÚC (đã chốt xong, đã hoàn thành, đã hủy, đã thua, " +
        "không theo nữa). " +
        "CHỈ trả JSON thô, KHÔNG markdown, KHÔNG giải thích. Ký tự đầu tiên BẮT BUỘC là '{'.";

    /// Gợi ý cho 1 công ty. Đã hỏi rồi và danh sách chưa đổi → trả ngay, không gọi AI.
    /// <param name="forceRefresh">Người dùng bấm "Phân loại lại" → bỏ qua bản đã lưu, hỏi AI lại.</param>
    /// Trả kèm nguồn ("cache" / "ai") và LÝ DO khi không có gợi ý — giao diện phải nói được vì sao,
    /// không thì người dùng bấm "Phân loại lại" mãi mà không hiểu tại sao chẳng có gì đổi.
    public async Task<(StatusHint? Hint, string Source, string? Reason)> GetAsync(string tenantId, string kind,
        IReadOnlyList<StatusOption> options, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || options.Count == 0) return (null, "none", "no-data");

        var key = $"sth|{tenantId}|{kind}|{HashOf(options)}";
        if (!forceRefresh && _cache.TryGet<StatusHint>(key, out var cached) && cached is { Open.Count: > 0 })
            return (cached, "cache", null);

        // Lần hỏi TỰ ĐỘNG (cache trống) không tính quota: đây là bước thiết lập, chặn nó lại chỉ
        // đổi lấy một bộ lọc trạng thái vô tác dụng. Còn "Phân loại lại" là thao tác chủ động của
        // người dùng nên tính bình thường — đó là chốt chặn chống bấm liên tục.
        using var _ = _ctx.Push(AiFeatures.StatusSemantics, tenantId, freeOfQuota: !forceRefresh);
        var (hint, reason) = await AskAsync(kind, options, ct);
        if (hint == null) return (null, "none", reason);
        _cache.Set(key, hint, Ttl);
        return (hint, "ai", null);
    }

    // ─── Hỏi AI ──────────────────────────────────────────────────────────────────
    private async Task<(StatusHint? Hint, string? Reason)> AskAsync(string kind,
        IReadOnlyList<StatusOption> options, CancellationToken ct)
    {
        var what = kind == "task" ? "công việc" : "cơ hội bán hàng";
        var list = string.Join("\n", options.Select(o => $"- {o.Value}: {o.Label}"));
        var prompt = $@"Đây là danh sách trạng thái {what} của một công ty du lịch:

{list}

Trả về JSON đúng dạng:
{{""open"": [mã còn phải làm], ""closed"": [mã đã kết thúc]}}

Quy tắc:
- MỌI mã trong danh sách phải xuất hiện ở đúng MỘT trong hai nhóm.
- Không chắc thì xếp vào ""open"" — bỏ sót một việc đã xong đỡ tệ hơn nuốt mất việc chưa làm.";

        try
        {
            var resolved = _models.Resolve(AiFeature.StatusSemantics);
            var provider = _registry.Resolve(resolved.Provider);
            var res = await provider.CompleteAsync(new CompleteRequest(
                Prompt: prompt, Provider: provider.Id, Model: resolved.Model,
                MaxTokens: 600, Temperature: 0.0, System: SystemPrompt, ApiKey: resolved.ApiKey), ct);

            var json = LooseJson.ExtractFirstObject(res.Text);
            if (string.IsNullOrWhiteSpace(json)) return (null, "ai-empty");
            using var doc = JsonDocument.Parse(json);
            var open = ReadIds(doc.RootElement, "open");
            var closed = ReadIds(doc.RootElement, "closed");
            if (open.Count == 0 && closed.Count == 0) return (null, "ai-empty");

            // Mã lạ (AI bịa) bị loại; mã bị bỏ quên coi như còn phải làm — an toàn theo hướng
            // "nhắc thừa" chứ không "nuốt mất việc".
            var known = options.Select(o => o.Value).ToHashSet();
            open = open.Where(known.Contains).ToList();
            closed = closed.Where(x => known.Contains(x) && !open.Contains(x)).ToList();
            foreach (var v in known)
                if (!open.Contains(v) && !closed.Contains(v)) open.Add(v);

            _log.LogInformation("Gợi ý trạng thái {Kind}: {Open} còn làm / {Closed} đã xong (model {Model})",
                kind, open.Count, closed.Count, res.Model);
            return (new StatusHint(open, closed), null);
        }
        // Hết lượt AI là lý do PHỔ BIẾN NHẤT khiến gợi ý vắng mặt, và nó khác hẳn "AI lỗi": người
        // dùng nạp thêm lượt là xong. Gộp chung vào một câu "đoán theo từ khoá" thì họ bấm Phân
        // loại lại mãi mà không hiểu vì sao chẳng có gì đổi (gặp thật ở vnexpresstour: 1005/1000).
        catch (Quota.QuotaExhaustedException)
        {
            _log.LogWarning("Gợi ý trạng thái {Kind}: hết lượt AI của công ty → client dùng lưới đỡ từ khoá", kind);
            return (null, "quota");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hỏi AI gợi ý trạng thái {Kind} lỗi → để client tự đoán theo tên", kind);
            return (null, "ai-error");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────
    private static List<int> ReadIds(JsonElement root, string name)
    {
        var outp = new List<int>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) outp.Add(n);
        return outp;
    }

    /// Vân tay của danh sách: đổi tên hay thêm trạng thái là khoá đổi → tự hỏi lại.
    private static string HashOf(IReadOnlyList<StatusOption> options)
    {
        var raw = string.Join("|", options.OrderBy(o => o.Value).Select(o => o.Value + ":" + o.Label));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }
}
