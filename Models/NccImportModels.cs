namespace TourkitAiProxy.Models;

/// 1 dòng NCC chuẩn hoá theo file_import_ncc.xlsx (13 cột).
/// Phần tài chính (Tổng mua, Đã trả…) mặc định 0 khi nhập mới — hệ thống cập nhật sau.
public record NccRow(
    string Code,           // Mã NCC (NCC761…)
    string Name,           // Tên NCC
    string? Phone,         // Số điện thoại
    string? Email,         // Email
    string? Type,          // Loại NCC — 1 trong 10 enum (xem NccImportService.AllowedTypes)
    int Quantity,          // Số lượng
    decimal TotalBuy,      // Tổng mua
    decimal Paid,          // Đã trả
    decimal Collected,     // Thu hộ
    decimal Owed,          // Còn nợ
    decimal Balance,       // Số dư
    string Status          // Tình trạng — Hoạt động | Ngừng
);

/// Request body cho POST /api/v1/ncc-import/extract (khi user dán text thay vì upload file).
public record NccExtractTextReq(string Text, string? Provider, string? Model);

/// Request body cho POST /api/v1/ncc-import/export (rows → Excel chuẩn).
public record NccExportReq(List<NccRow> Rows);

/// Response của extract: rows đã chuẩn hoá + log AI.
public record NccExtractResult(
    List<NccRow> Rows,
    string Source,          // "excel" | "csv" | "text" | "ai-text"
    int RawRowCount,
    int CleanedRowCount,
    long LatencyMs,
    int TokensIn,
    int TokensOut,
    string? Warning
);
