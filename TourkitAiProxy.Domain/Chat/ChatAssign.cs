namespace TourkitAiProxy.Domain.Chat;

/// Chế độ chia hội thoại cho nhân viên.
public static class CheDoPhanCong
{
    /// Máy không gán gì. Người mở được hội thoại thì tự nhận hoặc giao cho người khác.
    /// Theo luật xem, hội thoại chưa gán chỉ admin mở được — nên trên thực tế đây là
    /// "admin giao xuống", không phải "nhân viên tự bốc".
    public const short ThuCong = 1;

    /// Hội thoại nào chưa có người phụ trách thì gán người kế tiếp trong đội trực.
    public const short XoayVong = 2;
}

/// Cấu hình phân công của MỘT công ty. Chưa có dòng trong CSDL = giữ nguyên hành vi cũ.
///
/// <para><c>MemberIds</c> là ĐỘI TRỰC CHAT — mã những người được nhận hội thoại. Chỉ có mã:
/// tên hiển thị lấy từ danh sách nhân viên của ERP, chép vào đây là nhân đôi chỗ phải sửa khi
/// ai đó đổi tên.</para>
public record ChatAssignSettings(
    string TenantId,
    short  Mode,
    bool   ScopeOwnOnly,
    bool   AutoAssignOnReply,
    int[]  MemberIds,
    int?   RotationLastUserId);

/// Ai đang xem, và được xem tới đâu.
///
/// <para><b>Dựng ở tầng endpoint, KHÔNG dựng trong kho dữ liệu</b> — kho không biết gì về phiên
/// đăng nhập, và để nó tự đoán là mở đường cho một lần đoán sai thành lỗ hổng.</para>
public record NguoiXem(int? CrmUserId, bool XemTatCa)
{
    /// Dùng cho worker và webhook — chỗ không có người dùng nào đứng sau.
    public static readonly NguoiXem HeThong = new(null, true);
}
