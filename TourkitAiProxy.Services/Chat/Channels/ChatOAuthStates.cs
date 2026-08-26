// Services/Chat/Channels/ChatOAuthStates.cs
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Giữ tạm tham số <c>state</c> của vòng cấp quyền — <b>dùng chung cho Zalo OA và Facebook
/// Messenger</b>. Hai nhà cung cấp, một cơ chế: cả hai đều là OAuth 2.0 authorization-code.
///
/// <para><b>Vì sao cần.</b> Zalo/Meta đá trình duyệt về đường callback bằng một chuyển hướng
/// thường — không mang theo <c>X-Session-Id</c>, nên lúc nhận <c>code</c> máy chủ <b>không biết</b>
/// đây là công ty nào, tài khoản nào. Ghép lại bằng <c>state</c>: sinh ra ở đường có phiên, tra
/// lại ở đường công khai.</para>
///
/// <para><b>Đây là chốt chặn an ninh, không phải tiện ích.</b> Nếu để client tự khai tenant trên
/// URL callback thì ai biết đường dẫn cũng nhét được refresh token của OA mình vào công ty khác —
/// từ đó đọc và trả lời tin của khách công ty đó. <c>state</c> do MÁY CHỦ sinh ngẫu nhiên 32 byte,
/// <b>dùng một lần</b>, sống 10 phút.</para>
///
/// <para>Để trong bộ nhớ là đủ: cả vòng cấp quyền diễn ra trong vài chục giây và người dùng đang
/// ngồi trước màn hình. Chạy nhiều instance sau load-balancer thì lượt cấp quyền có thể rơi vào
/// instance khác và hỏng — lúc đó bấm lại là xong, chứ không mất mát gì.</para>
/// </summary>
public class ChatOAuthStates
{
    /// 10 phút: đủ để đăng nhập Zalo và bấm đồng ý, không đủ để một mã rò ra ngoài còn dùng được.
    private static readonly TimeSpan HanDung = TimeSpan.FromMinutes(10);

    private record Cho(string TenantId, string AccountId, string RedirectUri, DateTime HetHanUtc);

    private readonly ConcurrentDictionary<string, Cho> _cho = new(StringComparer.Ordinal);

    public string Tao(string tenantId, string accountId, string redirectUri)
    {
        DonRac();
        var ma = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _cho[ma] = new(tenantId, accountId, redirectUri, DateTime.UtcNow.Add(HanDung));
        return ma;
    }

    /// <summary>
    /// Tra và <b>xoá luôn</b> — dùng một lần. Trả <c>null</c> khi mã sai, đã dùng, hoặc hết hạn;
    /// ba trường hợp cố ý không phân biệt để bên ngoài không dò được mã nào từng tồn tại.
    /// </summary>
    public (string TenantId, string AccountId, string RedirectUri)? Nhan(string? ma)
    {
        DonRac();
        if (string.IsNullOrWhiteSpace(ma) || !_cho.TryRemove(ma, out var c)) return null;
        return c.HetHanUtc <= DateTime.UtcNow ? null : (c.TenantId, c.AccountId, c.RedirectUri);
    }

    private void DonRac()
    {
        var gio = DateTime.UtcNow;
        foreach (var kv in _cho)
            if (kv.Value.HetHanUtc <= gio) _cho.TryRemove(kv.Key, out _);
    }
}
