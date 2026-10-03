using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.AiKeys;

/// <summary>
/// Kiểm key AI riêng của công ty bằng MỘT lệnh gọi thật, TRƯỚC khi lưu.
///
/// <para>Key chưa từng gọi thành công thì không bao giờ được đem đi chạy việc thật của khách
/// (<c>TenantAiKeyRules.Decide</c> đòi <c>ValidatedAtUtc</c>). Lưu key sai mà không kiểm thì lệnh AI đầu
/// tiên của khách mới phát hiện — và lúc đó lớp bọc đã lùi sang key hệ thống, trừ lượt, khách không hiểu
/// vì sao.</para>
/// </summary>
public class TenantAiKeyValidator
{
    private readonly ProviderRegistry _providers;
    private readonly AiCallContext _ctx;
    private readonly ILogger<TenantAiKeyValidator> _log;

    public TenantAiKeyValidator(ProviderRegistry providers, AiCallContext ctx, ILogger<TenantAiKeyValidator> log)
    {
        _providers = providers; _ctx = ctx; _log = log;
    }

    /// <param name="Model">Model đã chốt — người dùng không chọn thì là model mặc định của nhà cung cấp.</param>
    public record Result(bool Ok, string? Error, string? Model);

    public async Task<Result> ValidateAsync(string tenantId, string provider, string? model, string apiKey,
        CancellationToken ct)
    {
        // ⚠️ KIỂM TRƯỚC khi miễn lượt bên dưới. Key rỗng thì nhà cung cấp tự lùi về key HỆ THỐNG
        // (ProviderKeyStore) — mà lệnh này đang miễn lượt, tức gọi bằng key hệ thống miễn phí.
        if (string.IsNullOrWhiteSpace(apiKey)) return new(false, "Chưa nhập key", null);

        IAiProvider p;
        try { p = _providers.Resolve(provider); }
        catch { return new(false, $"Nhà cung cấp '{provider}' không hợp lệ", null); }

        var m = string.IsNullOrWhiteSpace(model)
            ? p.Models.FirstOrDefault(x => x.Recommended)?.Id ?? p.Models.FirstOrDefault()?.Id
            : model.Trim();

        try
        {
            // Bypass: đi thẳng xuống nhà cung cấp, KHÔNG lùi key — không thì kiểm key hỏng mà ra xanh.
            // freeOfQuota: lệnh chạy bằng key KHÁCH VỪA NHẬP (đã chặn rỗng ở trên) nên khách tự trả tiền
            // nhà cung cấp — trừ lượt nữa là thu hai lần. Xem FreeOfQuotaGuardTests.
            using var _bypass = ByoAwareProvider.Bypass();
            using var _mien = _ctx.Push(AiFeatures.ByoKeyValidate, tenantId, sessionId: null, freeOfQuota: true);

            var r = await p.CompleteAsync(new CompleteRequest(
                Prompt: "Trả lời đúng một chữ: OK", Provider: p.Id, Model: m,
                MaxTokens: 16, Temperature: 0, System: null, ApiKey: apiKey.Trim()), ct);

            return new(true, null, r.Model is { Length: > 0 } ? r.Model : m);
        }
        catch (UpstreamException ex)
        {
            _log.LogInformation("[byo-key] kiểm key của {Tenant} ({P}) không qua: {S}", tenantId, provider, ex.Status);
            return new(false, ex.Status switch
            {
                401 or 403 => "Key không đúng hoặc đã bị thu hồi",
                402 => "Tài khoản AI hết tiền",
                404 => $"Không có model '{m}' ở nhà cung cấp này",
                429 => "Tài khoản AI hết hạn mức hoặc đang bị giới hạn — thử lại sau ít phút",
                400 when ex.Body.Contains("credit balance is too low", StringComparison.OrdinalIgnoreCase)
                    => "Tài khoản Claude hết tiền",
                // xAI báo key sai bằng 400, không phải 401 (bắt được thật 03/10/2026).
                400 when ex.Body.Contains("incorrect api key", StringComparison.OrdinalIgnoreCase)
                    => "Key không đúng hoặc đã bị thu hồi",
                _ => $"Nhà cung cấp từ chối ({ex.Status})",
            }, m);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "[byo-key] kiểm key của {Tenant} ({P}) hỏng", tenantId, provider);
            return new(false, "Không gọi được nhà cung cấp — kiểm lại kết nối rồi thử lại", m);
        }
    }
}
