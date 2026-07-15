using System.Text.Json;
using TourkitAiProxy.Services.Chat;
using Xunit;

public class ActionExecutorTests
{
    [Theory]
    [InlineData("cao", 1)] [InlineData("TB", 2)] [InlineData("thấp", 3)] [InlineData("", 0)]
    public void MapPriority_maps_vietnamese(string input, int expected)
        => Assert.Equal(expected, ActionExecutor.MapPriority(input));

    [Fact]
    public void AssignTask_payload_has_required_fields()
    {
        var json = ActionExecutor.BuildAssignTaskPayload(
            12, "Board A", "Gọi lại khách A", "ND", "15,18", 1,
            new DateTime(2026,7,15,0,0,0,DateTimeKind.Utc),
            new DateTime(2026,7,16,0,0,0,DateTimeKind.Utc), 30, 456);
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        Assert.Equal(12, r.GetProperty("workflowId").GetInt32());
        Assert.Equal("15,18", r.GetProperty("staffsInCharge").GetString());
        Assert.Equal(1, r.GetProperty("status").GetInt32());
        Assert.Equal(456, r.GetProperty("bookingTicketId").GetInt32());
    }

    [Fact]
    public void Appointment_payload_has_care_times()
    {
        var json = ActionExecutor.BuildAppointmentPayload(
            123, "Hẹn tư vấn", "chi tiết",
            new DateTime(2026,7,16,2,0,0,DateTimeKind.Utc),
            new DateTime(2026,7,16,3,0,0,DateTimeKind.Utc), 30, "A", "09", null,
            insUid: null, typeSchedule: 0);
        using var d = JsonDocument.Parse(json);
        Assert.Equal(123, d.RootElement.GetProperty("customerId").GetInt32());
        Assert.True(d.RootElement.TryGetProperty("careStartTime", out _));
    }

    [Fact]
    public void ParseUtc_treats_bare_datetime_as_vietnam_local()
    {
        var r = ActionExecutor.ParseUtc("2026-07-15T09:00");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2026, 7, 15, 2, 0, 0, DateTimeKind.Utc), r!.Value);
        Assert.Equal(DateTimeKind.Utc, r.Value.Kind);
    }

    [Fact]
    public void ParseUtc_honors_explicit_utc_z()
    {
        var r = ActionExecutor.ParseUtc("2026-07-15T09:00:00Z");
        Assert.Equal(new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc), r!.Value);
    }

    [Fact]
    public void ParseUtc_honors_explicit_offset()
    {
        var r = ActionExecutor.ParseUtc("2026-07-15T09:00:00+07:00");
        Assert.Equal(new DateTime(2026, 7, 15, 2, 0, 0, DateTimeKind.Utc), r!.Value);
    }

    [Fact]
    public void ParseUtc_null_on_garbage() => Assert.Null(ActionExecutor.ParseUtc("khong-phai-ngay"));
}
