using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Infrastructure.Security;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Đọc <c>Admin:Users</c> từ <c>appsettings.json</c> — tài khoản đăng nhập trang
/// <c>/admin-trav-ai</c>.
///
/// <para><b>Mật khẩu nên để dạng <c>ENC:&lt;chuỗi&gt;</c></b> (Crypton, cùng nếp với
/// <c>ConnectionStrings</c>, <c>Sso:Secret</c>, <c>Tingee:RelayToken</c>). Giải mã một lần lúc
/// khởi động, giữ trong bộ nhớ, không bao giờ ghi ra log.</para>
///
/// <para>Vẫn chấp nhận mật khẩu viết trần để bản cấu hình cũ không hỏng khi nâng cấp — nhưng khi
/// gặp thì <b>ghi cảnh báo</b>: <c>appsettings.json</c> có nằm trong <c>.gitignore</c> thật, nhưng
/// nó vẫn nằm trên đĩa máy chủ, vẫn đi vào bản sao lưu, vẫn hiện ra khi ai đó dán file cấu hình để
/// nhờ hỗ trợ. Mã hoá thì mấy đường đó đều vô hại.</para>
/// </summary>
public class AdminUserStore
{
    private readonly List<AdminUser> _users;

    public AdminUserStore(IConfiguration cfg, ILogger<AdminUserStore>? log = null)
    {
        _users = cfg.GetSection("Admin:Users").Get<List<AdminUser>>() ?? new();

        var soTran = 0;
        foreach (var u in _users)
        {
            if (GiaiMaNeuCan(u.Password) is { } ro) { u.Password = ro; }
            else soTran++;
        }
        if (soTran > 0)
            log?.LogWarning("Admin:Users — {So} mật khẩu đang để dạng chữ thường trong appsettings.json. "
                            + "Nên đổi sang dạng ENC: (Crypton) để không lộ khi sao lưu/chia sẻ file cấu hình.",
                            soTran);
    }

    /// <summary>Trả chuỗi đã giải mã nếu là <c>ENC:</c>; <c>null</c> nghĩa là để trần.</summary>
    private static string? GiaiMaNeuCan(string? v)
    {
        if (string.IsNullOrEmpty(v) || !v.StartsWith("ENC:", StringComparison.Ordinal)) return null;
        return Crypton.Decrypt(v[4..]);
    }

    public bool Authenticate(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;

        var duoc = false;
        foreach (var u in _users)
        {
            // KHÔNG thoát sớm khi khớp: so hết danh sách để thời gian trả lời không phụ thuộc
            // vào việc tên đăng nhập đúng hay sai, cũng không phụ thuộc user nằm ở vị trí nào.
            if (string.Equals(u.Username, username, StringComparison.Ordinal) &&
                BangNhau(u.Password, password))
                duoc = true;
        }
        return duoc;
    }

    /// <summary>So sánh không rò rỉ theo thời gian — đây là đường đăng nhập.</summary>
    private static bool BangNhau(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));

    public sealed class AdminUser
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
