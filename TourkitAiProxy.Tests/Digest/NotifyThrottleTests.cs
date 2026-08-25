using Xunit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Luật "có nên nhắc lại không". Sai ở đây hỏng theo CẢ HAI chiều mà không chiều nào báo lỗi:
/// nhắc thừa → người ta tắt tính năng; nhắc thiếu → việc rơi mất. Nên test kỹ hơn bình thường.
/// </summary>
public class NotifyThrottleTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 3, 0, 0, DateTimeKind.Utc);
    private static NotifyMark Mark(int times, int daysAgo, string? stamp = "2026-06-01")
        => new(times, Now.AddDays(-daysAgo - 10), Now.AddDays(-daysAgo), stamp);

    [Fact]
    public void Chua_nhac_bao_gio_thi_nhac()
    {
        var (skip, _) = NotifyThrottle.Decide(null, "2026-06-01", Now, minGapDays: 7, maxTimes: 3);
        Assert.False(skip);
    }

    [Fact]
    public void Vua_nhac_hom_kia_thi_im()
    {
        var (skip, reason) = NotifyThrottle.Decide(Mark(1, daysAgo: 2), "2026-06-01", Now, 7, 3);
        Assert.True(skip);
        Assert.Contains("2 ngày trước", reason);
    }

    [Fact]
    public void Qua_khoang_cach_thi_nhac_lai()
    {
        var (skip, _) = NotifyThrottle.Decide(Mark(1, daysAgo: 8), "2026-06-01", Now, 7, 3);
        Assert.False(skip);
    }

    [Fact]
    public void Du_so_lan_thi_thoi_han()
    {
        var (skip, reason) = NotifyThrottle.Decide(Mark(3, daysAgo: 30), "2026-06-01", Now, 7, 3);
        Assert.True(skip);
        Assert.Contains("đủ 3 lần", reason);
    }

    // ─── Phần đáng giá nhất: trạng thái đổi = đã có người xử lý thật ────────────────

    [Fact]
    public void Da_cham_soc_that_thi_XOA_BO_DEM_du_da_du_so_lan()
    {
        // Khách đã bị nhắc đủ 3 lần, NHƯNG ngày chăm sóc đã mới hơn → có người gọi thật rồi.
        // Phải coi như chưa nhắc bao giờ. Thiếu luật này thì "đã đủ 3 lần" thành án chung thân:
        // khách được chăm xong, nửa năm sau ngủ quên lại mà hệ thống vĩnh viễn không nhắc nữa.
        var (skip, _) = NotifyThrottle.Decide(Mark(3, daysAgo: 30, stamp: "2026-06-01"),
            stateNow: "2026-08-10", Now, minGapDays: 7, maxTimes: 3);
        Assert.False(skip);
    }

    [Fact]
    public void Da_cham_soc_that_thi_bo_qua_ca_khoang_cach_ngay()
    {
        // Kiểm TRƯỚC luật khoảng cách, không phải sau: vừa nhắc hôm qua nhưng khách đã được chăm
        // và lại tới hạn thì vẫn phải nhắc được.
        var (skip, _) = NotifyThrottle.Decide(Mark(1, daysAgo: 1, stamp: "2026-06-01"),
            stateNow: "2026-08-17", Now, minGapDays: 7, maxTimes: 3);
        Assert.False(skip);
    }

    [Fact]
    public void Trang_thai_KHONG_doi_thi_van_ap_luat_binh_thuong()
    {
        var (skip, _) = NotifyThrottle.Decide(Mark(1, daysAgo: 1, stamp: "2026-06-01"),
            stateNow: "2026-06-01", Now, minGapDays: 7, maxTimes: 3);
        Assert.True(skip);
    }

    [Fact]
    public void Doi_tuong_khong_co_dau_vet_trang_thai_thi_van_dem_binh_thuong()
    {
        // null và "" phải coi là MỘT. Nếu không, đối tượng không có dấu vết sẽ bị coi là "vừa đổi
        // trạng thái" ở mọi lượt → bộ đếm reset mãi → nhắc mỗi ngày, đúng thứ đang đi sửa.
        var (skip, _) = NotifyThrottle.Decide(new NotifyMark(3, Now.AddDays(-40), Now.AddDays(-30), null),
            stateNow: "", Now, minGapDays: 7, maxTimes: 3);
        Assert.True(skip);
    }

    // ─── Tắt từng luật ─────────────────────────────────────────────────────────────

    [Fact]
    public void So_lan_bang_0_la_khong_gioi_han_so_lan()
    {
        var (skip, _) = NotifyThrottle.Decide(Mark(99, daysAgo: 30), "2026-06-01", Now, 7, maxTimes: 0);
        Assert.False(skip);
    }

    [Fact]
    public void Khoang_cach_bang_0_la_khong_gioi_han_khoang_cach()
    {
        var (skip, _) = NotifyThrottle.Decide(Mark(1, daysAgo: 0), "2026-06-01", Now, minGapDays: 0, maxTimes: 3);
        Assert.False(skip);
    }

    [Fact]
    public void Het_so_lan_thi_chan_truoc_ca_khi_da_qua_khoang_cach()
    {
        // Thứ tự hai luật: hết lượt là hết hẳn, không phải "chờ đủ ngày rồi nhắc tiếp".
        var (skip, reason) = NotifyThrottle.Decide(Mark(3, daysAgo: 365), "2026-06-01", Now, 7, 3);
        Assert.True(skip);
        Assert.Contains("đủ 3 lần", reason);
    }
}
