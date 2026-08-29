using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh chỗ lấy ảnh đại diện của Telegram — nơi một dòng ngắn từng chặn đứng cả hộp thư.
///
/// <para><b>Chuyện đã xảy ra (28/08/2026).</b> Mã cũ viết
/// <c>anh?["result"]?["photos"]?[0]?[0]?["file_id"]</c>. Dấu <c>?.</c> chỉ chặn <c>null</c>, nó
/// <b>KHÔNG</b> chặn mảng RỖNG — mà Telegram trả <c>"photos": []</c> cho mọi khách chưa đặt ảnh
/// đại diện. Chỉ số 0 trên mảng rỗng ném <c>ArgumentOutOfRangeException</c>, và vì
/// <c>ContactProfileAsync</c> không nằm trong khối bắt lỗi nào, nó ném ra tận ngoài làm hỏng cả
/// sự kiện — <b>tin của khách không bao giờ vào hộp thư</b>.</para>
///
/// <para>Triệu chứng nhìn từ ngoài rất dễ đổ oan cho chỗ khác: liên hệ ĐƯỢC tạo (bước trước đó),
/// còn hội thoại thì không — nên trông y hệt lỗi tạo hội thoại. Một câu "Xin chào" của khách chưa
/// có ảnh đại diện là đủ để tái hiện.</para>
/// </summary>
public class TelegramProfilePhotoTests
{
    private static string Nguon()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/Channels/TelegramChatAdapter.cs");

    /// <summary>
    /// Bỏ mọi dòng chú thích trước khi soi. Không bỏ thì chính câu CẢNH BÁO viết trong mã
    /// ("tuyệt đối không viết ?[0]…") lại bị coi là vi phạm — guard bắt nhầm người đang làm
    /// đúng, và lần sau người ta sẽ sửa guard cho qua chuyện thay vì sửa mã.
    /// </summary>
    private static string NguonKhongChuThich()
        => string.Join(" ", Nguon()
            .Split('\n')
            .Where(d => !d.TrimStart().StartsWith("//")));

    [Fact]
    public void Khong_duoc_lay_chi_so_0_trong_chuoi_dau_hoi_tren_mang_JSON()
    {
        // Bắt đúng lối viết đã gây lỗi: một chuỗi ?[...] có chứa ?[0].
        // Viết đúng thì phải kiểm Count trước, nên không còn dạng này nữa.
        var xau = Regex.Matches(NguonKhongChuThich(), @"\?\[""[a-z_]+""\]\s*\?\[0\]")
            .Select(m => m.Value).ToList();

        Assert.True(xau.Count == 0,
            "Còn chỗ lấy chỉ số 0 bằng ?[0] trên mảng JSON: " + string.Join(" · ", xau)
            + ". Dấu ?. KHÔNG chặn mảng rỗng — phải kiểm Count trước, "
            + "nếu không một khách chưa đặt ảnh đại diện là đủ làm hỏng cả gói tin.");
    }

    [Fact]
    public void Phai_kiem_Count_truoc_khi_lay_anh_dai_dien()
    {
        // Bám vào chỗ GỌI THẬT (có tham số), không bám tên trần: tên đó còn nằm trong khối
        // tài liệu phía trên và bắt trúng đó là soi nhầm đoạn.
        var src = Nguon();
        var i = src.IndexOf("getUserProfilePhotos?user_id=", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy chỗ gọi getUserProfilePhotos");
        var than = src.Substring(i, System.Math.Min(1400, src.Length - i));

        Assert.Contains("is JsonArray", than);
        Assert.Contains("Count > 0", than);
    }
}
