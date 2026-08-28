using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Phân loại lỗi kênh.
///
/// <para><b>Vì sao đáng test từng mã một.</b> Đọc sai một mã không hiện ra lỗi gì trên màn hình:
/// tin vẫn nằm trong hàng đợi, vẫn thử lại, vẫn báo "chưa gửi được". Chỉ hai tuần sau mới lộ, khi
/// một kênh đã chết từ lâu mà không ai được báo, hoặc khi hàng đợi quay vòng hàng nghìn lượt cho
/// một khách đã chặn công ty từ đầu.</para>
/// </summary>
public class ChannelFailuresTests
{
    // ── Luật chung: thử lại và nối lại ─────────────────────────────────────

    [Theory]
    [InlineData(ChatFailure.Network)]
    [InlineData(ChatFailure.RateLimited)]
    [InlineData(ChatFailure.Unknown)]
    public void ThuLai_khi_hong_tam_thoi(ChatFailure loi)
        => Assert.True(ChannelFailures.ShouldRetry(loi));

    [Theory]
    [InlineData(ChatFailure.AuthFailed)]
    [InlineData(ChatFailure.PermissionDenied)]
    [InlineData(ChatFailure.QuotaExceeded)]
    [InlineData(ChatFailure.UserBlocked)]
    [InlineData(ChatFailure.InvalidRecipient)]
    [InlineData(ChatFailure.PayloadInvalid)]
    public void Khong_thu_lai_khi_thu_lai_cung_vo_ich(ChatFailure loi)
        => Assert.False(ChannelFailures.ShouldRetry(loi));

    /// <summary>
    /// Ranh giới quan trọng nhất của cả bảng: hỏng ở cấp KÊNH khác hỏng ở cấp MỘT TIN. Lẫn hai
    /// mức này là hoặc báo động mỗi lần một khách chặn, hoặc im lặng suốt trong khi kênh đã chết.
    /// </summary>
    [Fact]
    public void Chi_hong_cap_kenh_moi_doi_noi_lai()
    {
        Assert.True(ChannelFailures.NeedsReconnect(ChatFailure.AuthFailed));
        Assert.True(ChannelFailures.NeedsReconnect(ChatFailure.PermissionDenied));

        Assert.False(ChannelFailures.NeedsReconnect(ChatFailure.UserBlocked));
        Assert.False(ChannelFailures.NeedsReconnect(ChatFailure.QuotaExceeded));
        Assert.False(ChannelFailures.NeedsReconnect(ChatFailure.PayloadInvalid));
        Assert.False(ChannelFailures.NeedsReconnect(ChatFailure.Network));
    }

    /// <summary>Người trực chỉ đọc hai câu này, nên không câu nào được rỗng.</summary>
    [Fact]
    public void Moi_nhom_deu_co_cau_noi_cho_nguoi_truc()
    {
        foreach (ChatFailure loi in Enum.GetValues<ChatFailure>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ChannelFailures.Label(loi)));
            Assert.False(string.IsNullOrWhiteSpace(ChannelFailures.Fix(loi)));
        }
    }

    // ── Meta: Messenger + Instagram ────────────────────────────────────────

    [Theory]
    [InlineData(190)]   // khoá hết hạn
    [InlineData(102)]   // phiên không hợp lệ
    [InlineData(467)]   // khoá sai
    public void Meta_ma_xac_thuc_la_kenh_dut(int ma)
    {
        var nhom = ChannelFailures.FromMeta(ma);
        Assert.Equal(ChatFailure.AuthFailed, nhom);
        Assert.True(ChannelFailures.NeedsReconnect(nhom));
    }

    [Fact]
    public void Meta_551_la_khach_chan_chu_khong_phai_loi_minh()
        => Assert.Equal(ChatFailure.UserBlocked, ChannelFailures.FromMeta(551));

    /// <summary>
    /// Mã 200 của Meta vừa là dải "thiếu quyền" vừa là chỗ họ nhét ca khách tự tắt nhận tin.
    /// Chỉ mã phụ tách được hai ca — và hai ca đó cần hai cách xử ngược nhau.
    /// </summary>
    [Fact]
    public void Meta_200_doc_theo_ma_phu()
    {
        Assert.Equal(ChatFailure.UserBlocked, ChannelFailures.FromMeta(200, 1_545_041));
        Assert.Equal(ChatFailure.PermissionDenied, ChannelFailures.FromMeta(200));
        Assert.Equal(ChatFailure.PermissionDenied, ChannelFailures.FromMeta(230));
    }

    [Fact]
    public void Meta_khong_ro_ma_thi_lay_loai_lam_luoi_vet()
    {
        Assert.Equal(ChatFailure.AuthFailed, ChannelFailures.FromMeta(999_999, null, "OAuthException"));
        Assert.Equal(ChatFailure.Unknown, ChannelFailures.FromMeta(999_999));
        Assert.Equal(ChatFailure.Unknown, ChannelFailures.FromMeta(null));
    }

    [Fact]
    public void Meta_phan_biet_khoa_bi_thu_hoi_han_voi_het_han_tam()
    {
        Assert.True(ChannelFailures.MetaTokenRevoked(190, 460));
        Assert.False(ChannelFailures.MetaTokenRevoked(190, null));
        Assert.False(ChannelFailures.MetaTokenRevoked(102, 460));
    }

    // ── WhatsApp: cùng nhà Meta nhưng bảng mã khác hẳn ─────────────────────

    /// <summary>
    /// 131047 là mã <b>chỉ WhatsApp mới có</b>: cửa sổ chăm sóc khách 24 giờ đã đóng, phải chuyển
    /// sang mẫu duyệt sẵn. Dùng nhầm bảng của Messenger là đọc ra "không rõ" rồi thử lại vô ích.
    /// </summary>
    [Fact]
    public void WhatsApp_131047_la_het_cua_so_cham_soc()
        => Assert.Equal(ChatFailure.QuotaExceeded, ChannelFailures.FromWhatsApp(131_047));

    [Fact]
    public void WhatsApp_doc_dung_tung_nhom()
    {
        Assert.Equal(ChatFailure.AuthFailed, ChannelFailures.FromWhatsApp(190));
        Assert.Equal(ChatFailure.RateLimited, ChannelFailures.FromWhatsApp(130_429));
        Assert.Equal(ChatFailure.UserBlocked, ChannelFailures.FromWhatsApp(131_050));
        Assert.Equal(ChatFailure.InvalidRecipient, ChannelFailures.FromWhatsApp(33));
        Assert.Equal(ChatFailure.PermissionDenied, ChannelFailures.FromWhatsApp(131_031));
        Assert.Equal(ChatFailure.PayloadInvalid, ChannelFailures.FromWhatsApp(132_001));
        Assert.Equal(ChatFailure.Network, ChannelFailures.FromWhatsApp(131_016));
    }

    // ── Zalo: mã ÂM, và trả về kèm HTTP 200 ───────────────────────────────

    /// <summary>
    /// Ca đắt nhất của Zalo: khách chặn OA. Trước khi có bảng này, nó về từ Zalo kèm HTTP 200 nên
    /// mọi phép đoán theo mã HTTP đều kết luận "gửi xong".
    /// </summary>
    [Fact]
    public void Zalo_216_la_khach_chan_OA()
    {
        var nhom = ChannelFailures.FromZalo(-216);
        Assert.Equal(ChatFailure.UserBlocked, nhom);
        Assert.False(ChannelFailures.ShouldRetry(nhom));
    }

    [Fact]
    public void Zalo_doc_dung_tung_nhom()
    {
        Assert.Equal(ChatFailure.AuthFailed, ChannelFailures.FromZalo(-124));
        Assert.Equal(ChatFailure.QuotaExceeded, ChannelFailures.FromZalo(-115));
        Assert.Equal(ChatFailure.PermissionDenied, ChannelFailures.FromZalo(-120));
        Assert.Equal(ChatFailure.InvalidRecipient, ChannelFailures.FromZalo(-118));
        Assert.Equal(ChatFailure.PayloadInvalid, ChannelFailures.FromZalo(-130));
        Assert.Equal(ChatFailure.Unknown, ChannelFailures.FromZalo(0));
    }

    // ── Telegram: không có mã, phải đọc câu chữ ───────────────────────────

    [Fact]
    public void Telegram_doc_cau_chu_truoc_ma_http()
    {
        Assert.Equal(ChatFailure.UserBlocked,
            ChannelFailures.FromTelegram(400, "Forbidden: bot was blocked by the user"));
        Assert.Equal(ChatFailure.InvalidRecipient,
            ChannelFailures.FromTelegram(400, "Bad Request: chat not found"));
        Assert.Equal(ChatFailure.RateLimited,
            ChannelFailures.FromTelegram(429, "Too Many Requests: retry after 30"));
    }

    /// <summary>
    /// Riêng Telegram, 403 nghĩa là <b>khách chặn bot</b> — không phải "thiếu quyền" như phần lớn
    /// API khác. Đoán theo thói quen là xếp nhầm rồi bảo người quản trị đi sửa quyền.
    /// </summary>
    [Fact]
    public void Telegram_403_la_khach_chan_khong_phai_thieu_quyen()
    {
        Assert.Equal(ChatFailure.UserBlocked, ChannelFailures.FromTelegram(403, null));
        Assert.Equal(ChatFailure.PermissionDenied, ChannelFailures.FromHttp(403));
    }

    [Fact]
    public void Telegram_khong_khop_cau_nao_thi_lui_ve_ma_http()
    {
        Assert.Equal(ChatFailure.Network, ChannelFailures.FromTelegram(503, "gateway busy"));
        Assert.Equal(ChatFailure.PayloadInvalid, ChannelFailures.FromTelegram(400, "chuyện lạ"));
        Assert.Equal(ChatFailure.Unknown, ChannelFailures.FromTelegram(200, ""));
    }

    // ── TikTok: HTTP luôn 200, nên mã HTTP gần như vô dụng ────────────────

    [Fact]
    public void TikTok_doc_cau_chu_vi_http_luon_200()
    {
        Assert.Equal(ChatFailure.AuthFailed,
            ChannelFailures.FromTikTok(200, "access_token_expired"));
        Assert.Equal(ChatFailure.UserBlocked,
            ChannelFailures.FromTikTok(200, "dm_disabled for this user"));
        Assert.Equal(ChatFailure.RateLimited,
            ChannelFailures.FromTikTok(200, "rate_limit_exceeded"));
        Assert.Equal(ChatFailure.Unknown, ChannelFailures.FromTikTok(200, "chuyện lạ"));
    }

    // ── Lưới vét cuối ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(401, ChatFailure.AuthFailed)]
    [InlineData(403, ChatFailure.PermissionDenied)]
    [InlineData(404, ChatFailure.InvalidRecipient)]
    [InlineData(429, ChatFailure.RateLimited)]
    [InlineData(500, ChatFailure.Network)]
    [InlineData(503, ChatFailure.Network)]
    [InlineData(422, ChatFailure.PayloadInvalid)]
    [InlineData(200, ChatFailure.Unknown)]
    public void Ma_http_tran_van_xep_duoc_nhom(int ma, ChatFailure mong)
        => Assert.Equal(mong, ChannelFailures.FromHttp(ma));

    // ── SendResult.Fail để bảng luật quyết định, không ai tự đoán nữa ─────

    [Fact]
    public void SendResult_Fail_lay_quyet_dinh_thu_lai_tu_bang_luat()
    {
        var chan = SendResult.Fail(ChatFailure.UserBlocked, "khách chặn");
        Assert.False(chan.Ok);
        Assert.False(chan.ThuLai);
        Assert.Equal(ChatFailure.UserBlocked, chan.Failure);

        var mang = SendResult.Fail(ChatFailure.Network, "timeout");
        Assert.True(mang.ThuLai);
    }
}
