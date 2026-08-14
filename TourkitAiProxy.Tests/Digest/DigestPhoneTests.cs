using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Số nhận bản tin qua Zalo. Sai định dạng thì ZNS từ chối lúc gửi, mà lỗi đó chỉ admin nhìn thấy —
/// người đăng ký ngồi đợi mãi. Nên phải chặn ngay lúc lưu, và chặn cho đúng.
/// </summary>
public class DigestPhoneTests
{
    [Theory]
    [InlineData("0912345678", "0912345678")]
    [InlineData(" 0912 345 678 ", "0912345678")]   // dán từ danh bạ ra hay dính khoảng trắng
    [InlineData("091.234.5678", "0912345678")]
    [InlineData("+84912345678", "0912345678")]
    [InlineData("84912345678", "0912345678")]
    public void Chuan_hoa_ve_mot_dang_duy_nhat(string raw, string expected)
        => Assert.Equal(expected, DigestPhone.Normalize(raw));

    [Fact]
    public void Rong_thi_tra_null_chu_khong_tra_chuoi_rong()
    {
        Assert.Null(DigestPhone.Normalize(null));
        Assert.Null(DigestPhone.Normalize("   "));
    }

    [Theory]
    [InlineData("0912345678")]
    [InlineData("0387654321")]
    [InlineData("+84987654321")]
    public void Nhan_so_di_dong_hop_le(string raw) => Assert.True(DigestPhone.IsValid(raw));

    [Theory]
    [InlineData("02812345678")]   // số bàn — Zalo là ứng dụng di động, nhận vào chỉ tổ gửi hỏng
    [InlineData("091234567")]     // thiếu 1 số
    [InlineData("09123456789")]   // thừa 1 số
    [InlineData("1912345678")]    // không bắt đầu bằng 0
    [InlineData("0112345678")]    // đầu số không tồn tại
    [InlineData("khong-phai-so")]
    [InlineData("")]
    public void Chan_so_khong_dung(string raw) => Assert.False(DigestPhone.IsValid(raw));

    [Fact]
    public void Doi_sang_dang_Zalo_doi()
        => Assert.Equal("84912345678", DigestPhone.ToZaloFormat("0912345678"));

    [Fact]
    public void Rac_khong_cuu_duoc_thi_giu_nguyen_de_bao_loi_chu_khong_bia_so()
    {
        // Quan trọng: KHÔNG được tự nắn thành một số trông hợp lệ. Bịa ra số hợp lệ nghĩa là gửi
        // bản tin của người này tới máy người khác.
        Assert.Equal("khong-phai-so", DigestPhone.Normalize("khong-phai-so"));
        Assert.False(DigestPhone.IsValid("khong-phai-so"));
    }
}
