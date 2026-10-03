using TourkitAiProxy.Domain.AiKeys;
using Xunit;

namespace TourkitAiProxy.Tests.AiKeys;

/// <summary>
/// Luật chọn key: dùng key riêng của công ty hay key hệ thống.
///
/// <para>Hàm này nằm trên đường đi của MỌI lệnh AI (32 chỗ gọi, đo 03/10/2026). Nên điều quan
/// trọng nhất không phải nó chọn đúng khi có key, mà là <b>khi không có gì thì nó không đổi gì
/// cả</b> — chủ dự án dặn rõ: không được ảnh hưởng hệ thống đang chạy.</para>
/// </summary>
public class TenantAiKeyRulesTests
{
    private static readonly DateTime Validated = new(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc);

    private static TenantAiKey Key(bool enabled = true, DateTime? validated = null,
        string provider = "anthropic", string? model = "claude-sonnet-5")
        => new("staging.tourkit.vn", provider, model, "ENC", "sk-…abcd", enabled,
               validated ?? Validated, "admin", Validated);

    // ── Không có gì thì không đổi gì ─────────────────────────────────────────

    [Fact]
    public void Feature_off_never_uses_tenant_key_even_when_configured()
    {
        // Cờ tắt là cam kết "y hệt hôm nay" — kể cả công ty đã lỡ khai key từ trước.
        var d = TenantAiKeyRules.Decide(featureOn: false, "staging.tourkit.vn", Key(), "sk-real");
        Assert.False(d.UseTenantKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_tenant_means_system_key(string? tenant)
    {
        // Worker nền và lệnh không có phiên: không biết công ty nào thì không có key riêng nào.
        Assert.False(TenantAiKeyRules.Decide(true, tenant, Key(), "sk-real").UseTenantKey);
    }

    [Fact]
    public void Tenant_without_config_uses_system_key()
        => Assert.False(TenantAiKeyRules.Decide(true, "staging.tourkit.vn", null, null).UseTenantKey);

    [Fact]
    public void Disabled_config_uses_system_key()
        => Assert.False(TenantAiKeyRules.Decide(true, "t", Key(enabled: false), "sk-real").UseTenantKey);

    [Fact]
    public void Never_validated_key_is_not_used()
    {
        // Key chưa từng gọi thử thành công thì đừng đem đi chạy việc thật của khách.
        var chuaKiem = Key() with { ValidatedAtUtc = null };
        Assert.False(TenantAiKeyRules.Decide(true, "t", chuaKiem, "sk-real").UseTenantKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Decrypt_failure_falls_back_to_system_key(string? raw)
    {
        // Crypton trả chuỗi rỗng khi giải mã hỏng (không ném). Không có key thô thì không gọi được.
        Assert.False(TenantAiKeyRules.Decide(true, "t", Key(), raw).UseTenantKey);
    }

    // ── Có đủ điều kiện thì dùng key khách ───────────────────────────────────

    [Fact]
    public void Enabled_and_validated_key_is_used()
    {
        var d = TenantAiKeyRules.Decide(true, "t", Key(), "sk-real");
        Assert.True(d.UseTenantKey);
        Assert.Equal("anthropic", d.Provider);
        Assert.Equal("claude-sonnet-5", d.Model);
        Assert.Equal("sk-real", d.ApiKey);
    }

    [Fact]
    public void Missing_model_stays_null_and_is_NOT_borrowed_from_system_default()
    {
        // Bẫy có thật trong kế hoạch gốc: `config.Model ?? defaultModel`. defaultModel là model
        // của nhà cung cấp HỆ THỐNG (vd ds/deepseek-chat của 9routes). Khách chọn Anthropic mà
        // không ghi model thì đem tên model DeepSeek gọi Anthropic → hỏng ngay lượt đầu.
        //
        // Trả null để chỗ gọi dùng model mặc định CỦA CHÍNH nhà cung cấp khách chọn.
        var d = TenantAiKeyRules.Decide(true, "t", Key(model: null), "sk-real");
        Assert.True(d.UseTenantKey);
        Assert.Null(d.Model);
    }

    [Fact]
    public void Blank_model_is_treated_as_missing()
        => Assert.Null(TenantAiKeyRules.Decide(true, "t", Key(model: "  "), "sk-real").Model);
}
