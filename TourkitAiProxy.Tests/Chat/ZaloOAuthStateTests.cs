using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Vòng cấp quyền Zalo OA.
///
/// <para>Zalo <b>không cho copy Refresh Token</b> từ giao diện của họ — phải đi một vòng OAuth.
/// Chỗ nguy hiểm là đường callback: Zalo đá trình duyệt về bằng chuyển hướng thường nên nó
/// <b>không mang theo phiên</b>. Ghép lại công ty/tài khoản bằng <c>state</c>; để client tự khai
/// tenant trên URL thì ai biết đường dẫn cũng nhét được refresh token của OA mình vào công ty
/// khác, rồi đọc và trả lời tin của khách công ty đó.</para>
/// </summary>
public class ZaloOAuthStateTests
{
    [Fact]
    public void Ma_dung_MOT_lan()
    {
        // Mã còn dùng lại được là mã rò ra ngoài (lịch sử trình duyệt, log proxy) vẫn nhét được
        // token vào tài khoản người khác.
        var kho = new ZaloOAuthStates();
        var ma = kho.Tao("cong-ty-A", "acc1", "https://x/cb");

        Assert.NotNull(kho.Nhan(ma));
        Assert.Null(kho.Nhan(ma));
    }

    [Fact]
    public void Tra_ve_dung_cong_ty_va_tai_khoan_da_gui()
    {
        var kho = new ZaloOAuthStates();
        var ma = kho.Tao("cong-ty-B", "acc-9", "https://x/cb");

        var ra = kho.Nhan(ma);
        Assert.NotNull(ra);
        Assert.Equal("cong-ty-B", ra!.Value.TenantId);
        Assert.Equal("acc-9", ra.Value.AccountId);
        // redirect_uri giữ nguyên trong state chứ không dựng lại lúc đổi mã: Zalo đòi chuỗi khớp
        // Y HỆT, dựng lại hai lần là có ngày lệch một dấu gạch chéo.
        Assert.Equal("https://x/cb", ra.Value.RedirectUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("khong-phai-ma")]
    public void Ma_sai_thi_tra_null_chu_khong_nem(string? ma)
    {
        // Đây là đường CÔNG KHAI — ném ra ngoài là biến một lượt bấm nhầm thành lỗi 500.
        var kho = new ZaloOAuthStates();
        Assert.Null(kho.Nhan(ma));
    }

    [Fact]
    public void Hai_lan_tao_ra_hai_ma_khac_nhau()
    {
        // Đoán được mã là đoán được đường nhét token vào công ty khác.
        var kho = new ZaloOAuthStates();
        var a = kho.Tao("t", "a", "u");
        var b = kho.Tao("t", "a", "u");
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 32, "mã quá ngắn thì dò được");
    }

    [Fact]
    public void Duong_cap_quyen_mang_du_ba_tham_so()
    {
        var url = ZaloChatAdapter.DuongCapQuyen("123", "https://a.b/cb?x=1", "st1");

        Assert.StartsWith("https://oauth.zaloapp.com/v4/oa/permission?", url);
        Assert.Contains("app_id=123", url);
        Assert.Contains("state=st1", url);
        // redirect_uri PHẢI mã hoá: URL callback có thể chứa dấu ? và & (ngrok, tham số), để trần
        // là Zalo đọc nhầm thành tham số của chính nó rồi từ chối mà không nói vì sao.
        Assert.Contains("redirect_uri=https%3A%2F%2Fa.b%2Fcb%3Fx%3D1", url);
    }

    [Fact]
    public void Duong_callback_nam_trong_DuongRieng()
    {
        const string duong = "/api/v1/chat/oauth/zalo/callback";
        Assert.Contains(TourkitAiProxy.Endpoints.ChatInboxEndpoints.DuongRieng,
            p => duong.StartsWith(p + "/", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Chu_ky_webhook_dung_OA_Secret_Key_chu_khong_phai_App_Secret_Key()
    {
        // Zalo cấp HAI khoá khác nhau: App Secret Key để đổi token (gửi), OA Secret Key để ký
        // webhook (nhận). Một ô cho cả hai thì luôn có một chiều hỏng, mà thông báo lỗi không nói
        // ra điều đó — chỉ thấy "tin khách không vào hộp thư".
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/Channels/ZaloChatAdapter.cs");
        Assert.Contains("cfg.OaSecretKey", src);
        Assert.DoesNotContain("timestamp + cfg.SecretKey", src);

        // Nhưng phải LÙI VỀ khoá cũ khi ô mới còn trống — thêm một ô không được làm gãy cấu hình
        // đang chạy.
        Assert.Contains("IsNullOrWhiteSpace(cfg.OaSecretKey) ? cfg.SecretKey", src);
    }
}
