using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Bộ nhớ RESOLVE theo PHIÊN (in-memory): khi user đã chọn 1 lựa chọn trong action-clarify
/// (nhiều bản ghi trùng tên, vd 2 nhân viên "Phong"), NHỚ (sessionId, kind, tên) → id đã chọn.
///
/// Mục đích: fix bug "đã chọn rồi lại bắt chọn lại". Khi user BỔ SUNG THÔNG TIN qua chat (tạo 1 lượt
/// planner MỚI → mất staffResolvedIds trong proposal cũ), planner phát lại action với tên gốc mơ hồ
/// ("Phong") → nếu không nhớ sẽ clarify lại. Có bộ nhớ này, hệ thống tra ra id đã chọn và dùng THẲNG,
/// KHÔNG bắt user chọn lại.
///
/// - kind: "staff" | "customer" | "deal".
/// - Isolation theo sessionId + kind: KHÔNG rò chéo giữa phiên khác nhau, cũng không lẫn loại thực thể.
/// - So khớp tên KHÔNG phân biệt hoa/thường/dấu tiếng Việt/khoảng trắng thừa (xem <see cref="Norm"/>).
/// - State in-mem thuần → mất khi restart (chấp nhận được: chỉ là tiện lợi TRONG phiên, không phải nguồn sự thật).
/// </summary>
public class ActionResolutionMemory
{
    // key = "{sessionId}{kind}{normName}" → id đã chọn
    private readonly ConcurrentDictionary<string, int> _map = new();

    // Trần chống phình vô hạn (mỗi phiên vài chục entry là cùng — 5000 là rất thoáng).
    private const int MaxEntries = 5000;

    /// Ghi nhớ id user đã chọn cho (phiên, loại, tên). name rỗng/thiếu ngữ cảnh → bỏ qua (không lưu).
    public void Remember(string? sessionId, string? kind, string? name, int id)
    {
        var key = BuildKey(sessionId, kind, name);
        if (key is null) return;
        if (_map.Count >= MaxEntries) _map.Clear();   // xả thô khi chạm trần — an toàn, hiếm khi xảy ra
        _map[key] = id;
    }

    /// Trả id đã nhớ cho (phiên, loại, tên), hoặc null nếu chưa từng chọn.
    public int? Recall(string? sessionId, string? kind, string? name)
    {
        var key = BuildKey(sessionId, kind, name);
        if (key is null) return null;
        return _map.TryGetValue(key, out var v) ? v : (int?)null;
    }

    private static string? BuildKey(string? sessionId, string? kind, string? name)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(kind)) return null;
        var n = Norm(name);
        if (string.IsNullOrEmpty(n)) return null;
        return sessionId + "" + kind.Trim().ToLowerInvariant() + "" + n;
    }

    /// Chuẩn hóa tên để so khớp: bỏ dấu tiếng Việt, đ→d, thường hóa, gộp khoảng trắng.
    /// "Phong", "phong", " PHONG ", "Phòng" (nếu người nhập nhầm dấu) đều về cùng khóa cơ bản.
    internal static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lower = s.Trim().ToLowerInvariant().Replace('đ', 'd');
        var decomposed = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
