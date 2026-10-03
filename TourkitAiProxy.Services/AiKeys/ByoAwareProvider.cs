using TourkitAiProxy.Domain.AiKeys;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.AiKeys;

/// <summary>
/// Lớp BỌC quanh mỗi nhà cung cấp AI, lo hai việc của key riêng công ty (BYO):
/// <list type="number">
/// <item><b>Không trừ lượt</b> khi lệnh thật sự chạy bằng key của khách.</item>
/// <item><b>Lùi về key hệ thống và TRỪ lượt</b> khi key khách hết tiền hoặc sai — chủ dự án chốt
///   03/10/2026: khách không bao giờ bị đứt dịch vụ vì key riêng, nhưng nền tảng không trả hộ.</item>
/// </list>
///
/// <para><b>Vì sao bọc chứ không sửa sáu nhà cung cấp.</b> Cả sáu đã tôn trọng cờ
/// <c>Ctx.FreeOfQuota</c> (<c>if (!c.FreeOfQuota …) _quota.Consume(…)</c>) và đều ném
/// <see cref="UpstreamException"/> mang mã HTTP. Một lớp bọc là đủ, sáu nhà cung cấp không sửa dòng
/// nào.</para>
///
/// <para><b>⚠️ Miễn lượt theo TỪNG LỆNH, không theo công ty.</b> Cách đơn giản hơn — "công ty có key
/// riêng thì miễn lượt cả công ty" — là SAI: hai đường gọi công cụ của Anthropic (trợ lý số liệu, tác
/// vụ tự động) không lấy key qua <c>AiModelRegistry</c> mà lấy thẳng key hệ thống. Miễn lượt theo công
/// ty thì hai đường đó gọi bằng key hệ thống mà không trừ lượt — tức nền tảng trả AI hộ khách, miễn
/// phí, không ai biết. Ở đây chỉ miễn khi <c>req.ApiKey</c> ĐÚNG là key riêng của công ty.</para>
///
/// <para><b>Không có key riêng (hoặc cờ tắt) thì đi thẳng xuống nhà cung cấp, không làm gì thêm.</b></para>
/// </summary>
public sealed class ByoAwareProvider : IAiProvider
{
    private readonly IAiProvider _inner;
    private readonly IServiceProvider _sp;

    // Lấy lười qua IServiceProvider: AiModelRegistry và ProviderRegistry đều phụ thuộc chính các nhà
    // cung cấp đã bọc, lấy thẳng qua constructor là vòng phụ thuộc. Chỉ cần tới lúc LÙI KEY (hiếm).
    private TenantAiKeyStore Store => _sp.GetRequiredService<TenantAiKeyStore>();
    private AiCallContext Ctx => _sp.GetRequiredService<AiCallContext>();

    /// <summary>
    /// Đang trong lượt lùi về key hệ thống. Chặn vòng lặp: lượt lùi đi qua lớp bọc của nhà cung cấp
    /// hệ thống, và nếu (vô lý) key hệ thống trùng key khách thì nó sẽ lại tưởng là lệnh của khách.
    /// </summary>
    private static readonly AsyncLocal<bool> _inFallback = new();

    /// <summary>
    /// Tắt lớp bọc cho các lệnh trong khối <c>using</c>: đi thẳng xuống nhà cung cấp, KHÔNG lùi key.
    ///
    /// <para>Dùng khi KIỂM key (<see cref="TenantAiKeyValidator"/>). Không tắt thì kiểm lại đúng cái key
    /// đang lưu mà nay đã hỏng sẽ gặp 401 → lớp bọc lùi sang key hệ thống → trả về thành công → báo
    /// "key hợp lệ". Tức kiểm một key hỏng mà ra xanh.</para>
    /// </summary>
    public static IDisposable Bypass()
    {
        var truoc = _inFallback.Value;
        _inFallback.Value = true;
        return new Restore(truoc);
    }

    private sealed class Restore(bool truoc) : IDisposable
    {
        public void Dispose() => _inFallback.Value = truoc;
    }

    public ByoAwareProvider(IAiProvider inner, IServiceProvider sp)
    {
        _inner = inner; _sp = sp;
    }

    public string Id => _inner.Id;
    public string Label => _inner.Label;
    public IReadOnlyList<ProviderModel> Models => _inner.Models;
    public Task<IReadOnlyList<ProviderModel>> ListLiveModelsAsync(CancellationToken ct)
        => _inner.ListLiveModelsAsync(ct);

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        var tenant = TenantKeyCall(req);
        if (tenant is null) return await _inner.CompleteAsync(req, ct);

        try
        {
            CompleteResult r;
            using (FreeOfQuota()) r = await _inner.CompleteAsync(req, ct);
            Store.ReportOk(tenant);
            return r;
        }
        catch (UpstreamException ex) when (TenantAiKeyRules.ShouldFallBack(ex.Status, ex.Body))
        {
            var (prov, sysReq, lyDo) = PrepareFallback(tenant, req, ex);
            _inFallback.Value = true;
            try
            {
                var r = await prov.CompleteAsync(sysReq, ct);
                return r with { Warning = JoinWarning(r.Warning, lyDo) };
            }
            finally { _inFallback.Value = false; }
        }
    }

    public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta,
        CancellationToken ct)
    {
        var tenant = TenantKeyCall(req);
        if (tenant is null) return await _inner.StreamAsync(req, onDelta, ct);

        // Chỉ lùi khi CHƯA đẩy chữ nào ra. Hết tiền/sai key thì nhà cung cấp từ chối ngay ở mã HTTP,
        // trước khi có chữ nào — nên điều kiện này không bỏ sót ca cần lùi. Đã đẩy chữ rồi mà còn lùi
        // thì người đọc nhận hai câu trả lời dính vào nhau.
        var daDayChu = false;
        async Task Delta(string s) { daDayChu = true; await onDelta(s); }

        try
        {
            CompleteResult r;
            using (FreeOfQuota()) r = await _inner.StreamAsync(req, Delta, ct);
            Store.ReportOk(tenant);
            return r;
        }
        catch (UpstreamException ex) when (!daDayChu && TenantAiKeyRules.ShouldFallBack(ex.Status, ex.Body))
        {
            var (prov, sysReq, lyDo) = PrepareFallback(tenant, req, ex);
            _inFallback.Value = true;
            try
            {
                var r = await prov.StreamAsync(sysReq, onDelta, ct);
                return r with { Warning = JoinWarning(r.Warning, lyDo) };
            }
            finally { _inFallback.Value = false; }
        }
    }

    /// <summary>
    /// Mã công ty nếu lệnh này THẬT SỰ chạy bằng key riêng của công ty đó, ngược lại <c>null</c>.
    /// Cờ tắt thì trả null ngay, không đọc ngữ cảnh lệnh gọi. Không bao giờ ném.
    /// </summary>
    private string? TenantKeyCall(CompleteRequest req)
    {
        if (_inFallback.Value || string.IsNullOrEmpty(req.ApiKey)) return null;
        try
        {
            var store = Store;
            if (!store.FeatureOn) return null;
            var tenant = Ctx.Resolve().Tenant;
            // So KHỚP key, không chỉ "công ty có BYO" — xem TenantAiKeyStore.IsTenantKey.
            return store.IsTenantKey(tenant, req.ApiKey) ? tenant : null;
        }
        catch { return null; }
    }

    /// Miễn lượt cho đúng một lệnh, giữ nguyên tính năng/công ty/phiên đang có.
    private IDisposable FreeOfQuota()
    {
        var c = Ctx.Resolve();
        return Ctx.Push(c.Feature, c.Tenant, c.SessionId, freeOfQuota: true);
    }

    /// <summary>
    /// Ghi lại lỗi (trang cấu hình sẽ nói ra) rồi dựng lệnh gọi bằng key HỆ THỐNG. Lượt lùi chạy ngoài
    /// khối miễn lượt nên TRỪ LƯỢT như thường — đúng chốt của chủ dự án.
    /// </summary>
    private (IAiProvider Prov, CompleteRequest Req, string LyDo) PrepareFallback(string tenant,
        CompleteRequest original, UpstreamException ex)
    {
        var lyDo = TenantAiKeyRules.FallbackReason(ex.Status, ex.Body);
        Store.ReportFail(tenant, lyDo);
        _sp.GetRequiredService<ILogger<ByoAwareProvider>>().LogWarning(
            "[byo-key] key riêng của {Tenant} hỏng: {LyDo} — lùi về key hệ thống, CÓ trừ lượt", tenant, lyDo);

        var sys = _sp.GetRequiredService<AiModelRegistry>().ResolvePrimarySystem();
        var prov = _sp.GetRequiredService<ProviderRegistry>().Resolve(sys.Provider);
        return (prov, original with { Provider = sys.Provider, Model = sys.Model, ApiKey = sys.ApiKey }, lyDo);
    }

    private static string JoinWarning(string? cu, string lyDo)
    {
        var moi = $"Key AI riêng của công ty lỗi ({lyDo}) — lượt này dùng key hệ thống và đã trừ lượt.";
        return string.IsNullOrWhiteSpace(cu) ? moi : cu + " · " + moi;
    }
}
