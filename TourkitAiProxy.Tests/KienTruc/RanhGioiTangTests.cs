using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.KienTruc;

/// <summary>
/// Canh ranh giới tầng ở mức MÃ NGUỒN — phần biên dịch viên không thấy.
///
/// <para><b>Vì sao cần lớp này.</b> Luật phụ thuộc giữa các project đã do biên dịch viên ép: Shared
/// không tham chiếu gì, Domain chỉ tham chiếu Shared, Endpoints không tham chiếu Infrastructure
/// trực tiếp. Nhưng có những thứ biên dịch viên KHÔNG thấy được, và mỗi cái ở đây đều đã hỏng thật
/// ít nhất một lần:</para>
///
/// <list type="number">
/// <item>"Shared không biết gì về nghiệp vụ" — biên dịch được nhưng sai ý nghĩa;</item>
/// <item>"Domain là luật thuần" — kéo Dapper/HttpClient vào vẫn biên dịch ngon;</item>
/// <item>"chỉ Infrastructure mở kết nối CSDL" — Services thấy Dapper qua đường bắc cầu;</item>
/// <item>"namespace nói đúng tầng" — sai thì không có triệu chứng nào, chỉ gây hiểu nhầm.</item>
/// </list>
///
/// <para><b>Cả hai danh sách miễn trừ nay đều RỖNG</b> (25/08/2026). Giữ chúng lại làm chỗ ghi nợ
/// tạm thời cho lần sau, nhưng rỗng mới là trạng thái đúng: một luật có sẵn cửa thoát thì cửa đó
/// sớm muộn thành lối đi chính.</para>
///
/// <para>Quy ước bằng lời không giữ được. Ngày 25/08/2026 <c>CLAUDE.md</c> đã dài 1.086 dòng —
/// và chính hôm đó có người vi phạm quy ước đặt tên <b>vài giờ sau khi đọc nó</b>. File ấy nay đã
/// tách nhỏ ra <c>docs/</c>, nhưng tách chỉ làm quy ước DỄ ĐỌC hơn chứ không làm nó tự giữ mình:
/// đọc rồi vẫn quên. Test thì không quên.</para>
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
    /// <para><b>Danh sách miễn trừ nay RỖNG.</b> Từng có đúng một tên — <c>WorkflowEndpoints.cs</c>
    /// — nhưng hoá ra nó đã hết chạm CSDL từ lâu: thứ duy nhất còn nhắc "Dapper" trong file là một
    /// câu chú thích giải thích vì sao phải <c>SpecifyKind</c>. Miễn trừ ấy sống sót thêm một thời
    /// gian chỉ vì bản đầu của guard soi văn bản thô nên khớp cả chú thích; vá guard xong thì không
    /// ai quay lại xem danh sách còn cần nữa không.</para>
    ///
    /// <para>⚠️ Đó là cách một danh sách miễn trừ gây hại kể cả khi nội dung của nó đã sai: nó nói
    /// "chỗ này còn nợ" trong khi nợ đã hết, nên người đọc sau tưởng đây là ngoại lệ được phép và
    /// yên tâm thêm tên thứ hai. Thêm tên vào đây phải kèm lý do VÀ hạn trả.</para>
    /// </summary>
    private static readonly string[] MienTru = System.Array.Empty<string>();

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
    /// File còn chạm CSDL trong Services. <b>Danh sách này phải RỖNG.</b>
    ///
    /// <para>Từng có đúng một tên: <c>SaleBriefWorkflow</c> với ba câu SQL viết thẳng trong luồng
    /// workflow. Nợ đó đã trả (25/08/2026) — ba truy vấn nay ở
    /// <c>Infrastructure/Digest/SaleBriefRepository</c>.</para>
    ///
    /// <para>⚠️ Thêm tên vào đây phải kèm lý do VÀ hạn trả. Một danh sách miễn trừ không ai dọn
    /// chính là cách một luật chết dần mà vẫn trông như đang sống.</para>
    /// </summary>
    private static readonly string[] ServicesConChamDb = System.Array.Empty<string>();

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

    // ── Namespace phải nói đúng tầng ─────────────────────────────────────────

    /// <summary>
    /// Namespace của một file phải bắt đầu bằng tên project chứa nó.
    ///
    /// <para><b>Vì sao đáng canh.</b> Đợt tách kiến trúc cố ý GIỮ NGUYÊN namespace lúc chuyển file,
    /// để khỏi phải sửa <c>using</c> ở hơn 250 chỗ. Cái giá: 73 file nói dối về tầng của mình —
    /// <c>TourkitAiProxy.Services.Chat.Inbox.ChatRepository</c> thật ra nằm ở <c>Infrastructure</c>.
    /// Nợ đó đã trả ngày 25/08/2026; test này giữ cho nó không quay lại.</para>
    ///
    /// <para>Cái tạm bợ ấy tự lớn thêm mỗi lần thêm file: người viết sau nhìn hàng xóm rồi chép
    /// theo. Chặn ở đây thì lần chép đầu tiên đã đỏ.</para>
    /// </summary>
    [Fact]
    public void Namespace_phai_khop_project()
    {
        string[] projects =
        {
            "TourkitAiProxy.Shared", "TourkitAiProxy.Domain", "TourkitAiProxy.Infrastructure",
            "TourkitAiProxy.Services", "TourkitAiProxy.Endpoints", "TourkitAiProxy.Tests",
        };

        var pham = new List<string>();
        foreach (var prj in projects)
            foreach (var f in DocFileCs(prj))
            {
                var m = Regex.Match(File.ReadAllText(f), @"^namespace\s+([\w.]+)\s*;", RegexOptions.Multiline);
                if (m.Success && !m.Groups[1].Value.StartsWith(prj, StringComparison.Ordinal))
                    pham.Add($"{prj}/{Path.GetFileName(f)} — namespace {m.Groups[1].Value}");
            }

        Assert.True(pham.Count == 0,
            "Namespace phải bắt đầu bằng tên project chứa file. Vi phạm:\n  "
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
