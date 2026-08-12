using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// Enum số thứ tự (cho người tra) + cờ bit (cho máy lưu).
/// Điều quan trọng nhất phải khoá: kênh BẬT mà THIẾU thông tin nhận thì KHÔNG được tính là
/// "đang bật" — nếu tính thì nó vào danh sách còn-thiếu và bị thử lại mãi mà không bao giờ gửi nổi.
public class ChannelMaskTests
{
    private static DigestSubscription Sub(
        bool inApp = true, bool email = false, string? emailAddr = null,
        bool tele = false, string? chatId = null, bool zalo = false, string? zaloId = null)
        => new("t", "u", BriefTypes.Sale, true, 7, inApp, email, emailAddr, tele, chatId, zalo, zaloId, null, null);

    // ── Enum: 0 = CHƯA CHỌN GÌ, kênh thật đánh số từ 1 ─────────────────────────

    [Fact]
    public void So_0_la_CHUA_CHON_GI_khong_phai_tat_ca()
    {
        // Đây là khác biệt sống còn: coi 0 là "tất cả" thì bản ghi chưa cấu hình
        // sẽ bị hiểu là bật đủ 4 kênh — sai ngược hoàn toàn.
        Assert.Equal(0, (int)DigestChannel.None);
        Assert.Equal(ChannelMask.None, ChannelMask.ToMask(DigestChannel.None));
        Assert.NotEqual(ChannelMask.AllChannels, ChannelMask.ToMask(DigestChannel.None));
    }

    [Fact]
    public void Muon_TAT_CA_thi_la_15_chu_khong_phai_0()
        => Assert.Equal(15, ChannelMask.AllChannels);

    [Theory]
    [InlineData(DigestChannel.InApp, 1)]
    [InlineData(DigestChannel.Email, 2)]
    [InlineData(DigestChannel.Telegram, 3)]
    [InlineData(DigestChannel.Zalo, 4)]
    public void Kenh_that_danh_so_lien_tuc_tu_1(DigestChannel ch, int expectedOrdinal)
        => Assert.Equal(expectedOrdinal, (int)ch);

    [Theory]
    [InlineData(DigestChannel.InApp, 1)]
    [InlineData(DigestChannel.Email, 2)]
    [InlineData(DigestChannel.Telegram, 4)]
    [InlineData(DigestChannel.Zalo, 8)]
    public void So_thu_tu_doi_dung_sang_co_bit(DigestChannel ch, int expectedBit)
        => Assert.Equal(expectedBit, ChannelMask.ToMask(ch));

    [Fact]
    public void So_thu_tu_KHAC_co_bit_tu_kenh_thu_3_tro_di()
    {
        // Đây là lý do tách 2 khái niệm: telegram là kênh SỐ 3 nhưng bit là 4.
        // Gộp một vai thì thêm kênh thứ 5 phải nhớ ghi 16 thay vì 5 — rất dễ ghi nhầm.
        Assert.NotEqual((int)DigestChannel.Telegram, ChannelMask.ToMask(DigestChannel.Telegram));
        Assert.NotEqual((int)DigestChannel.Zalo, ChannelMask.ToMask(DigestChannel.Zalo));
    }

    // ── Cờ bit ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Bit_khong_trung_nhau_va_cong_lai_bang_AllChannels()
    {
        var all = new[] { ChannelMask.InApp, ChannelMask.Email, ChannelMask.Telegram, ChannelMask.Zalo };
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(ChannelMask.AllChannels, all.Aggregate(0, (a, b) => a | b));
    }

    [Theory]
    [InlineData("inapp", 1)]
    [InlineData("email", 2)]
    [InlineData("telegram", 4)]
    [InlineData("zalo", 8)]
    public void MaskOfId_khop_dung_bit(string id, int expected)
        => Assert.Equal(expected, ChannelMask.MaskOfId(id));

    [Theory]
    [InlineData("kenh-la")]
    [InlineData("")]
    [InlineData(null)]
    public void Id_la_tra_null_chu_KHONG_coi_la_tat_ca(string? id)
    {
        // Coi kênh lạ là "tất cả" thì một id sai sẽ đánh dấu đã-gửi cho MỌI kênh → mất tin thật.
        Assert.Null(ChannelMask.FromId(id));
        Assert.Equal(ChannelMask.None, ChannelMask.MaskOfId(id));
    }

    [Fact]
    public void EnabledOf_chi_tinh_kenh_du_thong_tin_nhan()
    {
        Assert.Equal(ChannelMask.InApp, ChannelMask.EnabledOf(Sub(email: true, emailAddr: null)));
        Assert.Equal(ChannelMask.InApp, ChannelMask.EnabledOf(Sub(email: true, emailAddr: "  ")));
        Assert.Equal(ChannelMask.InApp | ChannelMask.Email,
            ChannelMask.EnabledOf(Sub(email: true, emailAddr: "a@b.c")));
    }

    [Fact]
    public void EnabledOf_du_4_kenh_bang_AllChannels()
        => Assert.Equal(ChannelMask.AllChannels,
                        ChannelMask.EnabledOf(Sub(true, true, "a@b.c", true, "123", true, "u9")));

    [Fact]
    public void EnabledOf_tat_het_thi_bang_0()
        => Assert.Equal(0, ChannelMask.EnabledOf(Sub(inApp: false)));

    [Fact]
    public void Pending_chi_con_kenh_chua_gui_duoc()
    {
        var enabled = ChannelMask.InApp | ChannelMask.Email | ChannelMask.Telegram;
        var sent = ChannelMask.InApp | ChannelMask.Email;
        Assert.Equal(ChannelMask.Telegram, ChannelMask.Pending(enabled, sent));
    }

    [Fact]
    public void Gui_du_het_thi_khong_con_gi_de_lam()
    {
        var enabled = ChannelMask.InApp | ChannelMask.Email;
        Assert.Equal(0, ChannelMask.Pending(enabled, enabled));
    }

    [Fact]
    public void Kenh_da_gui_ma_nay_TAT_thi_khong_lam_pending_am()
    {
        // Hôm qua gửi email ok, hôm nay user tắt email → pending không được có email,
        // và tuyệt đối không ra số âm.
        var pending = ChannelMask.Pending(ChannelMask.InApp, ChannelMask.InApp | ChannelMask.Email);
        Assert.Equal(0, pending);
        Assert.True(pending >= 0);
    }

    [Fact]
    public void Describe_doc_duoc_bang_tieng_Viet()
    {
        Assert.Equal("(không kênh nào)", ChannelMask.Describe(ChannelMask.None));
        Assert.Equal("trong app", ChannelMask.Describe(ChannelMask.InApp));
        Assert.Equal("email+telegram", ChannelMask.Describe(ChannelMask.Email | ChannelMask.Telegram));
        Assert.Equal("trong app+email+telegram+zalo", ChannelMask.Describe(ChannelMask.AllChannels));
    }

    // ── Lọc/tìm kiếm theo kênh ─────────────────────────────────────────────────

    [Fact]
    public void Chua_chon_kenh_nao_thi_khong_loc_gi()
    {
        // wanted = 0 nghĩa là người dùng chưa tick gì → phải trả về hết, không phải trả về rỗng.
        Assert.True(ChannelMask.MatchesFilter(ChannelMask.None, ChannelMask.None));
        Assert.True(ChannelMask.MatchesFilter(ChannelMask.Email, ChannelMask.None));
        Assert.True(ChannelMask.MatchesFilter(ChannelMask.AllChannels, ChannelMask.None));
    }

    [Fact]
    public void Doi_15_la_doi_du_ca_4_kenh()
    {
        Assert.True(ChannelMask.MatchesFilter(ChannelMask.AllChannels, ChannelMask.AllChannels));
        Assert.False(ChannelMask.MatchesFilter(
            ChannelMask.InApp | ChannelMask.Email | ChannelMask.Telegram, ChannelMask.AllChannels));
    }

    [Fact]
    public void Tick_hai_kenh_la_doi_CO_CA_HAI_khong_phai_mot_trong_hai()
    {
        var wanted = ChannelMask.Email | ChannelMask.Zalo;
        Assert.True(ChannelMask.MatchesFilter(ChannelMask.Email | ChannelMask.Zalo, wanted));
        Assert.False(ChannelMask.MatchesFilter(ChannelMask.Email, wanted));   // chỉ có 1 → không khớp
        Assert.True(ChannelMask.HasAny(ChannelMask.Email, wanted));           // nhưng "có ít nhất 1" thì có
    }

    [Fact]
    public void HasAny_chua_chon_gi_thi_khong_co_gi_de_tim()
        => Assert.False(ChannelMask.HasAny(ChannelMask.AllChannels, ChannelMask.None));

    [Fact]
    public void Co_chan_so_lan_thu_trong_ngay()
    {
        // Phải có trần, nếu không thì kênh hỏng suốt (token hết hạn) bị thử lại mỗi giờ cả ngày.
        Assert.InRange(ChannelMask.MaxAttemptsPerDay, 1, 10);
    }
}
