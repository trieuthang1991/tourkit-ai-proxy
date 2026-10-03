using System.Collections.Concurrent;
using TourkitAiProxy.Domain.AiKeys;
using TourkitAiProxy.Infrastructure.AiKeys;
using TourkitAiProxy.Services.Bootstrap;

namespace TourkitAiProxy.Services.AiKeys;

/// <summary>
/// Bộ nhớ đệm key AI riêng của các công ty — thứ <c>AiModelRegistry.Resolve</c> hỏi ở MỌI lệnh AI.
///
/// <para><b>Cam kết với chủ dự án (03/10/2026): không ảnh hưởng hệ thống đang chạy.</b> Ba lớp:</para>
/// <list type="number">
/// <item><b>Cờ tắt</b> (<see cref="FeatureFlags.ByoAiKey"/>) → <see cref="Decide"/> trả "dùng key hệ
///   thống" ngay dòng đầu, không tra gì. Hành vi y hệt trước khi có tính năng này.</item>
/// <item><b>Không chạm CSDL trên đường gọi AI.</b> <c>Resolve</c> là hàm ĐỒNG BỘ nằm trên đường đi
///   của 32 chỗ gọi; đọc CSDL trong đó thì CSDL chậm là mọi lệnh AI chậm, CSDL sập là mọi lệnh AI
///   sập. Nên <see cref="Decide"/> chỉ tra một từ điển trong RAM; việc nạp từ CSDL chạy nền
///   (<see cref="TenantAiKeyRefresher"/>) mỗi phút.</item>
/// <item><b>Mọi lỗi → nuốt, ghi log, dùng key hệ thống.</b> Không đường nào ném ra cho chỗ gọi.</item>
/// </list>
///
/// <para><b>Nhiều máy chủ cùng chạy</b> (chat có Redis pub/sub nên đây là chuyện thật): công ty lưu
/// key ở máy A thì máy A thấy ngay (<see cref="RefreshAsync"/> gọi sau khi lưu), các máy khác thấy
/// trong vòng một phút. Chấp nhận được — key vừa lưu đã được kiểm, không phải chờ mới đúng.</para>
/// </summary>
public class TenantAiKeyStore
{
    private static readonly IReadOnlyDictionary<string, TenantAiKeyRepository.Loaded> Empty =
        new Dictionary<string, TenantAiKeyRepository.Loaded>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bản đệm DÙNG CHUNG mọi máy chủ trong Redis (chủ dự án yêu cầu 03/10/2026: "tránh chọc database
    /// liên tục, khi người dùng sửa thì xoá").
    ///
    /// <para>Ba tầng: RAM (đường gọi AI chỉ tra ở đây) → Redis (máy chủ đọc mỗi phút) → CSDL (chỉ khi
    /// Redis trống). Nên cả cụm máy chủ đọc CSDL khoảng <see cref="RedisTtl"/> một lần, thay vì mỗi máy
    /// mỗi phút.</para>
    ///
    /// <para>⚠️ Redis chỉ giữ key ĐÃ MÃ HOÁ (<c>ApiKeyEnc</c>), giải mã trong RAM từng máy. Key thô của
    /// khách không bao giờ nằm trong Redis — Redis dùng chung với TourKit.</para>
    ///
    /// <para><b>Không đặt Redis lên đường gọi AI.</b> <c>AiModelRegistry.Resolve</c> là hàm ĐỒNG BỘ trên
    /// đường đi của mọi lệnh AI; chèn một lượt gọi mạng vào đó là làm chậm mọi lệnh, và Redis chập là mọi
    /// lệnh chập theo.</para>
    /// </summary>
    internal const string RedisKey = "byo:keys:all";

    /// Kênh pub/sub báo "có công ty vừa sửa key" — mọi máy nạp lại ngay, không chờ hết nhịp.
    internal const string InvalidateChannel = "tkai:byo:invalidate";

    private static readonly TimeSpan RedisTtl = TimeSpan.FromMinutes(10);

    private readonly TenantAiKeyRepository _repo;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TenantAiKeyStore> _log;
    private readonly TourkitAiProxy.Infrastructure.Cache.RedisStore? _redis;
    private readonly TourkitAiProxy.Infrastructure.Cache.RedisProvider? _redisPubSub;
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// Bản chụp hiện hành. Thay NGUYÊN KHỐI khi nạp xong, không sửa tại chỗ — nhiều luồng cùng đọc.
    private volatile IReadOnlyDictionary<string, TenantAiKeyRepository.Loaded> _snapshot = Empty;

    /// <summary>
    /// Trạng thái lỗi biết được TẠI MÁY NÀY từ lần nạp trước tới giờ — đè lên bản chụp cho tới lần
    /// nạp sau. true = đang lỗi, false = vừa chạy lại được.
    ///
    /// <para>Có nó để <see cref="ReportOk"/> chỉ ghi CSDL khi trạng thái THẬT SỰ đổi: không có nó thì
    /// hoặc ghi ở mọi lượt thành công (thêm một lần chạm CSDL vào mọi lệnh AI), hoặc không bao giờ
    /// xoá được cờ lỗi cho tới lần nạp sau.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _localFailing = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="redis">Có thể null (test, hoặc chưa khai Redis) — khi đó đọc thẳng CSDL như trước.</param>
    public TenantAiKeyStore(TenantAiKeyRepository repo, IConfiguration cfg, ILogger<TenantAiKeyStore> log,
        TourkitAiProxy.Infrastructure.Cache.RedisStore? redis = null,
        TourkitAiProxy.Infrastructure.Cache.RedisProvider? redisPubSub = null)
    {
        _repo = repo; _cfg = cfg; _log = log; _redis = redis; _redisPubSub = redisPubSub;
    }

    /// <summary>
    /// Có công ty vừa lưu/xoá/bật/tắt key: xoá bản đệm Redis, báo mọi máy chủ nạp lại, và nạp lại
    /// ngay tại máy này. Không bao giờ ném — sửa key đã lưu xong vào CSDL rồi, đệm có hỏng thì cùng
    /// lắm các máy khác thấy chậm một phút.
    /// </summary>
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        try { _redis?.Delete(RedisKey); }
        catch (Exception ex) { _log.LogWarning(ex, "[byo-key] xoá đệm Redis hỏng"); }
        try
        {
            _redisPubSub?.Db?.Multiplexer.GetSubscriber().Publish(
                StackExchange.Redis.RedisChannel.Literal(InvalidateChannel), "1",
                StackExchange.Redis.CommandFlags.FireAndForget);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[byo-key] báo các máy khác nạp lại hỏng"); }
        await RefreshAsync(ct);
    }

    /// <summary>
    /// Nghe tín hiệu "có công ty vừa sửa key" từ máy khác. Gọi một lần lúc khởi động. Không có Redis
    /// thì thôi — các máy vẫn nạp lại theo nhịp, chỉ chậm hơn.
    /// </summary>
    public void SubscribeInvalidations()
    {
        try
        {
            _redisPubSub?.Db?.Multiplexer.GetSubscriber().Subscribe(
                StackExchange.Redis.RedisChannel.Literal(InvalidateChannel),
                (_, _) => { _ = RefreshAsync(); });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[byo-key] đăng ký nghe tín hiệu nạp lại hỏng"); }
    }

    /// Đọc bản đệm Redis (key còn mã hoá). null = không có / Redis tắt / hỏng → đi CSDL.
    private IReadOnlyList<TenantAiKey>? ReadRedis()
    {
        try
        {
            var json = _redis?.Get(RedisKey);
            return string.IsNullOrEmpty(json)
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<List<TenantAiKey>>(json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[byo-key] đọc đệm Redis hỏng — đọc CSDL");
            return null;
        }
    }

    private void WriteRedis(IReadOnlyList<TenantAiKey> rows)
    {
        try { _redis?.Set(RedisKey, System.Text.Json.JsonSerializer.Serialize(rows), RedisTtl); }
        catch (Exception ex) { _log.LogWarning(ex, "[byo-key] ghi đệm Redis hỏng"); }
    }

    public bool FeatureOn => FeatureFlags.ByoAiKey(_cfg);

    /// <summary>
    /// Chọn key cho một lệnh AI. Chỉ tra RAM, không bao giờ ném. Xem luật ở
    /// <see cref="TenantAiKeyRules.Decide"/>.
    /// </summary>
    public TenantKeyDecision Decide(string? tenantId)
    {
        if (!FeatureOn) return TenantKeyDecision.SystemKey;
        try
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return TenantKeyDecision.SystemKey;
            _snapshot.TryGetValue(tenantId, out var l);
            return TenantAiKeyRules.Decide(true, tenantId, l?.Key, l?.RawApiKey);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[byo-key] chọn key hỏng cho {Tenant} — dùng key hệ thống", tenantId);
            return TenantKeyDecision.SystemKey;
        }
    }

    /// <summary>
    /// Key <paramref name="apiKey"/> có ĐÚNG là key riêng đang dùng được của công ty không.
    ///
    /// <para><b>Chỗ DUY NHẤT so khớp key khách</b> — lớp bọc nhà cung cấp và đường gọi công cụ
    /// Anthropic đều hỏi ở đây để quyết có trừ lượt hay không. So khớp KEY chứ không chỉ hỏi "công ty
    /// có BYO không": lệnh nào tự mang key khác (key hệ thống, key client gửi) thì không phải lệnh của
    /// khách. Hỏi theo công ty là để lọt đường gọi bằng key hệ thống mà không trừ lượt — nền tảng
    /// trả AI hộ khách. Xem <c>FreeOfQuotaGuardTests</c>.</para>
    /// </summary>
    public bool IsTenantKey(string? tenantId, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return false;
        var d = Decide(tenantId);
        return d.UseTenantKey && string.Equals(apiKey, d.ApiKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nạp lại từ CSDL. Gọi định kỳ từ <see cref="TenantAiKeyRefresher"/>, và ngay sau khi một công
    /// ty lưu/xoá key để máy đó thấy liền. Không bao giờ ném.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Cờ tắt: bỏ bản chụp, KHÔNG truy vấn gì. Bật lại thì lần nạp sau tự lấp.
        if (!FeatureOn) { _snapshot = Empty; _localFailing.Clear(); return; }

        await _refreshing.WaitAsync(ct);
        try
        {
            // Redis trước, CSDL chỉ khi Redis trống — xem RedisKey.
            var rows = ReadRedis();
            if (rows is null)
            {
                rows = await _repo.LoadAllSealedAsync(ct);
                // null = CSDL lỗi → GIỮ bản cũ. Thay bằng rỗng thì một nhịp CSDL chập chờn là mọi công
                // ty đang dùng key riêng bỗng bị trừ lượt cho tới lần nạp sau.
                if (rows is null) return;
                WriteRedis(rows);
            }

            // Gán từng dòng, không ToDictionary: trùng khoá (không nên có, PK chặn) thì dòng sau
            // thắng thay vì ném làm hỏng cả lần nạp. Giải mã MỘT LẦN ở đây, không phải mỗi lệnh AI.
            var moi = new Dictionary<string, TenantAiKeyRepository.Loaded>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) moi[r.TenantId] = _repo.Unseal(r);

            _snapshot = moi;
            _localFailing.Clear();   // CSDL giờ là nguồn đúng
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[byo-key] nạp lại bộ đệm hỏng — giữ bản cũ");
        }
        finally { _refreshing.Release(); }
    }

    /// <summary>
    /// CHỈ cho test: nạp sẵn bản chụp mà không cần CSDL/Redis — để test được lớp bọc lùi key bằng hành
    /// vi thật. <c>internal</c>, mã sản phẩm không gọi được.
    /// </summary>
    internal void SeedForTests(params TenantAiKeyRepository.Loaded[] rows)
    {
        var moi = new Dictionary<string, TenantAiKeyRepository.Loaded>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in rows) moi[l.Key.TenantId] = l;
        _snapshot = moi;
        _localFailing.Clear();
    }

    /// <summary>Công ty đang lỗi key riêng (theo hiểu biết của máy này).</summary>
    internal bool IsFailing(string tenantId)
        => _localFailing.TryGetValue(tenantId, out var f)
            ? f
            : _snapshot.TryGetValue(tenantId, out var l) && l.Key.IsFailing;

    /// <summary>
    /// Key riêng vừa hỏng và hệ thống đã lùi về key chung. Ghi CSDL chạy nền, không chờ, không ném —
    /// lệnh AI đang lùi không phải đợi việc ghi sổ này.
    /// </summary>
    public void ReportFail(string tenantId, string reason)
    {
        _localFailing[tenantId] = true;
        _ = Task.Run(() => _repo.RecordFailAsync(tenantId, reason));
    }

    /// <summary>
    /// Key riêng vừa chạy được. <b>Chỉ ghi CSDL khi trước đó đang lỗi</b> — lượt thành công bình
    /// thường chỉ tốn một lần tra từ điển.
    /// </summary>
    public void ReportOk(string tenantId)
    {
        if (!IsFailing(tenantId)) return;
        _localFailing[tenantId] = false;
        _ = Task.Run(() => _repo.RecordOkAsync(tenantId));
    }
}

/// <summary>
/// Nạp lại <see cref="TenantAiKeyStore"/> mỗi phút. Chạy ở cả web lẫn worker vì cả hai đều gọi AI.
/// Cờ tắt thì mỗi nhịp chỉ là một phép so sánh, không chạm CSDL.
/// </summary>
public class TenantAiKeyRefresher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly TenantAiKeyStore _store;

    public TenantAiKeyRefresher(TenantAiKeyStore store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Máy khác sửa key → máy này nạp lại NGAY, không chờ hết nhịp một phút.
        _store.SubscribeInvalidations();
        while (!ct.IsCancellationRequested)
        {
            await _store.RefreshAsync(ct);
            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
