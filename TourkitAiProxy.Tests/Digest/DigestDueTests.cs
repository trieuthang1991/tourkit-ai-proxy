using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// Khoá logic "đến giờ gửi bản tin".
/// Đây là chỗ dễ sai nhất của tính năng: mốc "hôm nay" phải theo GIỜ VIỆT NAM, không phải UTC.
/// Lấy nhầm ngày UTC thì trong khoảng 0h–7h sáng VN sẽ lệch hẳn 1 ngày — đúng lúc bản tin sáng chạy.
public class DigestDueTests
{
    private static DigestSubscription Sub(int hour, DateTime? lastLocalDate = null, bool enabled = true)
        => new("t", "u", BriefTypes.Sale, enabled, hour, true, false, null, false, null, false, null, null, lastLocalDate);

    [Fact]
    public void Dung_gio_va_chua_gui_hom_nay_thi_due()
    {
        // 00:05 UTC = 07:05 VN
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.True(DigestDue.IsDue(Sub(7), utc));
    }

    [Fact]
    public void Sai_gio_thi_khong_due()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);   // 07h VN
        Assert.False(DigestDue.IsDue(Sub(8), utc));
    }

    [Fact]
    public void Da_gui_hom_nay_thi_khong_due()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.False(DigestDue.IsDue(Sub(7, lastLocalDate: new DateTime(2026, 8, 11)), utc));
    }

    [Fact]
    public void Gui_hom_qua_thi_hom_nay_due_lai()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.True(DigestDue.IsDue(Sub(7, lastLocalDate: new DateTime(2026, 8, 10)), utc));
    }

    [Fact]
    public void Tat_dang_ky_thi_khong_due()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.False(DigestDue.IsDue(Sub(7, enabled: false), utc));
    }

    [Fact]
    public void Nua_dem_VN_doi_ngay_dung()
    {
        // 17:30 UTC ngày 10 = 00:30 VN ngày 11 → sub 0h, lần gửi cuối là ngày 10 VN → due
        var utc = new DateTime(2026, 8, 10, 17, 30, 0, DateTimeKind.Utc);
        Assert.True(DigestDue.IsDue(Sub(0, lastLocalDate: new DateTime(2026, 8, 10)), utc));
    }

    // ── Các ca mình thêm ngoài plan: đúng chỗ ranh giới ngày dễ sai nhất ─────────

    [Fact]
    public void Truoc_nua_dem_VN_van_con_la_ngay_hom_truoc()
    {
        // 16:30 UTC ngày 10 = 23:30 VN ngày 10 → sub 23h, đã gửi ngày 10 rồi → KHÔNG gửi lại
        var utc = new DateTime(2026, 8, 10, 16, 30, 0, DateTimeKind.Utc);
        Assert.False(DigestDue.IsDue(Sub(23, lastLocalDate: new DateTime(2026, 8, 10)), utc));
    }

    [Fact]
    public void Ngay_UTC_va_ngay_VN_lech_nhau_thi_lay_theo_VN()
    {
        // 23:00 UTC ngày 10 = 06:00 VN ngày 11. Nếu lấy nhầm NGÀY UTC (=10) thì sẽ tưởng
        // "đã gửi hôm nay" và bỏ qua. Lấy đúng ngày VN (=11) thì phải due.
        var utc = new DateTime(2026, 8, 10, 23, 0, 0, DateTimeKind.Utc);
        Assert.Equal(11, DigestDue.NowVn(utc).Day);
        Assert.True(DigestDue.IsDue(Sub(6, lastLocalDate: new DateTime(2026, 8, 10)), utc));
    }

    [Fact]
    public void Chua_gui_lan_nao_thi_due()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.True(DigestDue.IsDue(Sub(7, lastLocalDate: null), utc));
    }

    [Fact]
    public void Gio_rac_thi_ve_7h_chu_khong_vo()
    {
        // SendHourLocal=99 → ClampHour về 7 → 07h VN vẫn gửi được, không phải "không bao giờ gửi".
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.True(DigestDue.IsDue(Sub(99), utc));
    }

    [Fact]
    public void LastSentLocalDate_co_kem_gio_van_so_theo_ngay()
    {
        // DB trả DATE nhưng nếu chỗ nào đó nhét cả giờ vào thì so sánh phải vẫn theo NGÀY.
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.False(DigestDue.IsDue(Sub(7, lastLocalDate: new DateTime(2026, 8, 11, 15, 42, 0)), utc));
    }
}
