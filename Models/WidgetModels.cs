namespace TourkitAiProxy.Models;

// Widget Chat — token gen per-tenant, paste vào <script data-token="trav_..."> ở site khách.
// AllowedOrigins JSON array (null = wildcard). Custom hết: bot name / greeting / system prompt / màu.

public record WidgetToken(
    string Token,
    string TenantId,
    string BotName,
    string Greeting,
    string SystemPrompt,
    string Color,
    bool Enabled,
    string? AllowedOrigins,
    int TotalMessages,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // CRM integration (Phase 2): nếu null/empty → bot chỉ FAQ; có session → bot gọi /api/ai/* qua JWT
    string? TourKitSessionId = null,
    string? AllowedTools = null,        // JSON array, vd ["tours","markets","booking_tickets"]
    int CacheTtlSeconds = 300
);

// Admin: tạo token mới. Các field optional có default (server tự fill).
public record CreateWidgetTokenReq(
    string? BotName,
    string? Greeting,
    string? SystemPrompt,
    string? Color,
    List<string>? AllowedOrigins,
    // CRM kết nối (optional): admin paste Crypton token TourKit → backend decrypt + tạo TkSession
    string? TourKitToken = null,
    List<string>? AllowedTools = null,
    int? CacheTtlSeconds = null
);

// Admin: edit token.
public record UpdateWidgetTokenReq(
    string? BotName,
    string? Greeting,
    string? SystemPrompt,
    string? Color,
    bool? Enabled,
    List<string>? AllowedOrigins,
    string? TourKitToken = null,         // paste lại để rotate / re-bind
    List<string>? AllowedTools = null,
    int? CacheTtlSeconds = null,
    bool? UnlinkCrm = null              // true → xóa TourKitSessionId, bot quay về FAQ
);

// Admin test kết nối CRM: trả về N tour mẫu để confirm bot sẽ thấy đúng tenant.
public record TestCrmResp(bool Ok, string? Message, int? SampleCount, List<string>? SampleTitles);

// DEV helper: encrypt/decrypt arbitrary plaintext (test vector cho partner integration).
public record CryptonDevReq(string? Plain);

// One-shot init: nhận Crypton token (cùng định dạng /login-token) → backend decrypt + login +
// tạo widget + link CRM → trả token widget. Tiện cho integration server-to-server, không gửi password plain.
//
// Crypton token = Crypton.Encrypt(JSON {username, password, domain}) — đã có tool encode ở TourKit Shared.
public record InitWidgetReq(
    // BẮT BUỘC — Crypton token (chuỗi mã hoá AES-256), backend decrypt ra creds.
    string Token,
    // Optional — config widget. Mọi field có default ở backend.
    string? BotName = null,
    string? Greeting = null,
    string? SystemPrompt = null,
    string? Color = null,
    List<string>? AllowedOrigins = null,
    List<string>? AllowedTools = null,
    int? CacheTtlSeconds = null,
    bool LinkCrm = true           // mặc định link CRM luôn (vì đã có creds)
);

// Init response: gọn — token + snippet + meta. KHÔNG leak password / sessionId / JWT.
public record InitWidgetResp(
    string Token,
    string EmbedSnippet,
    string BotName,
    string Color,
    string TenantId,
    bool CrmLinked,
    List<string> AllowedTools
);

// Public (widget.js fetch lúc mount): chỉ trả field cần render UI — KHÔNG leak SystemPrompt.
public record WidgetConfigResp(
    string BotName,
    string Greeting,
    string Color,
    bool Enabled
);

// Public (widget.js gửi chat): không gửi systemPrompt từ client, backend tự load theo token.
public record WidgetChatReq(
    string Token,
    string Message,
    List<WidgetChatMessage>? History,
    string? Visitor,        // optional client-supplied visitor id để gom transcript (chưa dùng)
    // Multimodal đính kèm (data-URL "data:image/jpeg;base64,..."). Chỉ Anthropic/OpenAI xử lý.
    List<string>? Images = null,
    List<string>? Documents = null    // PDF data-URL "data:application/pdf;base64,..."
);

// Wrapper SSE/buffered reply — usedCrm để widget render chip "Dữ liệu thật từ CRM".
public record WidgetChatReply(string Reply, bool UsedCrm, string? ToolName);

public record WidgetChatMessage(string Role, string Content);

// Admin list response wrapper với embed snippet sẵn.
public record WidgetTokenWithEmbed(
    string Token,
    string TenantId,
    string BotName,
    string Greeting,
    string SystemPrompt,
    string Color,
    bool Enabled,
    string? AllowedOrigins,
    int TotalMessages,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string EmbedSnippet,
    bool CrmLinked,                  // có TourKitSessionId hợp lệ
    List<string>? AllowedTools,      // deserialized cho FE
    int CacheTtlSeconds
);
