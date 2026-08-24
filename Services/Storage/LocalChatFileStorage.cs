// Services/Storage/LocalChatFileStorage.cs
namespace TourkitAiProxy.Services.Storage;

/// <summary>
/// Lưu vào đĩa máy chủ, phục vụ qua static files. LUÔN "đã cấu hình" — không cần tài khoản
/// cloud nào, đây là đường mặc định để tính năng gửi ảnh chạy được ngay cả khi chưa ai khai R2/S3.
///
/// <para>Cùng thư mục dữ liệu runtime với Visa (<c>data/visa-files/</c>) — xem quy ước đó trong
/// CLAUDE.md, đây là kho thứ hai theo cùng nếp: mỗi tenant một thư mục con, tên tệp có GUID để
/// tránh đụng nhau, KHÔNG cần đăng ký gì thêm để chạy dev/VPS tự quản.</para>
///
/// <para><b>KHÔNG hợp cho nhiều instance đứng sau load-balancer</b> — mỗi instance có đĩa riêng,
/// ảnh tải lên instance A sẽ 404 khi instance B phục vụ request đọc. Chỉ dùng khi chạy 1 tiến
/// trình web, hoặc đĩa dùng chung (network share). R2/S3 không có giới hạn này.</para>
/// </summary>
public class LocalChatFileStorage : IChatFileStorage
{
    private readonly string _dir;

    public bool Configured => true;
    public string Provider => "local";

    /// <param name="dir">Thư mục gốc, mặc định <c>data/chat-uploads</c> cạnh nơi app chạy.</param>
    public LocalChatFileStorage(string? dir)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? Path.Combine("data", "chat-uploads") : dir;
        Directory.CreateDirectory(_dir);
    }

    public async Task<string> UploadAsync(string key, Stream noiDung, string contentType, CancellationToken ct)
    {
        // key dạng "chat/{tenant}/{conv}/{guid}-{ten}" — giữ nguyên cấu trúc thư mục con để dễ dọn
        // theo tenant sau này, cùng quy ước với R2/S3.
        var duong = Path.Combine(_dir, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(duong)!);
        await using var f = File.Create(duong);
        await noiDung.CopyToAsync(f, ct);
        // Đường dẫn TƯƠNG ĐỐI — endpoint gọi hàm này tự thêm scheme+host, xem IChatFileStorage.
        return "/chat-files/" + key;
    }
}
