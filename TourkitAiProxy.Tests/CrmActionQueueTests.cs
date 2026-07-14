using TourkitAiProxy.Services.Crm;
using Xunit;

public class CrmActionQueueTests
{
    [Fact]
    public void Kind_constants_match_worker_contract()
    {
        Assert.Equal("assign-task", CrmActionKind.AssignTask);
        Assert.Equal("create-appointment", CrmActionKind.CreateAppointment);
    }

    [Fact]
    public void Status_constants_are_stable()
    {
        Assert.Equal(0, CrmActionStatus.Pending);
        Assert.Equal(2, CrmActionStatus.Done);
        Assert.Equal(3, CrmActionStatus.Failed);
    }

    [Fact]
    public void Input_record_carries_required_fields()
    {
        var i = new CrmActionInput("t1", "user@x", CrmActionKind.AssignTask, "{\"name\":\"x\"}");
        Assert.Equal("t1", i.TenantId);
        Assert.Equal("assign-task", i.Kind);
    }
}
