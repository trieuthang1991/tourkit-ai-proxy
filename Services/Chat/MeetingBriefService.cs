using System.Globalization;
using System.Text;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Reviews;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// S4 — "Thẻ chuẩn bị gặp khách". Gom những gì đã biết về một khách (hồ sơ + lịch sử mua + nhật ký
/// chăm sóc + hạng đã chấm + thư gần nhất) rồi để AI viết vài ý nên nói khi gặp.
///
/// <para><b>Chạy THEO YÊU CẦU, không phải tác vụ nền.</b> Bản spec gợi ý "trước lịch hẹn X giờ",
/// nhưng làm nền thì tốn một lượt AI cho MỌI cuộc hẹn — kể cả những cuộc mà nhân viên chẳng cần
/// chuẩn bị gì. Bấm lúc cần vừa rẻ hơn vừa đúng thời điểm hơn: người ta chuẩn bị ngay trước khi đi
/// gặp, không phải từ đêm hôm trước.</para>
///
/// <para><b>Số liệu do máy chủ gom, AI chỉ viết lời</b> — cùng luật với bản tin. AI không được suy
/// ra con số nào ngoài những gì đưa vào.</para>
///
/// <para>AI lỗi/hết lượt → vẫn trả về bản tóm tắt thô (<see cref="RenderFallback"/>). Sắp bước vào
/// phòng họp mà hệ thống báo lỗi trắng thì thà đưa dữ liệu thô còn hơn.</para>
/// </summary>
public class MeetingBriefService
{
    private readonly ReviewRepository _reviews;
    private readonly MailRepository _mails;
    private readonly ProviderRegistry _providers;
    private readonly AiModelRegistry _models;
    private readonly ILogger<MeetingBriefService> _log;

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public MeetingBriefService(ReviewRepository reviews, MailRepository mails,
        ProviderRegistry providers, AiModelRegistry models, ILogger<MeetingBriefService> log)
    { _reviews = reviews; _mails = mails; _providers = providers; _models = models; _log = log; }

    /// <param name="Text">Lời gợi ý của AI — hiển thị trong khung chat (prose).</param>
    /// <param name="Facts">Dữ kiện thô đã gom — hiển thị ở panel phải. Tách khỏi <paramref name="Text"/>
    /// để hai bên không in trùng nhau: chat đọc lời khuyên, panel tra lại số.</param>
    public record MeetingBrief(string CustomerName, string Text, string Facts, bool UsedAi);

    /// <param name="providerOverride">Cho phép A/B test provider như các service khác.</param>
    public async Task<MeetingBrief> BuildAsync(Customer customer, string tenantId,
        string? providerOverride = null, string? modelOverride = null, CancellationToken ct = default)
    {
        var review = _reviews.Get(tenantId, customer.Id);
        var mails = FindRecentMails(tenantId, customer);
        var facts = BuildFacts(customer, review, mails);

        try
        {
            var resolved = _models.Resolve(AiFeature.CustomerReview, providerOverride, modelOverride);
            var provider = _providers.Resolve(resolved.Provider);
            var r = await provider.CompleteAsync(new CompleteRequest(
                Prompt: BuildPrompt(customer, facts),
                Provider: provider.Id,
                Model: resolved.Model,
                // 900 bị cắt giữa chừng khi chạy thật ("cần hỏi rõ tên tour và ngày đi mong mu…").
                // Thẻ này có 3 phần + tối đa 5 gạch đầu dòng nên cần rộng hơn — cắt cụt đúng ở phần
                // "cần tránh" là mất đúng thứ nhân viên cần nhất.
                MaxTokens: 1600,
                Temperature: 0.4,
                System: null,
                ApiKey: resolved.ApiKey), ct);

            if (string.IsNullOrWhiteSpace(r.Text))
            {
                _log.LogWarning("[meeting-brief] tenant={T} khách={C} AI trả rỗng → dùng bản thô",
                    tenantId, customer.Name);
                return new(customer.Name, RenderFallback(facts), facts, UsedAi: false);
            }
            return new(customer.Name, r.Text.Trim(), facts, UsedAi: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[meeting-brief] tenant={T} khách={C} AI lỗi → dùng bản thô",
                tenantId, customer.Name);
            return new(customer.Name, RenderFallback(facts), facts, UsedAi: false);
        }
    }

    /// <summary>
    /// Thư gần nhất CỦA CHÍNH khách này. Khớp theo email — khách không có email thì bỏ qua hẳn
    /// thay vì dò theo tên: trùng tên là chuyện thường, mà đưa nhầm thư của người khác vào thẻ
    /// chuẩn bị gặp mặt thì nhân viên sẽ nói sai chuyện ngay trước mặt khách.
    /// </summary>
    private List<MailItem> FindRecentMails(string tenantId, Customer customer)
    {
        var email = customer.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email)) return new();
        try
        {
            return _mails.Filter(tenantId, status: null, category: null, search: email)
                .Where(m => (m.From?.Email ?? "").Equals(email, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.ReceivedAt, StringComparer.Ordinal)  // ReceivedAt là ISO-8601 → so chuỗi đúng thứ tự thời gian
                .Take(3)
                .ToList();
        }
        catch (Exception ex)
        {
            // Hộp thư chưa cấu hình / DB lỗi → thẻ vẫn dựng được từ hồ sơ, chỉ thiếu phần thư.
            _log.LogWarning(ex, "[meeting-brief] tenant={T} đọc thư của khách lỗi — bỏ qua phần thư", tenantId);
            return new();
        }
    }

    private static string Vnd(long v) => v.ToString("N0", Vi) + "đ";

    /// Dữ kiện thô đưa cho AI — cũng chính là bản dự phòng khi AI hỏng, nên phải tự đọc được.
    internal static string BuildFacts(Customer c, CustomerReview? review, IReadOnlyList<MailItem> mails)
    {
        var f = new StringBuilder();
        f.AppendLine($"- Khách: {c.Name}" + (string.IsNullOrWhiteSpace(c.Code) ? "" : $" ({c.Code})")
                     + $" · nhóm {c.Segment}");
        if (!string.IsNullOrWhiteSpace(c.Phone) || !string.IsNullOrWhiteSpace(c.Email))
            f.AppendLine($"- Liên hệ: {c.Phone ?? "—"} · {c.Email ?? "—"}");
        if (!string.IsNullOrWhiteSpace(c.Location)) f.AppendLine($"- Khu vực: {c.Location}");

        var m = c.Metrics;
        f.AppendLine($"- Đã đi {m.TotalTours} tour · tổng chi {Vnd(m.TotalSpent)} · trung bình {Vnd(m.Aov)}/tour"
                     + (string.IsNullOrWhiteSpace(m.LastPurchaseDate) ? "" : $" · lần cuối {Day(m.LastPurchaseDate)}"));

        if (m.ComplaintCount > 0 || m.CancelCount > 0)
            f.AppendLine($"- Từng phàn nàn {m.ComplaintCount} lần · huỷ {m.CancelCount} lần");
        if (m.LastCareDaysAgo is int careAgo)
            f.AppendLine($"- Lần chăm sóc gần nhất: {careAgo} ngày trước");

        // Hạng đã chấm là phần đáng giá nhất trước khi gặp mặt — nó đã tổng hợp sẵn khách này
        // đáng lo hay đáng đầu tư, nên đưa gần đủ vào thay vì chỉ một dòng tóm tắt.
        if (review != null)
        {
            f.AppendLine($"- Hạng đã chấm: {review.Rank}"
                         + (string.IsNullOrWhiteSpace(review.RankReason) ? "" : $" — {review.RankReason}"));
            if (!string.IsNullOrWhiteSpace(review.Alert?.Message) &&
                !string.Equals(review.Alert.Level, "none", StringComparison.OrdinalIgnoreCase))
                f.AppendLine($"- Cảnh báo (mức {review.Alert.Level}): {Trim(review.Alert.Message, 200)}");
            if (!string.IsNullOrWhiteSpace(review.Preferences))
                f.AppendLine($"- Sở thích/thói quen đã ghi nhận: {Trim(review.Preferences, 250)}");
            if (review.Concerns.Count > 0)
                f.AppendLine($"- Điểm cần lưu ý: {Trim(string.Join(" · ", review.Concerns), 250)}");
            if (!string.IsNullOrWhiteSpace(review.ActionNow?.Task))
                f.AppendLine($"- Việc nên làm ngay (theo bản chấm): {Trim(review.ActionNow.Task, 200)}");
            if (review.ProductSuggestions.Count > 0)
                f.AppendLine($"- Sản phẩm gợi ý: {Trim(string.Join(" · ", review.ProductSuggestions), 250)}");
        }

        if (!string.IsNullOrWhiteSpace(c.Note))
            f.AppendLine($"- Nhu cầu ban đầu ghi khi tạo khách: {Trim(c.Note, 300)}");

        if (c.Purchases.Count > 0)
        {
            f.AppendLine("- Tour đã mua gần đây:");
            foreach (var p in c.Purchases.Take(5))
                // Số 0 ở đây nghĩa là CRM chưa ghi, không phải "tour 0 đêm" — in ra thì AI sẽ bình
                // luận về nó (đã thấy thật: "tour có 0 đêm, 0 khách – cần kiểm tra thêm").
                f.AppendLine($"  · {Day(p.Date)} · {p.Destination}"
                             + (p.Nights > 0 ? $" · {p.Nights} đêm" : "")
                             + (p.Pax > 0 ? $" · {p.Pax} khách" : "")
                             + (p.Amount > 0 ? $" · {Vnd(p.Amount)}" : "")
                             + (string.IsNullOrWhiteSpace(p.Channel) ? "" : $" · qua {p.Channel}"));
        }

        if (c.CareLogs.Count > 0)
        {
            f.AppendLine("- Lần chăm sóc gần đây:");
            foreach (var l in c.CareLogs.Take(5))
                f.AppendLine($"  · {Day(l.Date)} · {l.Channel} · thái độ {l.Sentiment}: {Trim(l.Summary, 160)}"
                             + (string.IsNullOrWhiteSpace(l.Outcome) ? "" : $" → {Trim(l.Outcome, 80)}"));
        }

        if (mails.Count > 0)
        {
            f.AppendLine("- Thư gần nhất từ khách:");
            foreach (var mail in mails)
                // Nhóm thư (vd "Khiếu nại") và trạng thái ("Mới" = chưa ai trả lời) đều là thứ
                // không nên để khách nhắc trước.
                f.AppendLine($"  · {Day(mail.ReceivedAt)} · {Label(MailTaxonomy.Categories, mail.Category)}"
                             + $" · {Label(MailTaxonomy.Statuses, mail.Status)}: {Trim(mail.Subject, 120)}");
        }

        return f.ToString().TrimEnd();
    }

    private static string Label(IReadOnlyDictionary<string, string> map, string? key)
        => key != null && map.TryGetValue(key, out var v) ? v : "—";

    /// <summary>
    /// Chuẩn hoá mọi ngày về dd/MM/yyyy. Upstream trả lẫn lộn: có chỗ đã là "01/06/2026", có chỗ còn
    /// nguyên ISO ("2026-08-08T00:00:00"). Để lẫn thì AI đọc được nhưng viết lại y nguyên vào lời
    /// khuyên — đã thấy thật một câu chứa cả "2026-08-08" lẫn "04/08/2026".
    /// Đọc không ra thì in nguyên chuỗi, còn hơn nuốt mất dòng.
    /// </summary>
    private static string Day(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return "—";
        // Đã đúng dạng ngày Việt thì để yên — parse tiếp chỉ tạo cơ hội hiểu nhầm ngày/tháng.
        if (DateTime.TryParseExact(raw, "dd/MM/yyyy", Vi, DateTimeStyles.None, out _)) return raw;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
               ? d.ToString("dd/MM/yyyy", Vi)
               : raw;
    }

    private static string Trim(string? s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    internal static string BuildPrompt(Customer c, string facts) =>
        $"Bạn là trợ lý của nhân viên bán tour. Nhân viên sắp gặp khách **{c.Name}** và cần chuẩn bị trong 1 phút.\n" +
        "Từ CHÍNH XÁC các dữ kiện dưới đây, viết ngắn gọn tiếng Việt gồm 3 phần:\n" +
        "1. Khách này là ai (2-3 câu: đã đi gì, chi bao nhiêu, đang ở trạng thái nào).\n" +
        "2. 3-5 gạch đầu dòng NÊN NÓI GÌ — bám vào lịch sử thật, ưu tiên điều khách từng quan tâm.\n" +
        "3. 1-2 điều CẦN TRÁNH hoặc cần hỏi rõ (nếu dữ kiện cho thấy có rủi ro: lâu không mua lại, " +
        "từng phàn nàn, thư chưa được trả lời…). Không có gì đáng lưu ý thì ghi \"không có gì đặc biệt\".\n" +
        "TUYỆT ĐỐI không bịa thêm số, tên tour hay sự kiện nào ngoài dữ kiện. Không dùng tiêu đề markdown lớn.\n" +
        // Nhật ký chăm sóc là chữ nhân viên tự gõ, đủ kiểu viết tắt và có khi cụt ngủn.
        "Nhật ký chăm sóc là ghi chú tay của nhân viên, có thể viết tắt hoặc thiếu ngữ cảnh — " +
        "chỉ dùng để nhắc lại, đừng suy diễn ý định của khách từ đó.\n\n" +
        facts;

    /// AI hỏng thì vẫn phải có cái để đọc — sắp bước vào gặp khách rồi.
    internal static string RenderFallback(string facts)
        => "AI chưa viết được phần gợi ý, đây là dữ liệu thô về khách:\n\n" + facts;
}
