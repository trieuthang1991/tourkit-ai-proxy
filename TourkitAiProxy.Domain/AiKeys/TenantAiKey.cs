namespace TourkitAiProxy.Domain.AiKeys;

/// <summary>
/// Cấu hình key AI RIÊNG của một công ty (BYO — "bring your own key").
///
/// <para><b>Hai cách dùng AI song song</b> (chốt 03/10/2026): nạp tiền mua lượt và dùng key của
/// hệ thống như trước, HOẶC tự khai key của mình. Khai key riêng thì mọi lệnh AI của công ty chạy
/// bằng key đó và KHÔNG trừ lượt. Key riêng hết tiền thì lùi về key hệ thống và trừ lượt như
/// thường — xem kế hoạch P4, mục "Sửa đổi 03/10".</para>
///
/// <para><b>Nằm ở Domain</b> vì kiểu này và <see cref="MaskOf"/> đều thuần. Kế hoạch gốc (05/08)
/// đặt ở <c>Services/Providers</c> — đó là bố cục một project cũ, trước khi tách 6 tầng.</para>
/// </summary>
/// <param name="Provider">Mã nhà cung cấp: <c>openai</c> · <c>anthropic</c> · <c>deepseek</c> ·
///   <c>grok</c> · <c>opencode-go</c> · <c>nine-routes</c>.</param>
/// <param name="Model">Model mặc định công ty chọn. <c>null</c> = mặc định của nhà cung cấp.</param>
/// <param name="ApiKeyEnc">Key đã mã hoá Crypton. Key thô KHÔNG BAO GIỜ rời máy chủ, không vào log.</param>
/// <param name="Masked">Bản đã che (<c>sk-…abcd</c>) — thứ DUY NHẤT được trả ra giao diện.</param>
/// <param name="Enabled">Tắt = quay về key hệ thống + trừ lượt, nhưng giữ cấu hình để bật lại.</param>
public record TenantAiKey(
    string TenantId,
    string Provider,
    string? Model,
    string ApiKeyEnc,
    string Masked,
    bool Enabled,
    DateTime? ValidatedAtUtc,
    string? UpdatedBy,
    DateTime UpdatedAtUtc)
{
    private const string Hidden = "••••";

    /// <summary>
    /// Che key: chỉ lộ 3 ký tự đầu + 4 ký tự cuối.
    ///
    /// <para>Đủ để khách nhận ra key của mình (đầu cho biết loại, đuôi để đối chiếu) mà không đủ
    /// để chép lại dùng. Key từ 8 ký tự trở xuống thì che HẾT — lộ 3+4 trên một key 8 ký tự là
    /// lộ gần trọn.</para>
    /// </summary>
    public static string MaskOf(string? rawKey)
        => string.IsNullOrEmpty(rawKey) || rawKey.Length <= 8
            ? Hidden
            : rawKey[..3] + "…" + rawKey[^4..];
}
