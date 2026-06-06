using MimeKit;
using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests;

public class MailMapperTests
{
    private static MimeMessage Build(string from, string fromName, string subject, string body, string? messageId)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(fromName, from));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        if (messageId != null) msg.MessageId = messageId;
        msg.Date = new DateTimeOffset(2026, 6, 5, 8, 30, 0, TimeSpan.Zero);
        return msg;
    }

    [Fact]
    public void FromMime_extracts_core_fields()
    {
        var msg = Build("minh.tran@gmail.com", "minh.tran", "Đặt vé combo Phú Quốc", "Cứu! Mình cần 2 combo...", "<abc@mail.gmail.com>");
        var item = MailMapper.FromMime(msg, fallbackId: "fallback:1");

        // MimeKit chuẩn hóa Message-Id (bỏ <…>) — Id ổn định để dedupe.
        Assert.Equal("abc@mail.gmail.com", item.Id);
        Assert.Equal("minh.tran", item.From.Name);
        Assert.Equal("minh.tran@gmail.com", item.From.Email);
        Assert.Equal("Đặt vé combo Phú Quốc", item.Subject);
        Assert.Equal("Cứu! Mình cần 2 combo...", item.Body);
        Assert.Equal("moi", item.Status);
        Assert.Null(item.Category);
        Assert.False(item.IsRead);
    }

    [Fact]
    public void FromMime_produces_nonempty_id_even_without_explicit_message_id()
    {
        // MimeKit tự sinh Message-Id khi email thiếu header này → Id luôn non-empty
        // (key ổn định để dedupe). Nhánh fallbackId chỉ là phòng thủ cho trường hợp hiếm.
        var msg = Build("a@b.com", "A", "S", "B", messageId: null);
        var item = MailMapper.FromMime(msg, fallbackId: "fallback:42");
        Assert.False(string.IsNullOrWhiteSpace(item.Id));
    }

    [Fact]
    public void FromMime_handles_missing_subject()
    {
        var msg = Build("a@b.com", "A", "", "B", "<x@y>");
        var item = MailMapper.FromMime(msg, "f:1");
        Assert.Equal("(không tiêu đề)", item.Subject);
    }
}
