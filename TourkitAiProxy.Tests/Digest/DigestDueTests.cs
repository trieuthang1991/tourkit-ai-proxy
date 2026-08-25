using TourkitAiProxy.Services.Digest;
using Xunit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

public class DigestDueTests
{
    private static DigestSubscription Sub(int hour, bool enabled = true)
        => new("t", "u", BriefTypes.Sale, enabled, hour, true, false, null, false, null, false, null, null, null);

    // 07:00 VN = 00:00 UTC. Lead 10' → chuẩn bị được từ 06:50 VN = 23:50 UTC hôm trước.
    [Fact] public void Truoc_cua_so_lead_thi_chua_chuan_bi()
        => Assert.False(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 12, 23, 49, 0, DateTimeKind.Utc), 10));
    [Fact] public void Trong_cua_so_lead_thi_chuan_bi()
        => Assert.True(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 12, 23, 50, 0, DateTimeKind.Utc), 10));
    [Fact] public void Dung_gio_gui_van_chuan_bi_duoc()
        => Assert.True(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc), 10));
    [Fact] public void Qua_gio_nhieu_tieng_van_chuan_bi()
        => Assert.True(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 13, 8, 30, 0, DateTimeKind.Utc), 10));
    [Fact] public void Disabled_khong_chuan_bi()
        => Assert.False(DigestDue.ShouldPrepare(Sub(7, enabled: false), new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc), 10));
    [Fact] public void Gio_0h_thi_ca_ngay_deu_chuan_bi_duoc()
    {
        // 00:00 VN 13/8 = 17:00 UTC 12/8; và 17:00 VN cùng ngày = 10:00 UTC 13/8 — đều chuẩn bị được.
        Assert.True(DigestDue.ShouldPrepare(Sub(0), new DateTime(2026, 8, 12, 17, 0, 0, DateTimeKind.Utc), 10));
        Assert.True(DigestDue.ShouldPrepare(Sub(0), new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc), 10));
    }
    [Fact] public void Khong_chuan_bi_som_hon_lead_cho_lan_gui_ngay_mai()
    {
        // Bản tin 7h; 00:05 VN 13/8 (= 17:05 UTC 12/8) còn cách 7 tiếng → PHẢI False (chỉ prep khi ≤ lead phút trước).
        Assert.False(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 12, 17, 5, 0, DateTimeKind.Utc), 10));
    }
    [Fact] public void Gio_rac_kep_ve_7h()
        => Assert.True(DigestDue.ShouldPrepare(Sub(99), new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc), 10));
    [Fact] public void ScheduledUtc_dung_moc_gio_VN()
    {
        var utcNow = new DateTime(2026, 8, 12, 23, 55, 0, DateTimeKind.Utc);   // 06:55 VN
        Assert.Equal(new DateTime(2026, 8, 13, 0, 0, 0), DigestDue.SendMomentUtc(Sub(7), utcNow));
        var late = new DateTime(2026, 8, 13, 1, 30, 0, DateTimeKind.Utc);      // 08:30 VN
        Assert.Equal(late, DigestDue.SendMomentUtc(Sub(7), late));
    }
}
