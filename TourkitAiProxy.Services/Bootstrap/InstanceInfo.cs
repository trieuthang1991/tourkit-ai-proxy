using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Services.Storage;

namespace TourkitAiProxy.Services.Bootstrap;

/// <summary>
/// Bản tự khai của MỘT tiến trình: nó là ai, nó đang chạy tác vụ nền nào, nó ghi tệp vào đâu.
///
/// <para><b>Vì sao cần.</b> Hệ này chạy nhiều tiến trình cùng lúc — web trên máy chủ thật, worker
/// riêng (<c>TourkitAiProxy.Worker</c>), và máy dev của lập trình viên — mà cả ba <b>dùng chung
/// một CSDL</b>. Cấu hình thì nằm ở <c>appsettings.json</c>, vốn <b>gitignore và riêng từng máy</b>.
/// Nên hai tiến trình chạy cùng một mã hoàn toàn có thể cư xử khác nhau, và không có chỗ nào
/// nhìn ra được điều đó.</para>
///
/// <para>Đã trả giá hai lần trong một ngày (28/08/2026):</para>
/// <list type="number">
///   <item>Máy dev nâng cấp schema của CSDL production, còn bản đang chạy ngoài đó thì dùng mã cũ
///     — mọi tin của khách biến mất trong im lặng suốt nửa ngày.</item>
///   <item>Ảnh nhân viên gửi lưu vào đĩa máy chủ thay vì kho R2, chỉ vì
///     <c>appsettings.json</c> bên đó thiếu khối <c>Storage:R2</c>. Không log, không cảnh báo —
///     phát hiện bằng cách dò tay từng dòng trong CSDL.</item>
/// </list>
///
/// <para>Cả hai đều tan biến nếu mỗi tiến trình <b>nói to nó là ai</b> lúc khởi động, và trả lời
/// được câu đó khi bị hỏi qua <c>/healthz</c>.</para>
/// </summary>
public static class InstanceInfo
{
    /// <summary>Tên gọi một tiến trình: tên máy + số hiệu. Đủ để phân biệt khi đọc log gộp.</summary>
    public static string Ten => $"{Environment.MachineName}#{Environment.ProcessId}";

    /// <param name="Scheduler">Có chạy bộ hẹn giờ tác vụ tự động không (<c>Workflows:RunScheduler</c>).</param>
    /// <param name="ChatWorkers">Có chạy ba worker của hộp thư chat không.</param>
    /// <param name="Storage">Kho tệp đang dùng thật: <c>r2</c> · <c>s3</c> · <c>local</c>.</param>
    /// <param name="StorageBase">Đoạn đầu url của kho — nhìn là biết tệp đi đâu.</param>
    public record TrangThai(string Instance, bool Scheduler, bool ChatWorkers,
        string Storage, string? StorageBase);

    public static TrangThai Doc(IConfiguration cfg, IChatFileStorage kho, bool chatWorkers = true)
        => new(Ten,
               cfg.GetValue("Workflows:RunScheduler", false),
               chatWorkers,
               kho.Provider,
               kho.PublicBase);

    /// <summary>
    /// Một dòng log lúc khởi động. Cố ý ghi cả thứ "bình thường" chứ không chỉ thứ bất thường:
    /// giá trị của nó nằm ở chỗ <b>so hai tiến trình với nhau</b>, mà muốn so thì cả hai phải nói.
    /// </summary>
    public static string MotDong(TrangThai t)
        => $"[instance] {t.Instance} · scheduler={(t.Scheduler ? "CÓ" : "không")} "
         + $"· worker-chat={(t.ChatWorkers ? "CÓ" : "không")} "
         + $"· kho tệp={t.Storage}{(t.StorageBase is { Length: > 0 } b ? $" → {b}" : "")}";

    /// <summary>
    /// Kho <c>local</c> có đáng cảnh báo không.
    ///
    /// <para><b>Có</b>, và không phải vì nó hỏng — url <c>/chat-files/…</c> vẫn tải được. Vấn đề là
    /// tệp nằm trên ĐĨA của chính máy chủ ứng dụng: mỗi lần deploy là một lần có thể mất sạch
    /// (robocopy <c>/MIR</c> xoá thứ không có trong bản publish), mà đường dẫn thì đã ghi vĩnh viễn
    /// vào CSDL — mất là mọi tệp nhân viên từng gửi thành liên kết gãy, không có đường dựng lại.</para>
    /// </summary>
    public static bool DangLo(TrangThai t) => t.Storage == "local";
}
