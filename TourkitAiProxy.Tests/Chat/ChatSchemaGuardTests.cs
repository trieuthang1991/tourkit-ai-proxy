using System.Text.RegularExpressions;
using Xunit;
using TourkitAiProxy.Infrastructure.Chat.Inbox;

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
        var sql = DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");

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
        //
        // ⚠️ SO HAI BÊN VỚI NHAU, đừng ghi cứng danh sách cột. Bản trước viết
        // Assert.Contains("ON CONFLICT (tenant_id, channel, account_id, contact_external_id)")
        // — đến khi có người thêm surface + source_thread_id vào CẢ chỉ mục LẪN câu lệnh (tức
        // là làm ĐÚNG), test vẫn đỏ và chặn cả lượt publish. Một guard bắt nhầm người làm đúng
        // thì lần sau người ta sẽ sửa guard cho qua chuyện, và nó mất hẳn tác dụng.
        var chiMuc = CotTrong(
            DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs"),
            @"CREATE UNIQUE INDEX IF NOT EXISTS \w+\s+ON chat_conversations \(([^)]*)\)",
            "chỉ mục duy nhất trên chat_conversations");

        var onConflict = CotTrong(
            DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs"),
            @"INSERT INTO chat_conversations[\s\S]*?ON CONFLICT \(([^)]*)\)",
            "ON CONFLICT của câu lệnh thêm hội thoại");

        Assert.Equal(chiMuc, onConflict);
    }

    [Fact]
    public void Cau_soi_tep_phai_di_bang_chi_muc_co_dieu_kien()
    {
        // Vòng soi ảnh cũ chạy nền trên chính bảng tin nhắn — bảng chỉ phình chứ không co. Nó
        // chịu được là nhờ chỉ mục CÓ ĐIỀU KIỆN ix_msg_media_cho: chỉ mục chỉ chứa những tin còn
        // phải soi, nên chi phí tỉ lệ với phần việc còn lại chứ không với cỡ bảng.
        //
        // Nhưng Postgres chỉ dùng được chỉ mục đó khi câu WHERE PHỦ trọn vị từ của nó. Lệch một
        // chữ là nó lặng lẽ quay ra quét cả bảng: kết quả vẫn ĐÚNG, chỉ chậm dần theo thời gian
        // — kiểu hỏng không có lỗi nào hiện ra, và tới lúc thấy thì đã ngốn CSDL hàng tháng.
        var chiMuc = Regex.Match(
            DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs"),
            @"CREATE INDEX IF NOT EXISTS ix_msg_media_cho\s+ON chat_messages \(([^)]*)\)\s+WHERE ([^;]+);");
        Assert.True(chiMuc.Success, "Không thấy chỉ mục ix_msg_media_cho trong ChatDb");

        var repo = DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
        // Bám vào CHỮ KÝ hàm, không phải cái tên trần: tên này còn xuất hiện trong chú thích của
        // ClaimAvatarsAsync nằm phía trên, và bắt trúng chỗ đó là test đi canh nhầm câu lệnh.
        var cauLay = Regex.Match(repo,
            @"Task<IReadOnlyList<MediaToMirror>> ClaimMediaAsync[\s\S]*?WITH lay AS \(([\s\S]*?)\n            \)");
        Assert.True(cauLay.Success, "Không thấy câu WITH lay của ClaimMediaAsync");

        var vịTu = Gon(chiMuc.Groups[2].Value);
        Assert.Contains(vịTu, Gon(cauLay.Groups[1].Value));

        // Thứ tự cột của chỉ mục phải khớp ORDER BY, nếu không Postgres vẫn phải sắp lại tay —
        // và sắp lại tay trên một tập lớn thì mất đúng cái lợi vừa nói ở trên.
        Assert.Contains("ORDER BY " + Gon(chiMuc.Groups[1].Value), Gon(cauLay.Groups[1].Value));
    }

    /// <summary>Gộp mọi khoảng trắng thành một dấu cách để so hai đoạn SQL xuống dòng khác nhau.</summary>
    private static string Gon(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>
    /// Bóc danh sách cột trong ngoặc ra thành tập hợp đã chuẩn hoá.
    ///
    /// <para>Dùng TẬP HỢP chứ không phải danh sách: Postgres không quan tâm thứ tự cột trong
    /// ON CONFLICT, nên bắt đúng thứ tự là bắt một thứ không có thật.</para>
    ///
    /// <para>Cắt xuống dòng và khoảng trắng thừa vì cả hai chỗ đều xuống dòng cho vừa bề ngang.</para>
    /// </summary>
    private static SortedSet<string> CotTrong(string nguon, string mau, string ten)
    {
        var m = Regex.Match(nguon, mau);
        Assert.True(m.Success, $"Không tìm thấy {ten}");
        return new SortedSet<string>(m.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Regex.Replace(x, @"\s+", " ").Trim()),
            StringComparer.OrdinalIgnoreCase);
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
