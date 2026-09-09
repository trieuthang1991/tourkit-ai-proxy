using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Chốt chặn cho lỗi <b>"chạy được lúc dev, biến mất lúc phát hành"</b>.
///
/// <para><b>Lỗi này đã xảy ra thật và sống nhiều ngày mà không ai thấy.</b> Trang
/// <c>pages/chat-assign-settings.jsx</c> được khai trong <c>wwwroot/index.html</c> nhưng KHÔNG có
/// dòng <c>import</c> nào trong <c>wwwroot/bundle-entry.js</c>. Hai chế độ chạy đọc hai danh sách
/// khác nhau, nên hậu quả chia đôi:</para>
///
/// <list type="bullet">
///   <item>Chế độ dev (<c>dotnet run</c> Debug): Babel nạp theo <c>index.html</c> → trang chạy tốt.
///         Mọi lần thử tay đều xanh.</item>
///   <item>Chế độ bundle (<c>dotnet publish -c Release</c>): <c>StaticFilesSetup.ServeIndex</c> gỡ
///         SẠCH các thẻ <c>&lt;script type="text/babel"&gt;</c> và chỉ nạp <c>dist/app.bundle.js</c>
///         — thứ được dựng từ <c>bundle-entry.js</c>. Tệp bị quên không nằm trong bundle, nên
///         <c>window.ChatAssignSettingsPage</c> là <c>undefined</c> và màn hình vỡ.</item>
/// </list>
///
/// <para>Không có ngoại lệ nào lộ ra ở phía máy chủ, không có lỗi biên dịch, và <b>không bài kiểm
/// nào chạm tới</b>: bộ test C# đọc mã nguồn, còn bộ E2E chạy trên bản dev. Chỉ người dùng cuối
/// trên bản phát hành mới gặp.</para>
///
/// <para>Luật: mọi tệp <c>.jsx</c> khai trong <c>index.html</c> phải có mặt trong
/// <c>bundle-entry.js</c>. Thêm trang/thành phần mới thì sửa CẢ HAI chỗ.</para>
/// </summary>
public class FrontendBundleEntryGuardTests
{
    [Fact]
    public void Moi_tep_jsx_khai_trong_index_html_deu_phai_nam_trong_bundle_entry()
    {
        // Gỡ chú thích ở CẢ HAI phía. Lệch một bên là chốt nói sai ngay: bản đầu chỉ gỡ "//" bên
        // bundle-entry, nên `pages/home.jsx` — thứ đã tắt CÓ CHỦ ĐÍCH và tắt đúng cách ở cả hai
        // tệp — bị báo là "quên đăng ký". Một chốt báo động giả vài lần là một chốt sắp bị ai đó
        // tắt đi, và khi ấy nó không còn canh gì nữa.
        var html = BoChuThichHtml(DocFile("wwwroot/index.html"));
        var entry = BoChuThich(DocFile("wwwroot/bundle-entry.js"));

        // Chỉ xét thẻ text/babel: đó ĐÚNG là tập hợp mà chế độ bundle gỡ đi và trông chờ bundle
        // gánh lại. Các thẻ .js thường (chart-loader, tinymce-loader…) vẫn được nạp bình thường
        // ở cả hai chế độ nên không thuộc luật này.
        var khai = Regex.Matches(html, @"<script\s+type=""text/babel""\s+src=""([^""]+)""")
                        .Select(m => m.Groups[1].Value)
                        .ToList();

        Assert.True(khai.Count > 40,
            $"Chỉ thấy {khai.Count} thẻ text/babel trong index.html — cách khai đã đổi và chốt này " +
            "đang đo nhầm chỗ. Sửa biểu thức, đừng hạ ngưỡng.");

        var thieu = khai.Where(f => !entry.Contains("\"./" + f + "\"")).ToList();

        Assert.True(thieu.Count == 0,
            "Có tệp .jsx khai trong index.html mà THIẾU trong bundle-entry.js. Chúng chạy ở bản " +
            "dev nhưng biến mất khỏi bản phát hành (xem chú thích đầu lớp):\n  " +
            string.Join("\n  ", thieu) +
            "\nThêm dòng: import \"./<đường dẫn>\";");
    }

    [Fact]
    public void Bundle_entry_khong_tro_toi_tep_da_bi_xoa()
    {
        // Chiều ngược lại. Một dòng import trỏ vào tệp không còn tồn tại làm esbuild HỎNG HẲN,
        // và vì bước bundle chạy trong MSBuild lúc publish, thông báo lỗi lẫn giữa log build —
        // dễ đọc thành "publish lỗi gì đó" rồi chạy lại lần nữa.
        var goc = TimGocRepo();
        var entry = BoChuThich(DocFile("wwwroot/bundle-entry.js"));

        var mat = Regex.Matches(entry, @"import\s+""\.\/([^""]+\.jsx)""")
                       .Select(m => m.Groups[1].Value)
                       .Where(f => !File.Exists(Path.Combine(goc, "wwwroot",
                                                             f.Replace('/', Path.DirectorySeparatorChar))))
                       .ToList();

        Assert.True(mat.Count == 0,
            "bundle-entry.js đang import tệp không còn tồn tại — esbuild sẽ hỏng lúc publish:\n  " +
            string.Join("\n  ", mat));
    }

    /// <summary>
    /// Bỏ dòng bị chú thích (<c>//</c>) trước khi so khớp.
    ///
    /// <para>⚠️ Không có bước này thì chốt <b>không đỏ được</b> khi ai đó tắt một dòng import bằng
    /// cách comment nó lại — mà đó chính là cách người ta hay tắt tạm rồi quên bật. Đo thật
    /// 08/09/2026: phép phá "comment dòng import trang phân công" vẫn cho kết quả XANH, vì chuỗi
    /// đường dẫn vẫn nằm nguyên trong tệp. Chốt đếm cả mã đã chết là chốt canh hờ.</para>
    /// </summary>
    private static string BoChuThich(string js)
        => string.Join("\n", js.Split('\n').Where(d => !d.TrimStart().StartsWith("//")));

    /// <summary>Gỡ khối <c>&lt;!-- … --&gt;</c> — thẻ script bị tắt bằng chú thích HTML không tính là đã khai.</summary>
    private static string BoChuThichHtml(string html)
        => Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline);

    private static string TimGocRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static string DocFile(string duongDanTuongDoi)
    {
        var f = Path.Combine(TimGocRepo(), duongDanTuongDoi.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(f), $"Không thấy {duongDanTuongDoi}");
        return File.ReadAllText(f);
    }
}
