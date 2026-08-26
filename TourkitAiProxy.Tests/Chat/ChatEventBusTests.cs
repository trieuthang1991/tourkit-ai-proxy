using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Kênh đẩy sự kiện tới các tab đang mở hộp thư.
///
/// <para><b>Việc quan trọng nhất ở đây là kẹp tenant.</b> Bus gửi sự kiện của công ty nào cho đúng
/// người nghe của công ty đó — lọc ở endpoint thì một lần quên là hộp thư công ty này thấy tin của
/// công ty khác.</para>
/// </summary>
public class ChatEventBusTests
{
    [Fact]
    public async Task Nghe_dung_tenant_cua_minh()
    {
        var bus = new ChatEventBus();
        using var huy = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var nhan = new List<ChatEvent>();

        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.NgheAsync("cong-ty-A", huy.Token))
            {
                nhan.Add(e);
                if (nhan.Count == 1) break;
            }
        });

        // Chờ người nghe đăng ký xong. Bắn trước khi đăng ký thì sự kiện rơi vào hư không —
        // đúng như thiết kế (bắn là bỏ), nhưng test sẽ treo tới lúc hết giờ.
        await ChoDangKyAsync(bus, huy.Token);

        bus.Bao(new("cong-ty-B", 1, "tin-moi", 10));   // KHÔNG được thấy
        bus.Bao(new("cong-ty-A", 2, "tin-moi", 20));
        await doc;

        Assert.Single(nhan);
        Assert.Equal("cong-ty-A", nhan[0].TenantId);
        Assert.Equal(2, nhan[0].ConversationId);
    }

    [Fact]
    public void Bao_khi_khong_ai_nghe_thi_khong_nem()
    {
        // Webhook chạy nền, không ai mở hộp thư là chuyện bình thường — ném ở đây là chết luồng xử lý
        // tin của khách chỉ vì không có ai đang nhìn màn hình.
        var bus = new ChatEventBus();
        bus.Bao(new("cong-ty-A", 1, "tin-moi", 1));
    }

    [Fact]
    public async Task Huy_thi_go_nguoi_nghe_ra_khoi_danh_sach()
    {
        // Tab đóng mà người nghe còn nằm lại là rò rỉ: mỗi lần mở hộp thư thêm một hàng đợi 100 sự
        // kiện không ai đọc, và Bao() phải duyệt qua tất cả.
        var bus = new ChatEventBus();
        var huy = new CancellationTokenSource();

        var doc = Task.Run(async () =>
        {
            await foreach (var _ in bus.NgheAsync("cong-ty-A", huy.Token)) { }
        });

        using (var chờ = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await ChoDangKyAsync(bus, chờ.Token);
        Assert.Equal(1, bus.SoNguoiNghe);

        huy.Cancel();
        try { await doc; } catch (OperationCanceledException) { }
        huy.Dispose();

        Assert.Equal(0, bus.SoNguoiNghe);
    }

    [Fact]
    public async Task Hai_tab_cung_cong_ty_deu_nhan_duoc()
    {
        // Một nhân viên mở hai tab, hoặc hai nhân viên cùng công ty — sự kiện phải tới CẢ HAI.
        var bus = new ChatEventBus();
        using var huy = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task<ChatEvent> MotTabAsync()
        {
            await foreach (var e in bus.NgheAsync("cong-ty-A", huy.Token)) return e;
            throw new InvalidOperationException("luồng đóng trước khi có sự kiện");
        }

        var t1 = Task.Run(MotTabAsync);
        var t2 = Task.Run(MotTabAsync);
        await ChoDangKyAsync(bus, huy.Token, 2);

        bus.Bao(new("cong-ty-A", 7, "doi-trang-thai", 70));

        Assert.Equal(7, (await t1).ConversationId);
        Assert.Equal(7, (await t2).ConversationId);
    }

    /// Chờ tới khi đủ số người nghe đã đăng ký. Ngủ một khoảng cố định thì test lúc xanh lúc đỏ
    /// trên máy chạy chậm — thứ tệ hơn cả không có test, vì người sau sẽ chạy lại cho tới khi xanh.
    private static async Task ChoDangKyAsync(ChatEventBus bus, CancellationToken ct, int can = 1)
    {
        while (bus.SoNguoiNghe < can)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }
}
