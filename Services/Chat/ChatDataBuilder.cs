// Services/Chat/ChatDataBuilder.cs
using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Chuyen envelope /api/ai/* (items + summary + total + title) thanh ChatData.
/// Dung chung cho JsonPlannerAgent va NativeToolUseAgent.
/// </summary>
public static class ChatDataBuilder
{
    // ─── Build entry point ─────────────────────────────────────────────────────

    /// Doc envelope tool response → ChatData (stats + raw + title + suggestions).
    public static ChatData Build(ChatTool tool, JsonElement data)
    {
        JsonElement items = data;
        string   title   = tool.Title;
        int      total   = 0;
        JsonElement? summary = null;

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("items",   out var it)) items = it;
            if (data.TryGetProperty("title",   out var t) && t.ValueKind == JsonValueKind.String)
                title = t.GetString() ?? title;
            if (data.TryGetProperty("total",   out var to)
                && to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var tv))
                total = tv;
            if (data.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.Object)
                summary = sm;
        }

        var stats = BuildEnvelopeStats(tool, items, total, summary);
        var raw   = items.ValueKind == JsonValueKind.Undefined ? data : items;
        return new ChatData(tool.Kind, title, raw.Clone(), stats, null, SuggestFor(tool.Name));
    }

    // ─── Envelope stats ────────────────────────────────────────────────────────

    internal static List<ChatStat> BuildEnvelopeStats(
        ChatTool tool, JsonElement items, int total, JsonElement? summary)
    {
        var stats = new List<ChatStat>();

        // financial_summary: cac metric la items[] voi {key, label, value, formatted}
        if (tool.Name == "financial_summary" && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in items.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var key   = GetStr(m, "key")   ?? "";
                var label = GetStr(m, "label") ?? key;
                if (m.TryGetProperty("value", out var v) && TryNum(v, out var n))
                {
                    var f = GetStr(m, "formatted") ?? "";
                    stats.Add(new ChatStat(label, n, f.Contains('đ') ? "đ" : null, FinGroup(key, label)));
                }
            }
            return stats;
        }

        if (total > 0) stats.Add(new ChatStat("Tổng số", total, null));

        if (summary is { ValueKind: JsonValueKind.Object } sm)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in sm.EnumerateObject()) names.Add(p.Name);
            foreach (var p in sm.EnumerateObject())
            {
                var n = p.Name;
                if (n.EndsWith("Formatted", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith("Name",      StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith("Label",     StringComparison.OrdinalIgnoreCase)) continue;
                if (names.Contains(n + "Label") || names.Contains(n + "Name")) continue;
                if (!TryNum(p.Value, out var val)) continue;
                var unit = names.Contains(n + "Formatted") ? "đ" : null;
                stats.Add(new ChatStat(Friendly(n), val, unit));
            }
        }

        return stats;
    }

    // ─── Suggestions ──────────────────────────────────────────────────────────

    internal static List<string>? SuggestFor(string toolName) => toolName switch
    {
        "financial_summary" => new() { "Dòng tiền 12 tháng gần đây", "Top khách hàng tháng này", "Hiệu quả marketing", "Công nợ phải thu" },
        "cashflow"          => new() { "Chi tiết tài chính tháng này", "Top khách hàng", "Top seller doanh số" },
        "top_customers"     => new() { "Doanh thu tháng này", "Khách chưa chăm sóc", "Lịch hẹn CSKH" },
        "top_sellers"       => new() { "Doanh thu tháng này", "Top khách hàng" },
        "marketing"         => new() { "Top khách hàng", "Doanh thu tháng này" },
        "departures"        => new() { "Tour sắp khởi hành còn chỗ", "Top khách hàng" },
        "tours"             => new() { "Tour sắp khởi hành", "Doanh thu tháng này" },
        "customers"         => new() { "Top khách hàng", "Khách sinh nhật tháng này" },
        "booking_tickets"   => new() { "Top khách hàng", "Lịch hẹn CSKH" },
        _                   => null
    };

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kpiRevenue"]          = "Doanh thu",          ["kpiActualReceived"] = "Thực thu",
        ["kpiReceivable"]       = "Công nợ phải thu",   ["kpiOpportunities"]  = "Giá trị cơ hội",
        ["kpiTotalExpense"]     = "Tổng chi phí",       ["kpiActualExpense"]  = "Thực chi",
        ["kpiProviderDebt"]     = "Công nợ NCC",        ["kpiManagementCost"] = "Chi phí quản lý",
        ["kpiGrossProfit"]      = "Lợi nhuận gộp",      ["kpiActualProfit"]   = "Lợi nhuận thực",
        ["kpiNetProfit"]        = "Lợi nhuận ròng",     ["kpiCommission"]     = "Hoa hồng",
        ["totalTours"]          = "Số tour",             ["totalCustomers"]    = "Số khách",
        ["revenue"]             = "Doanh thu",           ["expense"]           = "Chi phí",
        ["profit"]              = "Lợi nhuận",           ["totalCount"]        = "Tổng",
        ["newCount"]            = "Mới",                 ["successCount"]      = "Thành công",
        ["failCount"]           = "Thất bại",            ["totalPayment"]      = "Tổng chi tiêu",
        ["totalTour"]           = "Số tour",             ["count"]             = "Số lượng",
        ["totalRevenue"]        = "Tổng chi tiêu",       ["actualRevenue"]     = "Thực thu",
        ["totalExpense"]        = "Tổng chi phí",        ["actualExpense"]     = "Thực chi",
        ["refund"]              = "Hoàn tiền",           ["pricePerSlot"]      = "Giá/khách",
        ["available"]           = "Còn chỗ",             ["booked"]            = "Đã đặt",
    };

    internal static string Friendly(string key) => Labels.TryGetValue(key, out var v) ? v : key;

    internal static string FinGroup(string key, string label)
    {
        var s = (key + " " + label).ToLowerInvariant();
        if (s.Contains("lợi nhuận") || s.Contains("loi nhuan") || s.Contains("profit") || s.Contains("lãi"))
            return "profit";
        if (s.Contains("chi phí")   || s.Contains("chi phi")   || s.Contains("expense") || s.Contains("cost")
            || s.Contains("hoa hồng") || s.Contains("commission") || s.Contains("thực chi")
            || s.Contains("quản lý") || s.Contains("management") || s.Contains("nợ ncc") || s.Contains("providerdebt"))
            return "expense";
        if (s.Contains("doanh thu") || s.Contains("revenue")    || s.Contains("thực thu") || s.Contains("received")
            || s.Contains("phải thu") || s.Contains("receivable") || s.Contains("cơ hội")  || s.Contains("opportun"))
            return "revenue";
        return "other";
    }

    internal static bool TryNum(JsonElement el, out double n)
    {
        n = 0;
        if (el.ValueKind == JsonValueKind.Number) { n = el.GetDouble(); return true; }
        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n)) return true;
        return false;
    }

    internal static string? GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.String
           ? p.GetString() : null;
}
