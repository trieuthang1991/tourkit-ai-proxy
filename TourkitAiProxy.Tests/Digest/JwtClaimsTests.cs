using TourkitAiProxy.Infrastructure.TourKit;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class JwtClaimsTests
{
    private static string MakeJwt(string payloadJson)
    {
        static string B64Url(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64Url("{\"alg\":\"HS256\"}")}.{B64Url(payloadJson)}.sig";
    }

    [Fact] public void Doc_user_id_dang_so()
        => Assert.Equal(123, JwtClaims.TryGetUserId(MakeJwt("{\"user_id\":123,\"tenant_id\":\"t\"}")));

    [Fact] public void Doc_user_id_dang_chuoi_so()
        => Assert.Equal(45, JwtClaims.TryGetUserId(MakeJwt("{\"user_id\":\"45\"}")));

    [Fact] public void Thieu_claim_tra_null()
        => Assert.Null(JwtClaims.TryGetUserId(MakeJwt("{\"tenant_id\":\"t\"}")));

    [Theory]
    [InlineData("")]
    [InlineData("khong.phai.jwt-hop-le")]
    [InlineData("1phan")]
    public void Jwt_rac_tra_null(string jwt) => Assert.Null(JwtClaims.TryGetUserId(jwt));

    [Fact]
    public void Doc_duoc_is_admin_dang_chuoi_True()
        // ERP ghi claim bằng bool.ToString() → "True"/"False", KHÔNG phải "true"/"false".
        // So sánh phân biệt hoa thường là hỏng im lặng: admin thành nhân viên thường.
        => Assert.True(JwtClaims.TryGetIsAdmin(MakeJwt("{\"is_admin\":\"True\"}")));

    [Fact]
    public void Doc_duoc_is_admin_dang_bool()
        => Assert.True(JwtClaims.TryGetIsAdmin(MakeJwt("{\"is_admin\":true}")));

    [Fact]
    public void Thieu_claim_thi_KHONG_phai_admin()
        // Sai theo hướng an toàn: thiếu claim mà đoán là admin thì cả công ty xem được hết.
        => Assert.False(JwtClaims.TryGetIsAdmin(MakeJwt("{\"user_id\":1}")));

    [Fact]
    public void Jwt_rac_thi_KHONG_phai_admin()
        => Assert.False(JwtClaims.TryGetIsAdmin("khong-phai-jwt"));
}
