using System.Text.Json;
using TourkitAiProxy.Services.Providers;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Khoá đúng cái bẫy đã dính 2 lần: THÊM member vào enum <see cref="AiFeature"/> mà quên khai khoá
/// <c>Models:{Tên}</c> trong appsettings. Không có gì báo — AiModelRegistry lặng lẽ rơi về
/// <c>Models:Primary</c>, không log, không cảnh báo, chỉ hoá đơn cuối tháng biết. Lần gần nhất là
/// <c>StatusSemantics</c>: enum có 14 member nhưng cả 2 file mẫu chỉ khai 13, nên tính năng thứ 14
/// chạy bằng Primary ngay từ hôm được thêm vào.
///
/// Test soi FILE MẪU (committed) chứ không soi appsettings.json thật — file thật gitignore, không có
/// trên máy CI, và mỗi máy một khác. Mẫu đúng thì người deploy copy ra mới đúng được.
/// </summary>
public class AppSettingsModelCoverageTests
{
    public static IEnumerable<object[]> ExampleFiles => new[]
    {
        new object[] { "appsettings.example.json" },
        new object[] { Path.Combine("TourkitAiProxy.Worker", "appsettings.example.json") },
    };

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Moi_feature_trong_enum_deu_duoc_khai_trong_file_mau(string relativePath)
    {
        var declared = ReadModelKeys(relativePath);

        var missing = Enum.GetNames<AiFeature>().Where(f => !declared.Contains(f)).ToList();

        Assert.True(missing.Count == 0,
            $"{relativePath} thiếu khoá Models cho: {string.Join(", ", missing)}. " +
            "Thiếu thì tính năng đó âm thầm chạy bằng Models:Primary — không log, không cảnh báo.");
    }

    /// Chiều ngược lại: khoá thừa (feature đã xoá khỏi enum, hoặc gõ sai tên) thì cấu hình ở đó
    /// KHÔNG có tác dụng gì cả — nhìn tưởng đã set model rẻ mà thật ra vẫn chạy Primary.
    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Khong_co_khoa_thua_khong_khop_enum_nao(string relativePath)
    {
        var declared = ReadModelKeys(relativePath);
        var known = Enum.GetNames<AiFeature>().Append("Primary").ToHashSet(StringComparer.Ordinal);

        var extra = declared.Where(k => !known.Contains(k)).ToList();

        Assert.True(extra.Count == 0,
            $"{relativePath} có khoá Models không khớp feature nào: {string.Join(", ", extra)}. " +
            "Khoá gõ sai tên thì không ai đọc tới, nhưng nhìn vào lại tưởng đã cấu hình xong.");
    }

    private static HashSet<string> ReadModelKeys(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"Không thấy {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var models = doc.RootElement.GetProperty("Models");

        return models.EnumerateObject()
            // "_comment*" là chú thích cho người đọc, không phải feature.
            .Where(p => !p.Name.StartsWith('_'))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// Test chạy từ bin/Debug/netX → đi ngược lên tới thư mục có file .sln/.csproj gốc.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TourkitAiProxy.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
