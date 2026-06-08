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

    [Fact]
    public void GetStringField_returns_string_value_or_null()
    {
        var json = JsonDocument.Parse("""{"a":"hello","b":42,"c":null}""").RootElement;
        Assert.Equal("hello", json.GetStringField("a"));
        Assert.Null(json.GetStringField("b"));           // not a string → null
        Assert.Null(json.GetStringField("c"));           // null → null
        Assert.Null(json.GetStringField("missing"));     // missing → null
    }

    [Fact]
    public void GetStringField_is_case_insensitive()
    {
        var json = JsonDocument.Parse("""{"Name":"X"}""").RootElement;
        Assert.Equal("X", json.GetStringField("name"));
        Assert.Equal("X", json.GetStringField("NAME"));
    }

    [Fact]
    public void GetStringListField_returns_strings_skipping_non_strings_and_blanks()
    {
        var json = JsonDocument.Parse("""{"items":["a","",null,42,"b","   ","c"]}""").RootElement;
        var list = json.GetStringListField("items");
        Assert.Equal(new[] { "a", "b", "c" }, list);    // skip empty/null/non-string
    }

    [Fact]
    public void GetStringListField_returns_empty_when_missing_or_not_array()
    {
        var json = JsonDocument.Parse("""{"a":"not-array"}""").RootElement;
        Assert.Empty(json.GetStringListField("a"));
        Assert.Empty(json.GetStringListField("missing"));
    }
}
