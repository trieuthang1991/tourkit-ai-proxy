namespace TourkitAiProxy.Infrastructure.TourKit;

/// Mã quyền TourKit (Function_Code) mà PROXY thực sự kiểm. Đồng bộ TAY với nguồn gốc
/// toutkit-app/TourKit.Shared/PermissionCodes.cs — CHỈ khai báo mã proxy dùng (không copy hết ~200 mã).
public static class TkPermissionCodes
{
    /// Công việc — tạo mới (assign_task). TaskingService.cs:545.
    public const string TaoViec = "CV_TAOMOI";
    /// Chăm sóc KH — tạo mới nhắc/hẹn (create_appointment). CustomerCareService.cs:595.
    public const string TaoNhacHen = "CS_KH_TAOMOI";
    /// Cấu hình hệ thống — xem (gate trang tích hợp; đồng bộ FE app.jsx CH_HT_XEM). PermissionCodes.cs:169.
    public const string CauHinhHeThong = "CH_HT_XEM";
    /// Tour GIT (tour đoàn) — tạo mới. PermissionCodes.cs:16. Gate màn Tính giá Tour + đồng bộ CRM loại GIT.
    public const string TaoTourGit = "TR_TD_TAOMOI";
    /// Tour FIT (tour khách lẻ) — tạo mới. PermissionCodes.cs:29. Gate đồng bộ CRM loại FIT.
    public const string TaoTourFit = "TR_TM_TAOMOI";
    /// Khách hàng — xem. PermissionCodes.cs:64. Gate màn Khách hàng (kể cả chấm hạng AI trên đó).
    public const string XemKhachHang = "KH_KH_XEM";
    /// Visa — xem. PermissionCodes.cs:149. Gate toàn bộ cụm Thẩm định Visa.
    public const string XemVisa = "VISA_XEM";

    /// <summary>
    /// Thêm <b>Cơ hội bán hàng</b> (= BookingTicket). Web cũ gác nút "THÊM MỚI" bằng mã này.
    ///
    /// <para><b>Proxy phải TỰ kiểm.</b> <c>BookingTicketService.CreateAsync</c> bên CRM không kiểm
    /// quyền — chỉ <c>CH_XEM/CH_XEM_ALL</c> khi xem và <c>CH_SUA</c> khi sửa được kiểm; việc gác
    /// lúc thêm nằm ở tầng màn hình của web cũ. Nên nếu proxy không chặn thì bất kỳ ai vào được
    /// hộp thư chat đều tạo được Cơ hội, kể cả người chỉ có quyền trực chat.</para>
    ///
    /// <para>Và phải kiểm lúc XẾP HÀNG, không đợi worker: worker chạy bằng quyền riêng của nó và
    /// không biết ai đã bấm nút, nên nó không kiểm thay được.</para>
    /// </summary>
    public const string TaoCoHoi = "CH_TAO_MOI";

    /// <summary>
    /// Hộp thư chat — xem phần ĐƯỢC GIAO cho mình. Đây là mức tối thiểu để vào được hộp thư.
    /// </summary>
    public const string ChatXem = "CHAT_XEM";

    /// <summary>
    /// Hộp thư chat — xem TẤT CẢ hội thoại của công ty, kể cả chưa giao cho ai.
    ///
    /// <para>Đây là câu trả lời cho "ai là quản trị chat". Trước 09/09/2026 câu đó được chốt
    /// bằng <c>Username == "admin"</c> — một chốt tạm, và nó sai theo CẢ HAI chiều: người
    /// tên khác mà CRM có cấp quyền này thì bị kẹp oan, còn ai đặt tên đăng nhập là "admin"
    /// thì xem được cả công ty dù CRM không cấp gì. Đo thật trên staging cùng ngày: tài khoản
    /// <c>AdminThoa</c> có CHAT_XEM_ALL mà bị kẹp, <c>trang01</c> không có quyền chat nào mà
    /// vẫn vào được hộp thư.</para>
    /// </summary>
    public const string ChatXemTatCa = "CHAT_XEM_ALL";
}
