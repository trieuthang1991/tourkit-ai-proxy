using Xunit;
using Xunit.Abstractions;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// KHÔNG phải test kiểm chứng — là công cụ IN RA bản tin để người thật đọc và góp ý câu chữ.
/// Chạy: dotnet test --filter "FullyQualifiedName~BriefSampleDump" --logger "console;verbosity=detailed"
///
/// Giữ lại trong repo vì mỗi lần sửa câu chữ bản tin đều cần xem lại nó ra sao — đọc code
/// StringBuilder không hình dung được, mà dựng workflow chạy thật thì chậm và cần dữ liệu.
/// </summary>
public class BriefSampleDump
{
    private readonly ITestOutputHelper _out;
    public BriefSampleDump(ITestOutputHelper o) => _out = o;

    private static readonly DateTime Today = new(2026, 8, 12);

    [Fact]
    public void In_ban_tin_giam_doc()
    {
        var d = new CeoBriefData(
            ThisMtd: new(4_114_777_029m, 148_420_468m, 3_966_356_561m),
            PrevMtd: new(3_290_000_000m, 205_000_000m, 3_085_000_000m),
            TopSellers: new() { "Nguyễn Văn An — 1,2 tỷ", "Trần Thu Hà — 890tr", "Lê Minh — 640tr" },
            NewDealsYesterday: 7, OpenPaymentAlerts: 2,
            TodayAppointments: 8, OverdueAppointments: 3);

        _out.WriteLine("================ CÓ AI VIẾT NHẬN ĐỊNH ================");
        var withAi = CeoBriefBuilder.WrapAiReply(
            "Tháng này công ty đang chạy tốt: doanh thu 4,11 tỷ, tăng 25% so cùng kỳ tháng trước, "
            + "trong khi chi phí giảm 28% nên lợi nhuận nhảy lên 3,97 tỷ. Đội bán hàng có An dẫn đầu "
            + "rõ rệt với 1,2 tỷ, gấp rưỡi người thứ hai. Hôm qua vào thêm 7 cơ hội mới, nhịp lead "
            + "vẫn đều. Điểm cần để mắt là 2 tour sắp khởi hành mà khách chưa thanh toán đủ — nên "
            + "nhắc bộ phận thu trước khi đoàn đi.",
            d, Today);
        _out.WriteLine(withAi.Title);
        _out.WriteLine("");
        _out.WriteLine(withAi.BodyMarkdown);

        _out.WriteLine("");
        _out.WriteLine("========= AI LỖI / HẾT LƯỢT → BẢN SỐ LIỆU THUẦN =========");
        var fb = CeoBriefBuilder.RenderFallback(d, Today);
        _out.WriteLine(fb.Title);
        _out.WriteLine("");
        _out.WriteLine(fb.BodyMarkdown);

        Assert.NotEmpty(withAi.BodyMarkdown);
    }

    [Fact]
    public void In_ban_tin_nhan_vien_ban_hang()
    {
        var input = new SaleBriefInput(
            Username: "an.nguyen", FullName: "Nguyễn Văn An",
            CoolingDeals: new()
            {
                new(9188, "Tour Nhật 6N5Đ tháng 10", "Anh Tuấn (0982385108)", 72, 5, "Đang tư vấn"),
                new(9201, "Combo Đà Nẵng 4N3Đ", "Chị Lan", 55, 8, "Chờ xử lý"),
                new(9177, "Hàn Quốc mùa lá đỏ", "Anh Dũng", 41, 12, "Đang tư vấn"),
            },
            TodayAppointments: new()
            {
                new("09:30", "Tư vấn tour Nhật", "Anh Tuấn"),
                new("14:00", "Chốt hợp đồng đoàn 20 khách", "Công ty Minh Phát"),
            },
            SleepingVips: new()
            {
                new("Trần Thu Hà", "A", 78),
                new("Công ty Đại Việt", "B", 64),
            },
            StaleQuotes: new()
            {
                new("Báo giá Hàn 5N4Đ đoàn 15 khách", "Anh Long", 9),
            },
            TenantMailPending: 12, TenantMailQuoteRequests: 3,
            HygieneDeals: new()
            {
                new(9150, "Tour Phú Quốc gia đình", null, 30, 21, "Chờ xử lý"),
            },
            MyPaymentAlerts: new()
            {
                new(9195, "Sài Gòn - Đà Nẵng", "Chị Mai", "Nguyễn Văn An",
                    46_904_686m, Today.AddDays(1), 1, 2, "payment:9195"),
            },
            MailSourceOk: true,
            TodayTasks: new()
            {
                new("Gửi hợp đồng đoàn Nhật 20 khách", "Cao", true),
                new("Gọi lại khách hỏi combo Đà Nẵng", "Trung bình", false),
                new("Cập nhật báo giá Hàn cho anh Long", "Thấp", false),
            },
            OverdueTaskCount: 1,
            OverdueAppointments: 1);

        var m = SaleBriefBuilder.Build(input, Today);
        _out.WriteLine("============== BẢN TIN NHÂN VIÊN BÁN HÀNG ==============");
        _out.WriteLine(m.Title);
        _out.WriteLine("");
        _out.WriteLine(m.BodyMarkdown);

        _out.WriteLine("");
        _out.WriteLine("=============== NGÀY KHÔNG CÓ VIỆC GẤP ===============");
        var quiet = SaleBriefBuilder.Build(new SaleBriefInput("an.nguyen", "Nguyễn Văn An",
            new(), new(), new(), new(), 4, 1, new(), new(), true), Today);
        _out.WriteLine(quiet.Title);
        _out.WriteLine("");
        _out.WriteLine(quiet.BodyMarkdown);

        Assert.NotEmpty(m.BodyMarkdown);
    }
}
