# Yêu cầu dữ liệu từ cơ quan công quyền

Quy trình xử lý khi có cơ quan nhà nước yêu cầu cung cấp dữ liệu cá nhân của người dùng — gồm
nội dung hội thoại, thông tin khách hàng, và dữ liệu nhận từ Meta.

**Vì sao có file này.** Hồ sơ App Review của Meta (mục *Xử lý dữ liệu* → `requests-4`) hỏi công ty
áp dụng những chính sách nào cho loại yêu cầu này, và ta đã khai bốn mục dưới đây. Khai rồi mà
không có quy trình thật thì lần sau bị hỏi lại là không trả lời được. File này là nội dung của lời
khai đó.

Người chịu trách nhiệm: **Tổng Giám đốc**. Không ai khác được tự quyết định cung cấp dữ liệu.

---

## 1 · Bắt buộc xem xét tính hợp pháp

Không cung cấp bất cứ dữ liệu nào trước khi kiểm ba việc:

- **Có văn bản chính thức không.** Yêu cầu qua điện thoại, tin nhắn, email không có dấu, hoặc do
  người tự giới thiệu là công an — **không xử lý**. Đề nghị họ gửi văn bản.
- **Đúng thẩm quyền không.** Cơ quan ra văn bản có quyền yêu cầu loại dữ liệu đó theo quy định nào,
  văn bản có dẫn căn cứ pháp lý hay không.
- **Phạm vi có xác định không.** Yêu cầu nêu rõ đối tượng và khoảng thời gian, hay là yêu cầu mở
  kiểu "toàn bộ dữ liệu khách hàng".

Không chắc thì hỏi luật sư **trước khi** trả lời, không phải sau. Chậm trả lời một yêu cầu hợp pháp
sửa được; giao dữ liệu sai thì không.

## 2 · Phản đối nếu yêu cầu bất hợp pháp

Yêu cầu không đạt phần 1 thì trả lời bằng văn bản, nêu rõ lý do chưa cung cấp và đề nghị bổ sung
căn cứ. Không im lặng, cũng không giao cho xong việc.

Yêu cầu vượt thẩm quyền hoặc phạm vi mở thì phản đối chính thức, và cân nhắc nhờ luật sư đứng tên
văn bản phản hồi.

## 3 · Giảm thiểu dữ liệu

Chỉ giao **đúng phần** yêu cầu nêu:

- Yêu cầu về một khách hàng thì không giao hội thoại của khách khác trong cùng hộp thư.
- Yêu cầu trong một khoảng thời gian thì không giao dữ liệu ngoài khoảng đó.
- Không giao khoá truy cập, chuỗi kết nối, hay quyền vào hệ thống — chỉ giao bản trích xuất dữ
  liệu. Cấp quyền truy cập trực tiếp vào hệ thống là mở cả dữ liệu của mọi công ty du lịch khác
  đang dùng chung nền tảng.
- Trước khi gửi, đọc lại bản trích xuất một lượt xem có lẫn dữ liệu của người không liên quan.

Nền tảng là đa khách hàng (multi-tenant): một truy vấn viết rộng tay là lấy ra dữ liệu xuyên nhiều
công ty. Đây là lý do phần này không phải hình thức.

## 4 · Ghi chép

Mỗi yêu cầu ghi một dòng vào sổ, kể cả yêu cầu bị từ chối:

| Trường | Nội dung |
|---|---|
| Ngày nhận | Ngày trên văn bản và ngày thực nhận |
| Cơ quan yêu cầu | Tên, số văn bản, người ký |
| Nội dung yêu cầu | Dữ liệu gì, về ai, khoảng thời gian nào |
| Căn cứ pháp lý họ dẫn | Điều khoản nào |
| Quyết định | Cung cấp / cung cấp một phần / từ chối |
| Lập luận | Vì sao quyết định như vậy; có hỏi luật sư không |
| Đã giao những gì | Liệt kê cụ thể, kèm bản sao thứ đã gửi |
| Đã thông báo cho ai | Công ty du lịch bị ảnh hưởng, hoặc lý do không thông báo |

Sổ lưu cùng hồ sơ pháp lý của công ty, không lưu trong repo này.

## 5 · Thông báo cho khách hàng bị ảnh hưởng

Dữ liệu hội thoại thuộc về công ty du lịch đang dùng nền tảng, không thuộc về TourKit. Nên khi phải
cung cấp dữ liệu của họ, thông báo cho họ — **trừ khi** văn bản yêu cầu hoặc pháp luật cấm tiết lộ.
Nếu bị cấm, ghi rõ điều đó vào sổ ở phần 4.

---

## Không áp dụng cho việc gì

Đây không phải quy trình cho yêu cầu nội bộ (nhân viên xin xuất dữ liệu), cho yêu cầu của chính
khách hàng về dữ liệu của họ (xem `travelai.vn/privacy`), hay cho tranh chấp dân sự giữa hai công
ty du lịch.
