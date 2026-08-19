using TourkitAiProxy.Services;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Cờ <c>TextFromReasoning</c> — chốt chặn để CHUỖI SUY NGHĨ của model không lọt ra thư gửi người dùng.
///
/// <para>Bối cảnh (19/08/2026): bản tin sáng gửi đi mang nguyên phần model tự nhủ — "Chúng ta cần trả
/// lời câu hỏi…", "Yêu cầu: Chọn TỐI ĐA 7 việc…" — và cụt giữa từ. Nguyên nhân: model reasoning tiêu
/// hết hạn mức vào phần nghĩ, <c>content</c> trả về rỗng, parser lấy tạm <c>reasoning_content</c> làm
/// Text (chủ ý, để debug). Text đó KHÔNG rỗng nên chốt <c>IsNullOrWhiteSpace</c> ở workflow không bắt
/// được, và bản dự phòng rule-based — vốn chạy tốt — không bao giờ được gọi.</para>
///
/// <para>Lỗi kiểu này KHÔNG làm gãy gì cả: không exception, không log lỗi, gửi vẫn báo thành công.
/// Chỉ người nhận mở thư ra mới thấy. Nên khoá bằng test.</para>
/// </summary>
public class UpstreamParserReasoningTests
{
    private const string Fmt = "openai";

    [Fact]
    public void Co_content_thi_KHONG_danh_dau_la_suy_nghi()
    {
        const string raw = """
        {"choices":[{"message":{"content":"Sáng nay gọi 3 khách.","reasoning_content":"Ta nên chọn..."},
          "finish_reason":"stop"}]}
        """;
        var p = UpstreamParser.Parse(raw, Fmt);

        Assert.Equal("Sáng nay gọi 3 khách.", p.Text);
        // Có câu trả lời thật thì reasoning chỉ là phần thừa — không được coi là hỏng.
        Assert.False(p.TextFromReasoning);
    }

    [Fact]
    public void Content_rong_ma_co_reasoning_content_thi_PHAI_danh_dau()
    {
        const string raw = """
        {"choices":[{"message":{"content":"","reasoning_content":"Chúng ta cần trả lời câu hỏi SÁNG NAY LÀM GÌ"},
          "finish_reason":"length"}]}
        """;
        var p = UpstreamParser.Parse(raw, Fmt);

        // Text vẫn được trả về (để debug xem model tiêu token vào đâu)...
        Assert.Contains("Chúng ta cần trả lời", p.Text);
        // ...nhưng phải kèm cờ, nếu không bên gọi tưởng đây là câu trả lời thật.
        Assert.True(p.TextFromReasoning);
    }

    [Fact]
    public void Ten_truong_reasoning_khong_hau_to_cung_tinh()
    {
        const string raw = """
        {"choices":[{"message":{"content":"","reasoning":"Hmm, để xem nào..."}}]}
        """;
        var p = UpstreamParser.Parse(raw, Fmt);
        Assert.True(p.TextFromReasoning);
    }

    [Fact]
    public void Khong_co_reasoning_thi_text_rong_va_khong_danh_dau()
    {
        const string raw = """{"choices":[{"message":{"content":""},"finish_reason":"stop"}]}""";
        var p = UpstreamParser.Parse(raw, Fmt);

        Assert.Equal("", p.Text);
        // Rỗng-thật khác rỗng-vì-nghĩ-hết-giờ: chốt IsNullOrWhiteSpace cũ đã lo được ca này rồi.
        Assert.False(p.TextFromReasoning);
    }

    [Fact]
    public void Duong_anthropic_khong_co_khai_niem_reasoning_content()
    {
        const string raw = """
        {"content":[{"type":"text","text":"Doanh thu tháng này tăng 12%."}],"stop_reason":"end_turn"}
        """;
        var p = UpstreamParser.Parse(raw, "anthropic");

        Assert.Equal("Doanh thu tháng này tăng 12%.", p.Text);
        Assert.False(p.TextFromReasoning);
    }
}
