using Microsoft.Extensions.Logging.Abstractions;
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
///
/// <para><b>Kẹp thứ hai: người xem.</b> Cùng công ty, cùng lý do — không kẹp trong bus thì một lần
/// quên ở endpoint là lộ mã hội thoại + nhịp hoạt động của hội thoại người khác đang phụ trách.</para>
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
            await foreach (var e in bus.SubscribeAsync("cong-ty-A", NguoiXem.HeThong, huy.Token))
            {
                nhan.Add(e);
                if (nhan.Count == 1) break;
            }
        });

        // Chờ người nghe đăng ký xong. Bắn trước khi đăng ký thì sự kiện rơi vào hư không —
        // đúng như thiết kế (bắn là bỏ), nhưng test sẽ treo tới lúc hết giờ.
        await ChoDangKyAsync(bus, huy.Token);

        bus.Publish(new("cong-ty-B", 1, "tin-moi", 10));   // KHÔNG được thấy
        bus.Publish(new("cong-ty-A", 2, "tin-moi", 20));
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
        bus.Publish(new("cong-ty-A", 1, "tin-moi", 1));
    }

    [Fact]
    public async Task Huy_thi_go_nguoi_nghe_ra_khoi_danh_sach()
    {
        // Tab đóng mà người nghe còn nằm lại là rò rỉ: mỗi lần mở hộp thư thêm một hàng đợi 100 sự
        // kiện không ai đọc, và Publish() phải duyệt qua tất cả.
        var bus = new ChatEventBus();
        var huy = new CancellationTokenSource();

        var doc = Task.Run(async () =>
        {
            await foreach (var _ in bus.SubscribeAsync("cong-ty-A", NguoiXem.HeThong, huy.Token)) { }
        });

        using (var chờ = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await ChoDangKyAsync(bus, chờ.Token);
        Assert.Equal(1, bus.SubscriberCount);

        huy.Cancel();
        try { await doc; } catch (OperationCanceledException) { }
        huy.Dispose();

        Assert.Equal(0, bus.SubscriberCount);
    }

    [Fact]
    public async Task Hai_tab_cung_cong_ty_deu_nhan_duoc()
    {
        // Một nhân viên mở hai tab, hoặc hai nhân viên cùng công ty — sự kiện phải tới CẢ HAI.
        var bus = new ChatEventBus();
        using var huy = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task<ChatEvent> MotTabAsync()
        {
            await foreach (var e in bus.SubscribeAsync("cong-ty-A", NguoiXem.HeThong, huy.Token)) return e;
            throw new InvalidOperationException("luồng đóng trước khi có sự kiện");
        }

        var t1 = Task.Run(MotTabAsync);
        var t2 = Task.Run(MotTabAsync);
        await ChoDangKyAsync(bus, huy.Token, 2);

        bus.Publish(new("cong-ty-A", 7, "doi-trang-thai", 70));

        Assert.Equal(7, (await t1).ConversationId);
        Assert.Equal(7, (await t2).ConversationId);
    }

    [Fact]
    public void Khong_co_Redis_van_chay_nhu_cu()
    {
        // Redis là TUỲ CHỌN. Thiếu nó thì bus vẫn phải chạy trong một instance — không được ném
        // lúc khởi động, vì máy dev và VPS nhỏ thường không cắm Redis.
        var bus = new ChatEventBus(null);
        bus.Publish(new("cong-ty-A", 1, "tin-moi", 1));
        Assert.False(bus.MultiInstance);
    }

    [Fact]
    public async Task Su_kien_tu_instance_khac_van_toi_duoc_nguoi_nghe()
    {
        // Đây là đường Redis đi vào: gói tin từ instance khác được bóc ra rồi đổ vào đúng người
        // nghe nội bộ. Không có Redis thật trong test nên gọi thẳng cửa vào đó.
        var bus = new ChatEventBus(null);
        using var huy = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeAsync("cong-ty-A", NguoiXem.HeThong, huy.Token)) return e;
            throw new InvalidOperationException("luồng đóng trước khi có sự kiện");
        });
        await ChoDangKyAsync(bus, huy.Token);

        bus.FromRemote(ChatEventBus.Pack("instance-khac", new("cong-ty-A", 9, "tin-moi", 90)));
        Assert.Equal(9, (await doc).ConversationId);
    }

    [Fact]
    public async Task AssignedUserId_song_sot_qua_Pack_Unpack_va_van_bi_kep_dung()
    {
        // Đường Redis đi vào KHÔNG được làm rớt AssignedUserId — rớt là instance khác lọc sai,
        // triệu chứng "thỉnh thoảng thấy sự kiện lạ" chỉ hiện ra khi đã cắm Redis thật trên prod.
        var bus = new ChatEventBus(null);
        using var huy = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeAsync("cong-ty-A", new NguoiXem(7, XemTatCa: false), huy.Token))
                return e;
            throw new InvalidOperationException("luồng đóng trước khi có sự kiện");
        });
        await ChoDangKyAsync(bus, huy.Token);

        // Gói từ instance khác, mang AssignedUserId=7 — phải sống sót qua Pack (tuần tự hoá) rồi
        // Unpack (FromRemote) và vẫn khớp đúng người đang nghe.
        bus.FromRemote(ChatEventBus.Pack("instance-khac",
            new ChatEvent("cong-ty-A", 5, "tin-moi", null) { AssignedUserId = 7 }));

        var e = await doc;
        Assert.Equal(5, e.ConversationId);
        Assert.Equal(7, e.AssignedUserId);
    }

    [Fact]
    public async Task Bo_qua_goi_tin_do_CHINH_MINH_phat()
    {
        // Redis trả gói tin về cho cả người phát. Không lọc thì mỗi sự kiện tới người nghe HAI
        // lần — giao diện tải lại gấp đôi, và lỗi kiểu đó chỉ lộ ra khi đã cắm Redis trên prod.
        var bus = new ChatEventBus(null);
        using var huy = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var nhan = new List<ChatEvent>();
        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeAsync("cong-ty-A", NguoiXem.HeThong, huy.Token))
            {
                nhan.Add(e);
                if (nhan.Count == 1) break;
            }
        });
        await ChoDangKyAsync(bus, huy.Token);

        bus.FromRemote(ChatEventBus.Pack(bus.InstanceId, new("cong-ty-A", 1, "tin-moi", 1)));   // của mình → bỏ
        bus.FromRemote(ChatEventBus.Pack("instance-khac", new("cong-ty-A", 2, "tin-moi", 2)));
        await doc;

        Assert.Single(nhan);
        Assert.Equal(2, nhan[0].ConversationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("khong-phai-json")]
    [InlineData("{}")]
    [InlineData("{\"tuAi\":\"x\"}")]
    public void Goi_tin_hong_thi_bo_qua_chu_khong_nem(string thô)
    {
        // Gói tin tới từ MẠNG: phiên bản cũ còn trong Redis, hoặc ai đó publish nhầm kênh. Ném ở
        // đây là chết luồng đăng ký, và từ đó instance này câm hẳn mà không ai biết.
        var bus = new ChatEventBus(null);
        bus.FromRemote(thô);
    }

    [Fact]
    public async Task Nguoi_bi_gioi_han_khong_nhan_su_kien_cua_hoi_thoai_nguoi_khac()
    {
        // Không kẹp ở đây thì nhân viên vẫn nhận chuông báo tin mới của hội thoại họ bấm vào
        // ra 404: vừa lộ đang có việc xảy ra, vừa trông như app hỏng.
        var bus = new ChatEventBus(null, NullLogger<ChatEventBus>.Instance);
        var nhan = new List<ChatEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeAsync("t1", new NguoiXem(7, XemTatCa: false), cts.Token))
                nhan.Add(e);
        });
        // Chờ đăng ký xong trước khi bắn — xem ChoDangKyAsync bên dưới: bắn trước khi đăng ký thì
        // sự kiện rơi vào hư không, KHÁC với cái bus đang canh (ai được thấy), nên phải chờ trước.
        await ChoDangKyAsync(bus, cts.Token);

        bus.Publish(new ChatEvent("t1", 100, "tin-moi", null) { AssignedUserId = 9 });
        bus.Publish(new ChatEvent("t1", 101, "tin-moi", null) { AssignedUserId = 7 });
        await Task.Delay(200);
        cts.Cancel();
        try { await doc; } catch (OperationCanceledException) { }

        Assert.Single(nhan);
        Assert.Equal(101, nhan[0].ConversationId);
    }

    [Fact]
    public async Task Admin_nhan_moi_su_kien_cua_cong_ty_minh()
    {
        var bus = new ChatEventBus(null, NullLogger<ChatEventBus>.Instance);
        var nhan = new List<ChatEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeAsync("t1", NguoiXem.HeThong, cts.Token))
                nhan.Add(e);
        });
        await ChoDangKyAsync(bus, cts.Token);

        bus.Publish(new ChatEvent("t1", 100, "tin-moi", null) { AssignedUserId = 9 });
        bus.Publish(new ChatEvent("t1", 101, "tin-moi", null) { AssignedUserId = null });
        await Task.Delay(200);
        cts.Cancel();
        try { await doc; } catch (OperationCanceledException) { }

        Assert.Equal(2, nhan.Count);
    }

    /// Chờ tới khi đủ số người nghe đã đăng ký. Ngủ một khoảng cố định thì test lúc xanh lúc đỏ
    /// trên máy chạy chậm — thứ tệ hơn cả không có test, vì người sau sẽ chạy lại cho tới khi xanh.
    private static async Task ChoDangKyAsync(ChatEventBus bus, CancellationToken ct, int can = 1)
    {
        while (bus.SubscriberCount < can)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }
}
