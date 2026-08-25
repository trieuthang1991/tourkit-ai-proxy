using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.KienTruc;

/// <summary>
/// Canh bộ tài liệu — cùng lý do với <see cref="RanhGioiTangTests"/>: thứ gì không ai kiểm thì
/// hỏng trong im lặng.
///
/// <para><b>Vì sao có lớp này.</b> Ngày 25/08/2026 <c>CLAUDE.md</c> đã phình tới 1.086 dòng. Nó
/// không phình một lần — mỗi đợt tính năng thêm vài chục dòng, mỗi lần đều hợp lý, và không lần nào
/// có ai nói "khoan đã". Tách nhỏ ra <c>docs/</c> chỉ chữa được HÔM NAY; không có gì cản thì cùng
/// một đường cong lặp lại. Test này là cái cản đó.</para>
///
/// <para>Kèm theo: mọi liên kết trong tài liệu phải trỏ tới file có thật. Đợt tách kiến trúc làm
/// <b>117 liên kết</b> chỉ sai đường một cách âm thầm — tài liệu vẫn mở ra đọc được, chỉ là bấm vào
/// thì không tới đâu. Tài liệu chỉ sai một nửa còn nguy hơn tài liệu thiếu: người đọc tin nó.</para>
/// </summary>
public class TaiLieuTests
{
    /// <summary>
    /// Trần dòng cho phần NGƯỜI viết trong <c>CLAUDE.md</c> (không tính khối do codegraph tự quản).
    ///
    /// <para>Con số 120 không thiêng — nó chỉ cần đủ chật để "thêm một mục nữa thôi" phải dừng lại
    /// và tự hỏi mục đó thuộc file nào trong <c>docs/</c>. Nâng trần thì phải kèm lý do vì sao nội
    /// dung mới KHÔNG thuộc về một file docs nào; "tiện tay để đây" không phải lý do.</para>
    /// </summary>
    private const int TranDong = 120;

    [Fact]
    public void CLAUDE_md_phai_ngan_va_chi_dinh_tuyen()
    {
        var noiDung = File.ReadAllText(Goc("CLAUDE.md"));

        // Khối codegraph do công cụ ngoài sinh ra và ghi đè theo cặp marker — không tính vào trần,
        // và cũng KHÔNG được tách sang file khác (lần nâng cấp sau nó tìm marker trong chính file này).
        var nguoiViet = noiDung;
        var moc = noiDung.IndexOf("<!-- codegraph:start -->", StringComparison.Ordinal);
        if (moc >= 0) nguoiViet = noiDung[..moc];

        var soDong = nguoiViet.TrimEnd().Split('\n').Length;
        Assert.True(soDong <= TranDong,
            $"CLAUDE.md phần người viết đang {soDong} dòng, trần là {TranDong}. Nội dung mới thuộc "
            + "về một file trong docs/ — CLAUDE.md chỉ là bảng định tuyến. Xem docs/ARCHITECTURE.md "
            + "nếu chưa rõ nội dung đó về đâu.");
    }

    [Fact]
    public void Bang_dinh_tuyen_khong_duoc_tro_vao_khoang_khong()
    {
        // Bảng định tuyến là thứ DUY NHẤT còn lại trong CLAUDE.md. Một dòng trỏ sai ở đây nghĩa là
        // cả một cụm kiến thức biến mất khỏi tầm với, mà không có triệu chứng nào.
        var thieu = LienKetHong("CLAUDE.md").ToList();
        Assert.True(thieu.Count == 0,
            "CLAUDE.md trỏ tới file không tồn tại:\n  " + string.Join("\n  ", thieu));
    }

    [Fact]
    public void Tai_lieu_khong_duoc_co_lien_ket_chet()
    {
        var thieu = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Goc("docs"), "*.md", SearchOption.AllDirectories))
        {
            var tuongDoi = Path.GetRelativePath(Goc("."), f).Replace('\\', '/');
            thieu.AddRange(LienKetHong(tuongDoi).Select(d => $"{tuongDoi} → {d}"));
        }

        Assert.True(thieu.Count == 0,
            $"{thieu.Count} liên kết trong docs/ trỏ tới file không tồn tại:\n  "
            + string.Join("\n  ", thieu));
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    /// <summary>Liệt kê link tương đối trỏ tới file mã nguồn/tài liệu KHÔNG tồn tại.</summary>
    private static IEnumerable<string> LienKetHong(string duongDanFile)
    {
        var thuMuc = Path.GetDirectoryName(Goc(duongDanFile))!;
        var noiDung = File.ReadAllText(Goc(duongDanFile));

        // Chỉ soi link tới file THẬT trong repo. Bỏ qua http(s), mailto, neo #… — chúng không
        // kiểm được ở đây, và bắt guard làm việc nó không làm nổi là cách nhanh nhất để nó bị tắt.
        var gocRepo = Goc(".");
        foreach (Match m in Regex.Matches(noiDung, @"\]\(([^)#:\s]+?\.(?:cs|jsx?|ps1|json|md|config|csproj))\)"))
        {
            var d = m.Groups[1].Value;
            var thuc = Path.GetFullPath(Path.Combine(thuMuc, d.Replace('/', Path.DirectorySeparatorChar)));

            // Link trỏ RA NGOÀI repo (vd sang `toutkit-app/`) thì bỏ qua: máy khác đặt repo anh em
            // ở chỗ khác, hoặc không có nó. Bắt lỗi ở đây là guard kêu oan theo từng máy — mà guard
            // kêu oan thì sớm muộn bị tắt, kéo theo cả phần nó canh đúng.
            if (!thuc.StartsWith(gocRepo, StringComparison.OrdinalIgnoreCase)) continue;

            if (!File.Exists(thuc)) yield return d;
        }
    }

    private static string Goc(string duongDanTuongDoi)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        return Path.GetFullPath(Path.Combine(d!.FullName,
            duongDanTuongDoi.Replace('/', Path.DirectorySeparatorChar)));
    }
}
