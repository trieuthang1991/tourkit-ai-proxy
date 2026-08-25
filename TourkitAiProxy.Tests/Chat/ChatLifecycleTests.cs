using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Vòng đời tin gửi đi. Nền tảng KHÔNG bảo đảm thứ tự webhook, nên mọi cập nhật trạng thái
/// phải đi qua một luật duy nhất — không thì dấu tích chạy ngược trước mắt nhân viên.
/// </summary>
public class ChatLifecycleTests
{
    [Theory]
    [InlineData(ChatState.Cho, ChatState.DaGui, true)]
    [InlineData(ChatState.DaGui, ChatState.DaNhan, true)]
    [InlineData(ChatState.DaNhan, ChatState.DaXem, true)]
    [InlineData(ChatState.DaGui, ChatState.DaXem, true)]    // nhảy cóc: chỉ nhận được "đã xem"
    public void Tien_len_thi_duoc(ChatState dangCo, ChatState moi, bool mong)
        => Assert.Equal(mong, ChatRules.KhongLui(dangCo, moi));

    [Theory]
    [InlineData(ChatState.DaXem, ChatState.DaNhan)]   // delivery tới SAU read — chuyện thường
    [InlineData(ChatState.DaXem, ChatState.DaGui)]
    [InlineData(ChatState.DaNhan, ChatState.DaGui)]
    public void Lui_lai_thi_bo_qua(ChatState dangCo, ChatState moi)
        => Assert.False(ChatRules.KhongLui(dangCo, moi));

    [Fact]
    public void Cung_mot_muc_thi_bo_qua()
        => Assert.False(ChatRules.KhongLui(ChatState.DaXem, ChatState.DaXem));

    [Fact]
    public void Tin_da_gui_duoc_thi_khong_the_thanh_hong()
    {
        // Hỏng (4) số lớn nhất nhưng KHÔNG phải mức cao nhất.
        Assert.False(ChatRules.KhongLui(ChatState.DaGui, ChatState.Hong));
        Assert.False(ChatRules.KhongLui(ChatState.DaXem, ChatState.Hong));
    }

    [Fact]
    public void Tin_dang_cho_thi_hong_duoc()
        => Assert.True(ChatRules.KhongLui(ChatState.Cho, ChatState.Hong));

    [Fact]
    public void Worker_gui_xong_phai_luu_ma_tin_cua_nen_tang()
    {
        // Không có CI chạy PostgreSQL nên canh ở mức mã nguồn. Mã tin nền tảng là thứ DUY NHẤT
        // đối chiếu được khi nền tảng báo lại — vứt đi là cả vòng đời tin vô nghĩa.
        var src = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatOutboxWorker.cs");
        Assert.Contains("LuuMaTinDaGuiAsync", src);
        Assert.Contains("kq.ExternalMsgId", src);
    }

    [Fact]
    public void Ghi_ma_tin_la_lenh_rieng_khong_gop_vao_doi_trang_thai()
    {
        // Gộp thì mỗi lần đổi trạng thái về sau phải nhớ truyền kèm mã; quên là xoá mất mã bằng null.
        var repo = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatRepository.cs");
        Assert.Contains("public async Task LuuMaTinDaGuiAsync", repo);
        Assert.DoesNotContain(
            "SetMessageStateAsync(string tenant, long messageId, ChatState tt, string? loi, string? maNenTang",
            repo);
    }
}
