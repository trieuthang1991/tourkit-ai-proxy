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
}
