using TourkitAiProxy.Infrastructure.Security;
using TourkitAiProxy.Infrastructure.TourKit;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Đọc mật khẩu đã mã hoá của một phiên đăng nhập.
///
/// <para><b>Ba ca KHÁC NHAU, trước 27/08/2026 gộp làm một.</b> Lớp đọc coi mọi kết quả rỗng là
/// "giải mã hỏng" rồi vứt phiên — nhưng <b>phiên SSO vốn KHÔNG có mật khẩu</b> (đăng nhập một chạm
/// từ CRM, ký bằng HMAC, zero password), nên nó cũng ra rỗng và bị vứt oan.</para>
///
/// <para>Hậu quả thật: mỗi lần khởi động lại, mọi phiên SSO biến mất và không khôi phục được — kể
/// cả khi tra thẳng theo id. Nặng nhất là bản tin sáng: <c>SaleBriefWorkflow</c> hỏi CSDL theo tên
/// người dùng, không thấy gì, rồi ghi nhật ký "chưa đăng nhập lần nào" — sai sự thật, và người đó
/// <b>không bao giờ nhận được bản tin</b>. Không lỗi, không ai biết.</para>
/// </summary>
public class TkSessionPasswordStateTests
{
    [Fact]
    public void Mat_khau_that_thi_giai_ra_dung()
    {
        var st = TkSessionRepository.ReadPassword(Crypton.Encrypt("m@tkh@u"), out var pwd);
        Assert.Equal(TkSessionRepository.PasswordState.Ok, st);
        Assert.Equal("m@tkh@u", pwd);
    }

    [Fact]
    public void Mat_khau_RONG_la_phien_SSO_va_phai_GIU_LAI()
    {
        // Đây là cả bài toán. Crypton.Encrypt("") ra một chuỗi base64 hợp lệ, KHÔNG rỗng; giải ra
        // thì lại rỗng. Trước đây chỗ này bị đọc thành "giải mã hỏng".
        var enc = Crypton.Encrypt("");
        Assert.False(string.IsNullOrEmpty(enc));

        var st = TkSessionRepository.ReadPassword(enc, out var pwd);
        Assert.Equal(TkSessionRepository.PasswordState.Sso, st);
        Assert.Equal("", pwd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cot_rong_han_la_thieu_du_lieu_chu_khong_phai_SSO(string? enc)
    {
        // Khác phiên SSO: SSO có chuỗi mã hoá đàng hoàng, chỉ nội dung là rỗng. Cột rỗng hẳn nghĩa
        // là dòng ghi thiếu — không suy ra được người này đăng nhập kiểu gì, nên không giữ.
        var st = TkSessionRepository.ReadPassword(enc, out var pwd);
        Assert.Equal(TkSessionRepository.PasswordState.MissingColumn, st);
        Assert.Equal("", pwd);
    }

    [Fact]
    public void Chuoi_khong_phai_base64_la_du_lieu_hong()
    {
        var st = TkSessionRepository.ReadPassword("khong-phai-base64!!!", out _);
        Assert.Equal(TkSessionRepository.PasswordState.Corrupt, st);
    }

    [Fact]
    public void Base64_hop_le_nhung_khong_giai_duoc_cung_la_du_lieu_hong()
    {
        // 5 byte — không chia hết cho khối 16 của AES nên chắc chắn ném, không phụ thuộc may rủi.
        var st = TkSessionRepository.ReadPassword(System.Convert.ToBase64String(new byte[5]), out _);
        Assert.Equal(TkSessionRepository.PasswordState.Corrupt, st);
    }

    [Fact]
    public void Phien_SSO_va_du_lieu_hong_KHONG_duoc_lan_lon()
    {
        // Chốt chặn của cả bản sửa: hai thứ này trước đây cho ra cùng một kết quả.
        var sso = TkSessionRepository.ReadPassword(Crypton.Encrypt(""), out _);
        var hong = TkSessionRepository.ReadPassword("!!!", out _);
        Assert.NotEqual(sso, hong);
    }
}
