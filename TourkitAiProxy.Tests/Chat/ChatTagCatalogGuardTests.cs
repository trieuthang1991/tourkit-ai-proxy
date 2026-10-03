using System.Collections.Generic;
using System.Linq;
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
    /// Nhãn của công ty này KHÔNG được lọt sang công ty khác.
    ///
    /// <para>Một câu lệnh quên <c>tenant_id</c> ở đây không gây lỗi, không gây chậm, và không
    /// hiện ra ở đâu cả: nó chỉ lặng lẽ trộn danh mục nhãn của mọi công ty vào một chỗ. Người
    /// dùng thấy nhãn lạ trong danh sách của mình, còn xoá một nhãn thì gỡ nhãn khỏi khách của
    /// công ty khác. Kiểu lỗi này chỉ lộ khi đã ra tới khách hàng.</para>
    /// </summary>
    [Fact]
    public void Moi_cau_lenh_cham_bang_nhan_deu_phai_kep_theo_cong_ty()
    {
        var kho = BoChuThich(DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs"));

        // MỘT ngoại lệ, và nó phải được khai ra ở đây chứ không được nới lỏng cả bài canh:
        // DeleteContactDataAsync (Meta Data Deletion Callback) CỐ Ý xoá xuyên công ty, vì Meta chỉ
        // gửi sang mã người dùng chứ không nói người đó từng nhắn cho công ty nào — mà một người
        // có thể đã nhắn cho hai công ty cùng dùng hệ này. Kẹp công ty ở đó là xoá thiếu, và lời
        // hứa "đã xoá" thành lời nói dối.
        kho = BoNgoaiLe(kho, "public async Task<KetQuaXoa> DeleteContactDataAsync");

        // Soi vào CHÍNH chuỗi SQL, không soi cửa sổ ký tự quanh nó.
        //
        // ⚠️ Bản đầu của chốt này lấy 400 ký tự quanh mỗi câu lệnh rồi tìm chữ "tenant" — và nó
        // XANH cả khi đã bỏ hẳn mệnh đề kẹp, vì tên tham số `string tenant` cùng `new { tenant }`
        // nằm ngay đó. Thử phá mới lộ ra. Một chốt xanh nhầm còn tệ hơn không có chốt: nó khiến
        // người sau tin rằng chỗ này đã được canh.
        var sql = ChuoiSql(kho)
            .Where(q => q.Contains("chat_tag_catalog", StringComparison.Ordinal)
                     || q.Contains("chat_contact_tags", StringComparison.Ordinal))
            .ToList();

        // Canh chính bài canh: phải THẤY các câu lệnh, không thì bài này xanh vì quét trượt.
        Assert.True(sql.Count >= 4,
            $"Chỉ thấy {sql.Count} câu SQL chạm bảng nhãn — bài canh đang soi nhầm chỗ.");

        foreach (var q in sql)
        {
            // Đòi ĐÚNG MỆNH ĐỀ KẸP, không chỉ đòi có chữ "tenant_id" đâu đó trong câu.
            //
            // ⚠️ Bản trước chỉ tìm chữ, và nó XANH khi đã bỏ hẳn mệnh đề WHERE — vì câu liệt kê
            // còn một dòng JOIN "t.tenant_id = d.tenant_id" đủ làm chữ đó xuất hiện. Chỉ lộ ra khi
            // thử phá đúng câu đó; chạy suông thì trông như đã được canh.
            var kep = Regex.IsMatch(q, @"tenant_id\s*=\s*@tenant")
                   || (Regex.IsMatch(q, @"INSERT\s+INTO", RegexOptions.IgnoreCase)
                       && Regex.IsMatch(q, @"\(\s*tenant_id\s*,")
                       && q.Contains("@tenant", StringComparison.Ordinal));
            Assert.True(kep,
                "Có câu SQL chạm bảng nhãn mà KHÔNG kẹp tenant_id = @tenant:\n" + q.Trim());
        }
    }

    /// <summary>
    /// Công ty phải lấy từ PHIÊN đăng nhập, không lấy từ thân yêu cầu hay tham số đường dẫn.
    ///
    /// <para>Kẹp đúng ở tầng SQL mà lại nhận mã công ty do người gọi đưa lên thì việc kẹp thành
    /// vô nghĩa: ai cũng tự khai mình thuộc công ty nào.</para>
    /// </summary>
    [Fact]
    public void Duong_nhan_lay_cong_ty_tu_phien_dang_nhap()
    {
        var src = BoChuThich(DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs"));

        foreach (var moc in new[] { "MapGet(\"/tags\"", "MapPost(\"/tags\"", "MapDelete(\"/tags/{id:long}\"" })
        {
            var than = CatKhoi(src, moc);
            Assert.Contains("SessionAuth.Read(ctx, sessions)", than);
            Assert.Contains("a.TenantId", than);
            Assert.DoesNotContain("body.TenantId", than);
            Assert.DoesNotContain("body?.TenantId", than);
        }
    }

    /// <summary>
    /// Cắt HẲN một phương thức ra khỏi phần được soi, và bắt nó phải TỒN TẠI.
    ///
    /// <para>Ngoại lệ phải gọi đúng tên. Đổi cách viết thành "bỏ qua câu nào có chữ Meta" thì
    /// bất kỳ câu lệnh mới nào lỡ nhắc tới Meta cũng được tha — ngoại lệ nới ra theo thời gian
    /// mà không ai quyết định điều đó.</para>
    /// </summary>
    private static string BoNgoaiLe(string src, string chuKy)
    {
        var i = src.IndexOf(chuKy, StringComparison.Ordinal);
        Assert.True(i > 0,
            $"Không thấy “{chuKy}” — ngoại lệ đã đổi tên hoặc biến mất. Xem lại chốt, đừng xoá nó.");
        var sau = src.Substring(i + chuKy.Length);
        var het = new[] { "\n    public", "\n    private", "\n    /// <summary>" }
            .Select(m => sau.IndexOf(m, StringComparison.Ordinal))
            .Where(x => x > 0)
            .DefaultIfEmpty(sau.Length)
            .Min();
        return src.Substring(0, i) + sau.Substring(het);
    }

    /// <summary>
    /// Tách mọi chuỗi SQL trong mã nguồn: cả chuỗi thô nhiều dòng (<c>"""…"""</c>) lẫn chuỗi
    /// một dòng. Trả về nội dung BÊN TRONG chuỗi, để chốt soi đúng câu lệnh chứ không soi
    /// mã C# quanh nó.
    /// </summary>
    private static List<string> ChuoiSql(string src)
    {
        var ra = new List<string>();
        foreach (Match m in Regex.Matches(src, "\"\"\"([\\s\\S]*?)\"\"\""))
            ra.Add(m.Groups[1].Value);
        // Bỏ phần chuỗi thô rồi mới quét chuỗi một dòng, tránh đếm trùng.
        var conLai = Regex.Replace(src, "\"\"\"([\\s\\S]*?)\"\"\"", "");
        foreach (Match m in Regex.Matches(conLai, "\"([^\"\\n]{12,})\""))
            ra.Add(m.Groups[1].Value);
        return ra;
    }

    /// Bỏ dòng chú thích trước khi soi — chú thích quanh đây có nhắc tên bảng và mệnh đề SQL,
    /// để nguyên thì bài canh đếm phải chữ của chính nó.
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                 && !d.TrimStart().StartsWith("*", StringComparison.Ordinal)
                                 && !d.TrimStart().StartsWith("--", StringComparison.Ordinal)));

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
