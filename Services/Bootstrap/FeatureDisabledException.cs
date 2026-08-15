namespace TourkitAiProxy.Services.Bootstrap;

/// <summary>
/// Ném khi có người gọi tới một tính năng đang TẮT bằng cờ <see cref="FeatureFlags"/>.
///
/// <para>Có kiểu riêng để endpoint phân biệt được với lỗi thật. Trước đây chỗ này ném
/// <c>InvalidOperationException</c> nên rơi vào bộ bắt lỗi chung → trả <b>500</b>: vừa nói sai với
/// người dùng (tính năng tắt là chuyện bình thường, không phải máy chủ hỏng), vừa làm bẩn log lỗi
/// — cảnh báo giả trộn lẫn với sự cố thật thì tới lúc có sự cố thật không ai để ý.</para>
///
/// <para>Trả <b>403</b> chứ không phải 404 như cụm bản tin: ở đó cả tuyến đường không được map nên
/// 404 là đúng nghĩa; còn ở đây tuyến vẫn tồn tại và yêu cầu vẫn hợp lệ, chỉ là hành động này chưa
/// được phép chạy.</para>
/// </summary>
public class FeatureDisabledException : Exception
{
    public FeatureDisabledException(string message) : base(message) { }
}
