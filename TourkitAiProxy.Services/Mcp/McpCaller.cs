namespace TourkitAiProxy.Services.Mcp;

/// <summary>
/// Người đang gọi <c>/mcp</c>, đã xác thực. Middleware đặt vào <c>HttpContext.Items["mcp.caller"]</c>
/// (<see cref="McpAuthenticator.ItemKey"/>); tool handler đọc từ đây, <b>không</b> đọc header lần hai —
/// đọc lại header là mở đường cho một nhánh quên kiểm thu hồi/phiên gốc.
///
/// <para><c>SessionId</c> là phiên TourKit của CHỦ KHOÁ: mọi lời gọi CRM đi bằng JWT của phiên này, tức
/// đúng quyền CRM của người đó. Nó là bí mật đăng nhập — không bao giờ trả ra trong kết quả tool.</para>
/// </summary>
public sealed record McpCaller(long TokenId, string SessionId, string TenantId, string Username);

/// <summary>Chủ của một phiên TourKit — đủ để đối chiếu với chủ khoá, không kéo cả <c>TkSession</c> vào.</summary>
public sealed record McpSessionOwner(string TenantId, string Username);
