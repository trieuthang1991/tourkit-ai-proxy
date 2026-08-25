using TourkitAiProxy.Domain.Chat;
// Services/Storage/ChatFileStorageRegistration.cs
namespace TourkitAiProxy.Services.Storage;

public static class ChatFileStorageRegistration
{
    /// <summary>
    /// Đăng ký ĐÚNG MỘT triển khai <see cref="IChatFileStorage"/> theo <c>Storage:Provider</c>
    /// (mặc định <c>local</c> — chạy được ngay không cần khai gì, xem lý do trong
    /// <see cref="LocalChatFileStorage"/>).
    /// </summary>
    public static IServiceCollection AddChatFileStorage(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<IChatFileStorage>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("storage");
            var goc = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
            var provider = (cfg["Storage:Provider"] ?? "local").Trim().ToLowerInvariant();
            return provider switch
            {
                "r2" => new S3CompatibleChatFileStorage("r2",
                    cfg["Storage:R2:AccessKeyId"], cfg["Storage:R2:SecretAccessKey"],
                    cfg["Storage:R2:Bucket"], cfg["Storage:R2:PublicBaseUrl"],
                    serviceUrl: string.IsNullOrWhiteSpace(cfg["Storage:R2:AccountId"]) ? null
                        : $"https://{cfg["Storage:R2:AccountId"]}.r2.cloudflarestorage.com",
                    region: null, log),
                "s3" => new S3CompatibleChatFileStorage("s3",
                    cfg["Storage:S3:AccessKeyId"], cfg["Storage:S3:SecretAccessKey"],
                    cfg["Storage:S3:Bucket"], cfg["Storage:S3:PublicBaseUrl"],
                    serviceUrl: null, region: cfg["Storage:S3:Region"], log),
                "local" => new LocalChatFileStorage(cfg["Storage:Local:Dir"], goc),
                _ => Rơi(provider, log, goc),
            };
        });
        return services;
    }

    private static IChatFileStorage Rơi(string provider, ILogger log, string contentRoot)
    {
        // Giá trị lạ (gõ sai "R2" hoa/thường không phải vấn đề vì đã ToLower ở trên; đây là khi
        // gõ hẳn tên khác) → về local thay vì crash lúc khởi động, nhưng NÓI RÕ để không âm thầm
        // dùng nhầm nhà cung cấp.
        log.LogWarning("[storage] Storage:Provider=\"{P}\" không nhận diện được — dùng local", provider);
        return new LocalChatFileStorage(null, contentRoot);
    }
}
