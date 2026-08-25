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
}
