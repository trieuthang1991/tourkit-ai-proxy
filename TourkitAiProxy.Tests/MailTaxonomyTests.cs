using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests;

public class MailTaxonomyTests
{
    [Theory]
    [InlineData("hoi_dat_tour", "hoi_dat_tour")]
    [InlineData("HOI_DAT_TOUR", "hoi_dat_tour")]   // không phân biệt hoa thường
    [InlineData("  spam  ", "spam")]               // trim
    [InlineData("không-biết", "khac")]             // lạ → khac
    [InlineData("", "khac")]
    [InlineData(null, "khac")]
    public void NormalizeCategory_maps_to_known_set(string? input, string expected)
        => Assert.Equal(expected, MailTaxonomy.NormalizeCategory(input));

    [Fact]
    public void Categories_has_six_entries_with_vietnamese_labels()
    {
        Assert.Equal(6, MailTaxonomy.Categories.Count);
        Assert.Equal("Khiếu nại", MailTaxonomy.Categories["khieu_nai"]);
    }

    [Fact]
    public void Tone_label_returns_vietnamese_for_known_else_default()
    {
        Assert.Equal("Lịch sự, trang trọng", MailTaxonomy.ToneLabel("lich_su"));
        Assert.Equal("Lịch sự, trang trọng", MailTaxonomy.ToneLabel("không-có"));   // fallback
    }

    [Fact]
    public void IsStatus_true_only_for_known()
    {
        Assert.True(MailTaxonomy.IsStatus("da_dong"));
        Assert.False(MailTaxonomy.IsStatus("bừa"));
    }
}
