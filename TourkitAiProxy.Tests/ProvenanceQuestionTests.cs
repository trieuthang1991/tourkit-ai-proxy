using System.Collections.Generic;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Chat;
using Xunit;

namespace TourkitAiProxy.Tests;

/// Khóa hành vi JsonPlannerAgent.IsProvenanceQuestion — nhận diện câu hỏi VỀ nguồn gốc/cách tính số liệu
/// (short-circuit tất định để tránh bug "lặp câu trả lời": planner hiểu nhầm follow-up → chọn lại tool
/// cũ → L2 cache trả nguyên văn câu trước).
public class ProvenanceQuestionTests
{
    [Theory]
    // Câu HỎI NGUỒN / cách tính → phải nhận diện là provenance.
    [InlineData("Số liệu này lấy từ đâu?")]
    [InlineData("số liệu này lấy từ đâu")]
    [InlineData("Nguồn số liệu này là gì?")]
    [InlineData("Con số này tính thế nào?")]
    [InlineData("Dữ liệu này ở đâu ra vậy?")]
    [InlineData("Báo cáo này dựa vào đâu?")]
    [InlineData("Số liệu này tính như thế nào?")]
    public void Detects_provenance_questions(string q)
        => Assert.True(JsonPlannerAgent.IsProvenanceQuestion(q));

    [Theory]
    // Câu SỐ LIỆU thật (kể cả có "này" hoặc "từ đâu"/"nào") → KHÔNG được nuốt thành provenance.
    [InlineData("Doanh thu tháng này")]                              // có " nay" nhưng không hỏi nguồn
    [InlineData("Doanh thu tháng này thế nào?")]
    [InlineData("Doanh thu đến từ thị trường nào nhiều nhất?")]      // breakdown: "tu ... nao nhieu nhat"
    [InlineData("Top khách hàng tháng này")]                        // breakdown: "top"
    [InlineData("Còn tháng trước thì sao?")]                        // follow-up hợp lệ, giữ nguyên tool
    [InlineData("Chi phí chủ yếu đến từ đâu?")]                     // breakdown: "chu yeu"
    [InlineData("Lợi nhuận này có đáng lo không?")]                 // đánh giá, không hỏi nguồn
    [InlineData("")]
    // ── Câu NỐI / follow-up: KHÔNG được nuốt thành provenance → phải chạy planner như thường ──
    [InlineData("So sánh doanh thu tháng trước")]                   // compare intent
    [InlineData("Doanh thu tháng này so với tháng trước")]
    [InlineData("So với cùng kỳ năm ngoái thì sao?")]
    [InlineData("Còn tháng trước thì sao?")]
    [InlineData("Còn thị trường Hàn Quốc thì sao?")]
    [InlineData("Vậy lợi nhuận thì sao?")]
    [InlineData("Chi tiết hơn giúp tôi")]
    public void Ignores_normal_data_questions(string q)
        => Assert.False(JsonPlannerAgent.IsProvenanceQuestion(q));

    // ── Seam ProvenanceShortCircuit — quyết định + giữ data + nêu đúng nguồn (dùng chung cả 2 path) ──

    private static SessionChatMemory MemoryWith(string? lastTool, ChatData? data, string? dataTitle = null)
        => SessionChatMemory.Empty() with { LastTool = lastTool, LastChatData = data, LastDataTitle = dataTitle };

    private static ChatData SampleData()
        => new("financial", "Dòng tiền tháng 8", null,
               new List<ChatStat> { new("Doanh thu", 1000, "đ", null) }, null);

    [Fact]
    public void ShortCircuit_provenance_returns_source_reply_and_keeps_old_data()
    {
        var data = SampleData();
        var mem = MemoryWith("cashflow", data);

        var r = JsonPlannerAgent.ProvenanceShortCircuit("Số liệu này lấy từ đâu?", mem);

        Assert.NotNull(r);
        Assert.Equal("cashflow", r!.Value.ToolName);
        Assert.Same(data, r.Value.Data);                              // GIỮ panel cũ (không mất chart)
        Assert.Contains("hệ thống ERP", r.Value.Reply);
        Assert.Contains("Doanh thu & Lợi nhuận", r.Value.Reply);      // tên nguồn thật từ ChatTools.Find("cashflow").Title
    }

    [Fact]
    public void ShortCircuit_normal_data_question_returns_null_so_planner_runs()
    {
        var mem = MemoryWith("cashflow", SampleData());
        Assert.Null(JsonPlannerAgent.ProvenanceShortCircuit("Doanh thu tháng này", mem));
    }

    [Fact]
    public void ShortCircuit_no_previous_data_returns_null()
    {
        // Chưa có số liệu lượt trước (phiên mới) → không short-circuit dù câu hỏi có dạng "lấy từ đâu".
        var mem = MemoryWith(null, null);
        Assert.Null(JsonPlannerAgent.ProvenanceShortCircuit("Số liệu này lấy từ đâu?", mem));
    }

    // ── ScrubToolNames — không để lộ tên tool nội bộ ra câu trả lời user ──

    [Fact]
    public void Scrub_replaces_leaked_tool_name_with_vietnamese_title()
    {
        // Đúng ca user gặp: planner nhét "tool cashflow (dòng tiền)" vào reply.
        var s = JsonPlannerAgent.ScrubToolNames(
            "Tôi lấy dữ liệu từ tool cashflow (dòng tiền) của hệ thống.");
        Assert.DoesNotContain("cashflow", s);
        Assert.Contains("Doanh thu & Lợi nhuận", s);
    }

    [Fact]
    public void Scrub_replaces_bare_technical_names()
    {
        var s = JsonPlannerAgent.ScrubToolNames("Số liệu từ financial_summary và top_customers.");
        Assert.DoesNotContain("financial_summary", s);
        Assert.DoesNotContain("top_customers", s);
    }

    [Fact]
    public void Scrub_keeps_common_words_untouched()
    {
        // "marketing" là từ thường — KHÔNG được thay dù trùng tên tool.
        const string txt = "Chi phí marketing tháng này tăng, khách đặt tour nhiều hơn.";
        Assert.Equal(txt, JsonPlannerAgent.ScrubToolNames(txt));
    }

    [Fact]
    public void ShortCircuit_falls_back_to_dataTitle_when_tool_has_no_catalog_title()
    {
        // Tool không có trong catalog → dùng LastDataTitle làm tên nguồn.
        var mem = MemoryWith("khong_ton_tai", SampleData(), dataTitle: "Báo cáo tùy chỉnh");
        var r = JsonPlannerAgent.ProvenanceShortCircuit("Nguồn số liệu này?", mem);
        Assert.NotNull(r);
        Assert.Contains("Báo cáo tùy chỉnh", r!.Value.Reply);
    }
}
