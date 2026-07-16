namespace TourkitAiProxy.Services.TourKit;

/// Mã quyền TourKit (Function_Code) mà PROXY thực sự kiểm. Đồng bộ TAY với nguồn gốc
/// toutkit-app/TourKit.Shared/PermissionCodes.cs — CHỈ khai báo mã proxy dùng (không copy hết ~200 mã).
public static class TkPermissionCodes
{
    /// Công việc — tạo mới (assign_task). TaskingService.cs:545.
    public const string TaoViec = "CV_TAOMOI";
    /// Chăm sóc KH — tạo mới nhắc/hẹn (create_appointment). CustomerCareService.cs:595.
    public const string TaoNhacHen = "CS_KH_TAOMOI";
    /// Cấu hình hệ thống — thao tác (gate trang tích hợp). PermissionCodes.cs:167.
    public const string CauHinhHeThong = "CH_HT_THAOTAC";
}
