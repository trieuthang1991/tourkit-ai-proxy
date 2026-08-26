// Services/Chat/Channels/MessengerPageChoices.cs
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <param name="PageId">Id Trang — cũng là <c>accountId</c> sau khi nối, vì webhook dùng chung
/// tra ngược ra công ty bằng chính id này.</param>
/// <param name="AccessToken">Page access token đã đổi sang bản dài hạn. <b>Đây là bí mật cấp
/// công ty</b> — ai cầm được là nhắn tin dưới danh nghĩa Trang đó.</param>
public sealed record TrangUngVien(string PageId, string Ten, string AccessToken);

/// <summary>
/// Giữ tạm danh sách Trang Facebook giữa hai nửa của bước nối: callback đã đổi được token và biết
/// người này quản trị những Trang nào, nhưng <b>chưa biết họ muốn nối Trang nào</b>.
///
/// <para><b>Vì sao Zalo không cần cái này.</b> Zalo hỏi <c>getoa</c> ra đúng MỘT OA nên nối xong
/// ngay trong callback. Meta trả <c>/me/accounts</c> — một người có thể quản trị chục Trang, kể cả
/// Trang chẳng liên quan gì tới công ty. Nối bừa Trang đầu danh sách là sai, nối hết là tệ hơn.</para>
///
/// <para><b>Vì sao phải giữ ở máy chủ.</b> Trang picker là trang CÔNG KHAI (Meta đá về bằng chuyển
/// hướng thường, không mang phiên). Nhét page access token vào HTML rồi nhận lại từ form là đưa
/// khoá cấp công ty đi một vòng qua trình duyệt — chỉ đưa một mã tra cứu vô nghĩa, token ở lại đây.</para>
///
/// <para><b>Cố ý KHÔNG dùng một lần</b>, khác <see cref="ChatOAuthStates"/>: công ty du lịch nhiều
/// chi nhánh sẽ nối vài Trang liền tay, bắt đăng nhập Facebook lại từ đầu cho mỗi Trang là hành
/// người dùng. Bù lại bằng hạn 10 phút — hết giờ thì bấm nối lại.</para>
/// </summary>
public class MessengerPageChoices
{
    private static readonly TimeSpan HanDung = TimeSpan.FromMinutes(10);

    private record Cho(string TenantId, IReadOnlyList<TrangUngVien> Trang, DateTime HetHanUtc);

    private readonly ConcurrentDictionary<string, Cho> _cho = new(StringComparer.Ordinal);

    public string Tao(string tenantId, IReadOnlyList<TrangUngVien> trang)
    {
        DonRac();
        var ma = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _cho[ma] = new(tenantId, trang, DateTime.UtcNow.Add(HanDung));
        return ma;
    }

    /// <summary>Danh sách Trang còn chờ chọn, để vẽ lại trang picker sau mỗi lượt nối.</summary>
    public (string TenantId, IReadOnlyList<TrangUngVien> Trang)? Xem(string? ma)
    {
        DonRac();
        if (string.IsNullOrWhiteSpace(ma) || !_cho.TryGetValue(ma!, out var c)) return null;
        return c.HetHanUtc <= DateTime.UtcNow ? null : (c.TenantId, c.Trang);
    }

    /// <summary>
    /// Một Trang cụ thể trong lượt chọn. Trả <c>null</c> khi mã sai/hết hạn <b>hoặc</b> khi
    /// <paramref name="pageId"/> không nằm trong danh sách — chốt chặn quan trọng: thiếu nó thì ai
    /// cầm mã cũng nối được Trang bất kỳ mà họ chỉ cần đoán id.
    /// </summary>
    public (string TenantId, TrangUngVien Trang)? Nhan(string? ma, string? pageId)
    {
        if (Xem(ma) is not { } c || string.IsNullOrWhiteSpace(pageId)) return null;
        var t = c.Trang.FirstOrDefault(x => x.PageId == pageId);
        return t is null ? null : (c.TenantId, t);
    }

    private void DonRac()
    {
        var gio = DateTime.UtcNow;
        foreach (var kv in _cho)
            if (kv.Value.HetHanUtc <= gio) _cho.TryRemove(kv.Key, out _);
    }
}
