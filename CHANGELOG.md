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

## Phiên bản 14/08/2026 — Bản tin đến đúng giờ hơn

### ✨ Tính năng mới
- **⏰ Bản tin được chuẩn bị sẵn từ trước.** Hệ thống soạn bản tin của bạn trước giờ nhận một chút, đến
  giờ chỉ việc gửi đi. Nhờ vậy bản tin tới đúng giờ hơn, kể cả lúc hệ thống đang bận.
- **📥 Bản tin luôn được lưu trong app.** Dù bạn chỉ chọn nhận qua Zalo hay Telegram, bản tin vẫn luôn
  nằm ở tab **Bảng tin** để đọc lại hoặc bấm **Nghe** — không sợ lỡ một buổi sáng là mất luôn.
- **✅ Không cần chọn kênh nào cũng được.** Trước đây bật nhận bản tin là bắt buộc phải tick ít nhất một
  nơi nhận. Nay bạn có thể chỉ đọc trong app, không cần email/Zalo/Telegram.
- **📱 Nhận bản tin qua Zalo chỉ cần nhập số điện thoại.** Trước đây ô này đòi một mã người dùng Zalo —
  thứ mà hầu như không ai biết lấy ở đâu. Nay bạn điền số của mình là xong, nhập kiểu nào cũng được
  (`0912345678`, `+84 912 345 678`, có dấu chấm hay khoảng trắng), hệ thống tự chỉnh về đúng dạng.
  Công ty cũng **không phải khai gì thêm** để bật kênh này.

### 🔧 Đã khắc phục
- **Mất bản tin của cả ngày khi hệ thống bận đúng khung giờ gửi.** Trước đây nếu hệ thống khởi động lại
  hoặc bận đúng giờ bạn chọn, bản tin hôm đó im lặng không tới và cũng không gửi bù. Nay hệ thống nhớ
  việc còn dang dở và gửi bù ngay khi hoạt động lại.
- **Một kênh hỏng làm mất bản tin ở kênh đó cả ngày.** Trước đây nếu Zalo hoặc Telegram lỗi lúc gửi, hệ
  thống vẫn coi như "đã gửi xong" và không đụng tới nữa. Nay mỗi nơi nhận được theo dõi riêng, hỏng ở
  đâu thì thấy rõ ở đó — quản trị viên mở trang theo dõi là biết ngay kênh nào chưa tới tay.
- **Trang theo dõi bản tin (quản trị) nói đúng hơn.** Cột "Hôm nay" nay tách rõ *đã gửi* / *gửi hỏng* /
  *đang chờ tới giờ*, thay vì gộp chung khiến bản tin chưa tới giờ trông như đang lỗi.

- **Nhập sai số Zalo nay báo ngay lúc lưu.** Trước đây điền nhầm (số bàn, thiếu số) vẫn lưu được, rồi
  sáng hôm sau không thấy tin mà chẳng biết vì sao. Nay hệ thống báo ngay khi bạn còn đang nhìn màn hình.

### 📌 Lưu ý
- Ô **"Trong app"** trong phần *Bản tin của tôi* nay luôn bật và không tắt được — đó là nơi lưu bản tin
  để bạn xem/nghe lại, không phải một kênh gửi.
- Tin nhắn Zalo là **lời nhắc ngắn** kèm ngày, không phải toàn bộ bản tin — đây là giới hạn của Zalo.
  Nội dung đầy đủ bạn đọc ở tab **Bảng tin**.

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
