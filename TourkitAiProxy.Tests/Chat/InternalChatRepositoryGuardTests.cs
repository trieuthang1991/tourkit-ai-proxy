using System.Text.RegularExpressions;
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Khoá phòng chat nội bộ (hàm thuần) + canh SQL của InternalChatRepository ở mức mã nguồn.
/// Canh đúng những chỗ đã chọn vì hiệu năng/cô lập tenant, không canh tổng quát.
/// </summary>
public class InternalChatRepositoryGuardTests
{
    [Fact]
    public void Khoa_1_1_khong_phu_thuoc_thu_tu()
    {
        Assert.Equal("direct:3:5", InternalRoomKey.Direct(5, 3));
        Assert.Equal(InternalRoomKey.Direct(3, 5), InternalRoomKey.Direct(5, 3));
    }

    [Fact]
    public void Khoa_1_1_tu_chat_voi_minh_thi_nem()
        => Assert.Throws<ArgumentException>(() => InternalRoomKey.Direct(4, 4));

    [Fact]
    public void Khoa_entity_dung_dinh_dang()
    {
        Assert.Equal("order:123", InternalRoomKey.ForEntity("order", "123"));
        Assert.Equal("company:9", InternalRoomKey.ForEntity("company", " 9 "));
        // Cơ hội bán hàng (BookingTicket) — loại RIÊNG, không dùng chung "order": hai thứ khác
        // nhau ở CRM, dùng chung khoá thì cơ hội #12 và đơn #12 rơi vào cùng một phòng.
        Assert.Equal("deal:12", InternalRoomKey.ForEntity("deal", "12"));
        Assert.NotEqual(InternalRoomKey.ForEntity("order", "12"), InternalRoomKey.ForEntity("deal", "12"));
        Assert.Throws<ArgumentException>(() => InternalRoomKey.ForEntity("lead", "1"));
        Assert.Throws<ArgumentException>(() => InternalRoomKey.ForEntity("order", " "));
    }

    [Fact]
    public void Khoa_nhom_moi_lan_khac_nhau()
    {
        var a = InternalRoomKey.Group();
        Assert.StartsWith("group:", a);
        Assert.NotEqual(a, InternalRoomKey.Group());
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("order", true)]
    [InlineData("company", true)]
    [InlineData("group", true)]
    [InlineData("deal", true)]
    [InlineData("lead", false)]
    [InlineData(null, false)]
    public void Loai_phong_hop_le(string? t, bool ok) => Assert.Equal(ok, InternalRoomKey.IsValidType(t));

    // ── Tệp đính kèm ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("anh.PNG", true)]
    [InlineData("bao-gia.pdf", true)]
    [InlineData("ds.xlsx", true)]
    [InlineData("trang.html", false)]   // kho local phục vụ trên miền proxy → chạy được script
    [InlineData("logo.svg", false)]
    [InlineData("x.js", false)]
    [InlineData("khong-duoi", false)]
    [InlineData("", false)]
    public void Chi_nhan_duoi_tep_an_toan(string ten, bool ok) => Assert.Equal(ok, InternalAttachmentRules.IsAllowed(ten));

    [Fact]
    public void Loai_tep_theo_duoi()
    {
        Assert.Equal("anh", InternalAttachmentRules.KindOf("a.JPG"));
        Assert.Equal("tep", InternalAttachmentRules.KindOf("a.pdf"));
    }

    [Fact]
    public void Url_dinh_kem_phai_cua_kho_dung_cong_ty_dung_phong()
    {
        var bases = new[] { "https://upload.travelai.vn/" };
        Assert.True(InternalAttachmentRules.IsOwnUrl("https://upload.travelai.vn/internal/staging/12/ab.png", "staging", 12, bases));
        Assert.False(InternalAttachmentRules.IsOwnUrl("https://upload.travelai.vn/internal/staging/13/ab.png", "staging", 12, bases));
        Assert.False(InternalAttachmentRules.IsOwnUrl("https://upload.travelai.vn/internal/erp/12/ab.png", "staging", 12, bases));
        Assert.False(InternalAttachmentRules.IsOwnUrl("https://evil.example/internal/staging/12/ab.png", "staging", 12, bases));
        Assert.False(InternalAttachmentRules.IsOwnUrl("javascript:alert(1)//https://upload.travelai.vn/internal/staging/12/", "staging", 12, bases));
        Assert.False(InternalAttachmentRules.IsOwnUrl("https://upload.travelai.vn/internal/staging/12/../13/x.png", "staging", 12, bases));
        Assert.False(InternalAttachmentRules.IsOwnUrl(null, "staging", 12, bases));
        // Trình duyệt tự giải %2e%2e thành ".." → thoát khỏi thư mục phòng. Kho không bao giờ cấp URL
        // có %, ? hay # nên chặn hết, khỏi phải đoán từng kiểu mã hoá.
        Assert.False(InternalAttachmentRules.IsOwnUrl("https://upload.travelai.vn/internal/staging/12/%2e%2e/13/x.png", "staging", 12, bases));
        Assert.False(InternalAttachmentRules.IsOwnUrl("https://upload.travelai.vn/internal/staging/12/a.png?x=1", "staging", 12, bases));
        Assert.False(InternalAttachmentRules.IsOwnUrl("https://upload.travelai.vn/internal/staging/12/a.png#x", "staging", 12, bases));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("khong-phai-json")]
    [InlineData("[1,2]")]
    [InlineData("{\"ten\":\"a.png\"}")]
    public void Goi_dinh_kem_hong_thi_bo_qua(string? json)
        => Assert.Null(TourkitAiProxy.Infrastructure.Chat.Inbox.InternalChatRepository.ReadAttachment(json));

    [Fact]
    public void Doc_goi_dinh_kem_hop_le()
    {
        var a = TourkitAiProxy.Infrastructure.Chat.Inbox.InternalChatRepository.ReadAttachment(
            "{\"url\":\"https://x/internal/t/1/a.png\",\"ten\":\"Ảnh chụp.png\",\"kich\":2048,\"loai\":\"anh\"}");
        Assert.NotNull(a);
        Assert.Equal("Ảnh chụp.png", a!.Name);
        Assert.Equal(2048, a.Size);
        Assert.Equal("anh", a.Kind);
    }

    // ── Quyền vào phòng theo cơ hội: hỏi CRM bằng phiên của chính người đó ──

    [Theory]
    [InlineData(200, null)]    // chỉ gọi từ nhánh NGOẠI LỆ — 2xx không bao giờ tới đây, không đoán
    [InlineData(403, false)]   // CRM từ chối dứt khoát
    [InlineData(404, false)]   // không có / không thuộc phạm vi xem
    [InlineData(502, null)]    // CRM hỏng → KHÔNG BIẾT (chỗ gọi phải chặn, không được cho qua)
    [InlineData(500, null)]
    public void Quyen_xem_co_hoi_tu_ma_CRM(int status, bool? ketQua)
        => Assert.Equal(ketQua, TourkitAiProxy.Services.Deals.DealOpportunityClient.ViewAccessFromStatus(status));

    // Luật bên web (BookingTicketAction: CH_XEM_ALL xem tất cả; chỉ CH_XEM thì lọc theo chính mình).
    [Theory]
    [InlineData(7, 7, null, true)]           // người tạo
    [InlineData(7, 3, ",1,7,9,", true)]      // người phụ trách (DB lưu ",1,7,9,")
    [InlineData(7, 3, "1,7", true)]          // dạng CSV thường
    [InlineData(7, 3, ",17,70,", false)]     // KHÔNG khớp nhầm 17/70 với 7
    [InlineData(7, 3, null, false)]
    [InlineData(7, 3, "", false)]
    [InlineData(7, null, " , ,abc", false)]  // dữ liệu bẩn
    public void Co_hoi_cua_minh(int me, int? insUid, string? phuTrach, bool ok)
        => Assert.Equal(ok, TourkitAiProxy.Domain.Deals.DealAccess.IsOwner(me, insUid, phuTrach));

    [Theory]
    [InlineData(",1,7,9,", new[] { 1, 7, 9 })]
    [InlineData("7, 7 ,3", new[] { 7, 3 })]
    [InlineData(" , ,abc,0,-2", new int[0])]
    [InlineData(null, new int[0])]
    public void Tach_ma_nguoi_phu_trach(string? csv, int[] mong)
        => Assert.Equal(mong, TourkitAiProxy.Domain.Deals.DealAccess.ParseUserIds(csv));

    [Fact]
    public void Phong_co_hoi_moi_tao_moi_them_nguoi_phu_trach_mot_lan()
    {
        // Thêm người phụ trách CHỈ khi phòng vừa được tạo — thêm ở mọi lần bấm là kéo lại người đã
        // bị bỏ ra khỏi phòng.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/InternalChatEndpoints.cs");
        Assert.Matches(@"created\s*&&\s*type\s*==\s*""deal""|type\s*==\s*""deal""\s*&&\s*created", src);
        // "Vừa tạo" = INSERT … DO NOTHING trả được dòng; không trả thì là phòng đã có.
        var tao = InternalChatGuardTests.ThanHam(ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs"), "CreateOrGetRoomAsync");
        Assert.Contains("DO NOTHING", tao);
        Assert.Contains("created = false", tao);
    }

    [Fact]
    public void Vao_phong_co_hoi_phai_kiem_quyen_CRM_khi_chua_la_thanh_vien()
    {
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/InternalChatEndpoints.cs");
        Assert.Contains("IsMemberByKeyAsync(", src);
        Assert.Contains("GetDetailAsync(", src);
    }

    // ── SQL ──────────────────────────────────────────────────────────────

    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs");

    [Fact]
    public void Moi_cau_SQL_deu_kep_tenant()
    {
        var khoi = Regex.Matches(Repo(), "\"\"\"([\\s\\S]*?)\"\"\"").Select(m => m.Groups[1].Value)
                        .Where(s => s.Contains("chat_")).ToList();
        Assert.NotEmpty(khoi);
        // Câu CHÈN mang tenant ở VALUES chứ không ở WHERE — vẫn là kẹp tenant.
        foreach (var s in khoi)
            Assert.True(s.Contains("tenant_id = @tenant") || s.Contains("VALUES (@tenant"),
                        "Câu SQL thiếu kẹp tenant:\n" + s);
    }

    [Fact]
    public void Tao_phong_ON_CONFLICT_khop_chi_muc()
    {
        var chiMuc = Regex.Match(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs"),
            @"CREATE UNIQUE INDEX IF NOT EXISTS ux_conv_scope_surface\s+ON chat_conversations \(([^)]*)\)");
        var onConflict = Regex.Match(Repo(), @"INSERT INTO chat_conversations[\s\S]*?ON CONFLICT \(([^)]*)\)");
        Assert.True(chiMuc.Success && onConflict.Success);
        static string Chuan(string s) => string.Join(",", s.Split(',').Select(x => x.Trim()));
        Assert.Equal(Chuan(chiMuc.Groups[1].Value), Chuan(onConflict.Groups[1].Value));
    }

    [Fact]
    public void Them_thanh_vien_mot_cau_unnest()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "CreateOrGetRoomAsync");
        Assert.Contains("unnest(@", than);
        Assert.Contains("ON CONFLICT (conversation_id, user_id) DO UPDATE", than);
        Assert.Contains("left_utc = NULL", than);
    }

    [Fact]
    public void Lich_su_di_dung_chi_muc_ix_msg_conv()
        => Assert.Contains("ORDER BY m.created_utc DESC, m.id DESC",
                           InternalChatGuardTests.ThanHam(Repo(), "ListMessagesAsync"));

    [Theory]
    [InlineData("AppendMessageAsync")]
    [InlineData("ListMessagesAsync")]
    [InlineData("ListMessagesAfterAsync")]
    [InlineData("MarkReadAsync")]
    [InlineData("RenameRoomAsync")]
    [InlineData("AddMembersAsync")]
    [InlineData("RemoveMemberAsync")]
    public void Kiem_thanh_vien_gop_trong_cau(string method)
        => Assert.Contains("FROM chat_room_members", InternalChatGuardTests.ThanHam(Repo(), method));

    [Theory]
    [InlineData("RenameRoomAsync")]
    [InlineData("AddMembersAsync")]
    [InlineData("RemoveMemberAsync")]
    public void Phong_1_1_khoa_cau_hinh(string method)
        => Assert.Contains("entity_type IS DISTINCT FROM 'direct'", InternalChatGuardTests.ThanHam(Repo(), method));

    [Theory]
    [InlineData("EditOwnMessageAsync")]
    [InlineData("DeleteOwnMessageAsync")]
    public void Sua_va_thu_hoi_chi_tin_cua_minh_va_tra_phong_de_bao(string method)
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), method);
        Assert.Contains("sender_user_id = @userId", than);
        Assert.Contains("channel = 7", than);
        Assert.Contains("deleted_utc IS NULL", than);           // tin đã thu hồi thì không sửa/thu hồi lại
        Assert.Contains("RETURNING conversation_id", than);     // để bus báo đúng phòng
    }

    // ── Sửa sau review cuối ──────────────────────────────────────────────

    [Theory]
    [InlineData("EditOwnMessageAsync")]
    [InlineData("DeleteOwnMessageAsync")]
    public void Sua_thu_hoi_cap_nhat_dong_xem_truoc_va_doi_con_la_thanh_vien(string method)
    {
        // Thu hồi mà dòng xem trước của phòng còn chữ cũ thì chữ đó vẫn hiện trên danh sách của MỌI
        // người — đúng thứ thu hồi sinh ra để gỡ. Người đã rời phòng không được sửa lịch sử.
        var than = InternalChatGuardTests.ThanHam(Repo(), method);
        Assert.Contains("last_preview", than);
        Assert.Contains("FROM chat_room_members", than);
    }

    [Fact]
    public void Danh_sach_phong_di_tu_thanh_vien()
    {
        // Đi từ chỉ mục thành viên (vài phòng mỗi người) chứ không đi chỉ mục hoạt động của cả công
        // ty rồi lọc kênh 7 — công ty nhiều hội thoại khách là quét gần hết.
        var than = InternalChatGuardTests.ThanHam(Repo(), "ListRoomsAsync");
        Assert.Contains("AS MATERIALIZED", than);
        Assert.Contains("GREATEST(r.last_read_at, mb.joined_utc", than);   // vào lại phòng: chưa đọc tính từ lúc vào lại
        Assert.Contains("hidden_utc", than);                                 // "Xoá phía tôi"
    }

    [Fact]
    public void Vao_lai_phong_tinh_lai_moc_vao()
        => Assert.Contains("SET joined_utc = now(), left_utc = NULL", InternalChatGuardTests.ThanHam(Repo(), "CreateOrGetRoomAsync"));

    [Fact]
    public void Xoa_phia_toi_la_xoa_mem_rieng_minh()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "HideRoomAsync");
        Assert.Contains("SET hidden_utc = now()", than);
        Assert.Contains("user_id = @userId", than);
        Assert.DoesNotContain("DELETE", than);
        Assert.Contains("ADD COLUMN IF NOT EXISTS hidden_utc timestamptz",
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs"));
    }

    [Theory]
    [InlineData("12", "12")]
    [InlineData("012", "12")]
    [InlineData("+12", "12")]
    [InlineData(" 12 ", "12")]
    [InlineData("abc", null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    public void Ma_co_hoi_chuan_hoa(string vao, string? ra)
        => Assert.Equal(ra, InternalRoomKey.CanonicalDealId(vao));

    // ── Lỗi nhỏ sau review (19/10/2026) ──────────────────────────────────

    // Khoá tự do do trang gọi tự sinh ("donhang_123", "deal_12131") — lấy-hoặc-tạo theo khoá.
    [Theory]
    [InlineData("donhang_123", "key:donhang_123")]
    [InlineData(" deal_12131 ", "key:deal_12131")]
    [InlineData("crm.don-hang:2026/1", null)]   // "/" không cho
    [InlineData("có dấu", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Khoa_tu_do(string? vao, string? ra)
        => Assert.Equal(ra, InternalRoomKey.ForCustomKey(vao));

    [Fact]
    public void Khoa_tu_do_toi_da_100_ky_tu()
    {
        Assert.NotNull(InternalRoomKey.ForCustomKey(new string('a', 100)));
        Assert.Null(InternalRoomKey.ForCustomKey(new string('a', 101)));
        Assert.True(InternalRoomKey.IsValidType("key"));
    }

    [Fact]
    public void Bam_Chat_phong_da_co_khong_ghi_gi()
    {
        // Phòng đã có: KHÔNG ghi dòng hội thoại (DO NOTHING rồi đọc lại) và chỉ ghi thành viên khi
        // người đó VÀO LẠI (đã rời) — trước đây mỗi lần bấm đều UPDATE hai bảng.
        var than = InternalChatGuardTests.ThanHam(Repo(), "CreateOrGetRoomAsync");
        Assert.Contains("DO NOTHING", than);
        Assert.Contains("WHERE chat_room_members.left_utc IS NOT NULL", than);
    }

    [Fact]
    public void Tin_he_thong_khong_tinh_chua_doc()
    {
        Assert.Contains("sender_kind <> 3", InternalChatGuardTests.ThanHam(Repo(), "ListRoomsAsync"));
        var than = InternalChatGuardTests.ThanHam(Repo(), "AppendSystemMessageAsync");
        Assert.Contains("tenant_id = @tenant", than);
        Assert.Contains(", 3,", than);   // sender_kind = 3 (hệ thống)
    }

    [Fact]
    public void Ma_phong_khong_phai_so_tra_JSON_khong_roi_xuong_SPA()
        => Assert.Contains("/{**rest}", ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/InternalChatEndpoints.cs"));

    [Fact]
    public void Nhan_vien_moi_hoi_lai_ERP_bo_qua_dem()
        => Assert.Contains("skipCache: true", ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/InternalChatEndpoints.cs"));

    [Theory]
    [InlineData("Trần Thu Hà", "đổi tên", "Đoàn Nhật", "Trần Thu Hà đã đổi tên phòng thành “Đoàn Nhật”")]
    [InlineData("Trần Thu Hà", "đổi tên", null, "Trần Thu Hà đã bỏ tên phòng")]
    [InlineData("Trần Thu Hà", "thêm", "Nguyễn Minh, Lê Văn Phúc", "Trần Thu Hà đã thêm Nguyễn Minh, Lê Văn Phúc vào phòng")]
    [InlineData("Trần Thu Hà", "bỏ", "Nguyễn Minh", "Trần Thu Hà đã bỏ Nguyễn Minh khỏi phòng")]
    [InlineData("Trần Thu Hà", "rời", null, "Trần Thu Hà đã rời phòng")]
    public void Cau_thong_bao_he_thong(string ai, string viec, string? gi, string mong)
        => Assert.Equal(mong, InternalRoomNotice.Text(ai, viec, gi));

    [Fact]
    public void Schema_co_cot_da_sua()
        => Assert.Contains("ALTER TABLE chat_messages ADD COLUMN IF NOT EXISTS edited_utc timestamptz",
                           ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs"));

    [Fact]
    public void Khong_co_ham_kiem_thanh_vien_rieng()
        => Assert.DoesNotContain("IsMemberAsync", Repo());

    [Fact]
    public void Dem_chua_doc_co_tran()
        => Assert.Contains("LIMIT 100", InternalChatGuardTests.ThanHam(Repo(), "ListRoomsAsync"));

    // ── Bấm Chat chỉ HỎI, chưa tạo: phòng sinh ra lúc gửi tin đầu tiên (chống bấm nhầm) ──
    [Theory]
    [InlineData("deal", "012", "deal:12")]
    [InlineData("deal", " 12 ", "deal:12")]
    [InlineData("order", "DH-0142", "order:DH-0142")]
    [InlineData("company", "55", "company:55")]
    [InlineData("key", "nhom_marketing", "key:nhom_marketing")]
    [InlineData("deal", "abc", null)]
    [InlineData("deal", null, null)]
    [InlineData("order", "", null)]
    [InlineData("key", "có dấu", null)]
    [InlineData("direct", "12", null)]
    [InlineData("group", "x", null)]
    [InlineData(null, "12", null)]
    public void Khoa_tra_phong_theo_ban_ghi(string? loai, string? ma, string? khoa)
        => Assert.Equal(khoa, InternalRoomKey.ForLookup(loai, ma));

    [Fact]
    public void Hoi_phong_theo_ban_ghi_khong_ghi_gi()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "FindRoomByKeyAsync");
        Assert.DoesNotContain("INSERT", than);
        Assert.DoesNotContain("UPDATE", than);
        Assert.Contains("tenant_id = @tenant", than);
        Assert.Contains("left_utc IS NULL", than);
        Assert.Contains("/rooms/lookup", ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/InternalChatEndpoints.cs"));
    }
}
