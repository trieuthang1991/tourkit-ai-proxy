using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Trưởng nhóm phòng chat nội bộ (chốt 27/10/2026): luật loại phòng, chữ ký CRM cho phòng đơn/công ty,
/// và canh SQL — chỉ trưởng nhóm bỏ người khác / chuyển quyền / xoá phòng.
/// </summary>
public class InternalRoomOwnerTests
{
    private const string Secret = "khoa-bi-mat-du-dai-16";

    [Theory]
    [InlineData("group", true, true, false)]
    [InlineData("deal", true, true, false)]
    [InlineData("key", true, true, false)]
    [InlineData("order", true, false, true)]
    [InlineData("company", true, false, true)]
    [InlineData("direct", false, false, false)]
    [InlineData(null, false, false, false)]
    public void Luat_theo_loai_phong(string? type, bool hasOwner, bool canTransfer, bool crmOwned)
    {
        Assert.Equal(hasOwner, InternalRoomOwner.HasOwner(type));
        Assert.Equal(canTransfer, InternalRoomOwner.CanTransfer(type));
        Assert.Equal(crmOwned, InternalRoomOwner.IsCrmOwned(type));
    }

    [Fact]
    public void Chu_ky_dung_thi_nhan()
    {
        var sig = InternalRoomOwnerSignature.Sign(Secret, "demo.tourkit.vn", "order:1-123", 45);
        Assert.True(InternalRoomOwnerSignature.Verify(Secret, "demo.tourkit.vn", "order:1-123", 45, sig));
        Assert.True(InternalRoomOwnerSignature.Verify(Secret, "DEMO.tourkit.vn ", "order:1-123", 45, sig.ToUpperInvariant()));
    }

    [Fact]
    public void Chu_ky_khop_HMAC_hex_thuong_tren_chuoi_tenant_khoa_nguoi()
    {
        // CRM (.NET Framework) ký y hệt: HMACSHA256(UTF8(secret)) trên "{tenant}|{roomKey}|{owner}", hex thường.
        using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(Secret));
        var mong = string.Concat(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes("demo.tourkit.vn|order:1-123|45"))
            .Select(b => b.ToString("x2")));
        Assert.Equal(mong, InternalRoomOwnerSignature.Sign(Secret, "demo.tourkit.vn", "order:1-123", 45));
    }

    [Theory]
    [InlineData("khac.tourkit.vn", "order:1-123", 45)]   // chép sang công ty khác
    [InlineData("demo.tourkit.vn", "order:1-124", 45)]   // chép sang đơn khác
    [InlineData("demo.tourkit.vn", "order:1-123", 46)]   // tự khai mình là trưởng nhóm
    public void Chu_ky_khong_chep_sang_cho_khac(string tenant, string key, int owner)
    {
        var sig = InternalRoomOwnerSignature.Sign(Secret, "demo.tourkit.vn", "order:1-123", 45);
        Assert.False(InternalRoomOwnerSignature.Verify(Secret, tenant, key, owner, sig));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("khong-phai-hex")]
    public void Chu_ky_thieu_hoac_hong_thi_bo(string? sig)
        => Assert.False(InternalRoomOwnerSignature.Verify(Secret, "demo.tourkit.vn", "order:1-123", 45, sig));

    [Fact]
    public void Chua_cau_hinh_secret_thi_khong_nhan_chu_ky_nao()
    {
        var sig = InternalRoomOwnerSignature.Sign(Secret, "demo.tourkit.vn", "order:1-123", 45);
        Assert.False(InternalRoomOwnerSignature.Verify(null, "demo.tourkit.vn", "order:1-123", 45, sig));
        Assert.False(InternalRoomOwnerSignature.Verify("", "demo.tourkit.vn", "order:1-123", 45, sig));
        Assert.False(InternalRoomOwnerSignature.Verify(Secret, "demo.tourkit.vn", "order:1-123", 0, sig));
    }

    [Theory]
    [InlineData("chuyển quyền", "Lê Văn Phúc", "Trần Thu Hà đã chuyển quyền trưởng nhóm cho Lê Văn Phúc")]
    [InlineData("rời, chuyển quyền", "Lê Văn Phúc", "Trần Thu Hà đã rời phòng — Lê Văn Phúc là trưởng nhóm mới")]
    [InlineData("thêm trưởng nhóm", "Đỗ Hồng Nhung", "Đỗ Hồng Nhung (người tạo) đã được thêm vào phòng và là trưởng nhóm")]
    [InlineData("đổi trưởng nhóm", "Đỗ Hồng Nhung", "Đỗ Hồng Nhung (người tạo) là trưởng nhóm")]
    public void Cau_thong_bao_truong_nhom(string viec, string gi, string mong)
        => Assert.Equal(mong, InternalRoomNotice.Text("Trần Thu Hà", viec, gi));

    // ── SQL ──────────────────────────────────────────────────────────────

    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs");

    [Fact]
    public void Bo_nguoi_khac_chi_truong_nhom()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "RemoveMemberAsync");
        Assert.Contains("v.owner_user_id = @userId", than);
        Assert.Contains("targetUserId != userId", than);
    }

    [Fact]
    public void Truong_nhom_roi_thi_quyen_sang_nguoi_vao_som_nhat_cung_cau()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "RemoveMemberAsync");
        Assert.Contains("ORDER BY mb.joined_utc, mb.user_id LIMIT 1", than);
        Assert.Contains("mb.user_id <> @userId", than);   // CTE thấy dữ liệu trước khi đổi — phải tự loại người rời
        Assert.Contains("SET owner_user_id = (SELECT user_id FROM n)", than);
    }

    [Fact]
    public void Chuyen_quyen_chi_truong_nhom_khong_cho_phong_don_cong_ty()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "TransferOwnerAsync");
        Assert.Contains("v.owner_user_id = @userId", than);
        Assert.Contains("NOT IN ('direct', 'order', 'company')", than);
        Assert.Contains("mb.user_id = @targetUserId AND mb.left_utc IS NULL", than);   // người nhận phải đang ở phòng
    }

    [Fact]
    public void Xoa_phong_chi_truong_nhom_xoa_mem_va_giai_phong_khoa()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "DeleteRoomAsync");
        Assert.Contains("owner_user_id = @userId", than);
        Assert.Contains("deleted_utc = now()", than);
        Assert.Contains("contact_external_id = contact_external_id || '#xoa:'", than);
        Assert.Contains("SET left_utc = now()", than);
        Assert.DoesNotContain("DELETE FROM", than);
    }

    [Fact]
    public void Dong_bo_truong_nhom_CRM_chi_khi_nguoi_do_dang_o_phong()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "SyncOwnerCoreAsync");
        Assert.Contains("entity_type IN ('order', 'company')", than);
        Assert.Contains("IS DISTINCT FROM @ownerUserId", than);   // giống thì không ghi — mở lại không sinh dòng chết
        Assert.Contains("mb.user_id = @ownerUserId AND mb.left_utc IS NULL", than);
    }

    [Fact]
    public void Nguoi_tao_ban_ghi_chua_tung_o_phong_thi_them_da_roi_thi_khong_keo_lai()
    {
        // Chốt 27/10/2026: "chưa từng ở" = không có dòng nào (kể cả dòng đã rời). Lọc theo left_utc ở câu chèn là
        // kéo lại người đã rời — đúng thứ user không muốn.
        var than = InternalChatGuardTests.ThanHam(Repo(), "SyncOwnerCoreAsync");
        var chen = System.Text.RegularExpressions.Regex.Match(than, @"ins AS \(([\s\S]*?)RETURNING user_id\)").Groups[1].Value;
        Assert.Contains("NOT EXISTS", chen);
        Assert.DoesNotContain("left_utc", chen);
        Assert.Contains("ON CONFLICT (conversation_id, user_id) DO NOTHING", chen);   // không bao giờ xoá left_utc của ai
        Assert.Contains("EXISTS (SELECT 1 FROM ins)", than);   // nhận quyền cả khi vừa được thêm (CTE thấy dữ liệu cũ)
    }

    [Fact]
    public void Schema_co_cot_truong_nhom_va_gan_cho_phong_cu()
    {
        var db = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        Assert.Contains("ADD COLUMN IF NOT EXISTS owner_user_id integer", db);
        Assert.Contains("ADD COLUMN IF NOT EXISTS deleted_utc   timestamptz", db);
        Assert.Contains("ix_conv_internal_no_owner", db);
    }

    [Fact]
    public void Endpoint_chi_tin_truong_nhom_CRM_khi_co_chu_ky()
    {
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/InternalChatEndpoints.cs");
        Assert.Contains("InternalRoomOwnerSignature.Verify(", src);
        Assert.Contains("\"/rooms/{id:long}/owner\"", src);
        Assert.Contains("MapDelete(\"/rooms/{id:long}\"", src);
    }
}
