using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Quyền "Xem tất cả đoạn chat" (CHAT_XEM_ALL) áp cho chat nội bộ — chốt 27/10/2026: thấy MỌI phòng nội bộ của công ty,
/// kể cả phòng mình không ở; không phụ thuộc nhóm / chi nhánh / quyền đơn hàng. Phòng mình không ở: chỉ xem.
/// </summary>
public class InternalChatViewAllTests
{
    private static string Repo() => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs");
    private static string Ep() => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/InternalChatEndpoints.cs");

    [Fact]
    public void Danh_sach_xem_tat_ca_moi_phong_noi_bo_con_nguoi_cua_cong_ty()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "ListAllRoomsAsync");
        Assert.Contains("v.tenant_id = @tenant AND v.channel = 7 AND v.deleted_utc IS NULL", than);   // khớp chỉ mục riêng phần
        Assert.Contains("o.left_utc IS NULL", than);                       // bỏ phòng không còn ai
        Assert.Contains("(mb.conversation_id IS NOT NULL) AS member", than);
        Assert.Contains("CASE WHEN mb.conversation_id IS NULL THEN 0", than);   // phòng không ở: không có chưa đọc
        Assert.Contains("ORDER BY v.last_activity_at DESC, v.id DESC", than);
        Assert.DoesNotContain("OFFSET", than);
    }

    [Fact]
    public void Chi_muc_rieng_phan_cho_danh_sach_xem_tat_ca()
        => Assert.Contains("ON chat_conversations (tenant_id, last_activity_at DESC, id DESC) WHERE channel = 7 AND deleted_utc IS NULL",
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs"));

    [Theory]
    [InlineData("ListMessagesAsync")]
    [InlineData("ListMessagesAfterAsync")]
    public void Doc_tin_phong_khong_o_chi_khi_co_quyen_va_phong_chua_xoa(string method)
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), method);
        Assert.Contains("@viewAll AND EXISTS (SELECT 1 FROM chat_conversations cv", than);
        Assert.Contains("cv.deleted_utc IS NULL", than);
        Assert.Contains("viewAll)) return null;", than);   // phòng rỗng cũng phân biệt đúng
    }

    [Fact]
    public void Ghi_tin_van_bat_buoc_la_thanh_vien()
        // Chỉ XEM: gửi tin / thả cảm xúc / đánh dấu đã đọc vẫn đòi đang ở phòng — muốn nhắn phải tự vào phòng trước.
        => Assert.DoesNotContain("viewAll", InternalChatGuardTests.ThanHam(Repo(), "AppendMessageAsync"));

    [Fact]
    public void Tu_vao_phong_khong_ap_phong_1_1_va_endpoint_doi_quyen()
    {
        Assert.Contains("v.entity_type IS DISTINCT FROM 'direct'", InternalChatGuardTests.ThanHam(Repo(), "JoinRoomAsync"));
        var ep = Ep();
        var join = ep[ep.IndexOf("\"/rooms/{id:long}/join\"", StringComparison.Ordinal)..];
        Assert.True(join.IndexOf("IsQuanTriChatAsync", StringComparison.Ordinal) < join.IndexOf("JoinRoomAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Endpoint_dung_chung_mot_ham_quyen_CHAT_XEM_ALL()
    {
        var ep = Ep();
        Assert.Contains("SubscribeInternalAsync(a.TenantId, me, ct, viewAll)", ep);
        Assert.DoesNotContain("\"CHAT_XEM_ALL\"", ep);   // không chép tay mã quyền — đi qua SessionAuth.IsQuanTriChatAsync
    }

    [Fact]
    public async Task Nguoi_xem_tat_ca_nhan_su_kien_moi_phong_noi_bo_cung_cong_ty()
    {
        var bus = new ChatEventBus(null);
        var nhan = new List<ChatEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeInternalAsync("A", 99, cts.Token, viewAll: true))
            { nhan.Add(e); if (nhan.Count == 2) break; }
        });
        await Task.Delay(100);
        bus.Publish(new ChatEvent("A", 1, "noi-bo", 10) { Recipients = new[] { 7 } });   // không ở phòng → vẫn nhận
        bus.Publish(new ChatEvent("B", 2, "noi-bo", 20) { Recipients = new[] { 7 } });   // khác công ty → không
        bus.Publish(new ChatEvent("A", 3, "tin-moi", 30));                               // hộp thư khách → không
        bus.Publish(new ChatEvent("A", 4, "noi-bo-phong", null) { Recipients = new[] { 5 } });
        await doc.WaitAsync(cts.Token);
        Assert.Equal(new long[] { 1, 4 }, nhan.Select(e => e.ConversationId).ToArray());
    }

    [Fact]
    public void Cau_thong_bao_tham_gia()
        => Assert.Equal("Trần Thu Hà đã tham gia phòng", InternalRoomNotice.Text("Trần Thu Hà", "vào", null));
}
