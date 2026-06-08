using System.Text.Json;

namespace TourkitAiProxy.Services.Json;

/// <summary>
/// Extension methods cho JsonElement — case-insensitive field lookup + tolerant type conversion.
/// Thay 12+ private helper instances duplicate trong Services/Visa, /Deals, /Tour, /Mail, /Reviews,
/// /TourKit, và Endpoints/TourEndpoints.cs.
/// </summary>
public static class JsonElementExtensions
{
    /// <summary>Case-insensitive property lookup. Trả false nếu element không phải object hoặc field missing.</summary>
    public static bool TryGetField(this JsonElement el, string name, out JsonElement value)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }

    /// <summary>Lấy string value của field. Trả null nếu missing / không phải string / blank.</summary>
    public static string? GetStringField(this JsonElement el, string name)
    {
        if (!el.TryGetField(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>Lấy list of non-blank strings. Trả empty list nếu missing / không phải array.</summary>
    public static List<string> GetStringListField(this JsonElement el, string name)
    {
        var list = new List<string>();
        if (!el.TryGetField(name, out var p) || p.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }
        return list;
    }
}
