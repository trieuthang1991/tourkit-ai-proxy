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
    public void Phong_da_co_khong_ai_tu_vao_chi_truong_nhom_them()
    {
        // Chốt 28/10/2026: phòng đã tạo xong thì KHÔNG ai tự vào — kể cả người chưa từng ở, tự rời hay bị bỏ. Có
        // CHAT_XEM_ALL thì chỉ xem; muốn nhắn phải được TRƯỞNG NHÓM thêm.
        var ep = Ep();
        Assert.DoesNotContain("/join\"", ep);                                     // không còn đường tự vào
        Assert.DoesNotContain("JoinRoomAsync", Repo());
        var tao = InternalChatGuardTests.ThanHam(Repo(), "CreateOrGetRoomAsync").Replace("\r\n", "\n");
        Assert.Contains(": Array.Empty<int>();", tao);                             // phòng đã có: không thêm ai (cả người bấm)
        Assert.Contains("if (ids.Length > 0)", tao);
        Assert.Contains("if (cid is not null && !cmember)", ep);                   // POST /rooms phòng đã có mà không ở → 403
        Assert.Contains("var canView = !member && id is not null", ep);            // lookup: không ở → chỉ xem (nếu có quyền)
        var them = InternalChatGuardTests.ThanHam(Repo(), "AddMembersAsync");
        Assert.Contains("v.owner_user_id = @userId", them);                        // chỉ trưởng nhóm thêm người
        Assert.Contains("removed_by = NULL", them);                                // được thêm lại thì hết dấu bị bỏ
    }

    [Fact]
    public void Bo_nguoi_ghi_dau_bi_bo_tu_roi_thi_khong()
    {
        var bo = InternalChatGuardTests.ThanHam(Repo(), "RemoveMemberAsync");
        Assert.Contains("SET left_utc = now(), removed_by = @userId", bo);
        Assert.Contains("SET left_utc = now(), removed_by = NULL", bo);
        Assert.Contains("ADD COLUMN IF NOT EXISTS removed_by integer",
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs"));
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
    public void Khong_co_quyen_chat_nao_thi_moi_duong_chat_noi_bo_deu_403()
    {
        // Chốt 28/10/2026: cần CHAT_XEM (cuộc mình ở) hoặc CHAT_XEM_ALL (mọi cuộc). Chốt nằm trong GuardAsync — mọi endpoint
        // của nhóm (kể cả /events) đi qua đó, không endpoint nào tự kiểm riêng mà quên.
        var ep = Ep();
        var guard = InternalChatGuardTests.ThanHam(ep, "GuardAsync");
        Assert.Contains("CanUseInternalChatAsync", guard);
        Assert.Contains("statusCode: 403", guard);
        var auth = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/SessionAuth.cs");
        var than = InternalChatGuardTests.ThanHam(auth, "CanUseInternalChatAsync");
        Assert.Contains("TkPermissionCodes.ChatXem)", than);
        Assert.Contains("IsQuanTriChatAsync(sid, sessions, ct)", than);   // "xem tất cả" đi qua nguồn duy nhất
    }

    [Fact]
    public void Ca_nhan_chi_thay_phong_minh_o_tat_ca_moi_thay_het()
    {
        // CHAT_XEM chỉ: danh sách luồng thường (phòng mình ở). Chế độ "mọi phòng" chỉ bật bằng CHAT_XEM_ALL.
        var ep = Ep();
        Assert.Contains("var viewAll = await SessionAuth.IsQuanTriChatAsync(a!.SessionId, sessions, ct);", ep);
        Assert.Contains("ListAllRoomsAsync(c, tenant, userId", InternalChatGuardTests.ThanHam(Repo(), "ListRoomsAsync"));
        Assert.Contains("viewAll\n            ? await ListAllRoomsAsync", InternalChatGuardTests.ThanHam(Repo(), "ListRoomsAsync").Replace("\r\n", "\n"));
    }

    [Fact]
    public void Doi_quyen_ben_CRM_phai_toi_proxy_khong_dong_bang()
    {
        // Lỗi 28/10/2026: bỏ tick "Xem tất cả đoạn chat" mà proxy vẫn cho xem tất cả — quyền nạp MỘT lần rồi đứng yên
        // (phiên được SSO dùng lại, EnsurePermissionsAsync no-op khi đã loaded). Nay: hết TTL thì nạp lại, và đăng nhập SSO
        // lại thì nạp ngay.
        var store = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/TourKit/TkSessionStore.cs");
        var ensure = InternalChatGuardTests.ThanHam(store, "EnsurePermissionsAsync");
        Assert.Contains("PermsTtl", ensure);
        Assert.DoesNotContain("if (s == null || s.PermissionsLoaded) return;", ensure);
        Assert.Contains("TimeSpan.FromMinutes(5)", store);
        var sso = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/CrmSsoEndpoints.cs");
        Assert.Contains("RefreshPermissionsAsync(s.Id", sso);
    }
}
