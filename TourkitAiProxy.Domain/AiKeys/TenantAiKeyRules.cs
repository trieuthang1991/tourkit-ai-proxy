namespace TourkitAiProxy.Domain.AiKeys;

/// <summary>
/// Kết quả chọn key cho một lệnh AI.
/// </summary>
/// <param name="UseTenantKey">true = gọi bằng key của công ty và KHÔNG trừ lượt.</param>
/// <param name="Model"><c>null</c> = dùng model mặc định CỦA CHÍNH <paramref name="Provider"/>,
///   tuyệt đối không mượn model mặc định của hệ thống — xem <see cref="TenantAiKeyRules.Decide"/>.</param>
public record TenantKeyDecision(bool UseTenantKey, string? Provider, string? Model, string? ApiKey)
{
    /// Không dùng key riêng: chỗ gọi đi tiếp đúng đường cũ, không đổi gì.
    public static readonly TenantKeyDecision SystemKey = new(false, null, null, null);
}

/// <summary>
/// Luật chọn key riêng của công ty hay key hệ thống. <b>Thuần</b> — không đọc cấu hình, không
/// chạm CSDL — để test được mọi nhánh mà không dựng gì.
/// </summary>
public static class TenantAiKeyRules
{
    // Giới hạn nhà cung cấp (ChatGPT · Claude · Grok) CHỈ nằm ở ô chọn trên giao diện — chủ dự án
    // chốt 03/10/2026, không chặn ở máy chủ. Công ty nào tự gọi API khai nhà cung cấp khác thì chỉ
    // ảnh hưởng chính họ.

    /// <summary>
    /// Chọn key cho một lệnh AI.
    ///
    /// <para><b>Mặc định là KHÔNG đổi gì.</b> Hàm này nằm trên đường đi của mọi lệnh AI, nên
    /// mọi trường hợp thiếu một điều kiện nào đều trả <see cref="TenantKeyDecision.SystemKey"/> —
    /// chỗ gọi đi tiếp y như trước khi có tính năng này. Chỉ khi ĐỦ cả năm điều kiện mới dùng key
    /// riêng: cờ bật · có công ty · có cấu hình · cấu hình đang bật · đã từng kiểm thành công ·
    /// giải mã được key.</para>
    ///
    /// <para><b>Model để trống thì giữ <c>null</c>, không lấp bằng model mặc định của hệ thống.</b>
    /// Kế hoạch gốc (05/08) viết <c>config.Model ?? defaultModel</c> — nhưng defaultModel thuộc nhà
    /// cung cấp của HỆ THỐNG. Khách chọn Anthropic mà không ghi model thì hệ thống sẽ đem tên model
    /// DeepSeek đi gọi Anthropic và hỏng ngay lượt đầu.</para>
    /// </summary>
    /// <param name="featureOn">Cờ <c>Features:ByoAiKey</c>.</param>
    /// <param name="rawApiKey">Key đã giải mã. Crypton trả rỗng khi giải mã hỏng — coi như không có.</param>
    public static TenantKeyDecision Decide(bool featureOn, string? tenantId, TenantAiKey? key,
        string? rawApiKey)
    {
        if (!featureOn) return TenantKeyDecision.SystemKey;
        if (string.IsNullOrWhiteSpace(tenantId)) return TenantKeyDecision.SystemKey;
        if (key is null || !key.Enabled) return TenantKeyDecision.SystemKey;
        // Chưa từng gọi thử thành công thì đừng đem đi chạy việc thật của khách.
        if (key.ValidatedAtUtc is null) return TenantKeyDecision.SystemKey;
        if (string.IsNullOrEmpty(rawApiKey)) return TenantKeyDecision.SystemKey;

        var model = string.IsNullOrWhiteSpace(key.Model) ? null : key.Model.Trim();
        return new TenantKeyDecision(true, key.Provider, model, rawApiKey);
    }

    // ── Lùi về key hệ thống ──────────────────────────────────────────────────

    /// Câu Claude dùng khi tài khoản hết tiền — kèm mã 400, KHÔNG phải 402.
    private const string AnthropicLowCredit = "credit balance is too low";

    /// Loại lỗi OpenAI dùng khi tài khoản hết tiền — kèm mã 429.
    private const string OpenAiInsufficientQuota = "insufficient_quota";

    /// <summary>
    /// Câu báo KEY SAI. xAI (Grok) gửi kèm mã <b>400</b>, không phải 401 — bắt được thật ngày 03/10/2026:
    /// <c>{"code":"invalid-argument","error":"Incorrect API key provided. …"}</c>. OpenAI dùng cùng câu này
    /// nhưng kèm 401 (đã lùi sẵn theo mã).
    /// </summary>
    private const string IncorrectApiKey = "incorrect api key";

    /// <summary>
    /// Key riêng hỏng tới mức phải lùi về key hệ thống (và TRỪ LƯỢT) không.
    ///
    /// <para><b>Lùi</b> khi key khách HẾT TIỀN hoặc SAI: 401 · 402 · 429 kèm
    /// <c>insufficient_quota</c> · 400 kèm câu "credit balance is too low" (Claude hết tiền) hoặc
    /// "Incorrect API key" (Grok sai key). Hai câu 400 là thứ danh sách gốc (401/402/429) bỏ sót.</para>
    ///
    /// <para><b>Không lùi</b> khi chỉ là gọi quá dày (429 thường — vài giây sau là được, lùi là đốt
    /// lượt của khách vô ích) hay nhà cung cấp đang lỗi (5xx — không liên quan key). Mọi lỗi 400
    /// khác cũng không: nội dung sai thì lùi cũng hỏng y vậy.</para>
    /// </summary>
    public static bool ShouldFallBack(int status, string? body)
    {
        var b = body ?? "";
        return status switch
        {
            401 or 402 => true,
            429 => b.Contains(OpenAiInsufficientQuota, StringComparison.OrdinalIgnoreCase),
            400 => b.Contains(AnthropicLowCredit, StringComparison.OrdinalIgnoreCase)
                || b.Contains(IncorrectApiKey, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// Câu ngắn ghi vào <c>LastFailReason</c> và hiện trên trang cấu hình. Tiếng Việt, có mã để còn
    /// tra; không đưa mã thô của nhà cung cấp (<c>insufficient_quota</c>…) lên màn hình.
    /// </summary>
    public static string FallbackReason(int status, string? body)
    {
        var b = body ?? "";
        return status switch
        {
            401 => "Key sai hoặc đã bị thu hồi (401)",
            402 => "Tài khoản AI hết tiền (402)",
            429 when b.Contains(OpenAiInsufficientQuota, StringComparison.OrdinalIgnoreCase)
                => "Tài khoản AI hết tiền hoặc hết hạn mức (429)",
            400 when b.Contains(AnthropicLowCredit, StringComparison.OrdinalIgnoreCase)
                => "Tài khoản Claude hết tiền (400)",
            400 when b.Contains(IncorrectApiKey, StringComparison.OrdinalIgnoreCase)
                => "Key sai hoặc đã bị thu hồi (400)",
            _ => $"Key riêng lỗi ({status})",
        };
    }
}
