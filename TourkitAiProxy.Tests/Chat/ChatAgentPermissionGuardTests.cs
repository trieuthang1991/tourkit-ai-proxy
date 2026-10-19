using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Luật 12/10/2026: chỉ người CÓ QUYỀN CHAT (CHAT_XEM / CHAT_XEM_ALL) mới được giao việc, vào đội
/// trực, và được vòng xoay chia việc. Chốt canh đọc văn bản nguồn — xem ChatAssignSchemaGuardTests
/// về lý do cách làm này; hành vi thật vẫn phải thử trên staging.
/// </summary>
public class ChatAgentPermissionGuardTests
{
    private static string Endpoints() =>
        ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    /// Thân handler, cắt tới dấu đóng của chính nó (cùng lối ThanHamPost bên ChatAssignSchemaGuardTests).
    private static string HandlerBody(string anchor)
    {
        var src = Endpoints();
        var start = src.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0) return "";
        var m = Regex.Match(src[start..], @"\A.*?\r?\n        \}\);", RegexOptions.Singleline);
        return m.Success ? m.Value : "";
    }

    [Fact]
    public void Assign_rejects_assignee_without_chat_permission_before_writing()
    {
        // Dấu nháy đóng trong neo để KHÔNG khớp nhầm ".../assign/me".
        var body = HandlerBody("MapPost(\"/conversations/{id:long}/assign\"");
        Assert.False(string.IsNullOrWhiteSpace(body), "Không cắt được thân đường giao việc");

        var check = body.IndexOf("!chatAgentIds.Contains(ma)", StringComparison.Ordinal);
        var write = body.IndexOf("repo.AssignAsync", StringComparison.Ordinal);
        Assert.True(check > 0, "Đường giao việc không kiểm người nhận có quyền chat");
        Assert.True(check < write, "Kiểm quyền chat phải nằm TRƯỚC lệnh ghi người phụ trách");
        // Không biết ai có quyền thì cho qua như cũ — chặn theo danh sách rỗng là chặn cả công ty.
        Assert.Contains("chatAgentIds is not null && !chatAgentIds.Contains(ma)", body);
    }

    [Fact]
    public void Saving_duty_team_rejects_only_NEW_members_without_chat_permission()
    {
        var body = HandlerBody("MapPut(\"/assign-settings\"");
        Assert.False(string.IsNullOrWhiteSpace(body), "Không cắt được thân đường lưu cấu hình");
        // Người ĐÃ trong đội mà nay mất quyền vẫn lưu lại được — không thì quản trị phải gỡ họ ra
        // mới lưu được bất kỳ thay đổi nào khác.
        Assert.Contains("!chatAgentIds.Contains(x) && !savedMembers.Contains(x)", body);
        var check = body.IndexOf("lackingPermission.Length > 0", StringComparison.Ordinal);
        var save = body.IndexOf("assign.LuuCauHinhAsync", StringComparison.Ordinal);
        Assert.True(check > 0 && check < save, "Phải chặn TRƯỚC khi lưu");
    }

    [Fact]
    public void Settings_return_filtered_pickers_and_unfiltered_names()
    {
        var body = HandlerBody("MapGet(\"/assign-settings\"");
        Assert.Contains("staffs            = nhanVien", body);
        Assert.Contains("allStaffs         = allStaff", body);
        Assert.Contains("membersNoChatPermission", body);
        // Danh sách đầy đủ phải được giữ TRƯỚC khi lọc.
        var keep = body.IndexOf("var allStaff = nhanVien;", StringComparison.Ordinal);
        var filter = body.IndexOf("nhanVien = nhanVien.Where(", StringComparison.Ordinal);
        Assert.True(keep > 0 && keep < filter, "allStaff phải giữ danh sách CHƯA lọc");
    }

    [Fact]
    public void Inbox_uses_unfiltered_list_for_names_and_seller_picker()
    {
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        // Chọn NGƯỜI BÁN khi tạo cơ hội không dính gì tới quyền chat.
        Assert.Contains("nhanVien={phanCong.allStaffs || phanCong.staffs}", jsx);
        // Tên người đang phụ trách từ trước không được rơi về "#mã".
        Assert.Contains("(phanCong.allStaffs || phanCong.staffs || []).find(nv => nv.id === maPT)", jsx);
    }

    [Fact]
    public void Round_robin_skips_members_without_chat_permission()
    {
        var repo = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs");
        Assert.Contains("AND u = ANY(@eligible)", repo);
        // Chỉ ghép mệnh đề khi có danh sách: không gửi mảng null cho Npgsql, và null = chia như cũ.
        Assert.Contains("eligibleUserIds is { Count: > 0 }", repo);

        // Luồng khách nhắn tới không có phiên → chỉ đọc bản đã lưu, không gọi ERP.
        var inbound = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs");
        Assert.Contains("_agents.GetCachedOnly(hoiThoai.TenantId)", inbound);
    }

    [Fact]
    public void Empty_permission_list_is_never_cached()
    {
        // Lưu rỗng = 24 giờ vòng xoay/giao việc chạy theo một danh sách sai dù ERP đã sửa xong.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/Inbox/ChatAgentPermissions.cs");
        var empty = src.IndexOf("if (ids.Count == 0)", StringComparison.Ordinal);
        var set = src.IndexOf("_redis.Set(", StringComparison.Ordinal);
        Assert.True(empty > 0 && empty < set, "Phải bỏ qua danh sách rỗng TRƯỚC khi ghi Redis");
        Assert.Contains("s is { UserIds.Length: > 0 }", src);
    }
}
