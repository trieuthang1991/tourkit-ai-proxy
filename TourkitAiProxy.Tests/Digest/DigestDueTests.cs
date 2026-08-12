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

    // ── Thử lại theo từng kênh (cờ bit) ─────────────────────────────────────────
    //
    // Trước khi có SentMask, cả 4 kênh dùng chung 1 mốc ngày: telegram hỏng lúc 7h thì bị đánh dấu
    // "đã gửi hôm nay" và im lặng mất tin luôn. Đây là bộ test khoá hành vi thay thế.

    /// 3 kênh (app + email + telegram), đủ thông tin nhận.
    private static DigestSubscription Sub3(int hour, DateTime? lastLocalDate = null,
        int sentMask = 0, int attempts = 0)
        => new("t", "u", BriefTypes.Sale, true, hour, true, true, "a@b.c", true, "123", false, null,
               null, lastLocalDate, sentMask, attempts);

    [Fact]
    public void Lan_dau_trong_ngay_thi_gui_MOI_kenh_dang_bat()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);   // 07h VN
        Assert.Equal(ChannelMask.InApp | ChannelMask.Email | ChannelMask.Telegram,
                     DigestDue.PendingFor(Sub3(7), utc));
    }

    [Fact]
    public void Kenh_hong_thi_gio_sau_thu_lai_DUNG_kenh_do()
    {
        // 7h: app+email ok, telegram hỏng. 8h VN (01:05 UTC) → chỉ còn telegram.
        var utc = new DateTime(2026, 8, 11, 1, 5, 0, DateTimeKind.Utc);
        var sub = Sub3(7, new DateTime(2026, 8, 11),
                       ChannelMask.InApp | ChannelMask.Email, attempts: 1);
        Assert.Equal(ChannelMask.Telegram, DigestDue.PendingFor(sub, utc));
    }

    [Fact]
    public void Gui_du_het_thi_gio_sau_khong_gui_lai()
    {
        var utc = new DateTime(2026, 8, 11, 1, 5, 0, DateTimeKind.Utc);
        var sub = Sub3(7, new DateTime(2026, 8, 11),
                       ChannelMask.InApp | ChannelMask.Email | ChannelMask.Telegram, attempts: 1);
        Assert.Equal(ChannelMask.None, DigestDue.PendingFor(sub, utc));
    }

    [Fact]
    public void Het_tran_so_lan_thu_thi_dung_han_trong_ngay()
    {
        // Kênh hỏng suốt (vd token Zalo hết hạn) không được thử lại mỗi giờ cả ngày.
        var utc = new DateTime(2026, 8, 11, 5, 5, 0, DateTimeKind.Utc);   // 12h VN
        var sub = Sub3(7, new DateTime(2026, 8, 11), ChannelMask.InApp,
                       attempts: ChannelMask.MaxAttemptsPerDay);
        Assert.Equal(ChannelMask.None, DigestDue.PendingFor(sub, utc));
    }

    [Fact]
    public void Sang_ngay_moi_thi_lam_lai_tu_dau_du_hom_qua_con_thieu()
    {
        // Hôm qua telegram hỏng và đã hết trần lượt thử → hôm nay vẫn phải gửi đủ 3 kênh,
        // chứ không phải "nợ" telegram của hôm qua.
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        var sub = Sub3(7, new DateTime(2026, 8, 10), ChannelMask.InApp,
                       attempts: ChannelMask.MaxAttemptsPerDay);
        Assert.Equal(ChannelMask.InApp | ChannelMask.Email | ChannelMask.Telegram,
                     DigestDue.PendingFor(sub, utc));
    }

    [Fact]
    public void Chua_gui_lan_nao_va_SAI_gio_thi_khong_thu_lai_som()
    {
        // Thử lại chỉ áp dụng cho ngày ĐÃ gửi. Chưa gửi mà không đúng giờ thì phải im,
        // nếu không bản tin sáng 7h sẽ bị bắn ngay lúc 0h khi workflow vừa bật.
        var utc = new DateTime(2026, 8, 11, 3, 5, 0, DateTimeKind.Utc);   // 10h VN
        Assert.Equal(ChannelMask.None, DigestDue.PendingFor(Sub3(7), utc));
    }

    [Fact]
    public void Ban_ghi_cu_da_gui_hom_nay_thi_KHONG_gui_trung_sau_khi_nang_cap()
    {
        // Bản ghi có từ trước khi thêm SentMask: mask=0, attempts=0. Nếu tính là "còn thiếu cả 3"
        // thì đúng ngày nâng cấp mọi người nhận tin 2 lần.
        var utc = new DateTime(2026, 8, 11, 1, 5, 0, DateTimeKind.Utc);
        Assert.Equal(ChannelMask.None,
            DigestDue.PendingFor(Sub3(7, new DateTime(2026, 8, 11)), utc));
    }

    [Fact]
    public void Bat_ban_tin_nhung_khong_co_noi_nhan_thi_khong_gui()
    {
        // Bật email/telegram mà bỏ trống địa chỉ → không có gì để gửi. Nếu vẫn tính là "đang bật"
        // thì nó nằm mãi trong danh sách còn-thiếu và bị thử lại vô ích.
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        var sub = new DigestSubscription("t", "u", BriefTypes.Sale, true, 7,
            false, true, null, true, "", false, null, null, null);
        Assert.Equal(ChannelMask.None, DigestDue.PendingFor(sub, utc));
        Assert.False(DigestDue.IsDue(sub, utc));
    }
}
