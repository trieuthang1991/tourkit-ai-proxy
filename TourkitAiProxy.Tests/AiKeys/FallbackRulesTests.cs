using TourkitAiProxy.Domain.AiKeys;
using Xunit;

namespace TourkitAiProxy.Tests.AiKeys;

/// <summary>
/// Khi nào thì key riêng của công ty hỏng tới mức phải lùi về key hệ thống (và trừ lượt).
///
/// <para>Chủ dự án chốt 03/10/2026: lùi khi key khách HẾT TIỀN hoặc SAI — không lùi khi chỉ là gọi
/// quá dày hay nhà cung cấp đang lỗi. Lùi nhầm thì tốn lượt của khách cho một lỗi tự hết sau vài
/// giây; không lùi khi cần thì khách bị đứt dịch vụ dù đã mua lượt.</para>
/// </summary>
public class FallbackRulesTests
{
    // ── Lùi ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Unauthorized_key_falls_back()
        => Assert.True(TenantAiKeyRules.ShouldFallBack(401, "{\"error\":\"invalid_api_key\"}"));

    [Fact]
    public void Payment_required_falls_back()
        => Assert.True(TenantAiKeyRules.ShouldFallBack(402, ""));

    [Fact]
    public void OpenAI_insufficient_quota_falls_back()
        => Assert.True(TenantAiKeyRules.ShouldFallBack(429,
            "{\"error\":{\"type\":\"insufficient_quota\",\"message\":\"You exceeded your current quota\"}}"));

    [Fact]
    public void Anthropic_low_credit_falls_back_even_though_it_is_a_400()
    {
        // Claude báo hết tiền bằng 400, không phải 402. Không bắt câu này thì khách dùng key Claude
        // hết tiền KHÔNG được lùi — ngược đúng điều chủ dự án yêu cầu.
        Assert.True(TenantAiKeyRules.ShouldFallBack(400,
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\"," +
            "\"message\":\"Your credit balance is too low to access the Anthropic API.\"}}"));
    }

    [Fact]
    public void Grok_wrong_key_falls_back_even_though_it_is_a_400()
    {
        // Thân lỗi THẬT, bắt được 03/10/2026 khi thử key Grok sai trên trang cấu hình. xAI báo key sai
        // bằng 400, KHÔNG phải 401. Không bắt câu này thì key Grok của khách bị thu hồi giữa chừng sẽ
        // KHÔNG được lùi — khách đứt dịch vụ, đúng ca chủ dự án yêu cầu phải chạy.
        Assert.True(TenantAiKeyRules.ShouldFallBack(400,
            "{\"code\":\"invalid-argument\",\"error\":\"Incorrect API key provided. " +
            "You can obtain an API key from https://console.x.ai.\"}"));
    }

    // ── KHÔNG lùi ────────────────────────────────────────────────────────────

    [Fact]
    public void Plain_rate_limit_does_not_fall_back()
    {
        // Gọi quá dày: vài giây sau là được. Lùi là đốt lượt của khách vô ích.
        Assert.False(TenantAiKeyRules.ShouldFallBack(429,
            "{\"error\":{\"type\":\"rate_limit_exceeded\",\"message\":\"Rate limit reached\"}}"));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(529)]   // Anthropic "overloaded"
    public void Provider_outage_does_not_fall_back(int status)
        => Assert.False(TenantAiKeyRules.ShouldFallBack(status, "upstream error"));

    [Fact]
    public void Other_400_does_not_fall_back()
    {
        // 400 vì nội dung sai (prompt quá dài…) không liên quan key — lùi cũng hỏng y vậy.
        Assert.False(TenantAiKeyRules.ShouldFallBack(400,
            "{\"error\":{\"message\":\"prompt is too long\"}}"));
    }

    [Fact]
    public void Null_body_is_safe()
        => Assert.False(TenantAiKeyRules.ShouldFallBack(429, null));

    // ── Lý do hiện cho người dùng ────────────────────────────────────────────

    [Theory]
    [InlineData(401, "")]
    [InlineData(402, "")]
    [InlineData(429, "insufficient_quota")]
    [InlineData(400, "credit balance is too low")]
    [InlineData(400, "Incorrect API key provided")]
    public void Reason_is_plain_vietnamese_with_the_code(int status, string body)
    {
        var lyDo = TenantAiKeyRules.FallbackReason(status, body);
        Assert.Contains(status.ToString(), lyDo);
        Assert.DoesNotContain("insufficient_quota", lyDo);   // không đưa mã thô của nhà cung cấp lên màn hình
        Assert.True(lyDo.Length <= 256);                     // vừa cột LastFailReason
    }
}
