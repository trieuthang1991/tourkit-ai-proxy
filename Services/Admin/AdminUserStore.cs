using Microsoft.Extensions.Configuration;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Đọc Admin:Users từ appsettings.json (plain text password — admin pool ≤5 user, self-host,
/// appsettings.json đã gitignore). Authenticate bằng string compare ordinal.
/// </summary>
public class AdminUserStore
{
    private readonly List<AdminUser> _users;

    public AdminUserStore(IConfiguration cfg)
    {
        _users = cfg.GetSection("Admin:Users").Get<List<AdminUser>>() ?? new();
    }

    public bool Authenticate(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;
        foreach (var u in _users)
        {
            if (string.Equals(u.Username, username, StringComparison.Ordinal) &&
                string.Equals(u.Password, password, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public sealed class AdminUser
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
