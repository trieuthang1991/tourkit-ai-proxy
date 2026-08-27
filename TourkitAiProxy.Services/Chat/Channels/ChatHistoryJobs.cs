// Services/Chat/Channels/ChatHistoryJobs.cs
using System.Collections.Concurrent;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Theo dõi các lượt <b>lấy lại hội thoại cũ</b> đang chạy.
///
/// <para><b>Vì sao phải chạy nền chứ không trả thẳng trong một lượt gọi.</b> Đọc 200 hội thoại,
/// mỗi hội thoại tới hai trang tin, là vài phút. Giữ một yêu cầu HTTP mở lâu như vậy thì hoặc
/// trình duyệt bỏ cuộc, hoặc proxy cắt — người dùng thấy "lỗi" trong khi việc vẫn đang chạy tốt,
/// rồi bấm lại lần nữa.</para>
///
/// <para><b>Vì sao trạng thái nằm trong BỘ NHỚ chứ không phải CSDL.</b> Phần việc đáng giữ đã nằm
/// an toàn ở hàng đợi <c>chat_inbound_events</c> rồi — cái mất khi khởi động lại chỉ là con số
/// tiến độ. Dựng thêm một bảng để giữ con số đó là thêm một thứ phải di trú, phải dọn, phải đồng
/// bộ giữa các tiến trình, đổi lại một tiện ích hiển thị. Khởi động lại giữa chừng thì người dùng
/// bấm lại: phần đã lấy được chống trùng bỏ qua, không mất gì.</para>
/// </summary>
public class ChatHistoryJobs
{
    /// <param name="Xong">Đã chạy xong chưa. Chưa xong thì <c>SoHoiThoai</c>/<c>SoTin</c> là số
    /// tạm.</param>
    /// <param name="Loi">Có lý do thì lượt này dừng sớm — vẫn giữ nguyên phần đã lấy được.</param>
    public record TrangThai(bool Xong, int SoHoiThoai, int SoTin, bool ConNua, string? Loi,
        DateTime BatDauUtc);

    private readonly ConcurrentDictionary<string, TrangThai> _viec = new();

    private static string Khoa(string tenantId, ChatChannel kenh, string accountId)
        => $"{tenantId}|{(short)kenh}|{accountId}";

    /// <summary>
    /// Ghi nhận một lượt vừa bắt đầu. Trả <c>false</c> khi <b>đang có lượt chạy dở</b>.
    ///
    /// <para>Chặn chạy chồng là bắt buộc, không phải cho gọn: hai lượt song song đọc cùng những
    /// hội thoại đó, tức nhân đôi số lượt gọi Graph mà không thêm được tin nào — và đó đúng là
    /// cách nhanh nhất để bị Facebook chặn tạm.</para>
    /// </summary>
    public bool BatDau(string tenantId, ChatChannel kenh, string accountId)
    {
        var k = Khoa(tenantId, kenh, accountId);
        if (_viec.TryGetValue(k, out var dangCo) && !dangCo.Xong) return false;
        _viec[k] = new(false, 0, 0, false, null, DateTime.UtcNow);
        return true;
    }

    public void KetThuc(string tenantId, ChatChannel kenh, string accountId,
        MetaHistoryImporter.KetQua kq)
    {
        var k = Khoa(tenantId, kenh, accountId);
        var batDau = _viec.TryGetValue(k, out var cu) ? cu.BatDauUtc : DateTime.UtcNow;
        _viec[k] = new(true, kq.SoHoiThoai, kq.SoTin, kq.ConNua, kq.Loi, batDau);
    }

    public TrangThai? Xem(string tenantId, ChatChannel kenh, string accountId)
        => _viec.TryGetValue(Khoa(tenantId, kenh, accountId), out var t) ? t : null;
}
