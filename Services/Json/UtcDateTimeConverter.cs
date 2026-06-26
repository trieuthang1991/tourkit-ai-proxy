using System.Text.Json;
using System.Text.Json.Serialization;

namespace TourkitAiProxy.Services.Json;

/// <summary>
/// Serialize MỌI DateTime kèm hậu tố 'Z' (UTC ISO-8601) cho JSON HTTP response.
///
/// LÝ DO: DateTime đọc từ SQL (Dapper, DATETIME2) có Kind=Unspecified → System.Text.Json mặc định
/// serialize KHÔNG có 'Z' → trình duyệt `new Date(str)` hiểu nhầm là giờ LOCAL → lệch +7h (VN UTC+7).
/// App lưu UTC toàn bộ (UtcNow / SYSUTCDATETIME), nên coi Unspecified = UTC là ĐÚNG.
///
/// PHẠM VI AN TOÀN: chỉ tác động field kiểu DateTime (workflows / quota-orders / widget-tokens — đều
/// UTC-stored). Các entity lưu giờ-local rồi đọc dạng STRING (Mail.ReceivedAt, Visa.CreatedAt qua
/// ToString) KHÔNG bị converter này đụng → không vỡ cặp tự-triệt-tiêu hiện có.
///
/// Áp dụng cho cả DateTime? (framework tự bọc nullable). Read: parse ISO bình thường.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc   => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc)   // Unspecified (từ SQL) = UTC
        };
        writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
    }
}
