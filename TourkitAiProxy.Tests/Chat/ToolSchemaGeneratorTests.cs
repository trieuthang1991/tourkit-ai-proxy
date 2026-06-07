// TourkitAiProxy.Tests/Chat/ToolSchemaGeneratorTests.cs
using System.Text.Json;
using TourkitAiProxy.Services.Chat;

namespace TourkitAiProxy.Tests.Chat;

public class ToolSchemaGeneratorTests
{
    // ─── BuildAnthropicTools: co ban ─────────────────────────────────────────────

    [Fact]
    public void BuildAnthropicTools_count_matches_catalog()
    {
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false);
        Assert.Equal(ChatTools.All.Count, tools.Length);
    }

    [Fact]
    public void BuildAnthropicTools_each_tool_has_name_description_input_schema()
    {
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false);
        var json  = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            Assert.True(el.TryGetProperty("name",         out var n) && n.ValueKind == JsonValueKind.String,
                "thieu truong name");
            Assert.True(el.TryGetProperty("description",  out var d) && d.ValueKind == JsonValueKind.String,
                "thieu truong description");
            Assert.True(el.TryGetProperty("input_schema", out var s) && s.ValueKind == JsonValueKind.Object,
                "thieu truong input_schema");
            Assert.True(s.TryGetProperty("type", out var t) && t.GetString() == "object",
                "input_schema.type phai la 'object'");
            Assert.True(s.TryGetProperty("properties", out _), "thieu properties");
            Assert.True(s.TryGetProperty("required",   out _), "thieu required");
        }
    }

    // ─── cache_control ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildAnthropicTools_cache_control_on_last_tool_when_enabled()
    {
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: true);
        var json  = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        var arr  = doc.RootElement.EnumerateArray().ToList();
        var last = arr[^1];
        Assert.True(last.TryGetProperty("cache_control", out var cc),
            "Tool cuoi phai co cache_control khi addCacheControl=true");
        Assert.True(cc.TryGetProperty("type", out var t) && t.GetString() == "ephemeral");
    }

    [Fact]
    public void BuildAnthropicTools_no_cache_control_when_disabled()
    {
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false);
        var json  = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        foreach (var el in doc.RootElement.EnumerateArray())
            Assert.False(el.TryGetProperty("cache_control", out _),
                "Khong duoc co cache_control khi addCacheControl=false");
    }

    [Fact]
    public void BuildAnthropicTools_only_last_tool_has_cache_control()
    {
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: true);
        var json  = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        var arr = doc.RootElement.EnumerateArray().ToList();
        // Tat ca tru tool cuoi deu KHONG co cache_control
        for (int i = 0; i < arr.Count - 1; i++)
            Assert.False(arr[i].TryGetProperty("cache_control", out _),
                $"Tool [{i}] khong duoc co cache_control");
    }

    // ─── InferSchema: param types ─────────────────────────────────────────────────

    [Theory]
    [InlineData("cashflow", "startDate", "string")]
    [InlineData("cashflow", "endDate",   "string")]
    [InlineData("cashflow", "groupBy",   "string")]
    [InlineData("tours",    "pageIndex", "integer")]
    [InlineData("tours",    "pageSize",  "integer")]
    [InlineData("tours",    "marketId",  "integer")]
    [InlineData("tasks",    "tabFilter", "integer")]
    [InlineData("appointments", "dateFilter", "integer")]
    public void InferSchema_correct_type_for_known_params(string toolName, string param, string expectedType)
    {
        var tools    = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false);
        var json     = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        var toolEl = doc.RootElement.EnumerateArray()
            .FirstOrDefault(t => t.TryGetProperty("name", out var n) && n.GetString() == toolName);
        Assert.NotEqual(default, toolEl);

        var props = toolEl.GetProperty("input_schema").GetProperty("properties");
        Assert.True(props.TryGetProperty(param, out var propEl),
            $"Tool {toolName} thieu param {param}");
        Assert.True(propEl.TryGetProperty("type", out var typeEl),
            $"Param {param} thieu truong 'type'");
        Assert.Equal(expectedType, typeEl.GetString());
    }

    [Fact]
    public void InferSchema_groupBy_has_enum_values()
    {
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false);
        var json  = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        var cashflow = doc.RootElement.EnumerateArray()
            .First(t => t.TryGetProperty("name", out var n) && n.GetString() == "cashflow");
        var groupBy = cashflow.GetProperty("input_schema")
            .GetProperty("properties")
            .GetProperty("groupBy");

        Assert.True(groupBy.TryGetProperty("enum", out var enm) && enm.ValueKind == JsonValueKind.Array);
        var vals = enm.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("day",   vals);
        Assert.Contains("month", vals);
    }

    [Fact]
    public void InferSchema_date_params_have_format_date()
    {
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false);
        var json  = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        // Kiem tra cashflow co startDate format=date
        var cashflow = doc.RootElement.EnumerateArray()
            .First(t => t.TryGetProperty("name", out var n) && n.GetString() == "cashflow");
        var startDate = cashflow.GetProperty("input_schema")
            .GetProperty("properties")
            .GetProperty("startDate");

        Assert.True(startDate.TryGetProperty("format", out var fmt) && fmt.GetString() == "date",
            "startDate phai co format=date");
    }

    // ─── Tool name va description dung catalog ────────────────────────────────────

    [Fact]
    public void BuildAnthropicTools_tool_names_match_catalog()
    {
        var tools     = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false);
        var json      = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        var schemaNames  = doc.RootElement.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToHashSet();
        var catalogNames = ChatTools.All.Select(t => t.Name).ToHashSet();

        Assert.Equal(catalogNames, schemaNames);
    }

    [Fact]
    public void BuildAnthropicTools_tool_without_params_has_empty_properties()
    {
        // departures va notifications khong co params
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false);
        var json  = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);

        var departures = doc.RootElement.EnumerateArray()
            .First(t => t.TryGetProperty("name", out var n) && n.GetString() == "departures");
        var props = departures.GetProperty("input_schema").GetProperty("properties");
        // properties la object rong
        Assert.Equal(0, props.EnumerateObject().Count());
    }
}
