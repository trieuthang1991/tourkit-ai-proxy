// Services/Chat/Inbox/ChatMediaBackfillWorker.cs
using System.Diagnostics;
using TourkitAiProxy.Infrastructure.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Soi ảnh CŨ trong hộp thư về kho riêng — ảnh trong tin và ảnh đại diện khách — chạy nền, tự
/// động, không ai phải bấm.
///
/// <para><b>Vì sao phải tự chạy.</b> Tin MỚI đã được soi ngay lúc nhận (xem
/// <see cref="ChatInboundService"/>), nhưng mọi ảnh nhận TRƯỚC hôm nay vẫn trỏ thẳng ra máy chủ
/// của kênh, mà url đó có hạn: đo trên hộp thư thật ngày 27/08/2026, ảnh Meta hết hạn
/// 01/09/2026 — năm ngày. Quá hạn là mất hẳn, không có đường lấy lại.</para>
///
/// <para>Bản đầu để người dùng tự bấm một nút trong phần cài đặt. Sai: người trực hộp thư không
/// có cách nào biết ảnh của mình đang đếm ngược, mà đến ngày hết hạn thì bấm cũng không cứu được
/// nữa. Việc cứu dữ liệu có hạn chót phải tự chạy; nút bấm chỉ hợp với việc người dùng CHỌN làm.</para>
///
/// <para><b>Nó ảnh hưởng thế nào tới trang web đang chạy — và ba chốt chặn.</b> Mỗi tệp là một
/// lượt tải về, một lượt nén ảnh (tốn CPU) rồi một lượt ghi lên kho; một hộp thư lâu năm có hàng
/// nghìn tệp. Vét nhanh nhất có thể sẽ giành băng thông và CPU với chính luồng tin đang phục vụ
/// khách, và đập vào CDN của Meta bằng hàng nghìn yêu cầu liên tiếp là cách nhanh nhất để bị chặn
/// tốc độ. Nên:</para>
/// <list type="number">
///   <item><b>Một việc một lúc.</b> Không chạy song song — đúng một lượt tải + một lượt nén tại
///     mỗi thời điểm, tức nhiều nhất một lõi CPU dù máy có bao nhiêu lõi.</item>
///   <item><b>Nghỉ bằng đúng thời gian vừa làm</b> (<see cref="NghiToiThieu"/> …
///     <see cref="NghiToiDa"/>). Tự co giãn: mẻ nào nặng — ảnh to, CDN chậm — thì nghỉ dài đúng
///     bấy nhiêu, nên phần thời gian worker này chiếm không bao giờ quá một nửa.</item>
///   <item><b>Bắt đầu muộn</b> (<see cref="ChoKhoiDong"/>): lúc máy chủ vừa lên đã đủ thứ tranh
///     nhau, thêm một việc không gấp vào đó là kéo dài thời gian trang web ì.</item>
/// </list>
///
/// <para><b>Còn khi dữ liệu nhiều lên thì sao.</b> Ba thứ giữ cho việc này không phình theo cỡ
/// hộp thư:</para>
/// <list type="bullet">
///   <item><b>Không quét cả bảng.</b> Câu hỏi đi qua chỉ mục có điều kiện <c>ix_msg_media_cho</c>
///     (xem <see cref="ChatDb"/>) — chỉ mục chỉ chứa những tin CÒN phải soi, nên chi phí tỉ lệ
///     với phần việc còn lại chứ không với số tin trong hộp thư. Vét sạch rồi thì mỗi vòng quét
///     chỉ là một lượt hỏi rỗng.</item>
///   <item><b>Tiến độ không lùi.</b> Mỗi dòng lấy ra được đánh dấu ngay trong CSDL
///     (<c>ChatRepository.ClaimMediaAsync</c>), nên ảnh đã chết hẳn rơi khỏi hàng chờ thay vì
///     được tải lại mãi mãi mỗi vòng.</item>
///   <item><b>Chỉ có nợ cũ mới phải vét.</b> Ảnh của tin mới đã soi ngay lúc nhận, nên phần việc
///     ở đây là một khối hữu hạn cứ nhỏ dần — khác hẳn một hàng đợi được nạp thêm liên tục.</item>
/// </list>
///
/// <para><b>Nhịp tự đổi theo việc:</b> còn cứu được ảnh thì quay lại sau
/// <see cref="ChuKyBan"/> để dọn nốt nợ cũ cho kịp hạn; một vòng không cứu được gì thì lùi về
/// <see cref="ChuKyRoi"/>. Nhờ vậy lúc mới bật thì nhanh, lúc hết việc thì gần như không tốn gì.</para>
/// </summary>
public class ChatMediaBackfillWorker : BackgroundService
{
    /// <summary>Đợi máy chủ lên hẳn rồi mới bắt đầu — lúc khởi động đã đủ việc tranh nhau.</summary>
    private static readonly TimeSpan ChoKhoiDong = TimeSpan.FromMinutes(1);

    /// <summary>Vòng vừa rồi CÓ cứu được ảnh → còn nợ cũ, quay lại sớm cho kịp hạn url.</summary>
    private static readonly TimeSpan ChuKyBan = TimeSpan.FromMinutes(15);

    /// <summary>Vòng vừa rồi không cứu được gì → hết việc (hoặc mạng đang hỏng), lùi lại cho rảnh máy.</summary>
    private static readonly TimeSpan ChuKyRoi = TimeSpan.FromHours(6);

    /// <summary>Nghỉ giữa hai mẻ ít nhất bấy nhiêu, kể cả khi mẻ chạy rất nhanh.</summary>
    private static readonly TimeSpan NghiToiThieu = TimeSpan.FromSeconds(2);

    /// <summary>Trần cho lần nghỉ — một mẻ kẹt mạng không được biến thành nửa tiếng đứng im.</summary>
    private static readonly TimeSpan NghiToiDa = TimeSpan.FromSeconds(30);

    /// <summary>Bao nhiêu tin/khách mỗi mẻ. Nhỏ để một mẻ hỏng cũng chỉ mất vài giây.</summary>
    private const int CoMe = 25;

    /// <summary>
    /// Chặn số mẻ mỗi vòng quét (25 × 200 = 5.000 tin). Không phải để tiết kiệm, mà để một hộp
    /// thư khổng lồ không giữ vòng lặp chạy vô tận: hết chặn thì nghỉ rồi vòng sau chạy tiếp.
    /// </summary>
    private const int MeToiDaMoiVong = 200;

    private readonly IServiceProvider _sp;
    private readonly ILogger<ChatMediaBackfillWorker> _log;

    public ChatMediaBackfillWorker(IServiceProvider sp, ILogger<ChatMediaBackfillWorker> log)
    { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(ChoKhoiDong, ct); }
        catch (OperationCanceledException) { return; }

        // Chờ schema dựng xong — cùng lý do với hai worker kia, xem ChatDb.DungSchema. Ở đây
        // ít gấp hơn (đã nghỉ một phút rồi) nhưng vẫn phải chờ: vòng vét đầu tiên đụng đúng
        // những cột mới nhất.
        try { await _sp.GetRequiredService<ChatDb>().DungSchema.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (TimeoutException) { _log.LogWarning("[chat/soi-lai] chờ dựng schema quá lâu, chạy tiếp"); }

        while (!ct.IsCancellationRequested)
        {
            var conViec = false;
            try { conViec = await OneSweepAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Không để vòng lặp chết vì một vòng hỏng — chết là ảnh ngừng được cứu trong im lặng.
                _log.LogError(ex, "[chat/soi-lai] vòng quét hỏng");
            }

            try { await Task.Delay(conViec ? ChuKyBan : ChuKyRoi, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Một vòng quét. Trả <c>true</c> nếu có cứu được ít nhất một ảnh (→ quay lại sớm).</summary>
    private async Task<bool> OneSweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ChatRepository>();
        var kho = scope.ServiceProvider.GetRequiredService<ChatMediaMirror>();
        // Chưa khai CSDL chat, hoặc chưa khai kho tệp: không có gì để làm, và cũng không có chỗ
        // nào để ghi vào. Về im lặng — đây là trạng thái bình thường của một máy chưa dùng chat.
        if (!repo.Configured || !kho.Configured) return false;

        var svc = scope.ServiceProvider.GetRequiredService<ChatInboundService>();

        // tenant = null: worker làm cho MỌI công ty trên máy chủ này, mỗi tệp vẫn vào kho của
        // công ty sở hữu tin đó.
        //
        // Hai luồng tách hẳn nhau: hai nguồn dữ liệu khác nhau, hết cái này không có nghĩa hết
        // cái kia. Ảnh đại diện ít hơn nhiều nhưng vỡ thì lộ hơn — nó hiện ở mọi dòng danh sách.
        var tep = await SweepStreamAsync((n, tran, c) => svc.BackfillMediaAsync(null, n, tran, c), ct);
        var anh = await SweepStreamAsync((n, tran, c) => svc.BackfillAvatarsAsync(null, n, tran, c), ct);

        // Chỉ ghi log khi CÓ việc: vòng rỗng mỗi sáu tiếng mà cũng ghi thì log đầy dòng vô nghĩa.
        if (tep.Examined + anh.Examined > 0)
            _log.LogInformation(
                "[chat/soi-lai] xong một vòng: tệp {SoiTep}/{XetTep}, ảnh đại diện {SoiAnh}/{XetAnh}",
                tep.Mirrored, tep.Examined, anh.Mirrored, anh.Examined);

        return tep.Mirrored + anh.Mirrored > 0;
    }

    /// <summary>
    /// Vét một luồng (tệp trong tin, hoặc ảnh đại diện) cho tới khi hết việc của vòng này.
    ///
    /// <para><b>Mỗi vòng chỉ chạy MỘT tầng thử lại.</b> Mẻ đầu không chặn tầng và trả về
    /// <c>Tier</c> = số lần đã thử thấp nhất nó gặp; từ mẻ thứ hai, con số đó thành TRẦN truyền
    /// xuống tận câu truy vấn. Nhờ vậy mỗi tin chỉ được thử đúng một lần mỗi vòng, và khoảng
    /// cách giữa hai lần thử chính là nhịp của vòng quét — một sự cố mạng năm phút không đốt
    /// sạch số lần thử của cả hộp thư.</para>
    ///
    /// <para>⚠️ <b>Trần phải chặn ở TRUY VẤN, không phải ở đây.</b> Bản đầu để vòng lặp này tự
    /// nhận ra "mẻ vừa rồi đã sang tầng trên" rồi thoát — nhưng lúc nhận ra thì mẻ đó đã tải
    /// xong, tức tin ở tầng trên vẫn ăn thêm một lượt oan. Bắt được trên dữ liệu thật
    /// (28/08/2026): một khách duy nhất trong hàng chờ mà cờ nhảy thẳng 0 → 2 trong một vòng.</para>
    ///
    /// <para>Còn đúng một chỗ chưa khít: mẻ ĐẦU của vòng có thể vắt qua ranh giới hai tầng (khi
    /// số việc còn lại ít hơn một mẻ), nên tối đa <see cref="CoMe"/> − 1 dòng ăn thêm một lượt.
    /// Chặn nốt thì phải hỏi trước "tầng thấp nhất là mấy" — thêm một lượt truy vấn cho MỌI vòng
    /// quét để cứu một ca hiếm và vô hại.</para>
    /// </summary>
    private async Task<(int Mirrored, int Examined)> SweepStreamAsync(
        Func<int, short, CancellationToken, Task<ChatInboundService.BackfillResult>> layMe,
        CancellationToken ct)
    {
        int soi = 0, xet = 0;
        var tran = ChatRepository.AnyTier;               // mẻ đầu: chưa biết tầng nào, không chặn

        for (var me = 0; me < MeToiDaMoiVong && !ct.IsCancellationRequested; me++)
        {
            var dongHo = Stopwatch.StartNew();
            var kq = await layMe(CoMe, tran, ct);
            dongHo.Stop();

            if (kq.Examined == 0) break;                 // hết việc của tầng này

            soi += kq.Mirrored; xet += kq.Examined;
            tran = kq.Tier;                              // từ đây chỉ nhận đúng tầng vừa gặp

            await NghiAsync(dongHo.Elapsed, ct);
        }

        return (soi, xet);
    }

    /// <summary>
    /// Nghỉ bằng đúng thời gian vừa làm, kẹp trong <see cref="NghiToiThieu"/>…<see cref="NghiToiDa"/>.
    /// Nuốt lệnh dừng: dừng giữa chừng là chuyện bình thường.
    /// </summary>
    private static async Task NghiAsync(TimeSpan vuaLam, CancellationToken ct)
    {
        var nghi = vuaLam < NghiToiThieu ? NghiToiThieu
                 : vuaLam > NghiToiDa ? NghiToiDa
                 : vuaLam;
        try { await Task.Delay(nghi, ct); }
        catch (OperationCanceledException) { }
    }
}
