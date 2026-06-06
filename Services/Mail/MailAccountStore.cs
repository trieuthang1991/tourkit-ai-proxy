using System.Text.Json;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Mail;

/// Lưu/đọc creds hộp thư Gmail + chữ ký. Ưu tiên: file data/mail-account.json
/// (App Password mã hóa Crypton) nhập từ UI → fallback config (Mail:Gmail:*) / env.
/// App Password KHÔNG lưu plaintext. JWT/secret pattern giống TkSessionStore.
public class MailAccountStore
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<MailAccountStore> _log;
    private readonly string _path;
    private readonly object _lock = new();
    private (string Address, string AppPassword)? _saved;
    private string _signature = "";

    public MailAccountStore(IWebHostEnvironment env, IConfiguration cfg, ILogger<MailAccountStore> log)
    {
        _cfg = cfg; _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "mail-account.json");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            var addr = root.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
            var encPwd = root.TryGetProperty("appPasswordEnc", out var p) ? p.GetString() ?? "" : "";
            _signature = root.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "";
            var pwd = string.IsNullOrEmpty(encPwd) ? "" : Crypton.Decrypt(encPwd);
            if (!string.IsNullOrWhiteSpace(addr)) { _saved = (addr, pwd); _log.LogInformation("Loaded mail account {Addr}", addr); }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Đọc mail-account.json lỗi"); }
    }

    /// Creds đang dùng: ưu tiên cái nhập từ UI (đã lưu), nếu không có thì config/env.
    public (string Address, string AppPassword) Get()
    {
        lock (_lock)
        {
            if (_saved is { } s && !string.IsNullOrWhiteSpace(s.Address)) return s;
        }
        var addr = _cfg["Mail:Gmail:Address"];
        if (string.IsNullOrWhiteSpace(addr)) addr = Environment.GetEnvironmentVariable("MAIL_GMAIL_ADDRESS");
        var pwd = _cfg["Mail:Gmail:AppPassword"];
        if (string.IsNullOrWhiteSpace(pwd)) pwd = Environment.GetEnvironmentVariable("MAIL_GMAIL_APP_PASSWORD");
        return (addr ?? "", pwd ?? "");
    }

    /// Lưu creds + chữ ký nhập từ UI (App Password mã hóa Crypton xuống đĩa).
    public void Set(string address, string appPassword, string? signature)
    {
        lock (_lock)
        {
            _saved = (address.Trim(), appPassword.Trim());
            _signature = (signature ?? "").Trim();
            try
            {
                var obj = new { address = address.Trim(), appPasswordEnc = Crypton.Encrypt(appPassword.Trim()), signature = _signature };
                File.WriteAllText(_path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
                _log.LogInformation("Saved mail account {Addr}", address.Trim());
            }
            catch (Exception ex) { _log.LogError(ex, "Lưu mail-account.json lỗi"); }
        }
    }

    public bool IsConfigured()
    {
        var (a, p) = Get();
        return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(p);
    }

    /// Địa chỉ đang cấu hình (cho UI hiển thị) — KHÔNG trả App Password.
    public string CurrentAddress() => Get().Address;

    /// Chữ ký công ty (do công ty tự đặt ở UI). Rỗng nếu chưa đặt — KHÔNG mặc định
    /// "Tourkit" vì đây là email công ty tour gửi cho khách của họ, không phải nền tảng.
    public string Signature() => _signature ?? "";

    /// Có đặt chữ ký riêng chưa?
    public bool HasSignature() => !string.IsNullOrWhiteSpace(_signature);
}
