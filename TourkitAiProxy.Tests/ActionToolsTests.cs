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
        var cat = ActionTools.CatalogForPrompt();
        foreach (var a in ActionTools.All) Assert.Contains(a.Name, cat);
    }

    [Fact]
    public void Kinds_are_correctly_assigned()
    {
        Assert.Equal(ActionKind.CrmQueue, ActionTools.Find("assign_task")!.Kind);
        Assert.Equal(ActionKind.Internal, ActionTools.Find("score_deal")!.Kind);
        Assert.Equal(ActionKind.Mail, ActionTools.Find("compose_mail")!.Kind);
    }
}
