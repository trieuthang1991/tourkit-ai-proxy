namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Lớp NHÃN cho <c>ILogger&lt;&gt;</c> của tầng endpoint. Không có thành viên nào, không khởi tạo
/// được — chỉ tồn tại để đặt tên nhóm log.
///
/// <para><b>Vì sao phải có.</b> Trước đây 14 file endpoint dùng <c>ILogger&lt;Program&gt;</c>.
/// Sau khi tách project, <c>Program</c> nằm ở tầng Api nên endpoint không thấy nữa — đó là một
/// phụ thuộc NGƯỢC (Endpoints → Api) mà suốt thời gian chung một assembly không ai nhìn ra.
/// Ranh giới thật làm nó lộ ngay lúc biên dịch.</para>
///
/// <para>Dùng chính lớp endpoint làm nhãn thì đẹp hơn, nhưng chúng đều là <c>static class</c> mà
/// C# không cho <c>static</c> làm tham số kiểu. Một nhãn chung là đủ, và <b>không tệ hơn hiện
/// trạng</b>: <c>ILogger&lt;Program&gt;</c> vốn cũng gộp tất cả vào một nhóm.</para>
/// </summary>
internal sealed class EndpointsLog
{
    private EndpointsLog() { }
}
