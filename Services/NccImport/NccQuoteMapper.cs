using System.Text.Json;

namespace TourkitAiProxy.Services.NccImport;

/// Body cho POST /api/v1/ncc-import/save: quote (đã bóc tách, có thể user sửa) + loại dịch vụ + mã (tuỳ chọn).
public record NccSaveReq(JsonElement Quote, int ServiceId, string? ProviderCode);

/// Payload gửi sang TourKit.Api POST /api/ai/providers — KHỚP ProviderCreateRequest bên toutkit-app.
/// (Proxy không share assembly DTO nên khai báo lại; ASP.NET bind case-insensitive.)
public class ProviderCreatePayload
{
    public string? ProviderCode { get; set; }
    public string ProviderName { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string? Address { get; set; }
    public string? Email { get; set; }
    public string? TaxCode { get; set; }
    public string? City { get; set; }
    public string? Note { get; set; }
    public string? DataServices { get; set; }
    public int ServiceId { get; set; }
    public List<ProviderPricePayload> Prices { get; set; } = new();
}

public class ProviderPricePayload
{
    public string PriceName { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal? ContractPriceKt { get; set; }
    public decimal? ContractPrice { get; set; }
    public decimal? PublicPrice { get; set; }
    public string? Description { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Map báo giá AI bóc tách (<c>quote {supplier, tables[], conditions[]}</c>) → <see cref="ProviderCreatePayload"/>.
///
/// Bảng giá → dòng giá (LOSSLESS): mỗi Ô GIÁ (cell số) = 1 <see cref="ProviderPricePayload"/>:
///   priceName = "{nhãn dòng} — {nhãn cột}" (chỉ thêm nhãn cột khi bảng có >1 cột giá),
///   publicPrice = số, quantity = 1, description = nhãn cột, note = tên bảng.
/// Cột number = cột giá; cột text = cột nhãn (ghép làm tên dòng). supplier.* → field NCC;
/// website/validYear + conditions[] → Note của NCC; contactName/contactPhone → dataServices (JSON).
/// </summary>
public static class NccQuoteMapper
{
    public static ProviderCreatePayload ToCreateProvider(JsonElement quote, int serviceId, string? providerCode)
    {
        var p = new ProviderCreatePayload
        {
            ServiceId = serviceId,
            ProviderCode = string.IsNullOrWhiteSpace(providerCode) ? null : providerCode!.Trim()
        };

        if (quote.ValueKind == JsonValueKind.Object &&
            quote.TryGetProperty("supplier", out var sup) && sup.ValueKind == JsonValueKind.Object)
        {
            p.ProviderName = (Str(sup, "name") ?? "").Trim();
            p.Address = Str(sup, "address");
            p.City = Str(sup, "city");
            p.Email = Str(sup, "email");
            p.PhoneNumber = FirstPhone(sup);
            p.TaxCode = Str(sup, "taxCode");          // schema báo giá có thể không có — null là OK
            p.DataServices = BuildContactJson(sup);
            p.Note = BuildNote(sup, quote);
        }

        if (quote.ValueKind == JsonValueKind.Object &&
            quote.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array)
            foreach (var tbl in tables.EnumerateArray())
                MapTable(tbl, p.Prices);

        return p;
    }

    private static void MapTable(JsonElement tbl, List<ProviderPricePayload> outRows)
    {
        if (tbl.ValueKind != JsonValueKind.Object) return;
        var title = Str(tbl, "title") ?? "";
        var cols = StrList(tbl, "columns");
        if (!tbl.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return;

        // Ma trận cell (giữ JsonElement để phân biệt number/string).
        var matrix = new List<List<JsonElement>>();
        foreach (var r in rows.EnumerateArray())
            if (r.ValueKind == JsonValueKind.Array)
                matrix.Add(r.EnumerateArray().ToList());
        if (matrix.Count == 0) return;

        int colCount = Math.Max(cols.Count, matrix.Max(r => r.Count));

        // Phân loại cột: giá (đa số cell là số) vs nhãn (text).
        var isPrice = new bool[colCount];
        for (int c = 0; c < colCount; c++)
        {
            int num = 0, txt = 0;
            foreach (var row in matrix)
            {
                if (c >= row.Count) continue;
                if (TryNum(row[c], out _)) num++;
                else if (HasText(row[c])) txt++;
            }
            isPrice[c] = num > 0 && num >= txt;
        }
        int priceColCount = isPrice.Count(x => x);

        for (int ri = 0; ri < matrix.Count; ri++)
        {
            var row = matrix[ri];

            // Tên dòng = ghép các cột nhãn (non-price) có text.
            var labelParts = new List<string>();
            for (int c = 0; c < colCount; c++)
                if (!isPrice[c] && c < row.Count && HasText(row[c]))
                    labelParts.Add(CellText(row[c]));
            var rowLabel = labelParts.Count > 0
                ? string.Join(" / ", labelParts)
                : (!string.IsNullOrEmpty(title) ? $"{title} #{ri + 1}" : $"Dòng {ri + 1}");

            // Mỗi cột giá có số → 1 dòng giá.
            for (int c = 0; c < colCount; c++)
            {
                if (!isPrice[c] || c >= row.Count) continue;
                if (!TryNum(row[c], out var val)) continue;
                var colLabel = c < cols.Count ? (cols[c] ?? "") : $"Cột {c + 1}";
                var name = (priceColCount > 1 && colLabel.Length > 0) ? $"{rowLabel} — {colLabel}" : rowLabel;
                if (name.Length > 250) name = name[..250];
                outRows.Add(new ProviderPricePayload
                {
                    PriceName = name,
                    Quantity = 1,
                    PublicPrice = val,
                    Description = string.IsNullOrWhiteSpace(colLabel) ? null : colLabel,
                    Note = string.IsNullOrWhiteSpace(title) ? null : title
                });
            }
        }
    }

    // ─── cell helpers ─────────────────────────────────────────────────────────────
    private static bool TryNum(JsonElement e, out decimal val)
    {
        val = 0;
        if (e.ValueKind == JsonValueKind.Number) { val = e.GetDecimal(); return true; }
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = (e.GetString() ?? "").Trim();
            if (s.Length == 0) return false;
            var cleaned = s.Replace("đ", "").Replace("₫", "")
                .Replace("VND", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "").Trim();
            if (cleaned.Length == 0) return false;
            // Có chữ cái → không phải giá (tránh "Phòng 2" bị coi là số).
            if (!cleaned.All(ch => char.IsDigit(ch) || ch == '.' || ch == ',' || ch == '-')) return false;
            var digits = cleaned.Replace(".", "").Replace(",", "");
            if (digits.Length > 0 && decimal.TryParse(digits, out val)) return true;
        }
        return false;
    }

    private static bool HasText(JsonElement e)
        => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString());

    private static string CellText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => (e.GetString() ?? "").Trim(),
        JsonValueKind.Number => e.ToString(),
        _ => ""
    };

    // ─── supplier helpers ───────────────────────────────────────────────────────
    private static string FirstPhone(JsonElement sup)
    {
        if (sup.TryGetProperty("phones", out var ph) && ph.ValueKind == JsonValueKind.Array)
            foreach (var x in ph.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                    return x.GetString()!.Trim();
        return Str(sup, "contactPhone") ?? "";
    }

    private static string? BuildContactJson(JsonElement sup)
    {
        var name = Str(sup, "contactName");
        var phone = Str(sup, "contactPhone");
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(phone)) return null;
        return JsonSerializer.Serialize(new { contactName = name, contactPhone = phone });
    }

    private static string? BuildNote(JsonElement sup, JsonElement quote)
    {
        var parts = new List<string>();
        var web = Str(sup, "website"); if (!string.IsNullOrWhiteSpace(web)) parts.Add("Website: " + web);
        var year = Str(sup, "validYear"); if (!string.IsNullOrWhiteSpace(year)) parts.Add("Áp dụng: " + year);
        if (quote.ValueKind == JsonValueKind.Object &&
            quote.TryGetProperty("conditions", out var cond) && cond.ValueKind == JsonValueKind.Array)
        {
            var lines = cond.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                .Select(c => "- " + c.GetString()!.Trim()).ToList();
            if (lines.Count > 0) { parts.Add("Điều kiện:"); parts.AddRange(lines); }
        }
        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    private static string? Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p)
            ? (p.ValueKind == JsonValueKind.String ? p.GetString()
               : p.ValueKind == JsonValueKind.Number ? p.ToString()
               : null)
            : null;

    private static List<string> StrList(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array)
            foreach (var x in p.EnumerateArray())
                list.Add(x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : x.ToString());
        return list;
    }
}
