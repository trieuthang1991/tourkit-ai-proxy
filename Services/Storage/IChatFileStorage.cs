// Services/Storage/IChatFileStorage.cs
namespace TourkitAiProxy.Services.Storage;

/// <summary>
/// Kho tệp cho ảnh/tệp nhân viên gửi trong Hộp thư chat — ba cách lưu, chọn qua
/// <c>Storage:Provider</c> (<c>r2</c> | <c>s3</c> | <c>local</c>, mặc định <c>local</c>).
///
/// <para><b>Vì sao cần chọn được, không chốt cứng một nơi:</b> R2 rẻ và không tính phí egress
/// (hợp cho ảnh Zalo/Telegram tải đi tải lại nhiều lần), S3 hợp khi hạ tầng công ty đã có sẵn
/// account AWS, và local KHÔNG CẦN TÀI KHOẢN CLOUD NÀO — chạy được ngay trên máy dev hoặc VPS tự
/// quản, đúng kiểu "không cấu hình gì cũng chạy" mà cả app này theo đuổi (xem cách Digest/Mail
/// đều có đường an toàn khi thiếu cấu hình).</para>
///
/// <para><b>KHÔNG rơi ngầm giữa các nhà cung cấp.</b> Admin chọn <c>r2</c> mà thiếu khoá thì tính
/// năng TẮT hẳn (báo rõ lý do), không tự động lùi về <c>local</c> — lùi ngầm nghĩa là ảnh tưởng
/// lưu cloud hoá ra nằm trên đĩa máy chủ, đầy đĩa hoặc mất máy là mất ảnh mà không ai biết.</para>
/// </summary>
public interface IChatFileStorage
{
    /// Đã đủ cấu hình để dùng chưa. False thì endpoint upload trả lỗi rõ ràng, không thử gọi.
    bool Configured { get; }

    /// Tên nhà cung cấp đang chọn — chỉ để hiện trong log/chẩn đoán.
    string Provider { get; }

    /// <summary>
    /// Tải một tệp lên. Trả về URL — TUYỆT ĐỐI (đã có scheme+host) với r2/s3; TƯƠNG ĐỐI
    /// (bắt đầu bằng "/") với local, vì kho local không tự biết tên miền công khai của máy chủ.
    /// Nơi gọi (endpoint, có <c>HttpContext</c>) phải tự thêm scheme+host khi thấy URL tương đối —
    /// giống hệt cách <c>webhookUrl</c> của Hộp thư chat đã làm.
    /// </summary>
    Task<string> UploadAsync(string key, Stream noiDung, string contentType, CancellationToken ct);
}
