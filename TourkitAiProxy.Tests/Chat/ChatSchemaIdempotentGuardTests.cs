using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Mọi câu trong lệnh dựng schema chat phải CHẠY LẠI ĐƯỢC.
///
/// <para><b>Vì sao đây là luật, không phải lời khuyên.</b> <c>ChatDb.InitAsync</c> chạy TOÀN BỘ
/// khối SQL ở MỖI LẦN KHỞI ĐỘNG, và chạy như MỘT batch. Một câu không chạy lại được thì lần khởi
/// động thứ hai ném — kéo cả khối dừng theo, tức là bảng khai sau nó không bao giờ được tạo. Triệu
/// chứng ở phía người dùng là "hôm qua chạy tốt, sáng nay hộp thư trắng", và không ai nghĩ tới
/// dòng DDL vừa thêm.</para>
///
/// <para>⚠️ <b>Chốt cũ chỉ kiểm câu TẠO BẢNG</b> (<see cref="ChatSchemaGuardTests"/>) — thêm cột
/// và tạo chỉ mục không ai canh. Đó là nợ ghi nhận ở review việc 1, nay trả. Hôm viết chốt này,
/// 17/17 câu thêm cột và 15/15 câu tạo chỉ mục đều đã đúng; việc của chốt là giữ cho câu THỨ 18
/// cũng đúng.</para>
///
/// <para><b>Không phủ <c>TourkitAiDb</c> (SQL Server) — cố ý.</b> Bên đó idempotent theo lối KHÁC:
/// bọc cả khối trong <c>IF OBJECT_ID(...) IS NULL BEGIN … END</c> chứ không gắn <c>IF NOT EXISTS</c>
/// vào từng câu. Đo thử bằng luật của file này thì 25/26 câu tạo bảng "vi phạm" — mà chúng hoàn
/// toàn đúng. Một chốt báo đỏ oan còn tệ hơn không có chốt: người ta tắt nó đi, rồi tắt luôn thói
/// quen nhìn. Muốn canh bên đó thì phải viết bộ kiểm theo KHỐI, để riêng một lần.</para>
/// </summary>
public class ChatSchemaIdempotentGuardTests
{
    /// <summary>
    /// Lệnh dựng schema chat, đã bỏ chú thích.
    ///
    /// <para>Bỏ chú thích là BẮT BUỘC: file này giải thích rất nhiều bằng chú thích SQL (<c>--</c>)
    /// và chú thích C# (<c>//</c>), trong đó có những câu nhắc nguyên văn "CREATE INDEX …" để kể
    /// một lỗi cũ. Tính chúng là mã thì chốt đỏ vì LỜI VĂN chứ không vì lệnh.</para>
    /// </summary>
    private static string Sql()
    {
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        return string.Join("\n", src.Split('\n')
            .Where(d => !d.TrimStart().StartsWith("--", StringComparison.Ordinal)
                     && !d.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    /// <summary>Mọi lượt khớp <paramref name="mo"/> phải mang <paramref name="cua"/> ngay trong câu.</summary>
    private static void MoiCauPhaiCo(string mo, string cua, string viSao)
    {
        var sql = Sql();
        var hit = Regex.Matches(sql, mo, RegexOptions.IgnoreCase).ToList();

        // Không khớp câu nào = biểu thức đã lạc khỏi cách viết thật của file. Im lặng bỏ qua thì
        // chốt thành vô dụng mà vẫn xanh — tệ hơn là không có chốt.
        Assert.True(hit.Count > 0, $"Không thấy câu nào khớp /{mo}/ — biểu thức đã lạc, chốt sẽ xanh giả");

        var pham = hit.Where(m => !m.Value.Contains(cua, StringComparison.OrdinalIgnoreCase))
                      .Select(m => Regex.Replace(m.Value, @"\s+", " ").Trim())
                      .ToList();
        Assert.True(pham.Count == 0,
            $"{pham.Count}/{hit.Count} câu thiếu \"{cua}\" — {viSao}\n  " + string.Join("\n  ", pham));
    }

    [Fact]
    public void Moi_cau_TAO_BANG_phai_chay_lai_duoc()
        => MoiCauPhaiCo(@"CREATE TABLE(\s+IF NOT EXISTS)?\s+\w+", "IF NOT EXISTS",
            "lần khởi động thứ hai sẽ ném \"relation already exists\" và dừng CẢ khối SQL");

    [Fact]
    public void Moi_cau_TAO_CHI_MUC_phai_chay_lai_duoc()
        => MoiCauPhaiCo(@"CREATE\s+(UNIQUE\s+)?INDEX(\s+IF NOT EXISTS)?\s+\w+", "IF NOT EXISTS",
            "chỉ mục đã có thì lần khởi động sau ném và dừng CẢ khối SQL");

    [Fact]
    public void Moi_cau_THEM_COT_phai_chay_lai_duoc()
        => MoiCauPhaiCo(@"ADD COLUMN(\s+IF NOT EXISTS)?\s+\w+", "IF NOT EXISTS",
            "cột đã có thì lần khởi động sau ném và dừng CẢ khối SQL");

    [Fact]
    public void Moi_cau_XOA_phai_chay_lai_duoc()
    {
        // Chiều ngược lại của cùng một luật: xoá thứ đã xoá rồi cũng ném. Ba câu DROP TABLE nằm
        // trong khối tự kiểm (dọn bảng khoá theo tên đăng nhập cũ) — khối đó chạy lại mỗi lần
        // khởi động y như phần còn lại.
        MoiCauPhaiCo(@"DROP TABLE(\s+IF EXISTS)?\s+\w+", "IF EXISTS",
            "bảng đã xoá rồi thì lần khởi động sau ném và dừng CẢ khối SQL");
        MoiCauPhaiCo(@"DROP INDEX(\s+IF EXISTS)?\s+\w+", "IF EXISTS",
            "chỉ mục đã xoá rồi thì lần khởi động sau ném và dừng CẢ khối SQL");
        MoiCauPhaiCo(@"DROP COLUMN(\s+IF EXISTS)?\s+\w+", "IF EXISTS",
            "cột đã xoá rồi thì lần khởi động sau ném và dừng CẢ khối SQL");
    }
}
