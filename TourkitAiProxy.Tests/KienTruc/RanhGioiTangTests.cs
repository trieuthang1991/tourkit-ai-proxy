using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.KienTruc;

/// <summary>
/// Canh ranh giới tầng ở mức MÃ NGUỒN — phần biên dịch viên không thấy.
///
/// <para><b>Vì sao cần lớp này.</b> Luật phụ thuộc giữa các project đã do biên dịch viên ép: Shared
/// không tham chiếu gì, Domain chỉ tham chiếu Shared, Application không tham chiếu Infrastructure.
/// Nhưng có ba thứ biên dịch viên KHÔNG thấy được, mà cả ba đều đã hỏng thật ít nhất một lần:</para>
///
/// <list type="number">
/// <item>"Shared không biết gì về nghiệp vụ" — biên dịch được nhưng sai ý nghĩa;</item>
/// <item>"endpoint không mở kết nối CSDL" — hiện có đúng 1 chỗ lọt;</item>
/// <item>"không để file .cs nằm trần ở gốc thư mục" — hiện có 7 file không nhà.</item>
/// </list>
///
/// <para>Quy ước bằng lời không giữ được: CLAUDE.md hơn 700 dòng, mà ngày 25/08/2026 vẫn có người
/// vi phạm quy ước đặt tên vài giờ sau khi đọc nó. Test thì không quên.</para>
/// </summary>
public class RanhGioiTangTests
{
    // ── Shared: tiện ích, KHÔNG phải nghiệp vụ ───────────────────────────────

    /// <summary>
    /// Danh từ nghiệp vụ TourKit. Xuất hiện trong Shared nghĩa là file đó đặt nhầm nhà —
    /// nó thuộc Domain (luật nghiệp vụ) chứ không phải tiện ích dùng chung.
    /// </summary>
    private static readonly string[] DanhTuNghiepVu =
    {
        "Tour", "Chat", "Mail", "Deal", "Visa", "Digest", "Ncc", "Khach", "Booking", "Crm",
    };

    [Fact]
    public void Shared_khong_duoc_biet_gi_ve_nghiep_vu()
    {
        // Đây là LUẬT KẾT NẠP của Shared. Không có nó, một project tên "Shared"/"Common"/"Helper"
        // sẽ thành Services/ thứ hai — và nhanh hơn, vì cái tên đã mời gọi sẵn "chưa biết để đâu
        // thì bỏ vào đây".
        var pham = new List<string>();
        foreach (var f in DocFileCs("TourkitAiProxy.Shared"))
        {
            var noiDung = ChiLayCode(File.ReadAllText(f));
            var ten = Path.GetFileName(f);
            foreach (var dt in DanhTuNghiepVu)
                if (ten.Contains(dt, StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(noiDung, $@"\b{dt}[A-Za-z]*\b"))
                    pham.Add($"{ten} — nhắc tới \"{dt}\"");
        }

        Assert.True(pham.Count == 0,
            "Shared chỉ chứa tiện ích KHÔNG biết gì về nghiệp vụ TourKit. Các chỗ sau vi phạm "
            + "(chuyển sang TourkitAiProxy.Domain):\n  " + string.Join("\n  ", pham.Distinct()));
    }

    [Fact]
    public void Shared_khong_duoc_tham_chieu_bat_cu_gi()
    {
        // Nửa đầu luật kết nạp do biên dịch viên ép — nhưng chỉ khi .csproj còn sạch.
        // Thêm một PackageReference vào đây là đã phá ranh giới mà không ai báo.
        var csproj = Regex.Replace(File.ReadAllText(Goc("TourkitAiProxy.Shared/TourkitAiProxy.Shared.csproj")),
            @"<!--.*?-->", " ", RegexOptions.Singleline);
        Assert.DoesNotContain("PackageReference", csproj);
        Assert.DoesNotContain("ProjectReference", csproj);
    }

    // ── Domain: luật thuần, không chạm I/O ───────────────────────────────────

    [Fact]
    public void Domain_khong_duoc_cham_CSDL_hay_mang()
    {
        // Domain phải chạy được trong mili giây, không dựng gì. Đó là lý do nó tách khỏi
        // Application — gộp lại là kéo phụ thuộc CSDL/mạng sang phần thuần.
        var cam = new[] { "Dapper", "SqlConnection", "NpgsqlConnection", "HttpClient", "IHttpClientFactory" };
        var pham = new List<string>();
        foreach (var f in DocFileCs("TourkitAiProxy.Domain"))
        {
            var noiDung = ChiLayCode(File.ReadAllText(f));
            foreach (var c in cam)
                if (noiDung.Contains(c, StringComparison.Ordinal))
                    pham.Add($"{Path.GetFileName(f)} — dùng {c}");
        }

        Assert.True(pham.Count == 0,
            "Domain là luật THUẦN. Các chỗ sau chạm I/O (chuyển sang Application hoặc "
            + "Infrastructure):\n  " + string.Join("\n  ", pham));
    }

    // ── Endpoint không mở kết nối CSDL ───────────────────────────────────────

    /// <summary>
    /// Endpoint chỉ được gọi service/repository, không tự mở kết nối.
    ///
    /// <para><b>Danh sách miễn trừ là NỢ, không phải ngoại lệ vĩnh viễn.</b> Ghi tên ở đây để test
    /// vẫn xanh trong lúc chưa dọn, nhưng mỗi dòng là một việc phải làm. Thêm tên mới vào đây thì
    /// phải kèm lý do — nếu không, luật này chết dần đúng kiểu quy ước bằng lời.</para>
    /// </summary>
    private static readonly string[] MienTru = { "WorkflowEndpoints.cs" };

    [Fact]
    public void Endpoint_khong_duoc_mo_ket_noi_CSDL()
    {
        var cam = new[] { "using Dapper", "new SqlConnection", "new NpgsqlConnection" };
        var pham = new List<string>();
        foreach (var f in DocFileCs("TourkitAiProxy.Endpoints"))
        {
            var ten = Path.GetFileName(f);
            if (MienTru.Contains(ten)) continue;
            var noiDung = ChiLayCode(File.ReadAllText(f));
            foreach (var c in cam)
                if (noiDung.Contains(c, StringComparison.Ordinal))
                    pham.Add($"{ten} — {c}");
        }

        Assert.True(pham.Count == 0,
            "Endpoint không được tự mở kết nối CSDL — gọi repository. Vi phạm:\n  "
            + string.Join("\n  ", pham));
    }

    // ── Services: nghiệp vụ, KHÔNG tự mở kết nối CSDL ────────────────────────

    /// <summary>
    /// File còn chạm CSDL trong Services. <b>Đây là NỢ, không phải ngoại lệ.</b>
    ///
    /// <para><c>SaleBriefWorkflow</c> có ba câu SQL viết thẳng trong luồng workflow. Tách ra cần
    /// sửa mã có logic thật, mà repo chưa có test tích hợp chạm CSDL — nên ghi nợ ở đây thay vì
    /// làm liều. Thêm tên mới vào danh sách này thì phải kèm lý do; không thì luật chết dần đúng
    /// kiểu quy ước bằng lời.</para>
    /// </summary>
    private static readonly string[] ServicesConChamDb = { "SaleBriefWorkflow.cs" };

    [Fact]
    public void Nghiep_vu_khong_tu_mo_ket_noi_CSDL()
    {
        // Sau khi tách Infrastructure, mọi truy cập CSDL phải nằm ở đó. Kiểm ở mức mã nguồn vì
        // biên dịch viên KHÔNG chặn được: Services có tham chiếu Infrastructure nên vẫn "with" tới
        // Dapper qua đường transitive.
        var cam = new[] { "using Dapper", "new SqlConnection", "new NpgsqlConnection" };
        var pham = new List<string>();
        foreach (var f in DocFileCs("TourkitAiProxy.Services"))
        {
            var ten = Path.GetFileName(f);
            if (ServicesConChamDb.Contains(ten)) continue;
            var noiDung = ChiLayCode(File.ReadAllText(f));
            foreach (var c in cam)
                if (noiDung.Contains(c, StringComparison.Ordinal))
                    pham.Add($"{ten} — {c}");
        }

        Assert.True(pham.Count == 0,
            "Truy cập CSDL thuộc TourkitAiProxy.Infrastructure. Các chỗ sau vi phạm:\n  "
            + string.Join("\n  ", pham));
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static string Goc(string duongDanTuongDoi)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, duongDanTuongDoi.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Bỏ chú thích, dòng <c>using</c> và <c>namespace</c> trước khi soi.
    ///
    /// <para><b>Vì sao bắt buộc.</b> Bản đầu của mấy test này soi thẳng văn bản thô và <b>cả ba đều
    /// báo nhầm ngay lần chạy đầu</b>: "Tour" khớp vào chính <c>namespace TourkitAiProxy…</c>, còn
    /// "Dapper" nằm trong một câu chú thích giải thích vì sao phải <c>SpecifyKind</c>. Guard hay
    /// kêu oan thì sớm muộn có người tắt nó đi — lúc đó nó tệ hơn là không có, vì vẫn tạo cảm giác
    /// đang được canh.</para>
    /// </summary>
    private static string ChiLayCode(string noiDung)
    {
        var s = Regex.Replace(noiDung, @"/\*.*?\*/", " ", RegexOptions.Singleline);   // /* … */
        var dong = s.Split('\n')
            .Select(d => Regex.Replace(d, @"//.*$", ""))                              // // …
            .Where(d => !d.TrimStart().StartsWith("using ")
                     && !d.TrimStart().StartsWith("namespace "));
        return string.Join("\n", dong);
    }

    private static IEnumerable<string> DocFileCs(string thuMuc)
    {
        var d = Goc(thuMuc);
        if (!Directory.Exists(d)) return Array.Empty<string>();
        return Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }
}
