namespace TourkitAiProxy.Services.Providers;

/// 17 feature dùng AI trong proxy. Mỗi enum value 1-1 với key `Models:{Name}` trong appsettings.
/// THÊM member ở đây thì phải khai luôn khoá `Models:{Name}` trong appsettings của CẢ web LẪN worker —
/// thiếu thì feature mới âm thầm chạy bằng `Models:Primary`, không log, không cảnh báo.
/// "Primary" KHÔNG có ở đây — nó chỉ là root fallback internal Registry.
public enum AiFeature
{
    /// Bản tin điều hành (ceo-brief). Chỉ viết lời từ số đã tính sẵn → model rẻ là đủ.
    Digest,
    ChatAnalytics,
    Wizard,
    TourBuilder,
    VisaScoring,
    VisaExtraction,
    DealScoring,
    MailDraft,
    MailCompose,
    MailClassify,
    CustomerReview,
    Widget,
    NccImport,
    /// Đọc TÊN trạng thái do từng công ty tự đặt → suy ra cái nào còn phải làm, cái nào đã xong.
    /// Chạy 1 lần cho mỗi công ty rồi lưu lại, chỉ chạy lại khi danh sách trạng thái đổi → model
    /// rẻ là đủ và chi phí gần như bằng 0.
    StatusSemantics,
    /// <summary>
    /// Trợ lý trả lời khách trong Hộp thư chat.
    ///
    /// <para>⚠️ Đây là tính năng DUY NHẤT nói thẳng với khách hàng thật — chữ nó viết ra đi
    /// tới điện thoại của khách, không ai duyệt lại. Nên nó cần chốt model nhất, mà lại là
    /// cái vào enum này muộn nhất.</para>
    ///
    /// <para>Trước 28/08/2026 nó không có mặt ở đây: mã gọi thẳng <c>Resolve(null)</c> với
    /// <c>Model: null</c>, nên rơi xuống model MẶC ĐỊNH của nhà cung cấp — đo trên lịch sử
    /// dùng thật là <c>claude-sonnet-4-5</c>, model đắt nhất cả hệ, trong khi
    /// <c>Models:Primary</c> khai <c>claude-haiku-4-5</c>. Khai <c>Models:ChatInbox</c> lúc đó
    /// cũng vô ích vì không chỗ nào đọc.</para>
    /// </summary>
    ChatInbox,

    /// <summary>
    /// Bước CHỌN API của trợ lý tra tour — đọc câu khách hỏi rồi quyết gọi đường nào, rút từ khoá
    /// và khoảng ngày ra.
    ///
    /// <para>Khoá RIÊNG, tách khỏi <see cref="ChatInbox"/> có chủ đích. Đây là việc máy móc, ra vài
    /// chục token JSON; còn ChatInbox là chữ gửi thẳng tới điện thoại khách. Dùng chung khoá nghĩa
    /// là hôm nào nâng model cho khách đọc thì bước chọn API tự nâng theo và tốn tiền hơn cho một
    /// việc không cần tới.</para>
    /// </summary>
    ChatInboxTourPlan,

    // ── MỘT ca dùng /completions có khoá riêng, tách theo X-Ai-Feature ───────
    //
    // Cố ý chỉ một. Ba ca kia dùng chung khoá của việc cùng bản chất, vì mỗi khoá mới là một thứ
    // phải nhớ chỉnh, nhớ đồng bộ giữa web và worker, nhớ kiểm lúc deploy:
    //   quote, quote-marketing → Models:Wizard   (hai bước của chính Wizard = cấu hình tính giá tour)
    //   zalo-compose           → Models:ChatInbox (viết chữ gửi thẳng tới khách, cùng bản chất với
    //                                              lượt trợ lý tự trả lời khách trong hộp thư)
    //
    // Cả ba vẫn có NHÃN CHI PHÍ riêng — nhãn để NHÌN tiền, khoá để NẮN model. Hai việc khác nhau,
    // không phải lúc nào cũng cần tách cả hai.

    /// <summary>
    /// Nút Gợi ý AI trong các hộp thoại nhập liệu.
    ///
    /// <para>Khoá riêng vì nó là ca duy nhất trong nhóm này <b>không ai ngoài công ty đọc</b>:
    /// nhân viên bấm, xem vài gợi ý rồi tự chọn. Sai cũng không đi ra ngoài, nên đây là chỗ hạ
    /// model thoải mái nhất mà không đánh đổi gì.</para>
    /// </summary>
    AiSuggest
}

public record ResolvedModel(string Provider, string Model, string? ApiKey);

/// <summary>
/// Single source of truth cho cấu hình AI model per-feature.
///
/// Resolution chain:
///   Provider: override → Models:{F}:Provider → Models:Primary:Provider → throw
///   Model:    override → Models:{F}:Model    → Models:Primary:Model    → provider.DefaultModel
///   ApiKey:   Models:{F}:ApiKey → Models:Primary:ApiKey (nếu provider == Primary's) →
///             Providers:{Provider}:ApiKey → env var → null (provider tự throw khi gọi upstream)
/// </summary>
public class AiModelRegistry
{
    private readonly IConfiguration _cfg;
    private readonly ProviderRegistry _providers;

    private static readonly Dictionary<string, (string Section, string Env)> ProviderKeyMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"]   = ("Anthropic",  "ANTHROPIC_API_KEY"),
            ["deepseek"]    = ("DeepSeek",   "DEEPSEEK_API_KEY"),
            ["openai"]      = ("OpenAI",     "OPENAI_API_KEY"),
            ["opencode-go"] = ("OpenCode",   "OPENCODE_API_KEY"),
            ["nine-routes"] = ("NineRoutes", "NINE_ROUTES_API_KEY"),
            ["grok"]        = ("Grok",       "GROK_API_KEY"),
        };

    // Key AI riêng của công ty (BYO). Có thể null — test dựng registry không cần tới, và khi null thì
    // hành vi y hệt trước khi có tính năng này.
    private readonly AiCallContext? _ctx;
    private readonly TourkitAiProxy.Services.AiKeys.TenantAiKeyStore? _byo;
    private readonly ILogger<AiModelRegistry>? _log;

    public AiModelRegistry(IConfiguration cfg, ProviderRegistry providers,
        AiCallContext? ctx = null, TourkitAiProxy.Services.AiKeys.TenantAiKeyStore? byo = null,
        ILogger<AiModelRegistry>? log = null)
    {
        _cfg = cfg;
        _providers = providers;
        _ctx = ctx;
        _byo = byo;
        _log = log;

        // Fail-fast nếu Primary thiếu — đây là invariant.
        if (string.IsNullOrWhiteSpace(_cfg["Models:Primary:Provider"]))
            throw new InvalidOperationException(
                "Cấu hình thiếu Models:Primary:Provider — đây là root fallback bắt buộc của AiModelRegistry.");
    }

    /// <summary>
    /// Chọn nhà cung cấp + model + key cho một tính năng.
    ///
    /// <para><b>Key AI riêng của công ty (BYO) thắng mọi thứ</b>, kể cả tham số ghi đè của từng
    /// lệnh: công ty đã khai key Claude thì mọi lệnh AI của họ chạy bằng Claude. Để tham số ghi đè
    /// (vd ô chọn model trên giao diện trỏ 9routes) thắng thì sẽ đem key Claude đi gọi 9routes — hỏng.
    /// Chủ dự án chốt 03/10/2026: "tất cả các chỗ call AI sẽ đọc cái này".</para>
    ///
    /// <para>Không có cấu hình BYO (hoặc cờ tắt) thì đi đúng đường cũ, không đổi một dòng nào —
    /// xem <see cref="ResolveSystem"/>.</para>
    /// </summary>
    public ResolvedModel Resolve(AiFeature feature, string? overrideProvider = null, string? overrideModel = null)
    {
        var byo = TenantDecision();
        if (byo.UseTenantKey)
        {
            var inst = _providers.All.FirstOrDefault(p =>
                string.Equals(p.Id, byo.Provider, StringComparison.OrdinalIgnoreCase));
            if (inst is not null)
            {
                // Model trống → model mặc định CỦA CHÍNH nhà cung cấp khách chọn, KHÔNG mượn
                // Models:{F}:Model (đó là model của nhà cung cấp hệ thống) — xem TenantAiKeyRules.
                var model = byo.Model
                    ?? inst.Models.FirstOrDefault(m => m.Recommended)?.Id
                    ?? inst.Models.First().Id;
                return new ResolvedModel(inst.Id, model, byo.ApiKey);
            }
            _log?.LogWarning("[byo-key] nhà cung cấp '{P}' của công ty chưa đăng ký — dùng key hệ thống",
                byo.Provider);
        }
        return ResolveSystem(feature, overrideProvider, overrideModel);
    }

    /// <summary>
    /// Có dùng key riêng cho lệnh này không. <b>Cờ tắt thì trả ngay, không làm gì thêm</b> — kể cả
    /// không đọc ngữ cảnh lệnh gọi. Không bao giờ ném.
    /// </summary>
    private Domain.AiKeys.TenantKeyDecision TenantDecision()
    {
        if (_byo is null || _ctx is null || !_byo.FeatureOn) return Domain.AiKeys.TenantKeyDecision.SystemKey;
        try { return _byo.Decide(_ctx.Resolve().Tenant); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "[byo-key] đọc ngữ cảnh lệnh gọi hỏng — dùng key hệ thống");
            return Domain.AiKeys.TenantKeyDecision.SystemKey;
        }
    }

    /// <summary>
    /// Đường chọn cũ, KHÔNG xét key riêng của công ty. Dùng cho <see cref="Snapshot"/> (trang debug
    /// của quản trị — tuyệt đối không được lộ key của khách) và cho việc lùi về key hệ thống.
    /// </summary>
    public ResolvedModel ResolveSystem(AiFeature feature, string? overrideProvider = null, string? overrideModel = null)
    {
        var section = feature.ToString();

        var provider = NotEmpty(overrideProvider)
            ?? NotEmpty(_cfg[$"Models:{section}:Provider"])
            ?? NotEmpty(_cfg["Models:Primary:Provider"])
            ?? throw new InvalidOperationException(
                $"Không resolve được provider cho feature {feature} — Models:Primary:Provider rỗng?");

        // Validate provider đã đăng ký trong DI (case-insensitive)
        var providerInstance = _providers.All.FirstOrDefault(p =>
            string.Equals(p.Id, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Provider '{provider}' (feature {feature}) chưa đăng ký trong DI — kiểm tra Program.cs.");

        var model = NotEmpty(overrideModel)
            ?? NotEmpty(_cfg[$"Models:{section}:Model"])
            ?? NotEmpty(_cfg["Models:Primary:Model"])
            ?? providerInstance.Models.FirstOrDefault(m => m.Recommended)?.Id
            ?? providerInstance.Models.First().Id;

        // ApiKey resolution
        var apiKey = NotEmpty(_cfg[$"Models:{section}:ApiKey"]);
        if (apiKey == null)
        {
            var primaryProv = _cfg["Models:Primary:Provider"];
            if (string.Equals(primaryProv, provider, StringComparison.OrdinalIgnoreCase))
                apiKey = NotEmpty(_cfg["Models:Primary:ApiKey"]);
        }
        apiKey ??= KeyFor(provider);

        return new ResolvedModel(provider, model, apiKey);
    }

    /// Key fallback theo TÊN PROVIDER (không qua feature). Dùng cho code không có feature context.
    /// Thứ tự: Providers:{X}:ApiKey → env var.
    public string? KeyFor(string providerId)
    {
        if (!ProviderKeyMap.TryGetValue(providerId, out var m)) return null;
        return NotEmpty(_cfg[$"Providers:{m.Section}:ApiKey"])
            ?? NotEmpty(Environment.GetEnvironmentVariable(m.Env));
    }

    /// <summary>
    /// Model + key HỆ THỐNG của <c>Models:Primary</c> — chỗ lùi về khi key riêng của công ty hỏng.
    ///
    /// <para>Lùi về Primary chứ không về đúng model của từng tính năng: chỗ lùi nằm quanh lệnh gọi
    /// provider, ở đó không còn biết lệnh này thuộc tính năng nào. Đo 03/10/2026: cả 18 khoá
    /// <c>Models:*</c> đều trỏ cùng một model, nên hôm nay không khác gì. Khi các tính năng tách
    /// model riêng thì lượt bị lùi sẽ chạy bằng model Primary — vẫn chạy, chỉ không đúng model chọn
    /// riêng cho tính năng đó.</para>
    /// </summary>
    public ResolvedModel ResolvePrimarySystem()
    {
        var provider = _cfg["Models:Primary:Provider"]!;
        var inst = _providers.All.FirstOrDefault(p =>
            string.Equals(p.Id, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider Primary '{provider}' chưa đăng ký.");
        var model = NotEmpty(_cfg["Models:Primary:Model"])
            ?? inst.Models.FirstOrDefault(m => m.Recommended)?.Id
            ?? inst.Models.First().Id;
        var key = NotEmpty(_cfg["Models:Primary:ApiKey"]) ?? KeyFor(provider);
        return new ResolvedModel(inst.Id, model, key);
    }

    /// <summary>
    /// Dump toàn bộ resolution — dùng cho admin endpoint debug.
    /// ⚠️ Đi <see cref="ResolveSystem"/>, KHÔNG đi <see cref="Resolve"/>: <c>ResolvedModel</c> mang key
    /// thô, mà trang quản trị mở trong phiên của một công ty thì <c>Resolve</c> sẽ trả key RIÊNG của
    /// công ty đó — tức in key của khách ra màn hình.
    /// </summary>
    public IReadOnlyDictionary<AiFeature, ResolvedModel> Snapshot()
        => Enum.GetValues<AiFeature>().ToDictionary(f => f, f => ResolveSystem(f));

    private static string? NotEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
