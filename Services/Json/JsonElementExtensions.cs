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
}
