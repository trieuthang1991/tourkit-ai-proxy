using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Digest.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// Khoá bất biến quan trọng nhất của bộ phát: MỘT KÊNH HỎNG KHÔNG ĐƯỢC LÀM CHẾT CÁC KÊNH CÒN LẠI.
/// Telegram sập thì bản tin vẫn phải vào được app và email.
public class DigestDispatcherTests
{
    private sealed class FakeChannel : IDigestChannel
    {
        public string Id { get; init; } = "fake";
        public bool Configured = true;
        public bool Result = true;
        public bool Throws = false;
        public int Calls;

        public bool IsConfigured(DigestSubscription sub) => Configured;

        public Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
        {
            Calls++;
            if (Throws) throw new InvalidOperationException("boom");
            return Task.FromResult(Result);
        }
    }

    private static DigestSubscription Sub() => new("t", "u", BriefTypes.Sale, true, 7,
        true, false, null, true, "123", false, null, null, null);

    private static DigestMessage Msg() => new("Tiêu đề", "body", "<p>body</p>", BriefTypes.Sale);

    private static DigestDispatcher Make(params IDigestChannel[] ch)
        => new(ch, NullLogger<DigestDispatcher>.Instance);

    [Fact]
    public async Task Kenh_loi_khong_chan_kenh_khac()
    {
        var a = new FakeChannel { Id = "a", Throws = true };
        var b = new FakeChannel { Id = "b" };
        var summary = (await Make(a, b).SendAsync(Sub(), Msg(), CancellationToken.None)).Summary;
        Assert.Equal(1, b.Calls);
        Assert.Contains("a:FAIL", summary);
        Assert.Contains("b:ok", summary);
    }

    [Fact]
    public async Task Kenh_chua_cau_hinh_bi_skip()
    {
        var a = new FakeChannel { Id = "a", Configured = false };
        var summary = (await Make(a).SendAsync(Sub(), Msg(), CancellationToken.None)).Summary;
        Assert.Equal(0, a.Calls);
        Assert.Contains("a:skip", summary);
    }

    // ── Ca thêm ngoài plan ────────────────────────────────────────────────────

    [Fact]
    public async Task Kenh_tra_false_ghi_FAIL_chu_khong_im_lang()
    {
        // Trả false (gửi hỏng nhưng không ném) phải hiện ra summary, nếu không thì mất dấu.
        var a = new FakeChannel { Id = "a", Result = false };
        var summary = (await Make(a).SendAsync(Sub(), Msg(), CancellationToken.None)).Summary;
        Assert.Contains("a:FAIL", summary);
    }

    [Fact]
    public async Task Kenh_dau_nem_thi_kenh_sau_VAN_chay()
    {
        // Thứ tự quan trọng: kênh ném nằm TRƯỚC. Nếu dispatcher không bọc try thì b không bao giờ chạy.
        var a = new FakeChannel { Id = "a", Throws = true };
        var b = new FakeChannel { Id = "b" };
        var c = new FakeChannel { Id = "c", Throws = true };
        var d = new FakeChannel { Id = "d" };
        var summary = (await Make(a, b, c, d).SendAsync(Sub(), Msg(), CancellationToken.None)).Summary;
        Assert.Equal(1, b.Calls);
        Assert.Equal(1, d.Calls);
        Assert.Contains("a:FAIL", summary);
        Assert.Contains("c:FAIL", summary);
    }

    [Fact]
    public async Task Khong_co_kenh_nao_van_tra_summary_chu_khong_no()
    {
        var summary = (await Make().SendAsync(Sub(), Msg(), CancellationToken.None)).Summary;
        Assert.NotNull(summary);
    }

    [Fact]
    public async Task Summary_co_du_moi_kenh_de_doc_o_lich_su_chay()
    {
        var a = new FakeChannel { Id = "inapp" };
        var b = new FakeChannel { Id = "email", Result = false };
        var c = new FakeChannel { Id = "telegram", Configured = false };
        var summary = (await Make(a, b, c).SendAsync(Sub(), Msg(), CancellationToken.None)).Summary;
        Assert.Contains("inapp:ok", summary);
        Assert.Contains("email:FAIL", summary);
        Assert.Contains("telegram:skip", summary);
    }
    // ── Danh sách kênh gửi được ───────────────────────────────────────────────

    [Fact]
    public async Task Chi_liet_ke_kenh_gui_DUOC()
    {
        var a = new FakeChannel { Id = "email", Result = false };
        var b = new FakeChannel { Id = "telegram" };
        var res = await Make(a, b).SendAsync(Sub(), Msg(), CancellationToken.None);

        // email hỏng thì KHÔNG được kể vào — kể bừa là người dùng tưởng đã nhận rồi.
        Assert.Equal(new[] { "telegram" }, res.SentChannels);
    }

    [Fact]
    public async Task Kenh_la_van_duoc_ghi_nhan_theo_dung_id_cua_no()
    {
        // Không còn bảng tra id→cờ bit, nên id lạ không còn bị nuốt mất như trước.
        var a = new FakeChannel { Id = "kenh-moi-chua-khai-bao" };
        var res = await Make(a).SendAsync(Sub(), Msg(), CancellationToken.None);
        Assert.Equal(new[] { "kenh-moi-chua-khai-bao" }, res.SentChannels);
        Assert.Contains("kenh-moi-chua-khai-bao:ok", res.Summary);
    }

    [Fact]
    public async Task Khong_gui_duoc_kenh_nao_thi_danh_sach_rong()
    {
        var a = new FakeChannel { Id = "email", Result = false };
        var b = new FakeChannel { Id = "telegram", Throws = true };
        var res = await Make(a, b).SendAsync(Sub(), Msg(), CancellationToken.None);
        Assert.Empty(res.SentChannels);
    }

    [Fact]
    public async Task Moi_kenh_da_cau_hinh_deu_duoc_gui_khong_con_gioi_han_nao()
    {
        // Bản cũ có tham số onlyMask để "chỉ thử lại kênh còn thiếu". Đã bỏ: bộ phát nay chỉ phục vụ
        // Gửi thử — người dùng bấm nút là muốn thử HẾT các kênh mình đã khai.
        var a = new FakeChannel { Id = "email" };
        var b = new FakeChannel { Id = "telegram" };
        var res = await Make(a, b).SendAsync(Sub(), Msg(), CancellationToken.None);
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
        Assert.Equal(new[] { "email", "telegram" }, res.SentChannels);
    }
}
