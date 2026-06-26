using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// 1 email trong hộp thư SmartMail. Lưu data/mails.json keyed by Id (Message-Id header, fallback uid).
/// Category null khi chưa phân loại; Status mặc định "moi".
public record MailItem(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("from")]       MailContact From,
    [property: JsonPropertyName("subject")]    string Subject,
    [property: JsonPropertyName("body")]       string Body,
    [property: JsonPropertyName("receivedAt")] string ReceivedAt,   // ISO-8601 (DateTime "o")
    [property: JsonPropertyName("isRead")]     bool IsRead,
    [property: JsonPropertyName("category")]   string? Category,     // hoi_dat_tour|xin_bao_gia|khieu_nai|xac_nhan|spam|khac
    [property: JsonPropertyName("status")]     string Status,        // moi|dang_xu_ly|da_phan_hoi|da_dong
    [property: JsonPropertyName("aiSummary")]  string? AiSummary,
    [property: JsonPropertyName("draft")]      MailDraft? Draft,
    [property: JsonPropertyName("bodyHtml")]   string? BodyHtml = null,  // HTML gốc (để render iframe); Body = text sạch
    [property: JsonPropertyName("autoReplyError")] string? AutoReplyError = null   // != null → auto-reply soạn/gửi LỖI (hiện cảnh báo ở UI)
);

public record MailContact(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("email")] string Email
);

public record MailDraft(
    [property: JsonPropertyName("tone")]        string Tone,
    [property: JsonPropertyName("instruction")] string? Instruction,
    [property: JsonPropertyName("text")]        string Text,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt
);

// ─── Request DTOs ─────────────────────────────────────────────────────────────
public record DraftReplyRequest(
    [property: JsonPropertyName("tone")]        string Tone,
    [property: JsonPropertyName("instruction")] string? Instruction,
    [property: JsonPropertyName("provider")]    string? Provider,
    [property: JsonPropertyName("model")]       string? Model,
    [property: JsonPropertyName("apiKey")]      string? ApiKey
);

public record UpdateStatusRequest(
    [property: JsonPropertyName("status")] string Status
);

/// Body cho POST /api/v1/mail/account — nhập creds Gmail + chữ ký từ UI.
public record MailAccountRequest(
    [property: JsonPropertyName("address")]     string Address,
    [property: JsonPropertyName("appPassword")] string AppPassword,
    [property: JsonPropertyName("signature")]   string? Signature
);

/// Body cho POST /api/v1/mail/{id}/reply/send — gửi nháp (có thể đã sửa) cho khách qua SMTP.
public record SendReplyRequest(
    [property: JsonPropertyName("text")]        string Text,
    [property: JsonPropertyName("tone")]        string? Tone,
    [property: JsonPropertyName("instruction")] string? Instruction
);

/// Body cho POST /api/v1/mail/compose/draft — AI soạn email MỚI (không phải trả lời).
public record ComposeDraftRequest(
    [property: JsonPropertyName("to")]          string To,
    [property: JsonPropertyName("subject")]     string? Subject,
    [property: JsonPropertyName("brief")]       string Brief,        // ý chính cần viết
    [property: JsonPropertyName("tone")]        string? Tone,
    [property: JsonPropertyName("provider")]    string? Provider,
    [property: JsonPropertyName("model")]       string? Model,
    [property: JsonPropertyName("apiKey")]      string? ApiKey
);

/// Body cho POST /api/v1/mail/compose/send — gửi email mới qua SMTP.
public record ComposeSendRequest(
    [property: JsonPropertyName("to")]      string To,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("text")]    string Text
);
