using TourkitAiProxy.Domain.AiKeys;
using Xunit;

namespace TourkitAiProxy.Tests.AiKeys;

/// <summary>
/// Che key AI của khách trước khi trả ra giao diện.
///
/// <para>Key thô KHÔNG BAO GIỜ rời máy chủ — GET chỉ trả bản đã che. Nên bản che phải đủ để khách
/// nhận ra "à đúng key của mình" (vài ký tự đầu cho biết loại key, vài ký tự cuối để đối chiếu),
/// mà không đủ để ai đứng sau lưng chép lại dùng được.</para>
/// </summary>
public class TenantAiKeyTests
{
    [Theory]
    [InlineData("sk-ant-1234567890abcd", "sk-…abcd")]
    [InlineData("sk-proj-ABCDEFGHIJKLMNOP", "sk-…MNOP")]
    public void MaskOf_shows_only_head_and_tail(string raw, string expected)
        => Assert.Equal(expected, TenantAiKey.MaskOf(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("12345678")]
    public void MaskOf_hides_short_keys_completely(string? raw)
    {
        // Key ngắn mà vẫn lộ 3 đầu + 4 cuối thì gần như lộ trọn — che hết.
        Assert.Equal("••••", TenantAiKey.MaskOf(raw!));
    }

    [Fact]
    public void MaskOf_never_contains_the_middle_of_the_key()
    {
        const string raw = "sk-ant-SECRETMIDDLEPART-wxyz";
        Assert.DoesNotContain("SECRETMIDDLEPART", TenantAiKey.MaskOf(raw));
    }
}
