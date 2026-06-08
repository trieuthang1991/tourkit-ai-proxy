using System.Text.Json;
using TourkitAiProxy.Services.Json;
using Xunit;

namespace TourkitAiProxy.Tests.Json;

public class JsonElementExtensionsTests
{
    [Fact]
    public void TryGetField_finds_property_case_insensitive()
    {
        var json = JsonDocument.Parse("""{"Name":"Trieu","AGE":30}""").RootElement;
        Assert.True(json.TryGetField("name", out var name));
        Assert.Equal("Trieu", name.GetString());
        Assert.True(json.TryGetField("age", out var age));
        Assert.Equal(30, age.GetInt32());
    }

    [Fact]
    public void TryGetField_returns_false_when_missing()
    {
        var json = JsonDocument.Parse("""{"a":1}""").RootElement;
        Assert.False(json.TryGetField("missing", out _));
    }

    [Fact]
    public void TryGetField_returns_false_for_non_object()
    {
        var json = JsonDocument.Parse("""[1,2,3]""").RootElement;
        Assert.False(json.TryGetField("anything", out _));
    }
}
