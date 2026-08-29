// Domain/Chat/MetaSignedRequest.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Bóc và kiểm gói <c>signed_request</c> của Meta.
///
/// <para><b>Dùng ở đâu:</b> Meta gọi đường "Data Deletion Callback" khi người dùng gỡ ứng dụng
/// khỏi tài khoản Facebook của họ và yêu cầu xoá dữ liệu. Gói tin không có chữ ký HTTP như
/// webhook tin nhắn — nó tự mang chữ ký bên trong, và đây là thứ DUY NHẤT chứng minh yêu cầu
/// thật sự đến từ Meta chứ không phải người ngoài gõ tay.</para>
///
/// <para><b>Dạng:</b> <c>{chữ ký base64url}.{gói JSON base64url}</c></para>
///
/// <para>⚠️ Chữ ký tính trên <b>CHUỖI ĐÃ MÃ HOÁ</b> của phần gói, không phải trên JSON đã giải.
/// Giải ra rồi mới băm là chữ ký không bao giờ khớp — và triệu chứng chỉ là "Meta bảo hỏng" mà
/// không nói vì sao.</para>
///
/// <para>⚠️ base64<b>url</b> chứ không phải base64 thường: <c>-</c> thay <c>+</c>, <c>_</c> thay
/// <c>/</c>, và bỏ phần đệm <c>=</c>. Dùng thẳng <c>Convert.FromBase64String</c> là ném lỗi ở
/// đúng những gói có ký tự đó — tức là hỏng lúc chạy thật chứ không hỏng lúc thử.</para>
/// </summary>
public static class MetaSignedRequest
{
    /// <param name="UserId">Mã người dùng theo phạm vi ứng dụng, do Meta cấp.</param>
    /// <param name="IssuedAt">Thời điểm Meta ký, giây Unix.</param>
    public record Payload(string UserId, long IssuedAt);

    /// <summary>
    /// Kiểm chữ ký rồi trả phần gói. <c>null</c> nghĩa là KHÔNG hợp lệ — chỗ gọi phải từ chối,
    /// tuyệt đối đừng xoá dữ liệu theo một yêu cầu chưa chứng minh được nguồn.
    /// </summary>
    public static Payload? Parse(string? signedRequest, string? appSecret)
    {
        if (string.IsNullOrWhiteSpace(signedRequest) || string.IsNullOrWhiteSpace(appSecret))
            return null;

        var phan = signedRequest.Split('.');
        if (phan.Length != 2) return null;

        byte[] chuKyGui;
        byte[] goiTho;
        try
        {
            chuKyGui = TuBase64Url(phan[0]);
            goiTho = TuBase64Url(phan[1]);
        }
        catch { return null; }

        // Băm trên CHUỖI đã mã hoá (phan[1]), không phải trên goiTho đã giải — xem ghi chú lớp.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var chuKyMong = hmac.ComputeHash(Encoding.UTF8.GetBytes(phan[1]));

        // So theo THỜI GIAN CỐ ĐỊNH. So bằng == thoát sớm ở byte đầu khác nhau, và chênh lệch
        // thời gian đó đủ để dò dần từng byte chữ ký.
        if (!CryptographicOperations.FixedTimeEquals(chuKyGui, chuKyMong)) return null;

        try
        {
            using var doc = JsonDocument.Parse(goiTho);
            var g = doc.RootElement;

            // Meta chỉ ký bằng HMAC-SHA256. Gói khai thuật toán khác là gói lạ — từ chối thay vì
            // đoán, kẻo mai sau ai đó thêm nhánh "none" cho tiện rồi mở toang cửa.
            if (g.TryGetProperty("algorithm", out var alg)
                && !string.Equals(alg.GetString(), "HMAC-SHA256", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!g.TryGetProperty("user_id", out var uid)) return null;
            var ma = uid.GetString();
            if (string.IsNullOrWhiteSpace(ma)) return null;

            var luc = g.TryGetProperty("issued_at", out var ia) && ia.TryGetInt64(out var v) ? v : 0;
            return new Payload(ma!, luc);
        }
        catch { return null; }
    }

    /// <summary>
    /// Mã xác nhận trả về cho Meta, cũng là mã người dùng tra tiến độ.
    ///
    /// <para>Sinh ngẫu nhiên chứ KHÔNG băm từ mã người dùng: mã này hiện trên một trang công khai,
    /// mà băm thì đoán ngược được ai đã yêu cầu xoá.</para>
    /// </summary>
    public static string NewConfirmationCode()
    {
        Span<byte> b = stackalloc byte[12];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    private static byte[] TuBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}
