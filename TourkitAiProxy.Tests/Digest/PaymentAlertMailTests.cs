using System.Text.Json;
using Xunit;
using TourkitAiProxy.Domain.Digest;
using TourkitAiProxy.Domain.Speech;

namespace TourkitAiProxy.Tests.Digest;

/// Thư "tour sắp đi mà khách còn nợ". Phải test ở đây vì đường gửi thật đi qua worker ở repo
/// khác — chạy thử đầu-cuối không kiểm được phần nội dung.
public class PaymentAlertMailTests
{
    private static readonly DateTime Today = new(2026, 8, 17);

    private static PaymentAlert A(int id, string title, string? customer, decimal outstanding,
        int daysLeft, string? seller = "NV A")
        => new(id, title, customer, seller, outstanding, Today.AddDays(daysLeft), daysLeft,
               daysLeft <= 3 ? 2 : 1, $"payment:{id}");

    /// Đọc lại bodyHtml đúng như worker sẽ đọc. Không so chuỗi thẳng trên ParamsJson: bộ tuần tự
    /// JSON mã hoá &lt; &gt; &amp; và cả chữ có dấu thành \uXXXX, so trên đó là so nhầm tầng.
    private static string Body(PaymentAlertMail.Result r)
        => JsonDocument.Parse(r.ParamsJson).RootElement.GetProperty("bodyHtml").GetString()!;

    [Fact]
    public void Gop_nhieu_tour_vao_MOT_thu_va_cong_tong()
    {
        var r = PaymentAlertMail.Build(new[] { A(1, "Tour A", "Khách 1", 5_000_000m, 5),
                                               A(2, "Tour B", "Khách 2", 3_000_000m, 2) }, Today);
        Assert.Contains("2 tour", r.Subject);
        // Dấu CHẤM (vi-VN) và KHÔNG phụ thuộc ngôn ngữ máy chủ — khớp các thẻ khác trong Bảng tin.
        Assert.Contains("8.000.000đ", Body(r));
    }

    [Fact]
    public void Mot_tour_thi_tieu_de_so_it()
        => Assert.Contains("1 tour", PaymentAlertMail.Build(new[] { A(1, "T", "K", 1m, 5) }, Today).Subject);

    [Fact]
    public void Gap_nhat_xep_len_dau()
    {
        // Còn 1 ngày phải đứng trước còn 6 ngày, dù nợ ít hơn.
        var b = Body(PaymentAlertMail.Build(new[] { A(1, "TourXa", "K", 9_000_000m, 6),
                                                    A(2, "TourGan", "K", 1_000m, 1) }, Today));
        Assert.True(b.IndexOf("TourGan", StringComparison.Ordinal)
                  < b.IndexOf("TourXa", StringComparison.Ordinal));
    }

    [Fact]
    public void Ten_tour_co_the_HTML_phai_duoc_escape()
    {
        // Tên tour do người ngoài nhập. Worker chèn NGUYÊN bodyHtml nên không escape ở đây là
        // thẻ lạ chạy thẳng vào thư người nhận.
        var b = Body(PaymentAlertMail.Build(new[] { A(1, "<script>x</script>", "K & Co", 1m, 5) }, Today));
        Assert.DoesNotContain("<script>", b);
        Assert.Contains("&lt;script&gt;", b);
        Assert.Contains("&amp;", b);
    }

    [Fact]
    public void Thieu_ten_khach_van_dung_duoc_thu()
        => Assert.Contains("—", Body(PaymentAlertMail.Build(
            new[] { A(1, "T", null, 1m, 5, seller: null) }, Today)));
}
