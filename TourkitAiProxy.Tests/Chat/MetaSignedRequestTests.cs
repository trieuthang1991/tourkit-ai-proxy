using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh việc bóc <c>signed_request</c> của Meta — thứ DUY NHẤT chứng minh yêu cầu xoá dữ liệu
/// thật sự đến từ Meta. Sai ở đây thì hoặc người ngoài xoá được dữ liệu khách hàng, hoặc yêu cầu
/// thật bị từ chối và Meta đánh trượt hồ sơ.
/// </summary>
public class MetaSignedRequestTests
{
    private const string BiMat = "app-secret-de-thu";

    /// Dựng một gói y như Meta gửi: chữ ký ký trên CHUỖI ĐÃ MÃ HOÁ của phần gói.
    private static string Ky(JsonObject goi, string biMat = BiMat)
    {
        var than = B64Url(Encoding.UTF8.GetBytes(goi.ToJsonString()));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(biMat));
        var chuKy = B64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(than)));
        return chuKy + "." + than;
    }

    private static string B64Url(byte[] b)
        => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonObject GoiChuan() => new()
    {
        ["algorithm"] = "HMAC-SHA256",
        ["issued_at"] = 1787900000,
        ["user_id"] = "8510768175",
    };

    [Fact]
    public void Goi_dung_thi_boc_ra_ma_nguoi_dung()
    {
        var p = MetaSignedRequest.Parse(Ky(GoiChuan()), BiMat);
        Assert.NotNull(p);
        Assert.Equal("8510768175", p!.UserId);
        Assert.Equal(1787900000, p.IssuedAt);
    }

    [Fact]
    public void Sai_khoa_bi_mat_thi_TU_CHOI()
    {
        // Đây là ca quan trọng nhất: khoá sai nghĩa là gói không phải của Meta.
        Assert.Null(MetaSignedRequest.Parse(Ky(GoiChuan(), "khoa-khac"), BiMat));
    }

    [Fact]
    public void Sua_goi_sau_khi_ky_thi_TU_CHOI()
    {
        // Người ngoài bắt được một gói thật rồi đổi user_id sang người khác để xoá dữ liệu của họ.
        var that = Ky(GoiChuan());
        var goiKhac = GoiChuan();
        goiKhac["user_id"] = "9999999999";
        var gia = that.Split('.')[0] + "." + Ky(goiKhac).Split('.')[1];
        Assert.Null(MetaSignedRequest.Parse(gia, BiMat));
    }

    [Theory]
    [InlineData("")]
    [InlineData("khong-co-dau-cham")]
    [InlineData("a.b.c")]
    [InlineData("!!!.!!!")]
    public void Goi_hong_dinh_dang_thi_tra_null_chu_khong_nem(string tho)
    {
        // Đường này CÔNG KHAI, ai cũng POST vào được. Ném lỗi là mở đường cho người ta chọc cho
        // đổ; trả null rồi từ chối lịch sự mới đúng.
        Assert.Null(MetaSignedRequest.Parse(tho, BiMat));
    }

    [Fact]
    public void Chua_khai_khoa_bi_mat_thi_TU_CHOI_chu_khong_cho_qua()
    {
        // Máy chủ quên khai AppSecret mà lại cho qua thì thành cửa xoá dữ liệu không cần chứng minh.
        Assert.Null(MetaSignedRequest.Parse(Ky(GoiChuan()), null));
        Assert.Null(MetaSignedRequest.Parse(Ky(GoiChuan()), "   "));
    }

    [Fact]
    public void Thuat_toan_la_thi_TU_CHOI()
    {
        var goi = GoiChuan();
        goi["algorithm"] = "none";
        Assert.Null(MetaSignedRequest.Parse(Ky(goi), BiMat));
    }

    [Fact]
    public void Thieu_user_id_thi_TU_CHOI()
    {
        var goi = GoiChuan();
        goi.Remove("user_id");
        Assert.Null(MetaSignedRequest.Parse(Ky(goi), BiMat));
    }

    [Fact]
    public void Doc_duoc_base64url_co_ky_tu_thay_the()
    {
        // Gói nào sinh ra byte cho ra '-' hoặc '_' mà đem giải bằng base64 thường là ném lỗi —
        // hỏng đúng ở một phần các gói thật, nên thử tay rất dễ lọt.
        for (var i = 0; i < 200; i++)
        {
            var goi = GoiChuan();
            goi["user_id"] = "user-" + i;
            Assert.NotNull(MetaSignedRequest.Parse(Ky(goi), BiMat));
        }
    }

    [Fact]
    public void Ma_xac_nhan_khong_trung_nhau_va_khong_doan_nguoc_duoc()
    {
        var ds = Enumerable.Range(0, 50).Select(_ => MetaSignedRequest.NewConfirmationCode()).ToList();
        Assert.Equal(ds.Count, ds.Distinct().Count());
        Assert.All(ds, m => Assert.Equal(24, m.Length));
        // Không được suy ra từ mã người dùng: mã này hiện trên trang CÔNG KHAI tra tiến độ.
        Assert.DoesNotContain("8510768175", string.Join(' ', ds));
    }

    // ── Canh đường xoá dữ liệu ở tầng endpoint/CSDL ─────────────────────────

    [Fact]
    public void Duong_xoa_du_lieu_phai_KIEM_CHU_KY_truoc_khi_xoa()
    {
        // Đường này CÔNG KHAI, ai cũng POST vào được. Bỏ bước kiểm chữ ký là dựng sẵn một cửa cho
        // người ngoài xoá dữ liệu khách hàng của mọi công ty.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var i = src.IndexOf("meta/data-deletion", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy đường Data Deletion Callback");
        var than = src.Substring(i, System.Math.Min(1800, src.Length - i));

        Assert.Contains("MetaSignedRequest.Parse", than);
        Assert.Contains("Chat:Messenger:AppSecret", than);
        // Phải TỪ CHỐI trước khi chạm tới hàm xoá.
        Assert.True(than.IndexOf("BadRequest", System.StringComparison.Ordinal)
                    < than.IndexOf("DeleteContactDataAsync", System.StringComparison.Ordinal),
            "Phải trả lỗi khi chữ ký sai TRƯỚC khi gọi xoá");
    }

    [Fact]
    public void Tra_ve_dung_hinh_dang_Meta_doi()
    {
        // Meta đòi đúng hai khoá này. Sai tên là họ báo lỗi cấu hình mà không nói vì sao.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        Assert.Contains("confirmation_code", src);
        Assert.Contains("url = $\"{goc}/api/v1/chat/data-deletion/", src);
    }

    [Fact]
    public void Xoa_ca_Messenger_lan_Instagram()
    {
        // Hai kênh dùng chung một ứng dụng Meta, mà mã người dùng gửi sang không nói rõ kênh nào.
        // Xoá thiếu một kênh là lời hứa "đã xoá" thành lời nói dối.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var i = src.IndexOf("meta/data-deletion", System.StringComparison.Ordinal);
        var than = src.Substring(i, System.Math.Min(1800, src.Length - i));
        Assert.Contains("ChatChannel.Messenger, ChatChannel.Instagram", than);
    }

    [Fact]
    public void Xoa_that_chu_khong_xoa_mem()
    {
        // Mọi chỗ khác trong hệ xoá mềm để giữ lịch sử nghiệp vụ. Riêng đường này thì không:
        // giữ lại "cho có dấu vết" đúng là thứ người ta yêu cầu bỏ đi.
        var repo = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
        var i = repo.IndexOf("DeleteContactDataAsync", System.StringComparison.Ordinal);
        var than = repo.Substring(i, System.Math.Min(2400, repo.Length - i));

        Assert.Contains("DELETE FROM chat_contacts", than);
        Assert.Contains("DELETE FROM chat_conversations", than);
        Assert.DoesNotContain("deleted_utc = now()", than);
        // Một giao dịch: xoá nửa chừng rồi hỏng còn tệ hơn chưa xoá.
        Assert.Contains("BeginTransactionAsync", than);
    }

    [Fact]
    public void Xoa_KHONG_khoa_theo_cong_ty()
    {
        // Meta chỉ gửi mã người dùng, không nói họ đã nhắn cho công ty nào — mà một người có thể
        // đã nhắn cho hai công ty cùng dùng hệ này.
        var repo = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
        var i = repo.IndexOf("DeleteContactDataAsync", System.StringComparison.Ordinal);
        var than = repo.Substring(i, System.Math.Min(2400, repo.Length - i));
        Assert.DoesNotContain("tenant_id = @tenant", than);
    }
}
