using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using Xunit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

public class DigestEnqueuePlannerTests
{
    private static DigestSubscription Sub(bool email = false, bool tele = false, bool zalo = false)
        => new("t", "u", BriefTypes.Sale, true, 7, true,
               email, email ? "a@b.vn" : null, tele, tele ? "123" : null, zalo, zalo ? "0912345678" : null, null, null);
    private static readonly DigestMessage Msg = new("Tiêu đề", "body md", "<p>html</p>", BriefTypes.Sale);
    private static readonly DateTime Sched = new(2026, 8, 13, 0, 0, 0);

    [Fact] public void Moi_kenh_ngoai_dang_bat_ra_1_dong()
        => Assert.Equal(3, DigestEnqueuePlanner.BuildRows(Sub(true, true, true), 42, Msg, Sched, "13/08/2026").Count);
    [Fact] public void Khong_kenh_ngoai_thi_khong_dong_nao()
        => Assert.Empty(DigestEnqueuePlanner.BuildRows(Sub(), 42, Msg, Sched, "13/08/2026"));
    [Fact] public void Bat_kenh_ma_trong_noi_nhan_thi_bo_kenh_do()
    {
        var s = new DigestSubscription("t", "u", BriefTypes.Sale, true, 7, true,
            true, "  ", true, "123", false, null, null, null);
        var rows = DigestEnqueuePlanner.BuildRows(s, 42, Msg, Sched, "13/08/2026");
        Assert.Single(rows);
        Assert.Equal(OutboundChannel.Telegram, rows[0].Channel);
    }
    [Fact] public void Dong_email_giu_nguyen_hop_dong_worker()
    {
        var r = DigestEnqueuePlanner.BuildRows(Sub(email: true), 42, Msg, Sched, "13/08/2026").Single();
        Assert.Equal(OutboundChannel.Email, r.Channel);
        Assert.Equal("daily-brief", r.Kind);  Assert.Equal("daily-brief", r.TemplateCode);
        Assert.Equal("a@b.vn", r.ToEmail);    Assert.Equal("Tiêu đề", r.Subject);
        Assert.Equal("42", r.SourceId);       Assert.Equal(Sched, r.ScheduledUtc);
        var p = JsonDocument.Parse(r.Params!).RootElement;
        Assert.Equal("<p>html</p>", p.GetProperty("bodyHtml").GetString());
        Assert.Equal("13/08/2026", p.GetProperty("date").GetString());
    }
    // Hợp đồng với worker bên toutkit-app: dòng telegram/zalo phải MANG ĐỦ nơi nhận + tiêu đề +
    // nội dung, vì worker gửi thẳng từ dòng này chứ KHÔNG đọc lại bảng Bảng tin của proxy.
    // Thiếu 'body' thì worker gửi ra tin rỗng mà không có lỗi nào nổi lên — nên khoá lại ở đây.
    [Fact] public void Dong_telegram_zalo_mang_du_noi_nhan_va_noi_dung()
    {
        var rows = DigestEnqueuePlanner.BuildRows(Sub(tele: true, zalo: true), 42, Msg, Sched, "13/08/2026");
        var tg = JsonDocument.Parse(rows.Single(r => r.Channel == OutboundChannel.Telegram).Data!).RootElement;
        var za = JsonDocument.Parse(rows.Single(r => r.Channel == OutboundChannel.Zalo).Data!).RootElement;

        Assert.Equal("123", tg.GetProperty("chatId").GetString());
        Assert.Equal("Tiêu đề", tg.GetProperty("title").GetString());
        Assert.Equal("body md", tg.GetProperty("body").GetString());

        Assert.Equal("0912345678", za.GetProperty("phone").GetString());
        Assert.Equal("Tiêu đề", za.GetProperty("title").GetString());
        Assert.Equal("body md", za.GetProperty("body").GetString());
    }

    // Params là hợp đồng RIÊNG của kênh email (worker render mẫu HTML từ đó). Kênh khác mà cũng
    // nhét Params vào thì worker email dễ bị dụ xử nhầm dòng không phải của nó.
    [Fact] public void Dong_telegram_zalo_KHONG_mang_Params()
    {
        var rows = DigestEnqueuePlanner.BuildRows(Sub(tele: true, zalo: true), 42, Msg, Sched, "13/08/2026");
        Assert.All(rows, r => Assert.Null(r.Params));
    }
}
