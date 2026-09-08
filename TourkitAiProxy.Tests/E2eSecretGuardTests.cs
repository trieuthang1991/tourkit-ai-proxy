using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Không mã phiên nào được ghim cứng trong bộ E2E.
///
/// <para><b>Đã xảy ra BA lần, và lần thứ ba là lần vừa rồi.</b> Mã phiên TourKit là khoá truy cập
/// dữ liệu công ty thật. Ghim nó vào file spec là hai cái sai cùng lúc:</para>
/// <list type="number">
///   <item>Phiên hết hạn thì bài không đỏ mà lặng lẽ <b>xanh</b> — vài file bắt 401 rồi
///   <c>console.warn</c> + <c>return</c>, tức Playwright ghi PASSED. Bộ E2E báo xanh trong khi
///   không bài nào thật sự chạy.</item>
///   <item>Khoá nằm trong repo <b>và trong lịch sử git</b>. Gỡ khỏi file không gỡ được khỏi lịch
///   sử — phiên đã ghim phải coi là đã lộ và phải huỷ ở phía máy chủ.</item>
/// </list>
///
/// <para>⚠️ <b>Vì sao chốt này soi theo DẠNG CHUỖI chứ không theo tên biến.</b> Ngày 08/09/2026
/// lượt dọn đầu tiên tìm theo tên (<c>TEST_SESSION</c>) nên gỡ được bảy file — rồi bỏ sót nguyên
/// một mã phiên THỨ HAI nằm trong chín file khác dưới tên <c>STAGING_SESSION</c>. Tìm theo tên là
/// tìm theo thói quen đặt tên của người viết trước; tìm theo dạng chuỗi thì đặt tên gì cũng lộ.</para>
///
/// <para>Bộ E2E là JavaScript nên không có test runner nào của nó chạy trong CI. Chốt này sống ở
/// bộ test C# vì đó là thứ DUY NHẤT chạy tự động ở repo này.</para>
/// </summary>
public class E2eSecretGuardTests
{
    /// Mã phiên TourKit là 32 ký tự hex (xem TkSessionStore). Bắt đúng dạng đó khi nó nằm trong
    /// dấu nháy — tức là một hằng trong mã, không phải một chuỗi ai đó nhắc tới trong câu văn.
    private static readonly Regex MaPhien = new(@"['""][0-9a-f]{32}['""]", RegexOptions.Compiled);

    [Fact]
    public void Khong_file_E2E_nao_duoc_ghim_cung_ma_phien()
    {
        var goc = GocRepo();
        var thuMuc = Path.Combine(goc, "e2e");
        Assert.True(Directory.Exists(thuMuc), $"Không thấy thư mục {thuMuc}");

        var pham = new List<string>();
        foreach (var f in Directory.EnumerateFiles(thuMuc, "*.*", SearchOption.AllDirectories))
        {
            // node_modules là mã của người khác; report/test-results là kết quả chạy, sinh ra rồi xoá.
            var duong = f.Replace('\\', '/');
            if (duong.Contains("/node_modules/") || duong.Contains("/report/")
                || duong.Contains("/test-results/")) continue;
            if (!duong.EndsWith(".js") && !duong.EndsWith(".mjs")
                && !duong.EndsWith(".json") && !duong.EndsWith(".md")) continue;
            // package-lock.json đầy hash integrity 32+ ký tự hex — không phải phiên, và không do ta viết.
            if (duong.EndsWith("package-lock.json")) continue;

            foreach (Match m in MaPhien.Matches(File.ReadAllText(f)))
                pham.Add($"{Path.GetRelativePath(goc, f).Replace('\\', '/')}: {m.Value}");
        }

        Assert.True(pham.Count == 0,
            "Có mã phiên ghim cứng trong bộ E2E — khoá truy cập dữ liệu công ty thật, và nó sẽ nằm "
            + "lại trong lịch sử git kể cả sau khi xoá khỏi file:\n  " + string.Join("\n  ", pham)
            + "\n\nDùng biến môi trường qua e2e/helpers/phien.js. Và HUỶ phiên vừa lộ ở phía máy chủ.");
    }

    [Fact]
    public void Bai_E2E_thieu_phien_phai_BO_QUA_chu_khong_gia_vo_xanh()
    {
        // Nửa còn lại của cùng một bài học. Gỡ được khoá mà vẫn giữ lối "bắt lỗi rồi return" thì
        // bộ E2E vẫn báo xanh khi không chạy gì — đúng kiểu hỏng đã để lọt hai lỗi chặn ngày
        // 08/09/2026 dù 1219 test xanh.
        //
        // Chỉ soi CHÍNH hai câu ăn theo 401/session: cấm trần `return` trong file .js là vô nghĩa.
        var goc = GocRepo();
        var pham = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(goc, "e2e", "tests"), "*.spec.js"))
        {
            var noi = File.ReadAllText(f);
            // KHÔNG đòi xuống dòng giữa warn và return. Bản đầu của chốt này viết
            // [\s;]*\n\s*return — chỉ bắt dạng HAI DÒNG, còn dạng một dòng
            // `{ console.warn(...); return; }` thì lọt qua. Tự lộ ra lúc chạy phép phá: phá đúng
            // bằng dạng một dòng mà chốt VẪN XANH. Đúng cơ chế mục ruỗng đã ghi sổ — chốt bám
            // CÁCH VIẾT của một bản chứ không bám luật.
            foreach (Match m in Regex.Matches(noi,
                @"console\.warn\([^)]*(?i:session|expired|401)[^)]*\)[\s;{}]*return"))
                pham.Add($"{Path.GetFileName(f)}: {m.Value.Split('\n')[0].Trim()}");
        }

        Assert.True(pham.Count == 0,
            "Bài E2E đang NUỐT lỗi phiên rồi return — Playwright ghi PASSED, nên phiên chết vài "
            + "tháng cũng không ai hay:\n  " + string.Join("\n  ", pham)
            + "\n\nDùng test.skip(!PHIEN, THIEU_PHIEN) khi CHƯA cấu hình phiên, và để bài ĐỎ khi "
            + "phiên có mà bị từ chối.");
    }

    private static string GocRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
