using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Mục "khách hạng A/B lâu chưa mua lại" của bản tin sáng phải gọi khách bằng TÊN.
///
/// <para><b>Chuyện đã xảy ra (09/09/2026).</b> Bảng <c>Reviews</c> của proxy chỉ giữ MÃ khách —
/// tên nằm bên CRM. <c>SaleBriefWorkflow</c> đưa thẳng mã vào ô tên của <c>CustomerLine</c>, nên
/// bản tin gửi đi đọc ra là: <i>"Liên hệ khách hạng B lâu chưa mua lại: 17646 (61 ngày)"</i>.
/// Tra lại thì 17646 là khách <i>Truong Hong Ha</i>.</para>
///
/// <para><b>Vì sao không ai thấy.</b> Không có lỗi nào bật ra: mục vẫn hiện, số ngày vẫn đúng,
/// số lượng vẫn đúng — chỉ mỗi chỗ quan trọng nhất là vô nghĩa. Mà đây lại đúng là mục bảo người
/// bán hàng đi gọi khách; không biết gọi ai thì cả mục bỏ đi. Kiểu hỏng này không có bài test
/// hành vi nào bắt được vì đầu ra vẫn "hợp lệ", nên chốt ở văn bản nguồn.</para>
/// </summary>
public class SaleBriefVipNameGuardTests
{
    private static string Workflow()
        => BoChuThich(Chat.ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Workflows/SaleBriefWorkflow.cs"));

    private static string Builder()
        => BoChuThich(Chat.ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Domain/Digest/SaleBriefBuilder.cs"));

    /// Bỏ mọi dòng chú thích trước khi soi — gồm cả <c>///</c>. Chú thích ở hai file này nhắc tới
    /// đúng những chuỗi mà chốt đang đòi (đường API, nhãn "Khách #", tên hằng số); tính chú thích
    /// là mã thì xoá sạch phần thân mà chốt vẫn xanh.
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", System.StringComparison.Ordinal)));

    [Fact]
    public void O_ten_khach_phai_lay_tu_luot_tra_ten_chu_khong_phai_ma()
    {
        var src = Workflow();

        // Luật chính, đặt ở DẠNG MÃ chứ không ở tên biến: đối số đầu của CustomerLine (ô Name)
        // phải đi ra từ một lượt tra bảng. Đổi tên biến thoải mái; nhét thẳng mã vào thì đỏ.
        Assert.Matches(@"new CustomerLine\(\s*\w+\.TryGetValue\(", src);

        // Và cấm đích danh dạng cũ, phòng ai đó thêm một chỗ dựng CustomerLine thứ hai từ mã.
        Assert.DoesNotMatch(@"new CustomerLine\(\s*\w+\.CustomerId", src);
    }

    [Fact]
    public void Phai_tra_ten_GOP_mot_luot_qua_CRM()
    {
        // Đường gộp (cùng đường luồng chấm hạng dùng) chứ không phải mỗi khách một lượt: đường
        // sang CRM có lúc chậm tới mức hết giờ chờ — đã đo 60 giây cho MỘT lượt gọi ngày
        // 09/09/2026 — nên mười mấy lượt nối tiếp là đủ để cả bản tin sáng lỡ giờ gửi.
        Assert.Contains("/api/ai/customers/context?ids=", Workflow());
    }

    [Fact]
    public void So_dong_tra_ten_phai_bam_theo_so_dong_duoc_neu_ten()
    {
        // Hai nơi, một con số. Bản tin nêu tên tối đa SoDongDuKien dòng mỗi mục; workflow tra tên
        // đúng chừng ấy. Để mỗi nơi giữ một số 12 riêng thì ngày nào đó bản tin nới lên 20 dòng
        // và bảy dòng cuối trống tên — không lỗi, không ai biết.
        Assert.Matches(@"public const int SoDongDuKien = \d+;", Builder());
        Assert.Contains("int take = SoDongDuKien", Builder());
        Assert.Contains("SaleBriefBuilder.SoDongDuKien", Workflow());
    }

    [Fact]
    public void Tra_ten_hut_thi_phai_hien_nhan_co_chu_khong_phai_so_tran()
    {
        // Tra hụt (CRM lỗi, khách đã xoá, mất quyền) là chuyện thường và KHÔNG được làm mất mục.
        // Nhưng lùi về số trần là quay lại đúng lỗi cũ: người đọc thấy "17646" và không biết đó
        // là gì. Có chữ "Khách #" thì ít nhất họ biết đây là một mã để đi tra.
        Assert.Contains("\"Khách #\"", Workflow());
    }
}
