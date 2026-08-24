using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh schema ở mức MÃ NGUỒN. Không có CI chạy PostgreSQL nên đây là lớp bảo vệ duy nhất
/// chống việc ai đó lỡ tay bỏ account_id khỏi khoá hội thoại.
/// </summary>
public class ChatSchemaGuardTests
{
    [Fact]
    public void Khoa_hoi_thoai_phai_co_account_id()
    {
        var sql = DocFile("Services/Chat/Inbox/ChatDb.cs");

        var m = Regex.Match(sql,
            @"CREATE UNIQUE INDEX IF NOT EXISTS \w+\s+ON chat_conversations \(([^)]*)\)");
        Assert.True(m.Success, "Không thấy chỉ mục duy nhất nào trên chat_conversations");

        var cot = m.Groups[1].Value;
        Assert.Contains("account_id", cot);
        Assert.Contains("tenant_id", cot);
        Assert.Contains("contact_external_id", cot);
    }

    [Fact]
    public void ON_CONFLICT_phai_khop_voi_chi_muc()
    {
        // Postgres đòi cột trong ON CONFLICT phải khớp một chỉ mục duy nhất. Lệch là lỗi lúc
        // CHẠY chứ không phải lúc biên dịch — nghĩa là chỉ lộ ra khi khách nhắn tin thật.
        var repo = DocFile("Services/Chat/Inbox/ChatRepository.cs");
        Assert.Contains("ON CONFLICT (tenant_id, channel, account_id, contact_external_id)", repo);
    }

    internal static string DocFile(string duongDanTuongDoi)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        var f = Path.Combine(d!.FullName, duongDanTuongDoi.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(f), $"Không thấy {f}");
        return File.ReadAllText(f);
    }
}
