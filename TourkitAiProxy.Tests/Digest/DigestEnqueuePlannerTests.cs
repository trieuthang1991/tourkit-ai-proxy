using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class DigestEnqueuePlannerTests
{
    private static DigestSubscription Sub(bool email = false, bool tele = false, bool zalo = false)
        => new("t", "u", BriefTypes.Sale, true, 7, true,
               email, email ? "a@b.vn" : null, tele, tele ? "123" : null, zalo, zalo ? "z9" : null, null, null);
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
    [Fact] public void Dong_telegram_zalo_nhe_khong_mang_body()
    {
        var rows = DigestEnqueuePlanner.BuildRows(Sub(tele: true, zalo: true), 42, Msg, Sched, "13/08/2026");
        var tg = rows.Single(r => r.Channel == OutboundChannel.Telegram);
        var za = rows.Single(r => r.Channel == OutboundChannel.Zalo);
        Assert.Null(tg.Params);  Assert.Null(za.Params);
        Assert.Equal("123", JsonDocument.Parse(tg.Data!).RootElement.GetProperty("chatId").GetString());
        Assert.Equal("z9", JsonDocument.Parse(za.Data!).RootElement.GetProperty("zaloUserId").GetString());
    }
}
