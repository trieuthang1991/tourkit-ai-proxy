using StackExchange.Redis;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Cache;

/// Multiplexer Redis DÙNG CHUNG (null nếu không cấu hình `Redis:ConnectionString`).
/// Decrypt `ENC:` (Crypton, copy thẳng từ TourKit.Api). Dùng cho store bền vững (nháp tour, review).
/// `AbortOnConnectFail=false` → Redis tạm xuống không chặn startup; store tự fallback file.
public class RedisProvider
{
    private readonly IDatabase? _db;
    public bool Available => _db != null;
    public IDatabase? Db => _db;

    public RedisProvider(IConfiguration cfg, ILogger<RedisProvider> log)
    {
        var conn = cfg["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(conn))
        {
            log.LogInformation("Redis chưa cấu hình → store bền vững dùng file");
            return;
        }
        if (conn.StartsWith("ENC:")) conn = Crypton.Decrypt(conn.Substring(4));
        try
        {
            var options = ConfigurationOptions.Parse(conn);
            options.AbortOnConnectFail = false;
            var mux = ConnectionMultiplexer.Connect(options);
            _db = mux.GetDatabase();
            log.LogInformation("RedisProvider connected ({Ep}, connected={C})",
                mux.GetEndPoints().FirstOrDefault(), mux.IsConnected);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Redis connect lỗi → store bền vững dùng file");
        }
    }
}
