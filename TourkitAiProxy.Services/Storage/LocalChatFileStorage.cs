using TourkitAiProxy.Domain.Chat;
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

    /// <inheritdoc/>
    /// <remarks>Tương đối, khớp đúng chuỗi mà <see cref="UploadAsync"/> trả về.</remarks>
    public string? PublicBase => "/chat-files/";

    /// <summary>
    /// Dựng đường dẫn tuyệt đối của kho, neo vào THƯ MỤC APP.
    ///
    /// <para><b>Phải là một nguồn</b>: nơi GHI (lớp này) và nơi PHỤC VỤ <c>/chat-files</c>
    /// (<c>Program.cs</c>) buộc phải ra cùng một thư mục. Hai bên tự dựng riêng thì lệch nhau lúc
    /// nào không biết, mà triệu chứng chỉ là ảnh 404 — không lỗi nào hiện lên.</para>
    ///
    /// <para><b>KHÔNG dùng <c>Directory.GetCurrentDirectory()</c></b>: thư mục làm việc của tiến
    /// trình không phải thư mục app. Chạy <c>dotnet run</c> ở gốc repo thì tình cờ trùng nên không
    /// lộ, nhưng dưới IIS nó thường là <c>C:\Windows\System32</c> — ảnh ghi ra ngoài app rồi mất
    /// khi deploy lại, hoặc ghi hỏng vì không có quyền. Đường dẫn ảnh lưu trong CSDL là vĩnh viễn
    /// nên một lần lệch là ảnh đó 404 mãi.</para>
    /// </summary>
    /// <param name="dir">Khai ở <c>Storage:Local:Dir</c>. Bỏ trống = <c>data/chat-uploads</c>.
    /// Đường dẫn tương đối hiểu là "cạnh app"; tuyệt đối thì giữ nguyên (trỏ sang ổ dữ liệu riêng).</param>
    /// <param name="contentRoot">Thư mục app — <c>IHostEnvironment.ContentRootPath</c>.</param>
    public static string ThuMucGoc(string? dir, string contentRoot)
    {
        var d = string.IsNullOrWhiteSpace(dir) ? Path.Combine("data", "chat-uploads") : dir.Trim();
        return Path.IsPathRooted(d) ? d : Path.Combine(contentRoot, d);
    }

    /// <param name="dir">Xem <see cref="ThuMucGoc"/>.</param>
    /// <param name="contentRoot">Thư mục app — <c>IHostEnvironment.ContentRootPath</c>.</param>
    public LocalChatFileStorage(string? dir, string contentRoot)
    {
        _dir = ThuMucGoc(dir, contentRoot);
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

    /// <inheritdoc/>
    public Task<string?> ExistingUrlAsync(string key, CancellationToken ct)
    {
        var duong = Path.Combine(_dir, key.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult<string?>(File.Exists(duong) ? "/chat-files/" + key : null);
    }
}
