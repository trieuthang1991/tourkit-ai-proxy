using System.Text.RegularExpressions;
using TourkitAiProxy.Configuration;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Ở chế độ prod-bundle, server phải gỡ thẻ <c>&lt;script src&gt;</c> của những file plain
/// <c>.js</c> đã nằm trong bundle.
///
/// <para>Bối cảnh (20/08/2026): danh sách gỡ vốn viết tay, đặt cạnh một danh sách viết tay khác
/// (<c>wwwroot/bundle-entry.js</c>), kèm sẵn chú thích dặn "thêm file mới nhớ thêm vào đây" — và
/// vẫn lệch 12 file. Hậu quả KHÔNG phải "tải đôi cho tốn băng thông": bản trong bundle nạp SAU nên
/// nó THẮNG, tức sửa một file plain .js mà chưa dựng lại bundle thì bản sửa im lặng không có tác
/// dụng ở prod. Dev không bao giờ lộ ra vì dev không có bundle.</para>
///
/// <para>Nay danh sách lấy thẳng từ bundle-entry.js. Test cuối là chốt chặn: đối chiếu index.html
/// THẬT với bundle-entry.js THẬT.</para>
/// </summary>
public class BundledPlainJsStripTests
{
    [Fact]
    public void Boc_dung_cac_import_js_bo_qua_jsx()
    {
        const string entry = """
        import "./lib/data.js";
        import "./lib/icons.jsx";
        import "./lib/util.js";
        import "./core/features.js";
        """;

        var paths = StaticFilesSetup.ParseBundledPlainJs(entry);

        Assert.Equal(new[] { "lib/data.js", "lib/util.js", "core/features.js" }, paths);
    }

    [Fact]
    public void Go_the_plain_nhung_KHONG_dung_toi_the_text_babel()
    {
        // core/storage.js là file .js nhưng khai type="text/babel" — _babelScriptRegex lo phần đó.
        // Nếu regex này cũng khớp thì hai lớp giẫm chân nhau, và lỗi thật bị che.
        var re = StaticFilesSetup.BuildBundledPlainJsRegex(new[] { "lib/util.js", "core/storage.js" });
        const string html = """
        <script src="lib/util.js"></script>
        <script type="text/babel" src="core/storage.js"></script>
        """;

        var sau = re.Replace(html, string.Empty);

        Assert.DoesNotContain("src=\"lib/util.js\"", sau);
        Assert.Contains("text/babel\" src=\"core/storage.js\"", sau);
    }

    [Fact]
    public void Khong_doc_duoc_bundle_entry_thi_van_go_2_file_chay_doi_la_hong()
    {
        // core/features.js gọi /api/v1/features lúc nạp → chạy đôi là hỏi server 2 lần rồi thay
        // luôn window.tourkitFeatures. Đường rơi phải giữ lại đúng 2 file này.
        var re = StaticFilesSetup.BuildBundledPlainJsRegex(Array.Empty<string>());
        const string html = """
        <script src="lib/data.js"></script>
        <script src="core/features.js"></script>
        <script src="lib/tinymce-loader.js"></script>
        """;

        var sau = re.Replace(html, string.Empty);

        Assert.DoesNotContain("lib/data.js", sau);
        Assert.DoesNotContain("core/features.js", sau);
        // tinymce-loader CỐ Ý nằm ngoài bundle (lazy-load ~5MB) → phải còn.
        Assert.Contains("lib/tinymce-loader.js", sau);
    }

    [Fact]
    public void Index_html_that_khong_con_the_nao_trung_voi_bundle_entry_that()
    {
        var root = RepoRoot();
        var entry = File.ReadAllText(Path.Combine(root, "wwwroot", "bundle-entry.js"));
        var html  = File.ReadAllText(Path.Combine(root, "wwwroot", "index.html"));

        var buLai = StaticFilesSetup.BuildBundledPlainJsRegex(StaticFilesSetup.ParseBundledPlainJs(entry))
                                    .Replace(html, string.Empty);

        // Mọi thẻ plain <script src="..."> còn lại PHẢI là file không có trong bundle.
        var trongBundle = StaticFilesSetup.ParseBundledPlainJs(entry)
                                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conSot = Regex.Matches(buLai, @"<script\s+src=[""']([^""']+)[""']")
                          .Select(m => m.Groups[1].Value)
                          .Where(trongBundle.Contains)
                          .ToList();

        Assert.True(conSot.Count == 0,
            "Các file này vừa có thẻ <script> riêng trong index.html vừa nằm trong bundle → ở prod " +
            "bản trong bundle sẽ đè lên bản mới: " + string.Join(", ", conSot));
    }

    /// Test chạy từ bin/Debug/netX → đi ngược lên tới thư mục có .csproj gốc.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TourkitAiProxy.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
