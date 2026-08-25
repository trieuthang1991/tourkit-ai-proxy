using TourkitAiProxy.Services.Storage;
using Xunit;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Kho ảnh/tệp cục bộ của chat phải neo vào THƯ MỤC APP, không phải thư mục làm việc của tiến trình.
///
/// <para><b>Lỗi đã xảy ra thật</b> (25/08): cả nơi ghi lẫn nơi phục vụ đều dựng đường dẫn từ
/// <c>Directory.GetCurrentDirectory()</c>. Chạy <c>dotnet run</c> ở gốc repo thì hai bên trùng nhau
/// nên không lộ; nhưng dưới IIS thư mục làm việc thường là <c>C:\Windows\System32</c> — ảnh hoặc ghi
/// ra ngoài app (mất khi deploy lại), hoặc ghi hỏng vì không có quyền. Đường dẫn ảnh đã lưu trong
/// CSDL là vĩnh viễn, nên một lần lệch là ảnh đó 404 mãi mãi.</para>
/// </summary>
public class LocalChatFileStorageTests
{
    private const string Goc = @"C:\app";

    [Fact]
    public void Khong_khai_gi_thi_neo_vao_thu_muc_app()
    {
        var d = LocalChatFileStorage.ThuMucGoc(null, Goc);
        Assert.True(Path.IsPathRooted(d), $"'{d}' không phải đường dẫn tuyệt đối");
        Assert.StartsWith(Goc, d, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chat-uploads", d);
    }

    [Fact]
    public void Khai_duong_dan_tuong_doi_thi_van_neo_vao_thu_muc_app()
    {
        // "kho/anh" là tương đối — phải hiểu là "cạnh app", không phải "cạnh chỗ tiến trình đang chạy".
        var d = LocalChatFileStorage.ThuMucGoc(Path.Combine("kho", "anh"), Goc);
        Assert.Equal(Path.Combine(Goc, "kho", "anh"), d);
    }

    [Fact]
    public void Khai_duong_dan_tuyet_doi_thi_giu_nguyen()
    {
        // Người vận hành trỏ sang ổ dữ liệu riêng — không được ghép thêm gì vào.
        var rieng = @"D:\du-lieu\chat";
        Assert.Equal(rieng, LocalChatFileStorage.ThuMucGoc(rieng, Goc));
    }

    [Fact]
    public void Chuoi_rong_tinh_nhu_khong_khai()
    {
        Assert.Equal(LocalChatFileStorage.ThuMucGoc(null, Goc), LocalChatFileStorage.ThuMucGoc("   ", Goc));
    }
}
