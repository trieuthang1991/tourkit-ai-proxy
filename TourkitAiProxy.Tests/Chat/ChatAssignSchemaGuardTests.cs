using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh schema phân công. Không có CI chạy PostgreSQL nên đây là lớp duy nhất: đọc thẳng
/// câu SQL trong ChatDb.cs.
/// </summary>
public class ChatAssignSchemaGuardTests
{
    private static string Sql() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");

    [Fact]
    public void Them_cot_nguoi_phu_trach_bang_ma_so()
    {
        // Cột cũ assigned_username giữ nguyên; cột mới mới là khoá so quyền.
        Assert.Contains("ADD COLUMN IF NOT EXISTS assigned_user_id integer", Sql());
        Assert.Contains("assigned_username", Sql());
    }

    [Fact]
    public void Doi_truc_la_MOT_COT_chu_khong_phai_bang_rieng()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_assign_settings", Sql());
        // Sau khi khoá định danh đổi sang mã người thì mỗi thành viên đội trực chỉ còn đúng
        // MỘT con số — tách bảng cho một cột số là thêm một lượt đọc mà không được gì.
        Assert.Contains("member_ids integer[]", Sql());
        Assert.DoesNotContain("chat_assign_members", Sql());
    }

    [Fact]
    public void Ma_nguoi_phu_trach_phai_la_int_CO_THE_NULL()
    {
        // NULL = chưa ai phụ trách. Khai `int` trần thì Dapper đổi NULL thành 0, KHÔNG báo lỗi,
        // và hai thứ chết theo:
        //   • vòng quay ngừng hẳn — điều kiện gán là `assigned_user_id IS NULL`, ghi 0 thì nó
        //     không bao giờ đúng nữa;
        //   • nhả việc không trả hội thoại về hàng chờ — nó thành "của" người mã 0 không tồn tại.
        var model = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Domain/Chat/ChatModels.cs");
        Assert.Contains("int? AssignedUserId", model);
    }

    [Fact]
    public void Con_tro_xoay_vong_luu_MA_NGUOI_chu_khong_phai_vi_tri()
    {
        // Lưu số thứ tự thì thêm/bớt một người là cả vòng lệch — im lặng.
        Assert.Contains("rotation_last_user_id", Sql());
        Assert.DoesNotContain("rotation_index", Sql());
    }

    [Fact]
    public void Moi_lenh_schema_deu_idempotent()
    {
        // SchemaSql chạy MỖI LẦN khởi động. Một lệnh không IF NOT EXISTS là app chết ở lần
        // khởi động thứ hai.
        var sql = Sql();
        foreach (var manh in new[] { "chat_assign_settings" })
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {manh}", sql);
    }
}
