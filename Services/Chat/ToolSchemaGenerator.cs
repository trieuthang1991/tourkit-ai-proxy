// Services/Chat/ToolSchemaGenerator.cs
namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Chuyen ChatTool catalog thanh danh sach Anthropic tool schema (JSON Schema tiêu chuẩn).
/// Dùng chung cho cả NativeToolUseAgent hiện tại lẫn provider khác sau này (OpenAI Responses API).
/// </summary>
public static class ToolSchemaGenerator
{
    /// <summary>
    /// Tao mang tool schema theo format Anthropic:
    ///   { name, description, input_schema: {type:"object", properties:{...}, required:[]} }
    /// Neu addCacheControl=true: gan cache_control:{type:"ephemeral"} vao tool CUOI (prompt cache).
    /// </summary>
    public static object[] BuildAnthropicTools(bool addCacheControl = true)
    {
        var tools = ChatTools.All;
        var result = new List<object>(tools.Count);

        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            var properties = new Dictionary<string, object>();
            foreach (var p in t.Params)
            {
                properties[p] = InferSchema(p);
            }

            bool isLast = (i == tools.Count - 1);

            // Tool cuoi + cache_control de Anthropic cache phan system+tools (tiet kiem input tokens).
            object toolObj = (isLast && addCacheControl)
                ? new
                {
                    name         = t.Name,
                    description  = t.Description,
                    input_schema = new { type = "object", properties, required = Array.Empty<string>() },
                    cache_control = new { type = "ephemeral" }
                }
                : (object)new
                {
                    name         = t.Name,
                    description  = t.Description,
                    input_schema = new { type = "object", properties, required = Array.Empty<string>() }
                };

            result.Add(toolObj);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Tao mang tool schema Anthropic tu ActionTools catalog (giao viec/tra loi mail/danh gia KH/...).
    /// Params cua action da mostly free-form string (xem ActionTools) nen schema don gian: moi property
    /// la {type:"string"}, khong required (planner co the omit field chua biet, ActionResolver o
    /// ChatAgentService se resolve/hoi lai sau). Dung chung voi BuildAnthropicTools trong cung 1 mang
    /// tools[] cua NativeToolUseAgent -- xem ghi chu "action tool interception" trong file do.
    /// </summary>
    public static object[] BuildAnthropicActionTools(bool addCacheControl = true)
    {
        var tools = ActionTools.All;
        var result = new List<object>(tools.Count);

        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            var properties = new Dictionary<string, object>();
            foreach (var p in t.Params)
                properties[p] = new { type = "string" };

            bool isLast = (i == tools.Count - 1);

            object toolObj = (isLast && addCacheControl)
                ? new
                {
                    name         = t.Name,
                    description  = t.Description,
                    input_schema = new { type = "object", properties, required = Array.Empty<string>() },
                    cache_control = new { type = "ephemeral" }
                }
                : (object)new
                {
                    name         = t.Name,
                    description  = t.Description,
                    input_schema = new { type = "object", properties, required = Array.Empty<string>() }
                };

            result.Add(toolObj);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Suy ra JSON Schema type tu ten param. Quy uoc don gian, khong can attribute.
    /// </summary>
    private static object InferSchema(string paramName)
    {
        // 1. Literal match TRUOC: cac param ten chua "date" nhung la so nguyen (override)
        if (paramName is "tabFilter" or "dateFilter")
            return new { type = "integer" };

        // 2. Substring "Date" sau: cac param ngay thuc su (dang yyyy-MM-dd)
        if (paramName.Contains("Date", StringComparison.OrdinalIgnoreCase))
            return new { type = "string", format = "date", description = "Ngày dạng yyyy-MM-dd" };

        // Phan trang: so nguyen
        if (paramName is "pageIndex" or "pageSize")
            return new { type = "integer" };

        // Nam + thang: so nguyen
        if (paramName is "year" or "month")
            return new { type = "integer" };

        // Enum groupBy
        if (paramName == "groupBy")
            return new { type = "string", @enum = new[] { "day", "month" }, description = "Nhóm theo ngày hoặc tháng" };

        // Id: so nguyen
        if (paramName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
            return new { type = "integer" };

        // Tat ca con lai: string
        return new { type = "string" };
    }
}
