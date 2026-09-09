using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Mọi lượt gọi ERP từ hộp thư chat phải lấy vé JWT qua <c>GetValidJwtAsync</c>, KHÔNG đọc thẳng
/// vé đang nằm trong phiên.
///
/// <para><b>Chuyện đã xảy ra (09/09/2026).</b> Màn hình cấu hình phân công gọi
/// <c>/api/ai/reference</c> bằng <c>s!.Jwt</c> — vé thô của phiên. Vé KHÔNG được lưu xuống CSDL,
/// nên máy chủ vừa khởi động lại là mọi phiên có vé rỗng; hết hạn mềm thì vé cũ cũng vô dụng. ERP
/// trả 401, ngoại lệ rơi vào <c>catch</c> vốn dựng để nuốt lỗi mạng, và người dùng thấy đúng MỘT
/// triệu chứng: ô chọn người phụ trách trống trơn.</para>
///
/// <para>Triệu chứng đó trùng khít với "ERP không có người bán nào", nên mất một buổi đi tìm lỗi
/// bên CRM trong khi CRM vẫn tốt. Mọi đường khác gọi ERP đều đi qua <c>GetValidJwtAsync</c> (nó tự
/// đăng nhập lại ngầm bằng mật khẩu đã lưu) — đây từng là chỗ DUY NHẤT còn sót.</para>
/// </summary>
public class ChatErpJwtGuardTests
{
    private static string Nguon()
        => BoChuThich(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs"));

    /// Bỏ chú thích trước khi soi — chú thích của chính bản vá có nhắc tới dạng mã bị cấm,
    /// để nguyên thì bài canh đếm phải chữ của mình và xanh giả.
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                && !d.TrimStart().StartsWith("*", StringComparison.Ordinal)
                                && !d.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    [Fact]
    public void Khong_duoc_dua_ve_tho_cua_phien_vao_loi_goi_ERP()
    {
        var src = Nguon();

        // Canh chính bài canh: phải THẤY lượt gọi ERP, không thì bài này xanh vì quét trượt.
        var soLuotGoi = Regex.Matches(src, @"api\.GetAsync\(").Count;
        Assert.True(soLuotGoi >= 1,
            "Không thấy lượt gọi api.GetAsync nào — bài canh đang soi nhầm chỗ, không phải đã an toàn");

        foreach (Match m in Regex.Matches(src, @"api\.GetAsync\(\s*([^,]+),"))
        {
            var thamSoVe = m.Groups[1].Value;
            Assert.False(thamSoVe.Contains(".Jwt", StringComparison.Ordinal),
                $"Lượt gọi ERP đang đưa vé thô của phiên ({thamSoVe.Trim()}). Vé không lưu xuống " +
                "CSDL nên máy chủ vừa khởi động là rỗng → ERP trả 401 → ô chọn người phụ trách " +
                "trống mà không ai biết vì sao. Lấy vé bằng sessions.GetValidJwtAsync(a.SessionId, ct).");
        }
    }

    [Fact]
    public void Duong_lay_danh_sach_nhan_vien_phai_goi_ham_lam_moi_ve()
    {
        // Vế khẳng định, đi kèm vế phủ định ở trên: không đưa vé thô là chưa đủ, phải đưa vé ĐÚNG.
        // Thiếu vế này thì xoá luôn lượt gọi ERP cũng làm bài canh trên xanh.
        var src = Nguon();
        var i = src.IndexOf("/api/ai/reference", StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy đường /api/ai/reference trong hộp thư chat");

        // Cắt ngược một khoảng đủ chứa phần lấy vé ngay trước lượt gọi.
        var truoc = src.Substring(Math.Max(0, i - 600), Math.Min(600, i));
        Assert.Contains("GetValidJwtAsync", truoc);
    }
}
