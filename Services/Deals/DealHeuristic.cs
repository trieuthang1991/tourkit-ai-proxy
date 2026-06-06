using System.Globalization;
using System.Text;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Deals;

/// Chấm điểm cơ hội bằng CODE (không AI) — thuần, test được.
///   • QuickScore: xếp sơ bộ toàn bộ pipeline (tầng 1) = stage·0.4 + value·0.3 + urgency·0.3.
///   • FinalPriority: sau khi AI có winRate → cân bằng EV (winRate×giá) + độ gấp (đúng lựa chọn user).
public static class DealHeuristic
{
    /// Điểm sơ bộ 0..1 cho 1 cơ hội (tầng 1, chọn top N).
    public static double QuickScore(DealOpportunity d)
        => StageScore(d.StatusName, d.Status) * 0.4 + ValueScore(d.TotalPrice) * 0.3 + UrgencyScore(d.AgeDays) * 0.3;

    /// Độ chín của giai đoạn (tên trạng thái tenant-config → maturity). Bỏ dấu để match.
    public static double StageScore(string? statusName, int status)
    {
        var s = Normalize(statusName);
        if (s.Length > 0)
        {
            if (Has(s, "dam phan", "thuong luong", "cho coc", "dat coc", "chot", "ky hop dong")) return 0.9;
            if (Has(s, "bao gia", "gui gia", "gui bao gia", "tu van gia")) return 0.65;
            if (Has(s, "tu van", "lien he", "dang xu ly", "cham soc", "follow", "theo doi")) return 0.45;
            if (Has(s, "moi", "tiep nhan", "chua xu ly")) return 0.2;
        }
        return status <= 1 ? 0.2 : 0.5;   // fallback theo int
    }

    /// Giá trị deal (log-normalize, tránh deal khổng lồ áp đảo).
    public static double ValueScore(long price)
    {
        if (price <= 0) return 0;
        var v = Math.Log10(1 + price / 1_000_000.0) / 2.5;   // 10tr≈.42, 50tr≈.68, 200tr≈.92, 500tr+≈1
        return Math.Clamp(v, 0, 1);
    }

    /// Độ gấp theo tuổi cơ hội: càng để lâu chưa chốt càng cần xử lý (cap 21 ngày).
    public static double UrgencyScore(int ageDays)
        => Math.Clamp(ageDays / 21.0, 0, 1);

    /// Cờ rủi ro suy ra từ tuổi (đang nguội nếu để lâu).
    public static string? RiskFlag(int ageDays)
        => ageDays >= 21 ? "nguoi" : null;

    /// Ưu tiên cuối sau khi có winRate: cân bằng EV + độ gấp (EV trội nhẹ).
    /// Độ gấp GIẢM theo khả năng thắng — deal gần như chết (win thấp) không bị đẩy top chỉ vì để lâu.
    /// Trả (priorityScore 0..100, expectedValue đồng).
    public static (double Priority, long ExpectedValue) FinalPriority(int winRate, long price, int ageDays)
    {
        var ev = (long)(winRate / 100.0 * price);
        var evScore = ValueScore(ev);                              // log-normalize EV như giá
        var winnability = winRate / 100.0;                          // 0..1
        var urgency = UrgencyScore(ageDays) * (0.4 + 0.6 * winnability);  // deal chết → ít độ gấp
        var priority = (evScore * 0.6 + urgency * 0.4) * 100;
        return (Math.Round(priority, 1), ev);
    }

    // ─── helpers ───────────────────────────────────────────────────────────────────
    private static bool Has(string s, params string[] keys)
    {
        foreach (var k in keys) if (s.Contains(k)) return true;
        return false;
    }

    /// lowercase + bỏ dấu tiếng Việt + đ→d (để match tên trạng thái không phụ thuộc dấu).
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lower = s.Trim().ToLowerInvariant().Replace('đ', 'd');
        var formD = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
