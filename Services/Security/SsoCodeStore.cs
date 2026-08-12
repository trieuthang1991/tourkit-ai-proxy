using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TourkitAiProxy.Services.Security;

/// <summary>
/// Kho "code 1-lần" cho SSO CRM → Trav-ai: register-code GHI → exchange ĐỌC+XOÁ.
/// MIRROR KojiCRM/Auth/SsoCodeStore.cs bên CRM (chiều ngược lại), giữ nguyên hợp đồng Save/TakeOnce.
///
/// Vì sao cần: code sinh ở register-code (request A, do CRM gọi server-to-server) nhưng dùng ở
/// exchange (request B, do browser mở) — 2 request khác nhau nên phải có chỗ gửi tạm.
///
/// GIỚI HẠN: bản này CHỈ có chế độ InMemory (ConcurrentDictionary + TTL), an toàn khi proxy chạy
/// 1 process. Nếu sau này scale ra nhiều instance sau load-balancer thì PHẢI chuyển sang Redis —
/// request exchange rơi vào instance khác sẽ không tra ra code và SSO hỏng ngẫu nhiên (~50%).
/// CRM đã có sẵn 2 chế độ, chọn bằng cờ CrmSsoForceInMemory; đây là chỗ cần bổ sung tương tự.
/// </summary>
internal static class SsoCodeStore
{
    private sealed class Entry
    {
        public required string Payload { get; init; }
        public DateTime ExpiresUtc { get; init; }
    }

    private static readonly ConcurrentDictionary<string, Entry> Mem = new();

    /// <summary>Lưu code kèm TTL. Trả false nếu store thất bại (hiện luôn true — InMemory không lỗi).</summary>
    public static bool Save(string code, string payload, TimeSpan ttl)
    {
        PruneExpired();
        Mem[code] = new Entry { Payload = payload, ExpiresUtc = DateTime.UtcNow.Add(ttl) };
        return true;
    }

    /// <summary>Đọc + XOÁ (one-time, chống replay). Trả null nếu không có / hết hạn / đã dùng.</summary>
    public static string? TakeOnce(string code)
    {
        if (!Mem.TryRemove(code, out var e)) return null;          // không có / đã dùng
        return DateTime.UtcNow > e.ExpiresUtc ? null : e.Payload;  // hết hạn
    }

    /// <summary>Code random 256-bit, hex lowercase — khớp SsoController.GenCode bên CRM.</summary>
    public static string GenCode() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// Dọn code quá hạn mà không ai dùng tới. Code đã dùng thì TakeOnce gỡ rồi, chỗ này chỉ lo phần rơi rớt.
    private static void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in Mem)
            if (now > kv.Value.ExpiresUtc) Mem.TryRemove(kv.Key, out _);
    }
}
