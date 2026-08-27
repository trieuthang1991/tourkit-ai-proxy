using TourkitAiProxy.Domain.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Người đang đăng ký bản tin nhưng KHÔNG nhận được — vì sao, và báo thế nào.
///
/// <para>Trước 27/08/2026 bản tin lặng lẽ bỏ qua họ rồi ghi nhật ký <i>"chưa đăng nhập lần nào"</i>.
/// Câu đó sai với cả hai nguyên nhân thật, và người mất bản tin thì không bao giờ biết.</para>
///
/// <para><b>Báo MỘT lần rồi TẮT đăng ký.</b> Người cần đọc lời nhắc chính là người không mở app,
/// nên nó phải đi qua thư. Nhắc xong thì tắt: lượt sau khỏi kiểm lại, và không có lá thư thứ hai.
/// Họ đăng nhập, thấy lý do trên thẻ "Bản tin của tôi", tự bật lại.</para>
/// </summary>
public class BriefReadinessTests
{
    private static DigestSubscription Sub(bool email = true, bool tele = false, bool zalo = false) =>
        new("cty", "an.nguyen", BriefTypes.Sale, true, 7,
            ChannelInApp: true,
            ChannelEmail: email, Email: email ? "an@cty.vn" : null,
            ChannelTelegram: tele, TelegramChatId: tele ? "123" : null,
            ChannelZalo: zalo, ZaloPhone: zalo ? "0900000001" : null,
            LastSentUtc: null, LastSentLocalDate: null);

    // ── Câu chữ ─────────────────────────────────────────────────────────────

    [Fact]
    public void Thieu_phien_phai_bao_DANG_NHAP_LAI_chu_khong_doi_mat_khau()
    {
        // Bắt người dùng gõ lại mật khẩu vào một ô nào đó vừa là bước lùi bảo mật, vừa trông y hệt
        // thư lừa đảo. Việc duy nhất họ cần làm là đăng nhập như bình thường.
        var m = BriefReadiness.BuildReminder(BriefReadinessReason.NoSession, BriefTypes.Sale,
            sinceUtc: new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc),
            nowUtc: new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("đăng nhập", m.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mật khẩu", m.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        // Nói rõ đã lỡ mấy hôm — cụ thể hơn hẳn một câu chung chung.
        Assert.Contains("3", m.BodyMarkdown);
    }

    [Fact]
    public void Phai_noi_ro_la_da_TAT_dang_ky_va_cach_bat_lai()
    {
        // Hệ thống vừa đổi cấu hình của người dùng. Không nói ra thì họ đăng nhập lại, tưởng xong,
        // rồi sáng mai vẫn không có bản tin — mất tin tưởng vào cả tính năng.
        var m = BriefReadiness.BuildReminder(BriefReadinessReason.NoSession, BriefTypes.Sale,
            null, DateTime.UtcNow);
        Assert.Contains("tạm tắt", m.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bật lại", m.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dang_nhap_lai_hong_phai_chi_sang_CRM()
    {
        // Ca này KHÁC ca trên: đăng nhập lại cũng không vào được (đổi mật khẩu / khoá tài khoản),
        // nên phải chỉ đúng chỗ cần xử lý thay vì bảo họ bấm lại một việc chắc chắn thất bại.
        var m = BriefReadiness.BuildReminder(BriefReadinessReason.ReloginFailed, BriefTypes.Sale,
            sinceUtc: null, nowUtc: DateTime.UtcNow);
        Assert.Contains("CRM", m.BodyMarkdown);
    }

    [Fact]
    public void Loi_nhac_KHONG_duoc_mang_dang_ban_tin()
    {
        // Ghi cùng loại với bản tin thì lượt sau hệ thống tưởng "hôm nay gửi rồi" và bản tin thật
        // không bao giờ tới — đổi một lỗi im lặng lấy một lỗi im lặng khác.
        var m = BriefReadiness.BuildReminder(BriefReadinessReason.NoSession, BriefTypes.Sale,
            null, DateTime.UtcNow);
        Assert.NotEqual(BriefTypes.Sale, m.Kind);
        Assert.Equal(BriefReadiness.ReminderKind, m.Kind);
    }

    // ── Kênh gửi: CHỈ thư và trong app ──────────────────────────────────────

    [Fact]
    public void Chi_gui_qua_THU_bo_Telegram_va_Zalo()
    {
        // Zalo đi bằng ZNS nên chỉ chở được mẫu đã duyệt — gửi tự do là chắc chắn hỏng, mà mỗi lần
        // hỏng lại đẻ một dòng hàng đợi làm người đọc nhật ký tưởng kênh Zalo đang lỗi.
        // Telegram bỏ theo yêu cầu: lời nhắc hành chính không nên chen vào kênh chat.
        var goc = Sub(email: true, tele: true, zalo: true);
        var dungDeNhac = BriefReadiness.ChannelsForReminder(goc);

        Assert.True(dungDeNhac.ChannelEmail);
        Assert.False(dungDeNhac.ChannelTelegram);
        Assert.False(dungDeNhac.ChannelZalo);

        // Bản gốc KHÔNG được đụng tới — nó còn dùng để gửi bản tin thật.
        Assert.True(goc.ChannelZalo);
        Assert.True(goc.ChannelTelegram);
    }

    [Fact]
    public void Khong_khai_thu_thi_khong_nhac_ra_ngoai_duoc()
    {
        // Ca này phải nhìn thấy được, không được im: chỗ gọi đọc cờ này để ghi vào tóm tắt lượt chạy,
        // vì người đó sẽ chỉ biết khi nào tự mở app.
        Assert.False(BriefReadiness.CanRemindByMail(Sub(email: false, tele: true, zalo: true)));
        Assert.True(BriefReadiness.CanRemindByMail(Sub(email: true)));
    }

    [Fact]
    public void Khai_thu_nhung_de_trong_dia_chi_cung_la_khong_gui_duoc()
    {
        var sub = Sub(email: true) with { };
        var rong = new DigestSubscription("cty", "an.nguyen", BriefTypes.Sale, true, 7,
            true, ChannelEmail: true, Email: "   ",
            ChannelTelegram: false, TelegramChatId: null,
            ChannelZalo: false, ZaloPhone: null, null, null);
        Assert.True(BriefReadiness.CanRemindByMail(sub));
        Assert.False(BriefReadiness.CanRemindByMail(rong));
    }

    // ── Mã lý do lưu xuống CSDL ─────────────────────────────────────────────

    [Fact]
    public void Ma_ly_do_on_dinh_va_khac_nhau()
    {
        // Mã này nằm trong CSDL và giao diện đọc để hiện dải đỏ — đổi chữ là dữ liệu cũ mồ côi.
        Assert.Equal("thieu-phien", BriefReadiness.ReasonCode(BriefReadinessReason.NoSession));
        Assert.Equal("dang-nhap-lai-hong", BriefReadiness.ReasonCode(BriefReadinessReason.ReloginFailed));
    }

    [Fact]
    public void Nhan_hien_thi_doc_duoc_cho_nguoi_dung()
    {
        // Giao diện hiện thẳng chuỗi này; mã kỹ thuật lọt ra là người dùng đọc "thieu-phien".
        foreach (var ly in new[] { BriefReadinessReason.NoSession, BriefReadinessReason.ReloginFailed })
        {
            var nhan = BriefReadiness.ReasonLabel(BriefReadiness.ReasonCode(ly));
            Assert.False(string.IsNullOrWhiteSpace(nhan));
            Assert.DoesNotContain("-", nhan);
        }
    }

    [Fact]
    public void Ma_la_thi_van_ra_mot_cau_doc_duoc()
    {
        // Dữ liệu cũ hoặc mã do phiên bản sau ghi vào: thà một câu chung chung còn hơn ô trống.
        Assert.False(string.IsNullOrWhiteSpace(BriefReadiness.ReasonLabel("cai-gi-do-la")));
    }
}
