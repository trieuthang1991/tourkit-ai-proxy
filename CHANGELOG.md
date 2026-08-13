<!--
  QUY TẮC (BẮT BUỘC — xem CLAUDE.md mục Conventions):
  • Mỗi lần public code (merge main/dev, release, deploy) PHẢI thêm 1 mục ở đây.
  • Viết CHO NGƯỜI DÙNG CUỐI: theo trải nghiệm ("Bạn có thể…", "Trước đây … nay …").
  • KHÔNG đưa mã commit/SHA, tên file/hàm/class, tên bảng SQL, thuật ngữ kỹ thuật vào đây.
  • Cấu trúc: "## Phiên bản dd/MM/yyyy — <tên>" → "### ✨ Tính năng mới" → "### 🔧 Đã khắc phục"
    (nói rõ người dùng gặp gì, nay hết thế nào) → tùy chọn "### 📌 Lưu ý" / "## 🔜 Sắp có".
  • Mới nhất ở TRÊN CÙNG. Tiếng Việt, giọng thân thiện.
-->
# Có gì mới

Những cập nhật gần đây của TRAV-AI, viết cho người dùng. Mới nhất ở trên cùng.

---

## Phiên bản 13/08/2026 — Nghe bản tin sáng + chọn bản tin theo vai trò

### ✨ Tính năng mới
- **🔊 Nghe bản tin sáng.** Vào tab **Bảng tin**, bấm nút **Nghe** trên bản tin để nghe đọc bằng giọng
  — tiện khi đang lái xe hay bận tay, không rảnh đọc. Bấm lần nữa để dừng.
- **👤 Mỗi người một loại bản tin theo vai trò.** Nhân viên bán hàng nhận *bản tin công việc*; giám đốc
  nhận *bản tin điều hành*. Bạn chọn **một** loại hợp với vai trò của mình — chọn loại này thì loại kia
  tự tắt, khỏi rối và khỏi trùng.
- **✉️ Email bản tin gọn gàng hơn.** Bản tin gửi qua email nay có bố cục đẹp, dễ đọc, kèm phần chân thư
  hướng dẫn đổi giờ/kênh nhận.

### 🔧 Đã khắc phục
- Nút Nghe: trước đây bấm nhanh nhiều lần có thể bị **kẹt ở "Đang tải…"** hoặc phát nhầm bản tin khác —
  nay bấm tới đâu chạy mượt tới đó, đổi bản tin là đọc ngay bản mới.
- Khi không nghe được (trình duyệt chặn hoặc chưa bật giọng đọc), nay hiện **thông báo rõ ràng** thay vì
  im lặng khiến bạn không biết chuyện gì.
- Nút Nghe hiển thị **đúng trạng thái đang phát**, dễ nhận biết hơn.

### 📌 Lưu ý
- Trên **iPhone (Safari)**, lần đầu bấm Nghe đôi khi cần **chạm thêm một lần** để phát — chúng tôi đang
  tiếp tục tối ưu.

---

## Phiên bản 12/08/2026 — Bản tin sáng tự động + Bảng tin

### ✨ Tính năng mới
- **📰 Bản tin sáng tự động.** Mỗi sáng bạn nhận sẵn bản tin, không phải tự vào hỏi:
  - *Nhân viên bán hàng:* việc cần làm hôm nay — cơ hội cần gọi lại, lịch hẹn, việc, báo giá cần đeo bám,
    tour sắp đi còn thiếu tiền.
  - *Giám đốc:* doanh thu / chi phí / lợi nhuận so với cùng kỳ, kèm điều đáng lưu ý nhất.
- **⏰ Tự chọn giờ và nơi nhận.** Chọn giờ nhận (giờ Việt Nam) và kênh: **trong app, email, Telegram,
  Zalo** — bật/tắt từng kênh tuỳ ý.
- **🔔 Bảng tin trong app.** Xem lại mọi bản tin và cảnh báo ở một nơi, có **chuông báo số chưa đọc**.
  Đây là nơi chắc chắn nhất — vẫn còn đủ ở đây kể cả khi email hay Telegram trục trặc.
- **💰 Cảnh báo thu tiền trước khởi hành.** Tour sắp đi (trong 7 ngày) mà khách chưa trả đủ → hệ nhắc
  ngay để kịp xử lý.
- **🧪 Gửi thử.** Bấm **Gửi thử** để kiểm tra kênh nhận đã hoạt động chưa — không ảnh hưởng bản tin thật
  sáng hôm sau.
- **🛡️ Riêng tư theo quyền.** Mỗi người chỉ thấy số liệu trong phạm vi quyền của tài khoản mình.
- **📊 Trang theo dõi cho quản trị.** Admin có trang **Bản tin** để theo dõi đăng ký của cả hệ thống và
  chỉ rõ nếu có ai đó đăng ký nhưng chưa nhận được (chưa bật lịch, bỏ trống nơi nhận, hoặc kênh gửi lỗi).

### 🔧 Đã khắc phục
- Trước đây nếu **một kênh gửi bị lỗi buổi sáng** (ví dụ Telegram), bản tin coi như **mất luôn**, không
  gửi lại — nay hệ **tự thử lại** đúng kênh đó, không bỏ sót tin.
- Giờ hiển thị trên bản tin từng bị **lệch 7 tiếng** — nay đúng giờ Việt Nam.
- Bản tin giám đốc từng **báo động nhầm** "cần xử lý ngay" với những việc tồn đọng cũ tích luỹ nhiều năm
  — nay gọi đúng là **"tồn đọng"** nên đọc không bị hiểu lầm là đang khẩn cấp trong ngày.
- Ô **chọn giờ** từng hiển thị sai giờ đã lưu (lưu 21h nhưng lại hiện 5h) — nay đủ 0–23h và hiển thị đúng.
- Nay **báo lỗi ngay lúc lưu** nếu thiếu thông tin (chưa chọn kênh, bật email mà chưa nhập địa chỉ…),
  thay vì để bạn chờ tới sáng mới phát hiện không nhận được.

---

## 🔜 Sắp có
- **Đăng nhập nhanh sang TRAV-AI thẳng từ TourkitERP** — chỉ bấm một nút, không phải nhập lại mật khẩu.
  (Đang hoàn thiện và kiểm tra kỹ trước khi mở.)
