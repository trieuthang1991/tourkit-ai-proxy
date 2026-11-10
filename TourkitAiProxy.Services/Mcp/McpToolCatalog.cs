using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.Chat;

namespace TourkitAiProxy.Services.Mcp;

/// <summary>
/// Sinh tool MCP từ <see cref="ChatTools.All"/> — mỗi <see cref="ChatTool"/> một tool đọc, cùng tên/tiêu
/// đề/mô tả mà trợ lý nội bộ đang dùng. <b>Một nguồn duy nhất</b>: thêm tool vào <c>ChatTools</c> là MCP
/// có luôn, không chép catalog lần hai (chép là lệch, và lệch thì AI bên ngoài gọi tool mà trợ lý đã bỏ).
///
/// <para><b>Chỉ đọc.</b> Tool hành động (giao việc, gửi mail…) là đợt sau sau cờ <c>Features:McpActions</c>
/// — không thêm ở đây. Annotation <c>readOnlyHint: true</c> để máy khách khỏi hỏi xác nhận mỗi lần gọi;
/// <c>openWorldHint: false</c> vì chỉ chạm CRM của chính công ty, không ra Internet.</para>
///
/// <para><b>Người gọi lấy từ <c>HttpContext.Items["mcp.caller"]</c></b> (middleware đặt sau khi kiểm khoá),
/// không bao giờ từ tham số tool — tham số do mô hình viết, tin nó là để mô hình chọn phiên của người khác.</para>
///
/// <para><b>Mọi lỗi thành kết quả <c>isError</c> tiếng Việt</b>, không thành lỗi JSON-RPC: theo đặc tả MCP,
/// lỗi JSON-RPC mô hình không thấy được nên không tự sửa được; kết quả lỗi thì mô hình đọc, sửa tham số,
/// gọi lại. Câu lỗi không bao giờ chứa phiên/JWT/stack trace.</para>
/// </summary>
public static class McpToolCatalog
{
    public static IReadOnlyList<McpServerTool> Create()
        => ChatTools.All.Select(t => (McpServerTool)new ChatReadTool(t)).ToList();

    /// <summary>
    /// Thân xử lý một lời gọi, tách khỏi SDK để test: ép tham số → chạy CRM qua <paramref name="run"/> →
    /// định dạng. <paramref name="run"/> ở bản chạy thật là <see cref="CrmToolRunner.RunAsync"/>.
    /// </summary>
    internal static async Task<CallToolResult> ExecuteAsync(ChatTool tool, McpCaller? caller, JsonElement? args,
        Func<McpCaller, JsonElement?, CancellationToken, Task<CrmToolOutcome>> run, ILogger log, CancellationToken ct)
    {
        // Filter của Task 3 đã chặn trước; đây là lớp thứ hai — đóng cửa là mặc định.
        if (caller == null) return Error(McpAuthenticator.MissingKey);

        var (prms, argError) = McpToolSchemas.Coerce(tool, args);
        if (argError != null) return Error(argError);

        CrmToolOutcome outcome;
        try
        {
            outcome = await run(caller, prms, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // máy khách huỷ — để SDK xử lý như huỷ, không phải lỗi tool
        }
        catch (Exception ex)
        {
            // Thử lại sau 401 mà vẫn hỏng thì runner bọc lỗi thật trong CrmRetryFailedException — bóc ra.
            var real = ex is CrmRetryFailedException { InnerException: { } inner } ? inner : ex;
            if (real is TourKitApiException api)
            {
                log.LogWarning("[mcp] tool={Tool} token={TokenId} CRM lỗi {Status}: {Msg}",
                    tool.Name, caller.TokenId, api.Status, api.Message);
                return Error(CrmErrorText(tool, api));
            }
            log.LogError(real, "[mcp] tool={Tool} token={TokenId} lỗi bất ngờ", tool.Name, caller.TokenId);
            return Error($"Không lấy được \"{tool.Title}\" do lỗi hệ thống — thử lại sau ít phút.");
        }

        var text = McpResultFormatter.ToText(tool, outcome.Data);
        if (MarketFilterDropped(prms, outcome.Path) is { } market)
            // Runner không khớp tên thì lặng lẽ bỏ lọc (hành vi chung với trợ lý). Ở MCP phải nói ra: không
            // thì mô hình trình bày số của MỌI thị trường như số của thị trường người dùng hỏi.
            text = $"⚠️ Không tìm thấy thị trường \"{market}\" — số liệu dưới đây KHÔNG lọc theo thị trường. " +
                   "Gọi list_markets để xem tên đúng.\n\n" + text;

        var result = new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        if (outcome.Data.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            result.StructuredContent = McpResultFormatter.ToStructured(outcome.Data);
        return result;
    }

    private static string CrmErrorText(ChatTool tool, TourKitApiException api) => api.Status switch
    {
        // 401 sau khi đã đăng nhập lại: phiên gốc của khoá không còn dùng được.
        401 => "Phiên TourKit gắn với khoá MCP này đã hết hạn — đăng nhập lại TourKit rồi tạo khoá mới ở trang Kết nối AI.",
        403 => $"Tài khoản của bạn không có quyền xem \"{tool.Title}\" trong CRM. {api.Message}".TrimEnd(),
        // Message của TourKitApiException là câu upstream trả về cho người dùng (đã tiếng Việt) — không chứa JWT.
        _ => $"CRM trả lỗi khi lấy \"{tool.Title}\": {api.Message}",
    };

    /// <summary>Tên thị trường người gọi gửi mà đường dẫn cuối không có <c>marketId</c> → trả tên đó.</summary>
    internal static string? MarketFilterDropped(JsonElement? prms, string path)
    {
        if (prms is not { ValueKind: JsonValueKind.Object } p
            || !p.TryGetProperty("marketName", out var mn) || mn.ValueKind != JsonValueKind.String) return null;
        if (p.TryGetProperty("marketId", out _)) return null;
        return path.Contains("marketId=", StringComparison.OrdinalIgnoreCase) ? null : mn.GetString();
    }

    private static CallToolResult Error(string text) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = text }],
    };

    /// <summary>Một <see cref="ChatTool"/> dưới dạng tool MCP. Dịch vụ lấy theo lời gọi (máy chủ không trạng thái).</summary>
    private sealed class ChatReadTool : McpServerTool
    {
        private readonly ChatTool _tool;
        private readonly Tool _protocol;

        public ChatReadTool(ChatTool tool)
        {
            _tool = tool;
            _protocol = new Tool
            {
                Name = tool.Name,
                Title = tool.Title,
                Description = tool.Description,
                InputSchema = McpToolSchemas.InputSchemaFor(tool),
                Annotations = new ToolAnnotations
                {
                    Title = tool.Title,
                    ReadOnlyHint = true,
                    OpenWorldHint = false,
                    DestructiveHint = false,
                    IdempotentHint = true,
                },
            };
        }

        public override Tool ProtocolTool => _protocol;

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var sp = request.Services!;
            var caller = sp.GetService<IHttpContextAccessor>()?.HttpContext?.Items[McpAuthenticator.ItemKey] as McpCaller;
            var runner = sp.GetRequiredService<CrmToolRunner>();
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpToolCatalog));
            JsonElement? args = request.Params?.Arguments is { Count: > 0 } a ? JsonSerializer.SerializeToElement(a) : null;
            return new ValueTask<CallToolResult>(ExecuteAsync(_tool, caller, args,
                (c, p, ct) => runner.RunAsync(c.SessionId, c.TenantId, _tool, p, ct), log, cancellationToken));
        }
    }
}
