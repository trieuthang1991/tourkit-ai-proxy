using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Services.Chat;
using Xunit;

public class ActionToolsTests
{
    [Fact]
    public void Find_is_case_insensitive()
        => Assert.NotNull(ActionTools.Find("REVIEW_CUSTOMER"));

    [Fact]
    public void Mail_and_crm_actions_need_confirm_but_review_does_not()
    {
        Assert.True(ActionTools.Find("send_mail_reply")!.NeedsConfirm);
        Assert.True(ActionTools.Find("assign_task")!.NeedsConfirm);
        Assert.False(ActionTools.Find("review_customer")!.NeedsConfirm);
        Assert.False(ActionTools.Find("check_mail")!.NeedsConfirm);
    }

    [Fact]
    public void Catalog_lists_every_action()
    {
        var cat = ActionTools.CatalogForPrompt(ActionTools.All);
        foreach (var a in ActionTools.All) Assert.Contains(a.Name, cat);
    }

    private static IConfiguration Cfg(params (string Key, string Val)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(x => x.Key, x => (string?)x.Val))
            .Build();

    /// Tool nằm sau cờ phải BIẾN MẤT khỏi danh mục gửi cho AI khi cờ tắt — chặn ở đây thì AI không
    /// biết là có nó để mà gọi. Chặn lúc thực thi thôi là muộn: AI đã hứa với người dùng rồi.
    [Fact]
    public void Tool_sau_co_bien_mat_khoi_danh_muc_khi_co_tat()
    {
        var off = ActionTools.Enabled(Cfg(("Features:MeetingBrief", "false")));
        Assert.DoesNotContain(off, a => a.Name == "prepare_meeting");
        Assert.DoesNotContain("prepare_meeting", ActionTools.CatalogForPrompt(off));

        var on = ActionTools.Enabled(Cfg(("Features:MeetingBrief", "true")));
        Assert.Contains(on, a => a.Name == "prepare_meeting");
    }

    /// THIẾU KEY = TẮT. Cố ý sai theo hướng an toàn: quên khai lúc deploy thì tính năng bị ẩn
    /// (phiền, sửa 1 dòng), còn mặc định bật thì tính năng chưa ra mắt lọt ra bản public.
    [Fact]
    public void Thieu_key_thi_coi_nhu_tat()
        => Assert.DoesNotContain(ActionTools.Enabled(Cfg()), a => a.Name == "prepare_meeting");

    /// Cờ chỉ được chặn ĐÚNG tool của nó — tắt nhầm cả loạt thì trợ lý câm mà không ai hiểu vì sao.
    [Fact]
    public void Co_tat_khong_lam_anh_huong_tool_khac()
    {
        var off = ActionTools.Enabled(Cfg());
        Assert.Equal(ActionTools.All.Count - 1, off.Count);
        foreach (var name in new[] { "check_mail", "review_customer", "score_deal", "assign_task" })
            Assert.Contains(off, a => a.Name == name);
    }

    [Fact]
    public void IsEnabled_dung_cho_chot_chan_luc_thuc_thi()
    {
        Assert.False(ActionTools.IsEnabled(Cfg(), "prepare_meeting"));
        Assert.True(ActionTools.IsEnabled(Cfg(("Features:MeetingBrief", "true")), "prepare_meeting"));
        Assert.True(ActionTools.IsEnabled(Cfg(), "review_customer"));   // khong nam sau co
    }

    [Fact]
    public void Kinds_are_correctly_assigned()
    {
        Assert.Equal(ActionKind.CrmQueue, ActionTools.Find("assign_task")!.Kind);
        Assert.Equal(ActionKind.Internal, ActionTools.Find("score_deal")!.Kind);
        Assert.Equal(ActionKind.Mail, ActionTools.Find("compose_mail")!.Kind);
    }
}
