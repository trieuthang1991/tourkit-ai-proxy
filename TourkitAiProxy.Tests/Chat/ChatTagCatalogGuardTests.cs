using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Chốt chặn cho <b>danh mục nhãn</b> (bảng <c>chat_tag_catalog</c>, thêm 08/09/2026).
///
/// <para>Nhãn có HAI đường vào và chúng phải gặp nhau ở cùng một chỗ:</para>
/// <list type="number">
///   <item><c>POST /chat/tags</c> — quản trị thêm nhãn từ bảng quản lý.</item>
///   <item><c>POST /chat/conversations/{id}/tags</c> — người trực gõ nhãn ngay trên khung chat.</item>
/// </list>
///
/// <para>Cả hai phải sinh slug bằng <b>cùng một</b> hàm <c>ChatRules.NormalizeSlug</c>. Dùng hai
/// cách chuẩn hoá khác nhau thì "Khách VIP" gõ ở khung chat và "Khách VIP" tạo ở bảng quản lý ra
/// hai slug khác nhau — màn hình hiện hai nhãn trông y hệt nhau, lọc theo cái này không ra khách
/// mang cái kia, và không ai nhìn giao diện mà đoán được vì sao.</para>
/// </summary>
public class ChatTagCatalogGuardTests
{
    [Fact]
    public void Hai_duong_tao_nhan_dung_CHUNG_mot_ham_chuan_hoa()
    {
        var src = DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

        // Đường danh mục.
        var dm = CatKhoi(src, "g.MapPost(\"/tags\"");
        Assert.Contains("ChatRules.NormalizeSlug", dm);

        // Đường gắn nhãn cho một hội thoại.
        var ht = CatKhoi(src, "g.MapPost(\"/conversations/{id:long}/tags\"");
        Assert.Contains("ChatRules.NormalizeSlug", ht);

        // Và nhãn gõ tay phải VÀO danh mục — thiếu vế này thì danh mục chỉ biết nhãn tạo từ màn
        // quản lý, còn nhãn người trực nghĩ ra trong lúc làm việc thì nằm ngoài và lần sau không
        // bấm chọn lại được.
        Assert.Contains("UpsertTagAsync", ht);
    }

    [Fact]
    public void Xoa_nhan_phai_go_khoi_moi_khach_dang_mang()
    {
        // Chỉ xoá dòng danh mục thì nhãn vẫn dính trên khách mà không còn tên để hiện — thành một
        // chuỗi lạ mà giao diện không gỡ ra được nữa. Hai lệnh DELETE, không phải một.
        var repo = DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
        var kh = CatKhoi(repo, "public async Task<int?> DeleteTagAsync");

        Assert.Contains("DELETE FROM chat_contact_tags", kh);
        Assert.Contains("DELETE FROM chat_tag_catalog", kh);
    }

    [Fact]
    public void Danh_muc_duoc_nap_tu_nhan_cu_khi_tao_bang()
    {
        // Không có bước nạp thì mọi nhãn đã gắn trước hôm bật tính năng biến mất khỏi thanh chọn:
        // vẫn nằm trong CSDL, vẫn hiện trên khách đang mang, nhưng không gắn cho người tiếp theo
        // được — trông như tính năng làm mất dữ liệu.
        var schema = DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");

        // ⚠️ Neo bằng \b. Không có nó thì "chat_tag_catalog" khớp cả "chat_tag_catalog_KHAC" —
        // đổi tên bảng đích thành bảng khác mà chốt vẫn xanh. Đo thật 08/09/2026: đây đúng là
        // phép phá duy nhất trong bốn phép lọt qua, vì regex bắt trúng phần TIỀN TỐ.
        Assert.Matches(@"CREATE TABLE IF NOT EXISTS chat_tag_catalog\b", schema);
        Assert.Matches(@"INSERT INTO chat_tag_catalog\s*\([\s\S]{0,220}FROM chat_contact_tags\b", schema);
        // Chạy lại nhiều lần phải vô hại — schema này chạy MỖI lần khởi động.
        Assert.Matches(@"INSERT INTO chat_tag_catalog\s*\([\s\S]{0,320}ON CONFLICT[^\n]*DO NOTHING", schema);
    }

    /// <summary>
    /// Cắt từ <paramref name="moc"/> tới mốc cú pháp kế tiếp cùng cấp.
    ///
    /// <para>Cắt theo CÚ PHÁP chứ không đếm số ký tự cố định: cửa sổ cố định đỏ oan ngay khi thân
    /// hàm dài thêm vài dòng, và một chốt hay đỏ oan là một chốt sắp bị tắt.</para>
    /// </summary>
    private static string CatKhoi(string src, string moc)
    {
        var i = src.IndexOf(moc, StringComparison.Ordinal);
        Assert.True(i >= 0, $"Không thấy mốc “{moc}” — mã đã đổi, sửa chốt cho khớp.");
        var sau = src.Substring(i + moc.Length);
        var het = new[] { "\n        g.Map", "\n    public", "\n    private", "\n    /// <summary>" }
            .Select(m => sau.IndexOf(m, StringComparison.Ordinal))
            .Where(x => x > 0)
            .DefaultIfEmpty(sau.Length)
            .Min();
        return sau.Substring(0, het);
    }

    private static string DocFile(string duongDanTuongDoi)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        var f = Path.Combine(d!.FullName, duongDanTuongDoi.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(f), $"Không thấy {duongDanTuongDoi}");
        return File.ReadAllText(f);
    }
}
