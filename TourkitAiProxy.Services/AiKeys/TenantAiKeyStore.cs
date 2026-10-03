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

    private readonly TenantAiKeyRepository _repo;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TenantAiKeyStore> _log;
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

    public TenantAiKeyStore(TenantAiKeyRepository repo, IConfiguration cfg, ILogger<TenantAiKeyStore> log)
    {
        _repo = repo; _cfg = cfg; _log = log;
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
            var all = await _repo.LoadAllAsync(ct);
            // null = CSDL lỗi → GIỮ bản cũ. Thay bằng rỗng thì một nhịp chập chờn là mọi công ty đang
            // dùng key riêng bỗng bị trừ lượt cho tới lần nạp sau.
            if (all is null) return;

            // Gán từng dòng, không ToDictionary: trùng khoá (không nên có, PK chặn) thì dòng sau
            // thắng thay vì ném làm hỏng cả lần nạp.
            var moi = new Dictionary<string, TenantAiKeyRepository.Loaded>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in all) moi[l.Key.TenantId] = l;

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

    /// <summary>Công ty đang lỗi key riêng (theo hiểu biết của máy này).</summary>
    private bool IsFailing(string tenantId)
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
        while (!ct.IsCancellationRequested)
        {
            await _store.RefreshAsync(ct);
            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
