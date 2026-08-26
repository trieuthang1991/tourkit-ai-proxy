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
}
