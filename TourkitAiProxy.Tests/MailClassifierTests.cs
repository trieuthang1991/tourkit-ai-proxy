using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests;

public class MailClassifierTests
{
    [Fact]
    public void Parse_plain_json()
    {
        var (cat, sum) = MailClassifier.ParseClassification(
            "{\"category\":\"hoi_dat_tour\",\"summary\":\"Khách cần 2 combo Phú Quốc gấp\"}");
        Assert.Equal("hoi_dat_tour", cat);
        Assert.Equal("Khách cần 2 combo Phú Quốc gấp", sum);
    }

    [Fact]
    public void Parse_strips_fences_and_thinking_prose()
    {
        var raw = "Để tôi suy nghĩ...\n```json\n{\"category\":\"khieu_nai\",\"summary\":\"Khách phàn nàn trễ chuyến\"}\n```\nXong.";
        var (cat, sum) = MailClassifier.ParseClassification(raw);
        Assert.Equal("khieu_nai", cat);
        Assert.Equal("Khách phàn nàn trễ chuyến", sum);
    }

    [Fact]
    public void Parse_unknown_category_falls_back_to_khac()
    {
        var (cat, _) = MailClassifier.ParseClassification("{\"category\":\"bịa_ra\",\"summary\":\"x\"}");
        Assert.Equal("khac", cat);
    }

    [Fact]
    public void Parse_garbage_returns_khac_and_empty_summary()
    {
        var (cat, sum) = MailClassifier.ParseClassification("không có json ở đây");
        Assert.Equal("khac", cat);
        Assert.Equal("", sum);
    }
}
