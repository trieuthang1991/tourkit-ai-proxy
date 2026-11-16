using System.Text.RegularExpressions;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Trang /help tải <c>/docs/features/&lt;slug&gt;.md</c> từ thư mục chạy, nên mỗi bài khai trong
/// <c>GUIDES</c> của <c>wwwroot/pages/help.jsx</c> PHẢI nằm trong danh sách trắng publish của
/// <c>TourkitAiProxy.csproj</c>.
///
/// <para>Bối cảnh (16/11/2026): publish chưa từng chép <c>docs/</c> → prod trả 404 cho mọi bài, trang
/// /help chỉ hiện "Không tải được hướng dẫn". Dev không thấy vì chạy thẳng từ thư mục nguồn.</para>
/// </summary>
public class HelpGuidesPublishTests
{
    // Tài liệu kỹ thuật cùng thư mục: /docs/features ai cũng đọc được nên KHÔNG được publish.
    private static readonly string[] TechnicalDocs =
        { "assistant", "chat-inbox-ky-thuat", "digest", "mail", "mcp", "workflows" };

    [Fact]
    public void Moi_bai_trong_help_jsx_deu_duoc_publish_va_co_file()
    {
        var root = RepoRoot();
        var published = PublishedGuides(root);
        var slugs = HelpSlugs(root);

        Assert.NotEmpty(slugs);
        foreach (var slug in slugs)
        {
            Assert.True(published.Contains(slug), $"Bài '{slug}' khai trong help.jsx nhưng thiếu trong danh sách publish của TourkitAiProxy.csproj → prod 404");
            Assert.True(File.Exists(Path.Combine(root, "docs", "features", slug + ".md")), $"Thiếu docs/features/{slug}.md");
        }
    }

    [Fact]
    public void Khong_publish_tai_lieu_ky_thuat()
    {
        var published = PublishedGuides(RepoRoot());
        foreach (var doc in TechnicalDocs)
            Assert.False(published.Contains(doc), $"docs/features/{doc}.md là tài liệu kỹ thuật — không được publish ra /docs/features");
        Assert.DoesNotContain(published, s => s.Contains('*'));
    }

    private static HashSet<string> PublishedGuides(string root)
    {
        var csproj = File.ReadAllText(Path.Combine(root, "TourkitAiProxy.csproj"));
        return Regex.Matches(csproj, @"docs/features/([^;""/]+)\.md").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(csproj, @"docs/features/([^;""]*\*[^;""]*)").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<string> HelpSlugs(string root)
    {
        var jsx = File.ReadAllText(Path.Combine(root, "wwwroot", "pages", "help.jsx"));
        var start = jsx.IndexOf("const GUIDES", StringComparison.Ordinal);
        Assert.True(start >= 0, "Không tìm thấy const GUIDES trong help.jsx");
        var end = jsx.IndexOf("];", start, StringComparison.Ordinal);
        return Regex.Matches(jsx[start..end], @"slug:\s*'([a-z0-9-]+)'").Select(m => m.Groups[1].Value).ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TourkitAiProxy.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
