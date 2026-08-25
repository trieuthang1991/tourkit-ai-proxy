namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// Số điện thoại nhận bản tin qua Zalo (ZNS nhắn theo SỐ, không phải theo Zalo user id).
///
/// <para><b>Chuẩn hoá lúc LƯU, không phải lúc gửi.</b> Người dùng gõ đủ kiểu: có dấu cách, có dấu
/// chấm, dán từ danh bạ ra thành <c>+84…</c>. Nếu để nguyên rồi lúc gửi mới xử thì cùng một người
/// có thể nằm trong DB dưới vài dạng khác nhau, và trang theo dõi nhìn vào không biết có trùng
/// không. Chuẩn về đúng một dạng <c>0xxxxxxxxx</c> ngay từ lúc lưu là hết chuyện đó.</para>
///
/// <para>Đổi sang dạng <c>84…</c> mà Zalo đòi thì để bên worker làm ngay trước khi gọi API — giữ
/// dạng người Việt đọc được trong DB và trên màn hình.</para>
/// </summary>
public static class DigestPhone
{
    /// <summary>
    /// Bỏ mọi ký tự không phải số, đưa <c>+84…</c> / <c>84…</c> về <c>0…</c>. Rác không cứu được
    /// thì trả nguyên bản đã cắt khoảng trắng để <see cref="IsValid"/> bắt và báo cho người dùng —
    /// KHÔNG tự bịa ra một số trông hợp lệ.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return raw.Trim();

        // "84912345678" hoặc "+84912345678" → "0912345678". Chỉ đổi khi phần còn lại đủ dài,
        // tránh biến một số nội bộ bắt đầu bằng 84 thành số di động.
        if (digits.StartsWith("84") && digits.Length >= 11) digits = "0" + digits[2..];

        return digits;
    }

    /// <summary>
    /// Số di động Việt Nam: 10 chữ số, bắt đầu <c>03/05/07/08/09</c>. Cố ý KHÔNG nhận số cố định —
    /// Zalo là ứng dụng di động, số bàn nhập vào chỉ tổ gửi hỏng mà người dùng không hiểu vì sao.
    /// </summary>
    public static bool IsValid(string? raw)
    {
        var p = Normalize(raw);
        if (p is not { Length: 10 }) return false;
        if (p[0] != '0' || !p.All(char.IsDigit)) return false;
        return p[1] is '3' or '5' or '7' or '8' or '9';
    }

    /// Dạng Zalo đòi khi gọi API: <c>0912345678</c> → <c>84912345678</c>.
    public static string ToZaloFormat(string phone) => "84" + Normalize(phone)![1..];
}
