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
/// Debug=true → response/SSE đính thêm field "trace" liệt kê các bước workflow đã chạy
/// (planner / dispatch / compare / analysis / guardrails…) cho team xem AI vận hành đúng/sai.
public record ChatRequest(
    [property: JsonPropertyName("messages")]  List<ChatTurn>? Messages,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("provider")]  string? Provider,
    [property: JsonPropertyName("model")]     string? Model,
    [property: JsonPropertyName("apiKey")]    string? ApiKey = null,
    [property: JsonPropertyName("debug")]     bool    Debug  = false
);

/// 1 thẻ số liệu bên panel phải. Value để raw (double) — frontend tự format (fmtVND/fmtNum).
public record ChatStat(string Label, double Value, string? Unit, string? Group = null);  // Group: revenue|expense|profit|null

/// Bộ số liệu KỲ ĐỐI CHIẾU (vd "Tháng 5/2026" so với primary "Tháng 6/2026").
/// Backend dựng khi AI gọi cùng 1 tool 2 lần với param khác nhau ("so với tháng trước / cùng kỳ năm ngoái").
/// Stats match với primary qua Label — frontend tính delta để hiện ▲/▼ % bên cạnh stat chính.
public record ChatDataCompare(
    string PrimaryLabel,        // vd "Tháng 6/2026", "Năm 2026"  — để dán nhãn bộ chính
    string CompareLabel,        // vd "Tháng 5/2026", "Năm 2025"  — để dán nhãn bộ đối chiếu
    List<ChatStat> CompareStats,// stats của kỳ đối chiếu (match Label với primary)
    JsonElement? CompareRaw     // rows kỳ đối chiếu để chart vẽ 2 series (timeline) nếu có
);

/// Dữ liệu hiển thị panel phải: kind (loại tool), raw JSON từ TourKit, thẻ số liệu, gợi ý hỏi tiếp.
public record ChatData(
    string Kind,
    string? Title,
    JsonElement? Raw,
    List<ChatStat> Stats,
    List<string>? Focus,        // chỉ số người dùng muốn (vd ["expense"]) → frontend chỉ vẽ/hiện cột này
    List<string>? Suggestions = null,  // tag gợi ý "xem gì tiếp theo" (chip bấm là hỏi luôn)
    ChatDataCompare? Compare = null,   // kỳ đối chiếu nếu câu hỏi yêu cầu so sánh (vd "so với tháng trước")
    // Map field→nhãn tiếng Việt CÓ DẤU lấy thẳng từ envelope /api/ai/* (columns). Frontend dùng để render
    // header bảng đúng thứ tự + đúng nhãn, KHÔNG tự suy ra từ tên field (tránh "SoDataKH"→"So Data K H").
    Dictionary<string, string>? Columns = null
);

/// Kết quả 1 lượt chat. Reply = phân tích (panel trái). Data = số liệu (panel phải).
/// Trace = bước workflow đã chạy (chỉ trả khi request.debug=true).
public record ChatResult(
    string Reply,
    string ToolName,
    object? ToolParams,
    ChatData? Data,
    long LatencyMs,
    int TokensIn,
    int TokensOut,
    string? Warning,
    TourkitAiProxy.Services.Workflow.WorkflowTrace? Trace = null
);
