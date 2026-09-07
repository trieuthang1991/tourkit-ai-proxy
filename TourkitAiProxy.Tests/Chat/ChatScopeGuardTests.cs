using System.Collections.Generic;
using System.Linq;
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
        // biên dịch được, và lỗ hổng đi thẳng lên bản chạy thật. Đòi nguyên văn "NguoiXem xem,"
        // (dấu phẩy ngay sau, không dấu "=") — pattern cũ chỉ đòi "NguoiXem " nên vẫn khớp cả
        // "NguoiXem xem = default!," tức không khoá được đúng điều chú thích này tuyên bố.
        Assert.Matches(@"GetConversationAsync\(\s*string tenant,\s*long id,\s*NguoiXem xem,", Repo());
    }

    [Fact]
    public void Ca_ba_ham_kep_luat_xem_bang_dung_menh_de_SQL()
    {
        // Đòi NGUYÊN VĂN mệnh đề, không chỉ đòi chữ "assigned_user_id" xuất hiện đâu đó — chữ đó
        // cũng nằm sẵn trong SELECT v.* của GetConversationAsync/ListConversationsAsync, nên một
        // assert lỏng sẽ xanh giả kể cả khi một trong ba hàm không hề kẹp luật xem.
        //
        // Phủ CẢ BA hàm đọc hội thoại theo id/tenant — thiếu CountAsync là chip đếm (tổng, chưa
        // đọc, theo kênh) lộ đúng con số mà luật 404 ở hai hàm kia đang giấu.
        var repo = Repo();
        foreach (var ten in new[] { "GetConversationAsync", "ListConversationsAsync", "CountAsync" })
        {
            var m = Regex.Match(repo, ten + @"(.{0,3000})", RegexOptions.Singleline);
            Assert.True(m.Success, $"Không thấy {ten}");
            Assert.Contains("@xemTatCa OR v.assigned_user_id = @maNguoi", m.Groups[1].Value);
        }
    }

    [Fact]
    public void Moi_route_mot_hoi_thoai_deu_doc_nguoi_xem_tu_phien()
    {
        var src = Endpoint();

        // Điểm bắt đầu của MỌI route (không riêng /conversations/{id...}) — dùng làm biên cắt
        // thân từng route. Cắt theo "route kế tiếp bất kỳ" thay vì so hai tổng số: một canary kiểu
        // "tổng route <= tổng lượt đọc người xem" chỉ kêu từ route bỏ sót THỨ HAI trở đi (route đầu
        // bị bỏ sót vẫn giữ đẳng thức tổng số bằng nhau) — cắt từng thân thì route đầu bị bỏ sót
        // cũng lộ ngay, kèm đúng tên route.
        var moiRoute = Regex.Matches(src, @"g\.Map(?:Get|Post|Put|Patch|Delete)\(""[^""]*""")
            .Select(m => m.Index).OrderBy(i => i).ToList();
        Assert.True(moiRoute.Count > 0, "Không thấy route nào trong ChatInboxEndpoints");

        var diemHoiThoai = Regex.Matches(src, @"g\.Map(?:Get|Post|Put|Patch|Delete)\(""(/conversations/\{id[^""]*)""");
        Assert.True(diemHoiThoai.Count >= 20,
            $"Chỉ thấy {diemHoiThoai.Count} route — biểu thức đã lạc khỏi cách viết thật");

        var boQua = new List<string>();
        foreach (Match m in diemHoiThoai)
        {
            var batDau = m.Index;
            var ketThuc = moiRoute.FirstOrDefault(i => i > batDau);
            // Route cuối cùng của cả file: không có route kế tiếp để cắt, lấy hết phần còn lại.
            var than = ketThuc > batDau ? src.Substring(batDau, ketThuc - batDau) : src.Substring(batDau);
            if (!than.Contains("ReadNguoiXemAsync"))
                boQua.Add(m.Groups[1].Value);
        }

        Assert.True(boQua.Count == 0,
            "Route sau đụng một hội thoại nhưng KHÔNG đọc người xem qua cửa chung: " +
            string.Join(", ", boQua));
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
