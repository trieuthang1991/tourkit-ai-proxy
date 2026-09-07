using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Mọi endpoint đụng MỘT hội thoại cụ thể đều phải đi qua cửa chung.
///
/// <para>Trước 07/09/2026 ChatInboxEndpoints dài 2.527 dòng và KHÔNG có một dòng kiểm quyền
/// nào: ai đăng nhập được là đọc được hội thoại của mọi đồng nghiệp. Lọc ở danh sách thôi thì
/// vẫn gõ thẳng /conversations/123 là đọc được — nên cửa phải nằm ở nơi lấy hội thoại ra.</para>
/// </summary>
public class ChatScopeGuardTests
{
    private static string Endpoint() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

    [Fact]
    public void Cua_chung_bat_buoc_nhan_nguoi_xem()
    {
        // Tham số BẮT BUỘC, không phải tuỳ chọn có mặc định: có mặc định thì quên truyền cũng
        // biên dịch được, và lỗ hổng đi thẳng lên bản chạy thật.
        Assert.Matches(@"GetConversationAsync\(\s*string tenant,\s*long id,\s*NguoiXem ", Repo());
    }

    [Fact]
    public void Danh_sach_hoi_thoai_cung_loc_theo_nguoi_xem()
    {
        var m = Regex.Match(Repo(), "ListConversationsAsync(.{0,3000})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy ListConversationsAsync");
        // Điều kiện phải nằm TRONG câu SQL, không phải lọc trong C# sau khi đã đọc hết về.
        Assert.Contains("assigned_user_id", m.Groups[1].Value);
    }

    [Fact]
    public void Moi_route_mot_hoi_thoai_deu_doc_nguoi_xem_tu_phien()
    {
        var src = Endpoint();
        // Đếm route dạng /conversations/{id...}
        var soRoute = Regex.Matches(src, @"g\.Map(?:Get|Post|Put|Patch|Delete)\(""/conversations/\{id").Count;
        Assert.True(soRoute >= 20, $"Chỉ thấy {soRoute} route — biểu thức đã lạc khỏi cách viết thật");

        var soCua = Regex.Matches(src, @"ReadNguoiXemAsync").Count;
        Assert.True(soCua >= soRoute,
            $"Có {soRoute} route đụng một hội thoại nhưng chỉ {soCua} lượt đọc người xem — " +
            "route nào đó đang bỏ qua cửa chung.");
    }

    [Fact]
    public void Hoi_thoai_CHUA_GAN_khong_hien_voi_nguoi_khong_phai_admin()
    {
        // ĐÂY LÀ QUYẾT ĐỊNH, KHÔNG PHẢI SƠ SUẤT (chủ dự án, 07/09/2026). Bản thiết kế đầu có
        // thêm vế "OR assigned_user_id IS NULL" để nhân viên nhìn thấy hàng chờ mà tự nhận;
        // quyết định cuối là BỎ vế đó. Test này khoá lại để đợt sau không ai "sửa" nó.
        var m = Regex.Match(Repo(), "GetConversationAsync(.{0,1500})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy GetConversationAsync");
        var than = m.Groups[1].Value;
        Assert.Contains("@xemTatCa OR v.assigned_user_id = @maNguoi", than);
        Assert.DoesNotContain("assigned_user_id IS NULL", than);
    }

    [Fact]
    public void Khong_duoc_xem_thi_tra_404_chu_khong_403()
    {
        // 403 nghĩa là "có hội thoại này nhưng anh không được xem" — tức xác nhận đúng cái
        // đang giấu. Dò tuần tự theo id là biết công ty có bao nhiêu khách.
        Assert.DoesNotContain("Bạn không được xem hội thoại này", Endpoint());
    }
}
