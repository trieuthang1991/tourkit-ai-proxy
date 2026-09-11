using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Chốt cho các đường chat ĐẨY VIỆC SANG CRM — ghi nhận chăm sóc, và sau này là Cơ hội bán hàng.
///
/// <para><b>Luật gốc, chủ dự án chốt 11/09/2026:</b> mọi thứ ghi sang hệ ngoài đều để lại. Phần
/// cần bắn sang CRM thì thả vào hàng đợi để xem và chuẩn hoá trước, rồi mới có đường đồng bộ.</para>
///
/// <para>Vi phạm luật này là kiểu vi phạm KHÔNG lộ ra: gọi thẳng CRM vẫn chạy được, vẫn trả 200,
/// vẫn trông đúng trên màn hình — chỉ là dữ liệu chưa chuẩn đã nằm trong CRM thật rồi, và không
/// có đường lùi. Nên phải canh ở mức mã nguồn chứ không trông vào việc nhớ.</para>
/// </summary>
public class ChatHangDoiCrmGuardTests
{
    private static string Endpoint()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    private static string ThanRoute(string route)
        => ChatSchemaGuardTests.ThanThanhVien(Endpoint(), route);

    [Fact]
    public void Duong_cham_soc_THA_HANG_DOI_chu_khong_goi_CRM()
    {
        var than = ThanRoute("g.MapPost(\"/conversations/{id:long}/cham-soc\"");

        Assert.Contains("EnqueueAsync", than);
        // Mọi lối ghi sang CRM đều đi qua TourKitApiClient. Không lối nào được xuất hiện ở đây.
        Assert.DoesNotContain("api.PostAsync", than);
        Assert.DoesNotContain("api.PutAsync", than);
        Assert.DoesNotContain("api.PatchAsync", than);
    }

    /// <summary>
    /// Chưa nối khách CRM thì TỪ CHỐI, không thả dòng.
    ///
    /// <para><c>CreateCustomerCareRequest.CustomerId</c> là bắt buộc bên CRM. Thả một dòng thiếu
    /// mã khách là đẩy cho worker một việc chắc chắn hỏng — mà lúc nó hỏng thì người bấm nút đã
    /// rời máy từ lâu, và thứ họ thấy lúc bấm là một thông báo thành công.</para>
    /// </summary>
    [Fact]
    public void Duong_cham_soc_doi_hoi_thoai_da_noi_khach_CRM()
    {
        var than = ThanRoute("g.MapPost(\"/conversations/{id:long}/cham-soc\"");

        Assert.Contains("CrmCustomerId", than);
        // Và phải từ chối trước khi thả dòng, không phải thả rồi mới xét.
        var viTriTuChoi = than.IndexOf("CrmCustomerId", System.StringComparison.Ordinal);
        var viTriTha = than.IndexOf("EnqueueAsync", System.StringComparison.Ordinal);
        Assert.True(viTriTuChoi < viTriTha,
            "Xét điều kiện nối khách SAU khi đã thả dòng — dòng hỏng vẫn nằm trong hàng đợi.");
    }

    [Fact]
    public void Duong_cham_soc_ghi_nhat_ky_va_gan_dung_nguon()
    {
        var than = ThanRoute("g.MapPost(\"/conversations/{id:long}/cham-soc\"");

        Assert.Contains("GhiNhatKyAsync", than);
        // Nguồn lấy từ hằng, không gõ chuỗi tay ở endpoint: gõ tay thì chỗ đọc và chỗ ghi lệch
        // nhau một dấu gạch là hàng đợi lọc không ra gì mà không ai thấy lỗi.
        Assert.Contains("CrmActionNguon.ChamSoc", than);
        Assert.Contains("CrmActionKind.CreateAppointment", than);
    }

    /// <summary>
    /// Danh sách việc của một hội thoại KHÔNG được trả nội dung gói tin ra giao diện.
    ///
    /// <para>Gói tin mang tên và số điện thoại khách. Khối này hiện cho mọi người có quyền vào hội
    /// thoại, trong khi trang theo dõi hàng đợi — nơi xem được gói tin — đã gác quyền quản trị.
    /// Trả nó ở đây là lặng lẽ mở rộng phạm vi người đọc được dữ liệu khách.</para>
    /// </summary>
    [Fact]
    public void Danh_sach_viec_theo_hoi_thoai_KHONG_tra_goi_tin()
    {
        var kho = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Crm/CrmActionQueueRepository.cs");
        var than = ChatSchemaGuardTests.ThanThanhVien(kho,
            "public async Task<List<CrmActionRow>> ListByReferAsync");

        Assert.Contains("'' AS PayloadJson", than);
    }
}
