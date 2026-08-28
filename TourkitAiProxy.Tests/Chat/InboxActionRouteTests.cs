using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class InboxActionRouteTests
{
    [Fact]
    public void Bang_theo_doi_phai_khoa_theo_TUNG_NGUOI()
    {
        // Theo dõi là chuyện của từng người, không phải của cả công ty. Thiếu username trong khóa
        // chính thì A bỏ theo dõi là B mất theo dõi theo — hỏng im lặng, giống hệt lỗi cột
        // agent_last_read_at dùng chung trước đây.
        var sql = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        var m = Regex.Match(sql,
            @"CREATE TABLE IF NOT EXISTS chat_conversation_follows[\s\S]*?PRIMARY KEY \(([^)]*)\)");
        Assert.True(m.Success, "Không thấy bảng chat_conversation_follows");

        var cot = m.Groups[1].Value;
        Assert.Contains("tenant_id", cot);
        Assert.Contains("conversation_id", cot);
        Assert.Contains("username", cot);
    }

    [Fact]
    public void Danh_sach_theo_doi_phai_tra_co_va_loc_theo_nguoi_dang_xem()
    {
        // Nếu không trả cờ thì giao diện không biết nên hiện Theo dõi hay Bỏ theo dõi; nếu lọc
        // không khóa username thì danh sách của A lại có ca B đang quan tâm.
        var model = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Domain/Chat/ChatModels.cs");
        var repo = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

        Assert.Contains("public bool Followed", model);
        var m = Regex.Match(repo, "ListConversationsAsync(.{0,2600})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy ListConversationsAsync trong ChatRepository");
        Assert.Contains("bool chiTheoDoi = false", m.Groups[1].Value);
        Assert.Contains("AS followed", m.Groups[1].Value);
        Assert.Contains("NOT @chiTheoDoi OR EXISTS", m.Groups[1].Value);
        Assert.Contains("f.username = @nguoiDung", m.Groups[1].Value);
    }

    [Fact]
    public void Duong_theo_doi_phai_xac_nhan_hoi_thoai_va_ghi_nhat_ky_khong_co_noi_dung()
    {
        // POST lẫn DELETE đều phải xác nhận tenant trước khi ghi/xóa và chỉ audit tên thao tác,
        // không được chép nội dung tin vào chat_audit.
        var endpoint = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        Assert.Contains("bool? followed", endpoint);
        Assert.Contains("followed == true", endpoint);
        Assert.Contains("g.MapPost(\"/conversations/{id:long}/follow\"", endpoint);
        Assert.Contains("g.MapDelete(\"/conversations/{id:long}/follow\"", endpoint);

        var post = Regex.Match(endpoint, "MapPost\\(\\\"/conversations/\\{id:long\\}/follow\\\"(.{0,1200})", RegexOptions.Singleline);
        var delete = Regex.Match(endpoint, "MapDelete\\(\\\"/conversations/\\{id:long\\}/follow\\\"(.{0,1200})", RegexOptions.Singleline);
        Assert.True(post.Success && delete.Success, "Thiếu đường theo dõi hoặc bỏ theo dõi");
        foreach (var route in new[] { post.Groups[1].Value, delete.Groups[1].Value })
        {
            Assert.Contains("GetConversationAsync(a.TenantId, id, ct) is null", route);
            Assert.Contains("SetFollowAsync(a.TenantId, id, a.Username", route);
            Assert.Contains("AppendAuditAsync(a.TenantId, id, a.Username", route);
            Assert.Contains(", null, ct)", route);
        }
    }

    [Fact]
    public void Giao_dien_phai_co_loc_va_bao_loi_khi_theo_doi_khong_thanh_cong()
    {
        var ui = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.Contains("'toi-theo-doi'", ui);
        Assert.Contains("q.set('followed', 'true')", ui);
        Assert.Contains("Tôi theo dõi", ui);
        Assert.Contains("/follow'", ui);
        Assert.Contains("Không cập nhật theo dõi được", ui);
        Assert.Contains("await r.json().catch(() => ({}))", ui);
    }

    [Fact]
    public void Chi_tiet_hoi_thoai_phai_tra_dung_co_theo_doi_cua_nguoi_dang_xem()
    {
        // Nút nằm ở khung chi tiết. Chỉ chọn cờ ở danh sách thì mở hội thoại lên lại hiện nút sai,
        // và vừa theo dõi xong tải lại chi tiết cũng mất trạng thái mới.
        var repo = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
        var endpoint = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var get = Regex.Match(repo, "GetConversationAsync(.{0,1000})", RegexOptions.Singleline);
        Assert.True(get.Success, "Không thấy GetConversationAsync trong ChatRepository");
        Assert.Contains("AS followed", get.Groups[1].Value);
        Assert.Contains("nguoiDung: a.Username", endpoint);
    }

    [Fact]
    public void Bo_theo_doi_trong_bo_loc_phai_bo_danh_sach_cu_truoc_khi_tai_lai()
    {
        // taiDsach() giữ các trang đã cuộn để tránh giật lúc SSE cập nhật. Sau khi BỎ theo dõi
        // trong bộ lọc "Tôi theo dõi", giữ quy tắc đó sẽ để chính hội thoại vừa bỏ còn nằm trong
        // danh sách — trái với lời hứa của bộ lọc. Callback phải dùng đúng reset có sẵn trước khi tải.
        var ui = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        // Chữ ký nhận tham số từ 28/08 (menu trên từng DÒNG phải tác động đúng dòng đó, không
        // phải hội thoại đang mở) — bắt mở ngoặc thôi, đừng ghim danh sách tham số.
        var m = Regex.Match(ui, @"async function doiTheoDoi\([^)]*\)(.{0,1000})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy callback doiTheoDoi");
        Assert.Matches(@"setDsach\(\[\]\);\s*setConTro\(null\);\s*await taiDsach\(\);", m.Groups[1].Value);
    }
}
