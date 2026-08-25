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

    [Theory]
    [InlineData(ChatState.DaNhan)]
    [InlineData(ChatState.DaXem)]
    public void Tin_chua_gui_di_thi_khong_the_da_nhan_hay_da_xem(ChatState moi)
    {
        // Kịch bản THẬT đã dựng được trên staging: nhân viên bấm gửi lúc 10:00:00 (tin vào hàng đợi,
        // trạng thái Chờ), worker gửi lúc 10:00:03 vì nhịp 5 giây. Khách đọc một tin CŨ lúc 10:00:01
        // → nền tảng báo mốc nước 10:00:01, mà mốc quét theo created_utc nên trúng luôn tin vừa tạo
        // còn chưa rời khỏi hệ thống.
        //
        // Để lọt thì nhân viên thấy "khách đã xem" một tin khách chưa hề nhận được — và ngay sau đó
        // worker gửi xong lại đặt về "đã gửi", tức dấu tích còn chạy ngược nữa.
        Assert.False(ChatRules.KhongLui(ChatState.Cho, moi));
    }

    [Fact]
    public void Danh_dau_moc_bo_qua_tin_con_trong_hang_doi()
    {
        // Luật trên phải được chặn CẢ trong SQL: cập nhật hàng loạt không đọc từng dòng ra để hỏi
        // ChatRules được.
        var repo = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatRepository.cs");
        var i = repo.IndexOf("DanhDauMocAsync", StringComparison.Ordinal);
        Assert.True(i > 0, "chưa có DanhDauMocAsync");
        var than = repo.Substring(i, Math.Min(900, repo.Length - i));
        Assert.Contains("state > 0", than);
    }

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

    [Fact]
    public void Zalo_bao_da_xem_thi_sinh_moc_co_thoi_diem()
    {
        var adapter = ChatSchemaGuardTests.DocFile("Services/Chat/Channels/ZaloChatAdapter.cs");
        // Mốc phải mang THỜI ĐIỂM: nền tảng báo kiểu "mọi tin trước lúc này đã đọc". Không có mốc
        // thì hoặc đánh dấu cả hội thoại (sai), hoặc không đánh dấu gì.
        Assert.Contains("Moc: new(ChatState.DaXem", adapter);
        Assert.DoesNotContain("SeenMarker", adapter);
    }

    [Fact]
    public void Moc_khong_con_bi_vut_di()
    {
        // Trước đây: `if (e.SeenMarker is not null) return;` — bóc ra rồi bỏ.
        var svc = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatInboundService.cs");
        Assert.Contains("DanhDauMocAsync", svc);
        Assert.DoesNotContain("SeenMarker", svc);
    }

    [Fact]
    public void Danh_dau_moc_chi_dung_cho_tin_MINH_gui()
    {
        // "Khách đã xem" nói về tin CỦA MÌNH. Quên kẹp direction thì tin của chính khách cũng bị
        // đánh dấu, vô nghĩa và làm hỏng bộ đếm chưa đọc.
        var repo = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatRepository.cs");
        var i = repo.IndexOf("DanhDauMocAsync", StringComparison.Ordinal);
        Assert.True(i > 0, "chưa có DanhDauMocAsync");
        var than = repo.Substring(i, Math.Min(900, repo.Length - i));
        Assert.Contains("direction = 1", than);
        Assert.Contains("created_utc <=", than);
    }

    [Fact]
    public void Messenger_boc_duoc_delivery_va_read()
    {
        var src = ChatSchemaGuardTests.DocFile("Services/Chat/Channels/MessengerChatAdapter.cs");
        Assert.Contains("\"delivery\"", src);
        Assert.Contains("\"read\"", src);
        Assert.Contains("watermark", src);
        // Không được còn dòng bỏ qua cả cụm delivery/read như trước.
        Assert.DoesNotContain("delivery/read/postback — chưa dùng ở đợt này", src);
    }

    [Fact]
    public void Telegram_khong_duoc_tu_nhay_trang_thai()
    {
        // Bot API không có báo đã nhận/đã xem. Tự đặt DaNhan khi gửi xong là NÓI DỐI nhân viên —
        // họ sẽ tưởng khách đã nhận trong khi mình không hề biết.
        var src = ChatSchemaGuardTests.DocFile("Services/Chat/Channels/TelegramChatAdapter.cs");
        Assert.DoesNotContain("ChatState.DaNhan", src);
        Assert.DoesNotContain("ChatState.DaXem", src);
        // Phải có chú thích giải thích, không thì người sau tưởng là thiếu sót rồi "sửa".
        // So KHÔNG phân biệt hoa thường: chú thích viết hoa để nhấn mạnh ("KHÔNG báo") vẫn là
        // lời giải thích hợp lệ — thứ cần canh là có giải thích, không phải kiểu chữ.
        Assert.Contains("không báo", src, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Giao_dien_noi_ro_kenh_nao_khong_bao_lai()
    {
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.Contains("kênh này không báo", jsx);
    }
}
