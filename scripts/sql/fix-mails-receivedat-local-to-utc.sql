/*
  dbo.Mails.ReceivedAt — đổi dữ liệu CŨ từ giờ địa phương (+07) về UTC.

  ⚠️ CHƯA CHẠY. Cần người quyết định trước khi chạy trên bất kỳ môi trường nào.

  ── Chuyện gì đã xảy ra ────────────────────────────────────────────────────────
  Đường GHI dùng `DateTime.TryParse` trần: chuỗi "…14:08:09Z" bị đổi sang giờ MÁY CHỦ thành
  "…21:08:09" rồi ghi thẳng vào cột DATETIME2 (cột không mang múi giờ) → sai vĩnh viễn.
  Đường ĐỌC lại dùng `ToString("o")` trên giá trị Dapper trả về (Kind=Unspecified) nên chuỗi ra
  KHÔNG có 'Z'.

  Hai lỗi ngược dấu nên CHE NHAU: lưu dư 7 tiếng, đọc ra thiếu 'Z' → trình duyệt hiểu là giờ địa
  phương → hiển thị ĐÚNG trên máy ở Việt Nam. Vì thế nó nằm im rất lâu; E2E bắt được 25/08/2026 vì
  soi hậu tố 'Z' thay vì nhìn màn hình.

  Mã nguồn đã sửa cả hai vế (MailRepository.DocMocUtc / MocUtcRaChuoi, có test). Từ nay thư MỚI lưu
  đúng UTC. Nhưng thư CŨ vẫn đang giữ giờ địa phương, mà đường đọc nay đóng dấu 'Z' cho chúng — nên
  thư cũ hiện ra như đã nhận ở TƯƠNG LAI (+7 giờ).

  ── Vì sao không tự chạy ──────────────────────────────────────────────────────
  Đây là sửa DỮ LIỆU của bảng đang chạy thật, không phải thêm cột. Ba câu hỏi phải trả lời trước:

    1. Có tenant nào từng chạy trên máy chủ đặt múi giờ KHÁC +07 không? Nếu có thì hằng số 7 sai
       cho tenant đó, và chạy chung một câu UPDATE là làm hỏng thêm.
    2. Có hàng nào đã được ghi SAU khi bản vá lên (tức đã đúng UTC) không? Trừ tiếp 7 tiếng cho
       những hàng đó là làm sai cái đang đúng. Câu dưới chặn bằng mốc thời gian triển khai —
       PHẢI điền đúng mốc đó.
    3. Có nơi nào khác đã sao chép cột này đi chưa (báo cáo, kho dữ liệu)?

  ── Cách chạy ─────────────────────────────────────────────────────────────────
    1. Sao lưu bảng (câu 0 bên dưới) — BẮT BUỘC.
    2. Chạy câu 1 (đếm) trước để biết ảnh hưởng bao nhiêu hàng.
    3. Điền @TrienKhaiUtc = thời điểm bản vá lên production, dạng UTC.
    4. Chạy câu 2 trong một giao dịch, soát lại rồi mới COMMIT.
*/

-- ── 0. Sao lưu (BẮT BUỘC trước khi sửa) ──────────────────────────────────────
-- SELECT * INTO dbo.Mails_backup_20260825 FROM dbo.Mails;

-- ── 1. Đếm trước: bao nhiêu hàng bị ảnh hưởng, mốc cũ nhất/mới nhất là gì ────
DECLARE @TrienKhaiUtc DATETIME2 = '2026-08-25T00:00:00';   -- ⚠️ ĐIỀN LẠI cho đúng

SELECT  TenantId,
        COUNT(*)          AS SoHang,
        MIN(ReceivedAt)   AS CuNhat,
        MAX(ReceivedAt)   AS MoiNhat
FROM    dbo.Mails
WHERE   ReceivedAt < @TrienKhaiUtc
GROUP BY TenantId
ORDER BY TenantId;

-- ── 2. Sửa: trừ 7 giờ cho các hàng ghi TRƯỚC lúc triển khai bản vá ───────────
-- BEGIN TRAN;
--
--   UPDATE dbo.Mails
--   SET    ReceivedAt = DATEADD(HOUR, -7, ReceivedAt)
--   WHERE  ReceivedAt < @TrienKhaiUtc;
--
--   -- Soát lại: không hàng nào được nằm ở tương lai nữa.
--   SELECT COUNT(*) AS ConNamOTuongLai
--   FROM   dbo.Mails
--   WHERE  ReceivedAt > SYSUTCDATETIME();
--
-- -- Đúng thì COMMIT, sai thì ROLLBACK.
-- -- COMMIT TRAN;
-- -- ROLLBACK TRAN;
