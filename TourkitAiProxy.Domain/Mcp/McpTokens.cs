using System.Security.Cryptography;
using System.Text;

namespace TourkitAiProxy.Domain.Mcp;

/// <summary>
/// Khoá MCP: chuỗi mà người dùng dán vào Claude Desktop / Cursor để máy khách MCP gọi vào TourKit
/// thay mặt họ. Dạng <c>tkmcp_</c> + 32 hex (16 byte ngẫu nhiên) — tiền tố riêng để nhận ra khoá
/// này khi nó lỡ lọt vào log hay chat, và để không lẫn với khoá widget (<c>trav_</c>).
///
/// <para><b>CSDL chỉ giữ bản băm SHA-256.</b> Khoá đủ entropy (128 bit) nên băm nhanh là đủ — không
/// cần bcrypt. Lộ CSDL không lộ khoá dùng được.</para>
/// </summary>
public static class McpTokens
{
    private const string Prefix = "tkmcp_";

    public static string New()
        => Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Đúng dạng chưa có nghĩa là khoá còn sống — chỉ để loại sớm chuỗi rác trước khi chạm CSDL.</summary>
    public static bool LooksValid(string? t)
    {
        if (t is null || t.Length != Prefix.Length + 32 || !t.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (var i = Prefix.Length; i < t.Length; i++)
        {
            var c = t[i];
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f')) return false;
        }
        return true;
    }

    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

/// <summary>Một khoá đã tạo. KHÔNG có trường chứa khoá — chỉ lúc tạo mới thấy khoá một lần.</summary>
public record McpTokenRow(long Id, string TenantId, string Username, string SessionId, string Label,
    DateTime CreatedUtc, DateTime? LastUsedUtc, DateTime? RevokedUtc);
