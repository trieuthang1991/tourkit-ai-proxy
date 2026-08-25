using TourkitAiProxy.Domain.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Đo "nhắc rồi có ai gọi không". Con số này dùng để quyết định GIỮ HAY BỎ cả tính năng, nên sai
/// theo hướng lạc quan là tệ nhất — nó giữ lại một thứ không ai dùng. Vì thế các ca không chắc
/// chắn đều phải rơi về "chưa gọi".
/// </summary>
public class CareFollowUpTests
{
    private static Dictionary<int, DateTime?> Now(params (int Id, string? Date)[] rows)
        => rows.ToDictionary(r => r.Id, r => r.Date == null ? (DateTime?)null : DateTime.Parse(r.Date));

    [Fact]
    public void Ngay_cham_soc_moi_hon_luc_nhac_thi_tinh_la_DA_GOI()
    {
        var (reached, checkedCount) = CareFollowUp.Measure(
            new[] { (1, (string?)"2026-05-11") }, Now((1, "2026-08-15")));
        Assert.Equal(1, reached);
        Assert.Equal(1, checkedCount);
    }

    [Fact]
    public void Ngay_cham_soc_KHONG_doi_thi_chua_ai_goi()
    {
        var (reached, _) = CareFollowUp.Measure(
            new[] { (1, (string?)"2026-05-11") }, Now((1, "2026-05-11")));
        Assert.Equal(0, reached);
    }

    [Fact]
    public void Luc_nhac_chua_tung_duoc_cham_nay_da_co_ngay_thi_la_DA_GOI()
    {
        // Mốc trống = lúc nhắc khách chưa từng được chăm. Nay có ngày → rõ ràng mới liên hệ.
        var (reached, _) = CareFollowUp.Measure(
            new[] { (1, (string?)null) }, Now((1, "2026-08-15")));
        Assert.Equal(1, reached);
    }

    [Fact]
    public void Van_chua_tung_duoc_cham_thi_khong_tinh()
    {
        var (reached, checkedCount) = CareFollowUp.Measure(
            new[] { (1, (string?)null) }, Now((1, null)));
        Assert.Equal(0, reached);
        Assert.Equal(1, checkedCount);   // vẫn tra được → vẫn nằm trong mẫu
    }

    [Fact]
    public void Khong_tra_duoc_trang_thai_thi_KHONG_tinh_vao_mau()
    {
        // Khách bị xoá/gộp bên CRM → không có trong kết quả hỏi ngược. Đếm họ vào mẫu sẽ kéo tỉ lệ
        // xuống một cách oan uổng, làm tính năng trông tệ hơn thực tế.
        var (reached, checkedCount) = CareFollowUp.Measure(
            new[] { (1, (string?)"2026-05-11"), (2, (string?)"2026-05-11") }, Now((1, "2026-08-15")));
        Assert.Equal(1, reached);
        Assert.Equal(1, checkedCount);
    }

    [Fact]
    public void Moc_khong_doc_duoc_thi_KHONG_tinh_la_da_goi()
    {
        // Thà báo thiếu còn hơn báo thừa: con số này quyết định giữ hay bỏ tính năng.
        var (reached, checkedCount) = CareFollowUp.Measure(
            new[] { (1, (string?)"khong-phai-ngay") }, Now((1, "2026-08-15")));
        Assert.Equal(0, reached);
        Assert.Equal(1, checkedCount);
    }

    [Fact]
    public void Ngay_hien_tai_CU_HON_luc_nhac_thi_khong_tinh()
    {
        // Xảy ra khi CRM được khôi phục từ bản sao lưu. Không phải "đã gọi" — chỉ là dữ liệu lùi.
        var (reached, _) = CareFollowUp.Measure(
            new[] { (1, (string?)"2026-08-15") }, Now((1, "2026-05-11")));
        Assert.Equal(0, reached);
    }

    [Fact]
    public void Chi_khac_gio_trong_cung_mot_ngay_thi_khong_tinh()
    {
        // Mốc lưu theo NGÀY. Cùng ngày mà lệch giờ không phải là lần chăm mới — tính vào là thổi
        // phồng hiệu quả bằng chính hành động ghi nhận của hệ thống.
        var (reached, _) = CareFollowUp.Measure(
            new[] { (1, (string?)"2026-08-18") }, Now((1, "2026-08-18T23:59:00")));
        Assert.Equal(0, reached);
    }

    [Fact]
    public void Khong_co_ai_da_nhac_thi_tra_ve_khong_tren_khong()
    {
        // "0/0" khác hẳn "0/25": một cái là chưa đo được, một cái là nhắc mà không ai làm gì.
        var (reached, checkedCount) = CareFollowUp.Measure(
            Array.Empty<(int, string?)>(), new Dictionary<int, DateTime?>());
        Assert.Equal(0, reached);
        Assert.Equal(0, checkedCount);
    }
}
