using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Đánh thức worker ngay khi hàng đợi có việc.
///
/// <para>Trước 26/08/2026 hai worker chỉ chạy theo nhịp cố định (vào 2 giây, ra 5 giây), nên một
/// lượt qua lại với khách <b>đứng chờ tới 7 giây trong hàng đợi</b>. Nhân viên không nhận ra vì màn
/// hình của mình hiện tin ngay khi bấm gửi — chỉ khách là chờ thật.</para>
/// </summary>
public class ChatWorkSignalTests
{
    [Fact]
    public async Task Co_tin_hieu_thi_khong_phai_cho_het_nhip()
    {
        // Cốt lõi của cả bản sửa: đánh trước thì lượt chờ trả về NGAY, không ăn hết hạn chờ.
        var tin = new ChatWorkSignal();
        tin.Danh(ChatLan.Ra);

        var dh = System.Diagnostics.Stopwatch.StartNew();
        var duocDanhThuc = await tin.ChoAsync(ChatLan.Ra, TimeSpan.FromSeconds(5), default);
        dh.Stop();

        Assert.True(duocDanhThuc);
        Assert.True(dh.ElapsedMilliseconds < 500, $"Chờ mất {dh.ElapsedMilliseconds}ms, đáng lẽ tức thì");
    }

    [Fact]
    public async Task Khong_co_tin_hieu_thi_van_chay_theo_nhip()
    {
        // Nhịp là LƯỚI AN TOÀN, không bỏ: chạy nhiều máy chủ thì tín hiệu chỉ đánh thức worker cùng
        // tiến trình, và việc tới hạn thử lại thì không ai đánh thức cả.
        var tin = new ChatWorkSignal();
        Assert.False(await tin.ChoAsync(ChatLan.Vao, TimeSpan.FromMilliseconds(80), default));
    }

    [Fact]
    public async Task Hai_lan_KHONG_danh_thuc_lan_nhau()
    {
        // Tin khách gửi tới và tin mình gửi đi là hai worker riêng. Dùng chung một tín hiệu thì mỗi
        // lượt webhook lại đánh thức cả worker gửi — quay không tải, và ngược lại.
        var tin = new ChatWorkSignal();
        tin.Danh(ChatLan.Vao);

        Assert.False(await tin.ChoAsync(ChatLan.Ra, TimeSpan.FromMilliseconds(80), default));
        Assert.True(await tin.ChoAsync(ChatLan.Vao, TimeSpan.FromMilliseconds(80), default));
    }

    [Fact]
    public async Task Danh_don_dap_KHONG_lam_worker_quay_khong_tai()
    {
        // Tín hiệu là "có việc", không phải "có bao nhiêu việc": worker dậy MỘT lần rồi vét sạch
        // hàng đợi. Đếm dồn thì một đợt 100 tin làm worker quay 99 vòng không có gì để làm.
        var tin = new ChatWorkSignal();
        for (var i = 0; i < 100; i++) tin.Danh(ChatLan.Vao);

        Assert.True(await tin.ChoAsync(ChatLan.Vao, TimeSpan.FromMilliseconds(80), default));
        Assert.False(await tin.ChoAsync(ChatLan.Vao, TimeSpan.FromMilliseconds(80), default));
    }

    [Fact]
    public void Danh_khong_bao_gio_nem()
    {
        // Gọi từ đường webhook và đường gửi tin. Ném ở đó là một tin của khách rơi mất, chỉ vì một
        // chi tiết nội bộ của hàng đợi.
        var tin = new ChatWorkSignal();
        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 1000; i++) { tin.Danh(ChatLan.Vao); tin.Danh(ChatLan.Ra); }
        });
        Assert.Null(ex);
    }

    // ── Canh chỗ dễ quên ────────────────────────────────────────────────────

    [Fact]
    public void Moi_cho_xep_hang_doi_deu_danh_thuc_worker()
    {
        // Quên một chỗ thì tin ở đó chậm một nhịp mà KHÔNG có dấu hiệu gì: không lỗi, không log,
        // chỉ "thỉnh thoảng hơi chậm" — thứ gần như không ai lần ra được.
        foreach (var (tep, ham, lan) in new[]
        {
            ("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs", "EnqueueInboundAsync", "ChatLan.Vao"),
            ("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs", "EnqueueOutboxAsync", "ChatLan.Ra"),
            ("TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs", "EnqueueOutboxAsync", "ChatLan.Ra"),
        })
        {
            var src = ChatSchemaGuardTests.DocFile(tep);
            var tu = 0;
            while (true)
            {
                var i = src.IndexOf(ham, tu, StringComparison.Ordinal);
                if (i < 0) break;
                tu = i + ham.Length;

                // Đánh thức phải nằm ngay sau lượt xếp hàng, trong vòng vài dòng.
                var sau = src.Substring(i, Math.Min(400, src.Length - i));
                Assert.True(sau.Contains("Danh(") && sau.Contains(lan),
                    $"{tep}: gọi {ham} mà không đánh thức {lan} ngay sau đó.");
            }
            Assert.True(tu > 0, $"Không thấy {ham} trong {tep} — test này đã lạc hậu.");
        }
    }

    [Fact]
    public void Worker_van_giu_nhip_lam_luoi_an_toan()
    {
        // Thay nhịp bằng "chỉ chờ tín hiệu" là việc do máy chủ KHÁC ghi vào sẽ nằm lại vĩnh viễn,
        // và việc tới hạn thử lại không bao giờ được nhặt lên.
        foreach (var tep in new[]
        {
            "TourkitAiProxy.Services/Chat/Inbox/ChatInboundWorker.cs",
            "TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs",
        })
        {
            var src = ChatSchemaGuardTests.DocFile(tep);
            Assert.Contains("ChoAsync(", src);
            Assert.Contains("Nhip", src);
        }
    }
}
