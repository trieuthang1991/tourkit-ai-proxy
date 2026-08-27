using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nhật ký thao tác. Spec §1.3: "Mọi thao tác nhạy cảm đều được phân quyền và audit".
///
/// <para>Nhận/nhả việc, đổi trạng thái, tạm dừng bot, gỡ kết nối kênh — trước đây <b>không lưu
/// dấu vết nào</b>. Khi khách khiếu nại "ai nói câu này với tôi" thì không tra được, và khi một
/// hội thoại bị đóng nhầm thì không biết ai đóng.</para>
/// </summary>
public class ChatAuditGuardTests
{
    private static string Db() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
    private static string Endpoint() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    [Fact]
    public void Co_bang_nhat_ky()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_audit", Db());
        // Tra theo hội thoại là cách dùng duy nhất hiện có — thiếu chỉ mục là quét cả bảng mỗi lần
        // mở panel hồ sơ, và bảng này chỉ có lớn thêm.
        Assert.Contains("ON chat_audit (tenant_id, conversation_id, created_utc DESC)", Db());
    }

    [Theory]
    [InlineData("nhan-viec")]
    [InlineData("nha-viec")]
    [InlineData("chuyen-viec")]
    [InlineData("doi-trang-thai")]
    [InlineData("tam-dung-bot")]
    [InlineData("go-ket-noi")]
    public void Moi_thao_tac_nhay_cam_deu_duoc_ghi(string hanhDong)
    {
        // Canh ở mức mã nguồn vì không có CI chạy PostgreSQL. Thiếu một hành động ở đây thì lỗ
        // hổng chỉ lộ ra lúc cần tra — tức là lúc đã muộn.
        Assert.Contains(hanhDong, Endpoint());
    }

    [Fact]
    public void Nhan_viec_that_su_goi_ghi_nhat_ky()
    {
        var src = Endpoint();
        var m = Regex.Match(src, "ClaimConversationAsync(.{0,1200})", RegexOptions.Singleline);
        Assert.True(m.Success);
        Assert.Contains("AppendAuditAsync", m.Groups[1].Value);
    }

    [Fact]
    public void KHONG_chep_noi_dung_tin_vao_nhat_ky()
    {
        // Tin đã nằm ở chat_messages. Chép lại là nhân đôi dữ liệu khách VÀ nhân đôi chỗ phải xoá
        // khi khách yêu cầu xoá dữ liệu — sót một chỗ là vẫn còn lưu trái ý khách.
        // Soi TỪNG lời gọi tới hết dấu chấm phẩy, không soi một cửa sổ ký tự cố định: cửa sổ dễ
        // trùm sang mã bên cạnh rồi báo đỏ vì một chữ chẳng liên quan.
        var goi = Regex.Matches(Endpoint(), @"AppendAuditAsync\([^;]*;", RegexOptions.Singleline)
            .Select(x => x.Value).ToList();
        Assert.NotEmpty(goi);

        foreach (var cam in new[] { "body.Text", "tin.Body", "LastPreview", "Summarize" })
            Assert.DoesNotContain(goi, g => g.Contains(cam, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Duong_dan_nhat_ky_nam_trong_DuongRieng()
    {
        // Không nằm trong danh sách thì lúc tắt cờ Features:Chat, đường này rơi vào MapFallback và
        // trả index.html kèm 200 thay vì 404 — client gọi API nhận về HTML.
        const string duong = "/api/v1/chat/conversations/1/audit";
        Assert.Contains(TourkitAiProxy.Endpoints.ChatInboxEndpoints.OwnedPaths,
            p => duong.StartsWith(p + "/", System.StringComparison.Ordinal));
    }
}
