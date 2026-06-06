using System.Text.Json;
using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

// ─── /api/v1/login-token ──────────────────────────────────────────────────────

/// Body: { token } — token = Crypton.Encrypt(JSON {username,password,domain}).
public record LoginTokenRequest(
    [property: JsonPropertyName("token")] string Token
);

/// Body: { username, password, domain } — đăng nhập trực tiếp (form), không qua token mã hóa.
/// Credentials đi qua body HTTPS tới proxy (server-side) rồi login TourKit — KHÔNG mã hóa ở client.
public record LoginCredRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("domain")]   string Domain
);

/// Trả sessionId (client giữ, gửi kèm mỗi /chat). JWT KHÔNG trả ra client — giữ server-side.
public record LoginTokenResponse(
    string SessionId,
    string TenantId,
    string? FullName,
    string? CompanyName,
    long ExpiresAt
);

// ─── /api/v1/chat ─────────────────────────────────────────────────────────────

public record ChatTurn(
    [property: JsonPropertyName("role")]    string Role,     // "user" | "assistant"
    [property: JsonPropertyName("content")] string Content
);

/// SessionId có thể nằm trong body HOẶC header X-Session-Id (endpoint ưu tiên header).
public record ChatRequest(
    [property: JsonPropertyName("messages")]  List<ChatTurn>? Messages,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("provider")]  string? Provider,
    [property: JsonPropertyName("model")]     string? Model,
    [property: JsonPropertyName("apiKey")]    string? ApiKey = null
);

/// 1 thẻ số liệu bên panel phải. Value để raw (double) — frontend tự format (fmtVND/fmtNum).
public record ChatStat(string Label, double Value, string? Unit, string? Group = null);  // Group: revenue|expense|profit|null

/// Dữ liệu hiển thị panel phải: kind (loại tool), raw JSON từ TourKit, thẻ số liệu, gợi ý hỏi tiếp.
public record ChatData(
    string Kind,
    string? Title,
    JsonElement? Raw,
    List<ChatStat> Stats,
    List<string>? Focus,        // chỉ số người dùng muốn (vd ["expense"]) → frontend chỉ vẽ/hiện cột này
    List<string>? Suggestions = null   // tag gợi ý "xem gì tiếp theo" (chip bấm là hỏi luôn)
);

/// Kết quả 1 lượt chat. Reply = phân tích (panel trái). Data = số liệu (panel phải).
public record ChatResult(
    string Reply,
    string ToolName,
    object? ToolParams,
    ChatData? Data,
    long LatencyMs,
    int TokensIn,
    int TokensOut,
    string? Warning
);
