using System.Text.Json;
using TourkitAiProxy.Infrastructure.Cache;
using TourkitAiProxy.Infrastructure.TourKit;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Ai trong công ty CÓ QUYỀN CHAT (CHAT_XEM hoặc CHAT_XEM_ALL của CRM) — để chỉ giao việc, xếp đội
/// trực và chia vòng xoay cho những người đó (chốt 12/10/2026). Giao cho người không có quyền chat
/// là giao vào chỗ không ai thấy: người nhận không mở được hộp thư.
///
/// <para><b>Nguồn:</b> ERP <c>/api/ai/users-with-permission</c>. Quyền trong CRM gán theo PHÒNG BAN,
/// proxy không đọc được CSDL CRM, nên phải hỏi ERP — mà hỏi ERP thì cần vé của một người đang đăng
/// nhập. Luồng khách nhắn tới (vòng xoay) không có ai đăng nhập, nên kết quả được lưu vào Redis mỗi
/// lần có người mở hộp thư, và luồng đó đọc bản đã lưu.</para>
///
/// <para><b>Không biết thì KHÔNG lọc</b> (trả <c>null</c>): ERP chưa có đường mới (deploy proxy trước
/// ERP), ERP lỗi, hoặc trả về rỗng. Lọc theo một danh sách rỗng là chặn giao việc cho cả công ty chỉ
/// vì một lượt gọi hỏng — tệ hơn hẳn hành vi cũ là cho chọn mọi người bán.</para>
/// </summary>
public sealed class ChatAgentPermissions
{
    /// Quá tuổi này thì lượt có phiên hỏi lại ERP — đổi quyền bên CRM thấy sau tối đa 10 phút.
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    /// Giữ bản cuối cùng lâu hơn nhiều cho luồng không phiên: một đêm không ai mở hộp thư thì vòng
    /// xoay vẫn còn danh sách để lọc.
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    private const string Codes = TkPermissionCodes.ChatXem + "," + TkPermissionCodes.ChatXemTatCa;

    private readonly TourKitApiClient _api;
    private readonly RedisStore _redis;
    private readonly ILogger<ChatAgentPermissions> _log;

    public ChatAgentPermissions(TourKitApiClient api, RedisStore redis, ILogger<ChatAgentPermissions> log)
    {
        _api = api; _redis = redis; _log = log;
    }

    private sealed record Snapshot(DateTime FetchedUtc, int[] UserIds);

    private static string Key(string tenantId) => $"chat:quyen-chat:{tenantId}";

    /// <summary>
    /// Tập mã người có quyền chat, hỏi lại ERP khi bản đã lưu quá 10 phút. <c>null</c> = không biết,
    /// chỗ gọi phải xử như trước khi có luật này (không lọc).
    /// </summary>
    public async Task<IReadOnlySet<int>?> GetAsync(string tenantId, string jwt, CancellationToken ct)
    {
        var saved = Read(tenantId);
        if (saved is not null && DateTime.UtcNow - saved.FetchedUtc < FreshFor)
            return ToSet(saved);

        try
        {
            var r = await _api.GetAsync(jwt, $"/api/ai/users-with-permission?codes={Uri.EscapeDataString(Codes)}", ct);
            var ids = new List<int>();
            if (r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                    if (it.ValueKind == JsonValueKind.Number && it.TryGetInt32(out var id) && id > 0)
                        ids.Add(id);

            if (ids.Count == 0)
            {
                // Không lưu: lưu rỗng là 24 giờ không lọc được gì dù ERP đã sửa xong.
                _log.LogWarning("[chat/quyen-chat] ERP trả 0 người có quyền chat cho {T} — tạm không lọc", tenantId);
                return ToSet(saved);
            }

            var snap = new Snapshot(DateTime.UtcNow, ids.Distinct().ToArray());
            _redis.Set(Key(tenantId), JsonSerializer.Serialize(snap), KeepFor);
            return ToSet(snap);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 404 ở đây thường là ERP chưa deploy đường mới. Dùng bản cũ nếu còn, không thì không lọc.
            _log.LogWarning(ex, "[chat/quyen-chat] không hỏi được ERP ai có quyền chat cho {T} — {Cach}",
                tenantId, saved is null ? "tạm không lọc" : "dùng bản đã lưu");
            return ToSet(saved);
        }
    }

    /// <summary>
    /// Chỉ đọc bản đã lưu, không gọi ERP — cho luồng không có phiên đăng nhập (khách nhắn tới).
    /// </summary>
    public IReadOnlySet<int>? GetCachedOnly(string tenantId) => ToSet(Read(tenantId));

    private Snapshot? Read(string tenantId)
    {
        var raw = _redis.Get(Key(tenantId));
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<Snapshot>(raw); }
        catch { return null; }
    }

    private static IReadOnlySet<int>? ToSet(Snapshot? s)
        => s is { UserIds.Length: > 0 } ? s.UserIds.ToHashSet() : null;
}
