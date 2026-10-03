using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh độ trễ gửi của hộp thư chat.
///
/// <para><b>Chuyện đã xảy ra (sửa 02/10/2026).</b> Tin của nhân viên bị giữ lại
/// <c>Chat:UndoSendSeconds</c> giây (mặc định 5) cho kịp bấm Thu hồi. Worker thì ngủ cố định một
/// nhịp 5 giây rồi mới vét hàng đợi — nên nó tỉnh dậy giữa chừng, thấy chưa tới giờ, ngủ tiếp
/// trọn một nhịp nữa. Tin thật sự đi trong khoảng <i>hoãn…hoãn+5 giây</i>, tức <b>5–10 giây</b>.
/// Nhân viên chỉ thấy "gửi rất lâu", không có gì trong log nói vì sao, và vì nó áp cho MỌI kênh
/// nên dễ đổ oan cho kênh đang thử.</para>
///
/// <para>Cách sửa: ngủ tới ĐÚNG lúc dòng sớm nhất đến hạn. Nhóm test này giữ cách sửa đó khỏi bị
/// hoàn nguyên — hoàn nguyên thì không có gì hỏng, chỉ chậm lại, nên không ai phát hiện.</para>
/// </summary>
public class ChatOutboxDelayGuardTests
{
    private static string Worker()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs");

    private static string Repo()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

    private static string KhongChuThich(string src)
        => string.Join("\n", src.Split('\n').Where(d => !d.TrimStart().StartsWith("//")));

    [Fact]
    public void Worker_KHONG_duoc_ngu_co_dinh_mot_nhip()
    {
        // Đây chính là dòng đã gây ra "gửi rất lâu".
        Assert.DoesNotContain("WaitAsync(ChatLane.Out, Tick", KhongChuThich(Worker()));
    }

    [Fact]
    public void Worker_phai_ngu_theo_gio_den_han()
    {
        var src = KhongChuThich(Worker());
        Assert.Contains("ChoBaoLauAsync", src);
        Assert.Contains("NextOutboxDueInAsync", src);
    }

    [Fact]
    public void Gio_den_han_phai_tru_theo_dong_ho_CSDL()
    {
        // Trừ theo DateTime.UtcNow của máy chủ ứng dụng là sai khi nó lệch giờ với CSDL: ngủ hụt
        // thì vét trượt rồi lại ngủ trọn nhịp (đúng lỗi cũ quay lại), ngủ quá thì tin đi muộn.
        // Mà lệch đồng hồ giữa hai máy thì không ai đi tìm.
        var i = Repo().IndexOf("NextOutboxDueInAsync", System.StringComparison.Ordinal);
        Assert.True(i > 0, "không tìm thấy NextOutboxDueInAsync");
        var than = Repo()[i..];
        than = than[..System.Math.Min(1200, than.Length)];
        Assert.Contains("now()", than);
        Assert.DoesNotContain("DateTime.UtcNow", than);
    }

    [Fact]
    public void Phai_co_san_chan_quay_vong_nong()
    {
        // Dòng đến hạn bị tiến trình khác giành mất → thời gian còn lại bằng 0. Không có sàn thì
        // vòng lặp quay tít đốt CPU và dội truy vấn vào CSDL.
        Assert.Contains("ToiThieu", KhongChuThich(Worker()));
    }
}
