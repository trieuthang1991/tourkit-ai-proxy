using System.Text.Json;
using TourkitAiProxy.Infrastructure.Cache;
using TourkitAiProxy.Infrastructure.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Danh sách nhân viên (người bán) của công ty, lấy từ ERP, đệm Redis 2 tiếng theo công ty.
///
/// <para>Tách khỏi <c>/api/v1/chat/assign-settings</c> (19/10/2026) để chat nội bộ dùng CHUNG — hai
/// bản chép là hai chỗ phải nhớ sửa mỗi lần ERP đổi dạng dữ liệu, mà khối này đã sửa ba lần vì
/// đúng chuyện đó (vé JWT rỗng sau khởi động lại, GetInt32 ném, name không phải chuỗi).</para>
/// </summary>
public static class ChatStaffDirectory
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <returns>
    /// <c>Staffs</c>: mỗi dòng dạng <c>{ id, name }</c> — đối tượng vô danh khi vừa hỏi ERP,
    /// <c>JsonElement</c> khi đọc từ đệm (đọc mã bằng <see cref="StaffId"/>).
    /// <c>ErpRows</c>: số dòng ERP thực sự trả về (0 khi đọc từ đệm).
    /// </returns>
    public static async Task<(List<object> Staffs, int ErpRows)> ListAsync(TkSessionStore sessions,
        TourKitApiClient api, RedisStore redis, string tenantId, string sessionId, ILogger log,
        CancellationToken ct)
    {
        // Danh sách nhân viên đổi rất thưa (chỉ khi nhân sự thay đổi bên ERP) nhưng bị hỏi ở
        // MỖI lần mở hộp thư và MỖI lần mở màn hình cấu hình. Đệm 2 tiếng theo công ty.
        //
        // Không có Redis thì Get trả null và ta gọi thẳng ERP như cũ — đệm là tối ưu, không
        // phải điều kiện để chạy.
        var khoaDem = $"chat:nhanvien:{tenantId}";
        var nhanVien = new List<object>();
        // Số dòng ERP trả về. Khi danh sách rỗng thì đây là dữ kiện quan trọng nhất: 0 dòng nghĩa
        // là công ty không có ai, còn 108 dòng mà vẫn rỗng nghĩa là ERP trả người nhưng thiếu tên
        // — hai ca đòi hai cách xử lý khác hẳn.
        var soDongErpTra = 0;
        var daDem = redis.Get(khoaDem);
        if (daDem is not null)
        {
            try { nhanVien = JsonSerializer.Deserialize<List<object>>(daDem, Web) ?? new(); }
            catch { /* đệm hỏng thì coi như chưa có */ }
        }
        if (nhanVien.Count > 0) return (nhanVien, soDongErpTra);

        // Tên nhân viên lấy từ ERP, KHÔNG lưu trong CSDL chat: lưu là nhân đôi chỗ phải sửa khi ai
        // đó đổi tên. Best-effort — upstream hỏng thì trả danh sách rỗng, hộp thư vẫn chạy, chỉ mất
        // ô chọn người.
        var hoiHong = false;
        try
        {
            // Vé JWT phải lấy qua GetValidJwtAsync, KHÔNG đọc thẳng phiên. Vé không được lưu xuống
            // CSDL, nên máy chủ vừa khởi động lại là mọi phiên có vé RỖNG — đọc thẳng thì ERP trả
            // 401 và ô chọn người trống trơn. Đã trả giá 09/09/2026.
            var jwt = await sessions.GetValidJwtAsync(sessionId, ct);
            var r = await api.GetAsync(jwt, "/api/ai/reference", ct);
            if (r.TryGetProperty("lookups", out var lk)
                && lk.TryGetProperty("sellers", out var sellers)
                && sellers.ValueKind == JsonValueKind.Array)
                foreach (var it in sellers.EnumerateArray())
                {
                    soDongErpTra++;
                    // ⚠️ Khoá số có thể là "value" HOẶC "id" tuỳ enum. TryGetInt32 chứ không GetInt32:
                    // một bản ghi lệch dạng ném ra ngoài vòng lặp là xoá sạch CẢ danh sách.
                    var ma = 0;
                    if (it.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number
                        && v.TryGetInt32(out var vn))
                        ma = vn;
                    else if (it.TryGetProperty("id", out var i2) && i2.ValueKind == JsonValueKind.Number
                        && i2.TryGetInt32(out var idn))
                        ma = idn;
                    // GetString() NÉM nếu "name" không phải chuỗi — cùng lớp lỗi với GetInt32 ở trên.
                    var ten = it.TryGetProperty("name", out var n)
                        && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    if (ma > 0 && !string.IsNullOrWhiteSpace(ten))
                        nhanVien.Add(new { id = ma, name = ten });
                }
        }
        // Nuốt lỗi là ĐÚNG (mất ô chọn người còn hơn tắt cả màn hình), nuốt IM LẶNG là sai — đã trả
        // giá 08/09/2026: danh sách tụt từ 108 xuống 0 mà log không có gì để lần.
        catch (Exception ex)
        {
            hoiHong = true;
            log.LogWarning(ex, "[chat/assign-settings] không lấy được danh sách nhân viên " +
                "từ ERP cho {T} — ô chọn người phụ trách sẽ trống", tenantId);
        }

        // ⚠️ CHỈ đệm khi danh sách KHÁC RỖNG — đệm một danh sách rỗng do ERP lỗi nhất thời là khoá
        // cả công ty ra khỏi màn hình cấu hình suốt hai tiếng.
        if (nhanVien.Count > 0)
            redis.Set(khoaDem, JsonSerializer.Serialize(nhanVien, Web), TimeSpan.FromHours(2));
        // Rỗng mà không ngoại lệ có HAI ca, đòi hai người khác nhau đi sửa — gộp một dòng là chỉ
        // đường sai (trả giá 09/09/2026). !hoiHong ở cả hai nhánh: thiếu là ghi hai dòng mâu thuẫn.
        else if (!hoiHong && soDongErpTra == 0)
            log.LogWarning("[chat/assign-settings] ERP không trả người bán nào cho {T} " +
                "— ô chọn người phụ trách sẽ trống", tenantId);
        else if (!hoiHong)
            log.LogWarning("[chat/assign-settings] ERP trả {N} người bán cho {T} nhưng " +
                "KHÔNG dòng nào dùng được (thiếu mã, hoặc thiếu tên) — ô chọn người phụ " +
                "trách sẽ trống. Kết nối vẫn tốt: lỗi nằm ở dữ liệu ERP trả về.",
                soDongErpTra, tenantId);
        return (nhanVien, soDongErpTra);
    }

    /// Mã của một dòng trong danh sách nhân viên — dòng mới dựng là đối tượng vô danh, dòng đọc từ
    /// đệm Redis là JsonElement, nên đi qua JSON cho cả hai.
    public static int? StaffId(object staff)
    {
        var e = staff is JsonElement je ? je : JsonSerializer.SerializeToElement(staff, Web);
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var v)
               && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var id) ? id : null;
    }
}
