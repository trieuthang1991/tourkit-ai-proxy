// Services/Storage/S3CompatibleChatFileStorage.cs
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Storage;

/// <summary>
/// Dùng chung cho R2 và S3 thật — cả hai nói cùng giao thức S3, khác mỗi cách dựng client (R2 cần
/// <c>ServiceURL</c> riêng theo AccountId; S3 dùng <c>RegionEndpoint</c> chuẩn) và nguồn URL công
/// khai. Viết hai lớp gần như giống hệt nhau chỉ để phân biệt tên nhà cung cấp là trùng lặp vô ích.
/// </summary>
public class S3CompatibleChatFileStorage : IChatFileStorage
{
    private readonly IAmazonS3? _s3;
    private readonly string? _bucket;
    private readonly string? _publicBase;

    public bool Configured => _s3 is not null;
    public string Provider { get; }

    /// <inheritdoc/>
    public string? PublicBase => _publicBase;

    /// <param name="provider">"r2" hoặc "s3" — chỉ để log, không rẽ nhánh hành vi.</param>
    /// <param name="serviceUrl">R2: <c>https://{accountId}.r2.cloudflarestorage.com</c>. S3: null
    /// (dùng <paramref name="region"/> thay).</param>
    public S3CompatibleChatFileStorage(string provider, string? accessKey, string? secretKey,
        string? bucket, string? publicBaseUrl, string? serviceUrl, string? region,
        ILogger log)
    {
        Provider = provider;
        _bucket = bucket;
        _publicBase = publicBaseUrl?.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey)
            || string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(_publicBase))
        {
            // KHÔNG rơi về local — xem lý do ở IChatFileStorage. Chỉ tắt và nói rõ vì sao.
            log.LogWarning("[storage/{P}] thiếu Storage:{P}:* — gửi ảnh/tệp trong chat sẽ tắt", provider, provider.ToUpperInvariant());
            return;
        }

        var cfg = new AmazonS3Config { ForcePathStyle = true };
        if (!string.IsNullOrWhiteSpace(serviceUrl)) cfg.ServiceURL = serviceUrl;
        else if (!string.IsNullOrWhiteSpace(region)) cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        else { log.LogWarning("[storage/{P}] thiếu ServiceURL lẫn Region — tắt", provider); return; }

        _s3 = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), cfg);
    }

    public async Task<string> UploadAsync(string key, Stream noiDung, string contentType, CancellationToken ct)
    {
        if (_s3 is null) throw new InvalidOperationException($"Storage:{Provider} chưa cấu hình");
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = noiDung,
            ContentType = contentType,
            DisablePayloadSigning = true,   // R2 không cần chữ ký payload theo chunk; S3 chấp nhận luôn
        }, ct);
        return $"{_publicBase}/{key}";
    }

    /// <inheritdoc/>
    public async Task<string?> ExistingUrlAsync(string key, CancellationToken ct)
    {
        if (_s3 is null) return null;
        try
        {
            // HEAD chứ không GET: chỉ cần biết CÓ hay không, kéo cả tệp về để trả lời câu hỏi đó
            // là tự chuốc lấy đúng cái chi phí mình đang muốn tránh.
            await _s3.GetObjectMetadataAsync(_bucket, key, ct);
            return $"{_publicBase}/{key}";
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;   // chưa có — đường thường, không phải lỗi
        }
        catch
        {
            // Mạng chập, khoá sai… → coi như chưa có rồi tải lại. Tốn thêm một lượt còn hơn để
            // tin của khách kẹt lại vì một phép kiểm phụ. Không ghi log ở đây: hàm này gọi cho
            // TỪNG tệp của MỌI tin, một trục trặc mạng là log ngập.
            return null;
        }
    }
}
