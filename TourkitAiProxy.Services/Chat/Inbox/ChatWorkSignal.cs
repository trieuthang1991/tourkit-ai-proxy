// Services/Chat/Inbox/ChatWorkSignal.cs
namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>Hai hàng việc của hộp thư chat, mỗi cái một tín hiệu riêng.</summary>
public enum ChatLane
{
    /// Tin KHÁCH gửi tới, webhook vừa ghi thân thô.
    In = 0,

    /// Tin MÌNH gửi đi, vừa xếp vào hàng đợi.
    Out = 1,
}

/// <summary>
/// Đánh thức worker ngay khi có việc, thay vì để nó ngủ hết nhịp rồi mới dậy hỏi cơ sở dữ liệu.
///
/// <para><b>Vì sao cần.</b> Hai worker vốn chạy theo nhịp cố định (vào 2 giây, ra 5 giây). Nghĩa là
/// một lượt qua lại với khách <b>đứng chờ tới 7 giây trong hàng đợi</b> mà không làm gì — nhân viên
/// bấm gửi, màn hình mình hiện ngay, còn khách thì mãi mới nhận. Đó là toàn bộ cảm giác "gửi được
/// nhưng không mượt".</para>
///
/// <para><b>Không phải đổi sang SignalR.</b> Đường đẩy xuống trình duyệt (SSE) vốn đã tức thì; nút
/// cổ chai nằm ở nhịp ngủ của worker. Đổi giao thức đẩy không chạm được vào chỗ này.</para>
///
/// <para><b>Nhịp cũ VẪN GIỮ làm lưới an toàn</b>, không bỏ. Tín hiệu chỉ là đường nhanh:
/// <list type="bullet">
/// <item>Chạy nhiều máy chủ sau bộ cân tải thì tín hiệu chỉ đánh thức worker <b>cùng tiến trình</b>;
/// việc do máy khác ghi vào vẫn phải đợi nhịp. Bỏ nhịp là những việc đó nằm lại vĩnh viễn.</item>
/// <item>Sót một lượt gọi <see cref="Danh"/> ở đâu đó thì chậm một nhịp, chứ không mất tin.</item>
/// <item>Việc tới hạn <b>thử lại</b> không do ai đánh thức cả — chỉ nhịp mới nhặt lên.</item>
/// </list></para>
/// </summary>
public class ChatWorkSignal
{
    // Mỗi làn một semaphore đếm từ 0: Cho() chặn cho tới khi có ai Release, hoặc hết hạn chờ.
    private readonly SemaphoreSlim[] _co =
    {
        new(0, 1),
        new(0, 1),
    };

    /// <summary>
    /// Báo "có việc mới" cho một làn. Gọi được từ bất kỳ luồng nào, <b>không bao giờ chặn</b> —
    /// đường webhook và đường gửi tin không được phép chờ ai.
    /// </summary>
    /// <remarks>
    /// Trần 1: tín hiệu là "có việc", không phải "có bao nhiêu việc". Worker dậy một lần rồi vét
    /// sạch hàng đợi, nên đếm 100 lần cũng thừa 99 — mà đếm dồn thì worker quay không tải 99 vòng.
    /// Đầy sẵn thì <see cref="SemaphoreSlim.Release()"/> ném, nên nuốt luôn.
    /// </remarks>
    public void Signal(ChatLane lan)
    {
        try { _co[(int)lan].Release(); }
        catch (SemaphoreFullException) { /* đã có tín hiệu chờ sẵn — đúng ý */ }
    }

    /// <summary>
    /// Chờ tín hiệu, nhiều nhất <paramref name="toiDa"/>. Trả <c>true</c> khi được đánh thức,
    /// <c>false</c> khi hết hạn chờ. Cả hai đều dẫn tới một nhịp làm việc — giá trị trả về chỉ để
    /// ghi log và đo.
    /// </summary>
    public async Task<bool> WaitAsync(ChatLane lan, TimeSpan toiDa, CancellationToken ct)
    {
        try { return await _co[(int)lan].WaitAsync(toiDa, ct); }
        catch (OperationCanceledException) { return false; }
    }
}
