using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class InboxActionTests
{
    [Fact]
    public void Moc_chua_doc_phai_lui_ve_TRUOC_tin_cuoi_cua_khach()
    {
        // Danh sách tính chưa đọc bằng phép so LỚN HƠN THỰC SỰ:
        //   contact_replied_at > last_read_at
        // Nên đặt mốc BẰNG đúng thời điểm tin cuối là hội thoại vẫn hiện "đã đọc" — bấm nút mà
        // không có gì xảy ra, và không ai đoán ra tại sao.
        var tinCuoi = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        var moc = ChatRules.MocChuaDoc(tinCuoi);
        Assert.True(moc < tinCuoi);
    }

    [Fact]
    public void Moc_chua_doc_khong_lui_qua_xa()
    {
        // Lùi cả phút thì những tin của khách gửi TRƯỚC đó vài giây cũng thành chưa đọc theo —
        // đúng thì đúng nhưng người dùng chỉ định đánh dấu một hội thoại, không phải cả cụm.
        var tinCuoi = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        var moc = ChatRules.MocChuaDoc(tinCuoi);
        Assert.True(tinCuoi - moc <= System.TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Moc_chua_doc_giu_nguyen_Kind_Utc()
    {
        // Kind=Unspecified lọt xuống Dapper là lệch +7h khi đọc lại — xem docs/datetime-convention.md.
        var moc = ChatRules.MocChuaDoc(new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal(DateTimeKind.Utc, moc.Kind);
    }
}
