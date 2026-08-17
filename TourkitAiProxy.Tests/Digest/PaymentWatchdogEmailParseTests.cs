using TourkitAiProxy.Services.Workflows;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// Tách danh sách email người dùng gõ tay vào ô cấu hình.
public class PaymentWatchdogEmailParseTests
{
    [Fact]
    public void Trong_thi_khong_co_dia_chi_nao()
    {
        Assert.Empty(PaymentWatchdogWorkflow.ParseEmails(null));
        Assert.Empty(PaymentWatchdogWorkflow.ParseEmails("   "));
    }

    [Fact]
    public void Nhan_ca_phay_cham_phay_va_xuong_dong()
    {
        // Người dùng hay dán từ Excel/Zalo sang nên dấu ngăn cách mỗi lúc một kiểu.
        var r = PaymentWatchdogWorkflow.ParseEmails("a@x.vn, b@x.vn; c@x.vn\nd@x.vn");
        Assert.Equal(new[] { "a@x.vn", "b@x.vn", "c@x.vn", "d@x.vn" }, r);
    }

    [Fact]
    public void Bo_chuoi_khong_phai_email()
    {
        // Gõ nhầm tên người vào đây thì bỏ, đừng xếp vào hàng đợi để rồi sinh một dòng lỗi mà
        // không ai hiểu vì sao.
        Assert.Equal(new[] { "ok@x.vn" }, PaymentWatchdogWorkflow.ParseEmails("ke toan, ok@x.vn"));
    }

    [Fact]
    public void Trung_dia_chi_chi_gui_mot_lan()
        => Assert.Single(PaymentWatchdogWorkflow.ParseEmails("A@X.vn, a@x.vn"));
}
