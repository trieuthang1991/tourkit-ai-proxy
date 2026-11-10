using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Domain.Mcp;

/// <summary>
/// Đổi envelope <c>/api/ai/*</c> (<c>{section,title,count,total,period,summary,items[]}</c>) thành kết quả
/// tool MCP: văn bản cho mô hình ĐỌC + bản cấu trúc cho máy khách nào biết dùng.
///
/// <para><b>Vì sao cắt 20 dòng / 30.000 ký tự.</b> Văn bản kết quả đi thẳng vào ngữ cảnh của mô hình
/// bên máy khách — mỗi ký tự là token người dùng trả tiền, và bảng 200 dòng làm mô hình đọc sót hơn là
/// đọc kỹ hơn. Cắt thì PHẢI nói còn bao nhiêu: không nói, mô hình tưởng 20 dòng là tất cả rồi kết luận
/// "công ty có 20 khách chưa chăm sóc" trong khi thật là 340.</para>
///
/// <para>Thuần (không ASP.NET/SDK) để test được; lớp nối SDK ở <c>Services/Mcp/McpToolCatalog</c>.</para>
/// </summary>
public static class McpResultFormatter
{
    public const int MaxRows = 20;
    public const int MaxChars = 30_000;

    // Một ô dài (ghi chú, mô tả tour) không được nuốt cả ngân sách ký tự của bảng.
    private const int MaxCell = 200;
    // Chừa chỗ cho dòng "còn N dòng…" cuối bảng.
    private const int FooterReserve = 400;

    public static string ToText(ChatTool tool, JsonElement envelope)
    {
        try { return Build(tool, envelope); }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
        {
            // Envelope lạ (CRM đổi dạng) không được làm hỏng cả lời gọi tool — báo chữ, không ném.
            return $"Dữ liệu CRM trả về cho \"{tool.Title}\" không đọc được (dạng dữ liệu lạ).";
        }
    }

    /// <summary>
    /// Bản <c>structuredContent</c>: envelope gốc, chỉ cắt <c>items</c> còn ≤ 20 (các trường khác giữ nguyên
    /// — total vẫn là tổng thật). MCP đòi structuredContent là OBJECT nên mảng trơn (vd <c>list_markets</c>)
    /// được bọc thành <c>{items,total}</c>; dữ liệu hỏng → <c>{}</c>.
    /// </summary>
    public static JsonElement ToStructured(JsonElement envelope)
    {
        JsonObject obj;
        if (envelope.ValueKind == JsonValueKind.Array)
        {
            obj = new JsonObject
            {
                ["items"] = TakeArray(envelope),
                ["total"] = envelope.GetArrayLength(),
            };
        }
        else if (envelope.ValueKind == JsonValueKind.Object)
        {
            obj = JsonNode.Parse(envelope.GetRawText())!.AsObject();
            if (envelope.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                obj["items"] = TakeArray(items);
        }
        else obj = new JsonObject();
        return JsonSerializer.SerializeToElement(obj);
    }

    private static JsonArray TakeArray(JsonElement arr)
    {
        var a = new JsonArray();
        foreach (var it in arr.EnumerateArray().Take(MaxRows)) a.Add(JsonNode.Parse(it.GetRawText()));
        return a;
    }

    // ─── Văn bản ──────────────────────────────────────────────────────────────

    private static string Build(ChatTool tool, JsonElement env)
    {
        if (env.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return $"Dữ liệu CRM trả về cho \"{tool.Title}\" không đọc được (không phải bảng dữ liệu).";

        var title = tool.Title;
        int? total = null;
        JsonElement? items = null, summary = null, period = null;
        Dictionary<string, string>? labels = null;

        if (env.ValueKind == JsonValueKind.Array) items = env;
        else
        {
            if (env.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                title = t.GetString()!;
            if (env.TryGetProperty("total", out var to) && to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var tv))
                total = tv;
            if (env.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array) items = it;
            if (env.TryGetProperty("summary", out var sm) && sm.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                summary = sm;
            if (env.TryGetProperty("period", out var pe) && pe.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                period = pe;
            if (env.TryGetProperty("columns", out var co) && co.ValueKind == JsonValueKind.Object)
            {
                labels = new(StringComparer.OrdinalIgnoreCase);
                foreach (var p in co.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) labels[p.Name] = p.Value.GetString()!;
            }
        }

        var sb = new StringBuilder();
        sb.Append("## ").Append(title).Append('\n');
        if (period is { } per) sb.Append("Kỳ: ").Append(PeriodText(per)).Append('\n');

        var rowCount = items?.GetArrayLength() ?? 0;
        var hasSummary = summary is { } s && !(s.ValueKind == JsonValueKind.Object && !s.EnumerateObject().Any());
        if (hasSummary) AppendSummary(sb, summary!.Value, labels);

        if (items == null && env.ValueKind == JsonValueKind.Object && !hasSummary)
        {
            // Envelope không có items (vd chi tiết một bản ghi): in thẳng các trường của nó.
            AppendFields(sb, env);
            return Cap(sb.ToString());
        }

        if (rowCount == 0)
        {
            sb.Append(hasSummary
                ? "\nKhông có dữ liệu chi tiết từng dòng cho bộ lọc này.\n"
                : "\nKhông có dữ liệu cho bộ lọc này.\n");
            return Cap(sb.ToString());
        }

        var all = total.HasValue && total.Value > rowCount ? total.Value : rowCount;
        sb.Append("\nTổng: ").Append(all.ToString(CultureInfo.InvariantCulture)).Append(" dòng.\n\n");
        var shown = AppendTable(sb, items!.Value, labels);

        var remaining = all - shown;
        if (remaining > 0)
        {
            sb.Append("\n_… còn ").Append(remaining.ToString(CultureInfo.InvariantCulture))
              .Append(" dòng nữa chưa hiện");
            // Mỗi lần chỉ HIỆN 20 dòng: trang 50 dòng thì dòng 21–50 không bao giờ thấy được, nên
            // gợi ý phân trang theo 20 chứ không chỉ "gửi pageIndex". Luôn nói, kể cả khi upstream đã trả
            // trọn bộ trong một trang (all == rowCount): phần bị cắt vẫn chỉ xem được bằng cách này.
            sb.Append(" — thu hẹp bộ lọc, hoặc xem tiếp bằng pageSize=20 và pageIndex=2, 3…");
            sb.Append("._\n");
        }
        return Cap(sb.ToString());
    }

    public static string Cap(string s)
        => s.Length <= MaxChars ? s : s[..(MaxChars - 30)] + "\n_… (đã cắt bớt cho gọn)._";

    private static string PeriodText(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return Scalar(p);
        // CRM trả {startDate,endDate,label}: in "label (2026-11-01 → 2026-11-30)" — ngày dạng ISO để mô hình
        // dùng lại làm tham số được, nhãn để người đọc thấy ngay ("Tất cả" = không giới hạn ngày).
        string? Str(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
        string Day(string s) => s.Length >= 10 && s[4] == '-' ? s[..10] : s;
        var (label, from, to) = (Str("label"), Str("startDate"), Str("endDate"));
        if (label != null || from != null || to != null)
        {
            var range = from != null || to != null ? $"{(from != null ? Day(from) : "…")} → {(to != null ? Day(to) : "…")}" : null;
            return label != null && range != null ? $"{label} ({range})" : label ?? range!;
        }
        var parts = p.EnumerateObject()
            .Where(x => x.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .Select(x => x.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? $"{x.Name}: {Compact(x.Value)}" : $"{x.Name}: {Scalar(x.Value)}")
            .ToList();
        return string.Join(", ", parts);
    }

    private static void AppendSummary(StringBuilder sb, JsonElement s, Dictionary<string, string>? labels)
    {
        sb.Append("\n**Tóm tắt**\n");
        if (s.ValueKind == JsonValueKind.Object)
            foreach (var p in s.EnumerateObject())
            {
                // Nhãn: cột của envelope → nhãn chung của trợ lý → tên khoá. Kèm tên khoá trong ngoặc khi có
                // nhãn, để mô hình đối chiếu được với structuredContent.
                var label = labels != null && labels.TryGetValue(p.Name, out var l) ? l : ChatDataBuilder.Friendly(p.Name);
                sb.Append("- ").Append(label);
                if (!string.Equals(label, p.Name, StringComparison.Ordinal)) sb.Append(" (").Append(p.Name).Append(')');
                sb.Append(": ")
                  .Append(p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? Compact(p.Value) : Scalar(p.Value))
                  .Append('\n');
            }
        else sb.Append("- ").Append(s.ValueKind == JsonValueKind.Array ? Compact(s) : Scalar(s)).Append('\n');
    }

    private static void AppendFields(StringBuilder sb, JsonElement obj)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name is "section" or "title") continue;
            sb.Append("- ").Append(p.Name).Append(": ")
              .Append(p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? Compact(p.Value) : Cell(p.Value))
              .Append('\n');
        }
    }

    /// <summary>Bảng markdown ≤ <see cref="MaxRows"/> dòng và vừa ngân sách ký tự. Trả số dòng đã in.</summary>
    private static int AppendTable(StringBuilder sb, JsonElement items, Dictionary<string, string>? labels)
    {
        var rows = items.EnumerateArray().Take(MaxRows).ToList();
        int shown = 0;

        // Dòng không phải object (mảng số/chuỗi) → gạch đầu dòng.
        if (rows.Any(r => r.ValueKind != JsonValueKind.Object))
        {
            foreach (var r in rows)
            {
                var line = "- " + Cell(r) + "\n";
                if (sb.Length + line.Length + FooterReserve > MaxChars) break;
                sb.Append(line); shown++;
            }
            return shown;
        }

        // Cột = hợp các khoá theo thứ tự gặp đầu tiên (dòng sau có thể có trường dòng đầu không có).
        var cols = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
            foreach (var p in r.EnumerateObject())
                if (seen.Add(p.Name)) cols.Add(p.Name);

        sb.Append("| ").Append(string.Join(" | ", cols.Select(c => Esc(labels != null && labels.TryGetValue(c, out var l) ? l : c)))).Append(" |\n");
        sb.Append('|').Append(string.Concat(cols.Select(_ => " --- |"))).Append('\n');
        foreach (var r in rows)
        {
            var cells = cols.Select(c => r.TryGetProperty(c, out var v) ? Cell(v) : "");
            var line = "| " + string.Join(" | ", cells) + " |\n";
            if (sb.Length + line.Length + FooterReserve > MaxChars) break;
            sb.Append(line); shown++;
        }
        return shown;
    }

    private static string Cell(JsonElement v)
    {
        var s = v.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? Compact(v) : Scalar(v);
        s = Esc(s);
        return s.Length <= MaxCell ? s : s[..MaxCell] + "…";
    }

    private static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    private static string Compact(JsonElement v)
    {
        // GetRawText giữ nguyên khoảng trắng gốc — ghi lại cho gọn, giữ chữ Việt (không \uXXXX).
        var s = JsonSerializer.Serialize(v, CompactJson);
        return s.Length <= 120 ? s : s[..120] + "…";
    }

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Ký tự '|' và xuống dòng làm vỡ bảng markdown.
    private static string Esc(string s)
        => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|");
}
