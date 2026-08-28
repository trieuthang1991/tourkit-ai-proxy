<!--
  QUY TẮC (BẮT BUỘC — xem docs/conventions.md):
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

## Phiên bản 28/08/2026 — Thêm 6 ngày để trả lời khách Facebook

### ✨ Tính năng mới
- **Bốn nút mới trên mỗi hội thoại.** **Chưa đọc** — trả lại dấu chưa đọc khi bạn lỡ mở nhầm,
  chỉ ảnh hưởng tới mình bạn chứ không tới đồng nghiệp. **Theo dõi** — ngó một ca khó mà không
  phải nhận việc, nhiều người cùng theo dõi được, và có bộ lọc **Tôi theo dõi** ở đầu danh sách.
  **Chặn trong hộp thư** — khách quấy rối thì hộp thư ẩn họ đi và trợ lý ngừng trả lời.
  **Xoá / Sửa tin** — rê chuột vào tin của bạn là thấy.

  *Lưu ý quan trọng:* chặn và xoá **chỉ có tác dụng trong hộp thư của bạn**. Facebook, Instagram,
  WhatsApp, Zalo và TikTok không cho phép doanh nghiệp chặn khách hay thu hồi tin đã gửi — nên
  khách vẫn nhắn tới được và vẫn thấy tin cũ. Màn hình nói rõ điều này trước khi bạn bấm. Tin đã
  xoá không biến mất khỏi hội thoại mà hiện thành dòng *"Tin đã bị xoá khỏi hộp thư"*, để bạn
  không tưởng mình nhớ nhầm. Nút **Sửa** chỉ hiện với tin **chưa gửi đi được** (đang chờ hoặc gửi
  hỏng) — tin đã tới tay khách thì sửa bản của mình chỉ làm hộp thư ghi sai thứ khách thật sự nhận.

- **Có chỗ chỉnh trợ lý chat rồi.** Mở **Cài đặt hộp thư → Trợ lý**: bật/tắt trợ lý, viết
  những gì nó cần biết về công ty bạn (chuyên tuyến nào, xưng hô ra sao, không nhận đoàn
  dưới bao nhiêu khách), đặt câu chào, và chọn nhân viên trả lời xong thì trợ lý im bao lâu.
  Trước đây tất cả những thứ này nằm cứng trong máy chủ, mọi công ty dùng chung một kiểu.

  *Lưu ý:* phần bạn viết **thêm vào** chứ không thay thế các luật an toàn. Trợ lý vẫn không
  bao giờ tự báo giá, lịch khởi hành hay số chỗ còn — nó chưa đọc dữ liệu thật của công ty.

- **Quản lý mẫu trả lời nhanh ngay trong app.** Mở **Cài đặt hộp thư → Mẫu trả lời**: thêm,
  sửa, xoá mẫu và gắn nút bấm cho từng mẫu. Trước đây không có màn hình nào — mẫu chỉ tạo
  được bằng cách nhờ người kỹ thuật, nên gần như không ai dùng.

- **Gửi kèm nút bấm cho khách.** Bấm **+ Nút** ở ô soạn, đặt chữ trên nút rồi gửi. Hai kiểu:
  để trống đường dẫn thì khách bấm là **coi như họ nhắn đúng câu đó** — trợ lý trả lời tiếp
  bình thường, hợp cho câu hỏi kiểu "Anh quan tâm tuyến nào?" với ba nút Nhật · Hàn · Đài;
  điền đường dẫn thì bấm là mở trang. Nút đã gửi hiện lại trong dòng tin để sau này đọc lại
  vẫn biết khách được mời chọn gì.

  *Lưu ý:* mỗi nơi cho số nút khác nhau — Facebook và Instagram 13 nút (3 nếu có nút mở
  trang), Zalo 5, WhatsApp 3, Telegram 8. Soạn quá thì hệ thống bỏ bớt và **báo cho bạn biết**
  ngay sau khi gửi. TikTok không có nút; WhatsApp không nhận nút mở trang trong tin thường.

- **Hết hạn trả lời vẫn nhắn được cho khách, bằng tin mẫu đã duyệt.** Trước đây quá 24 giờ
  (Facebook, WhatsApp) hoặc 48 giờ (Zalo) là hộp thư câm hẳn — không gửi được xác nhận đặt
  tour, không nhắc ngày khởi hành, không báo đổi giờ bay. Nay ngay tại chỗ báo "hết hạn" có
  thêm nút **Gửi tin mẫu**: chọn một mẫu công ty đã đăng ký và được nền tảng duyệt, điền vài ô
  rồi gửi. Ô nào có ví dụ sẵn thì hệ thống điền trước cho bạn sửa.

  *Lưu ý:* mẫu phải đăng ký bên nền tảng trước (Zalo ZNS · Meta Business) và chờ họ duyệt —
  duyệt xong là tự hiện ở đây, không phải khai lại. Mẫu đang chờ duyệt vẫn hiện nhưng chưa bấm
  gửi được. **Tin mẫu có thể tính phí và không thu hồi được**, nên màn hình nhắc trước khi gửi.
  Zalo gửi tin mẫu **theo số điện thoại**, nên hội thoại nào chưa có số của khách thì chưa gửi
  được — màn hình nói rõ và chỉ chỗ điền số.

- **Nối WhatsApp và TikTok chỉ còn một nút.** Trước đây hai kênh này bắt bạn tự vào bảng điều
  khiển của Meta và TikTok tìm bốn dòng mã kỹ thuật rồi chép sang — hầu như không ai làm được
  nếu không có người kỹ thuật ngồi cạnh. Nay bấm **Kết nối WhatsApp** hoặc **Kết nối TikTok**,
  đăng nhập tài khoản của bạn, chọn số điện thoại (hoặc tài khoản) rồi quay về — xong. Hệ thống
  tự lấy hết những mã đó.

  *Lưu ý:* ô khai tay vẫn còn, dành cho công ty nào đã tự tạo ứng dụng riêng bên Meta/TikTok.

- **Lấy lại các đoạn chat cũ của Facebook và Instagram.** Nối kênh xong, những cuộc trò chuyện
  có từ trước đó không tự về hộp thư. Nay mở **Kết nối kênh**, chọn tài khoản rồi bấm **Lấy hội
  thoại cũ** — hệ thống kéo về, mất vài phút, tin nào đã có thì bỏ qua. Trợ lý **không** trả lời
  lại những tin cũ này, và chúng nằm đúng ngày giờ thật chứ không dồn lên đầu hộp thư.

  *Lưu ý:* chỉ Facebook và Instagram lấy lại được. Telegram, Zalo và TikTok thì nền tảng không
  cho đọc lại quá khứ — kênh mới nối chỉ nhận tin từ lúc nối trở đi. WhatsApp có lấy được, nhưng
  chỉ khi công ty chuyển sang từ ứng dụng WhatsApp Business và chỉ trong 24 giờ đầu sau khi nối.

### 🔧 Đã khắc phục
- **Khách thả cảm xúc trên Facebook: nay hộp thư hiện.** Trước đây khách thả tim hay biểu tượng
  vào tin của bạn thì hộp thư không hiện gì cả — trong khi Instagram thì có. Nay Facebook cũng
  hiện, kể cả lúc khách gỡ ra.

  *Lưu ý:* Trang Facebook nào đã nối **từ trước bản này** cần vào **Kết nối kênh** bấm nối lại một
  lượt — Facebook chỉ nhận danh sách loại tin lúc kết nối.
- **Khách bấm nút "Gửi tới Messenger" trên website: nay biết họ đến từ đâu.** Trước đây đường này
  không được ghi nhận, nên trang đích nào kéo được khách về thì không có số liệu. Nay hiện trong
  mục "Đến từ" của hội thoại như các nguồn khác.
- **Ảnh khách gửi không còn biến mất sau vài ngày.** Facebook, Instagram, WhatsApp, Zalo và
  Telegram đều chỉ cho xem ảnh trong một thời gian ngắn — Facebook là **5 ngày** — sau đó ảnh
  trong hộp thư thành ô vỡ và **không cách nào lấy lại**. Ảnh hoá đơn, hộ chiếu, ảnh phòng
  khách sạn khách gửi đều mất theo. Nay mọi ảnh và tệp khách gửi được **giữ lại bản riêng của
  công ty bạn** ngay khi nhận, nên mở lại hội thoại một năm sau vẫn xem được. Ảnh đại diện của
  khách cũng vậy — trước đây nó cũng hết hạn và cả danh sách hội thoại hiện một dãy khuôn mặt vỡ.

  Ảnh **đã nhận từ trước** cũng được cứu: hệ thống tự lặng lẽ tải về dần trong nền, bạn không
  phải bấm gì. Ảnh chụp bằng điện thoại đời mới được thu nhỏ vừa đủ đọc rõ chữ, nên hộp thư mở
  nhanh hơn trước.

  *Lưu ý:* ảnh nào đã quá hạn trước khi bản này chạy thì không cứu được nữa — hệ thống bỏ qua
  thay vì thử mãi.
- **Trợ lý nhớ được đoạn hội thoại, không còn trả lời lạc đề.** Trước đây nó chỉ đọc đúng câu
  khách vừa gửi, nên khách hỏi "Tour Nhật bao nhiêu ạ?" rồi hỏi tiếp "Thế còn tháng 10?" là
  nó không biết đang nói về tour nào. Khách nào cũng nhắn kiểu đó, nên trợ lý lạc đề gần như
  mọi cuộc dài quá hai lượt. Nay nó đọc lại các tin gần nhất trước khi trả lời — số tin do bạn
  đặt trong Cài đặt hộp thư.
- **Màn hình xem lại một kênh đã nối gọn hẳn.** Trước đây mở một bot Telegram ra là gặp lại ba
  bước hướng dẫn cách tạo bot — thứ chỉ cần lúc thêm mới — cùng một ô mã bot che sao mời bạn gõ
  vào, mà để trống thì không ai đoán được là giữ hay xoá. Nay màn hình đó chỉ còn thứ bạn thật
  sự mở nó ra để làm: đổi tên gợi nhớ. Mã bot hiện thành dòng **đã lưu** kèm nút **Đổi**, bấm
  mới ra ô nhập. Mã bot và địa chỉ nhận tin nén thành hai dòng nhỏ ở cuối để đối chiếu khi cần.
- **Instagram không còn bắt bạn đi tìm mã.** Kênh này vốn nối kèm theo Trang Facebook, nhưng
  giao diện lại bày ô nhập như thể phải khai riêng. Nay nó chỉ thẳng sang **Kết nối Facebook**.
- **Kênh chưa được quản trị khai khoá thì nói rõ ra.** Trước đây WhatsApp và TikTok cứ đổ bốn ô
  kỹ thuật ra màn hình, làm bạn tưởng phải tự vào bảng điều khiển Meta hay TikTok tìm mã. Nay
  màn hình nói đúng việc cần làm: kênh này nối bằng một nút, chỉ là quản trị hệ thống chưa khai
  khoá — báo họ giúp bạn.
- **TikTok không còn tự ngắt sau một ngày.** Chìa khoá TikTok cấp chỉ sống 24 giờ. Trước đây hệ
  thống không tự xin chìa mới, nên đúng một ngày sau khi nối là mọi tin gửi đi đều hỏng — mà
  câu báo lỗi không hề nhắc tới chuyện hết hạn nên rất khó đoán ra. Nay hệ thống tự gia hạn
  trước khi hết, bạn không phải làm gì.
- **WhatsApp: nhận cả tin nhân viên trả lời thẳng từ điện thoại.** Trước đây hộp thư chỉ thấy
  câu khách hỏi, không thấy câu đã trả lời từ ứng dụng WhatsApp trên máy — nên trợ lý trả lời
  đè lên người thật. Nay cả hai chiều đều hiện lên.
- **Khách nhắn Facebook/Instagram từ hôm trước: nay vẫn trả lời được.** Trước đây quá 24 giờ
  kể từ tin của khách là ô soạn khoá lại — khách nhắn tối thứ Sáu, sáng thứ Hai bạn vào thì
  đã không gõ được gì, dù mở Messenger ra vẫn nhắn bình thường. Hoá ra Facebook cho **nhân
  viên** trả lời tới **7 ngày**, chỉ là trợ lý tự động thì không. Nay hộp thư mở đúng như
  vậy: quá 24 giờ, bạn vẫn nhắn tay được thêm 6 ngày nữa, và có một dòng nhắc ngay trên
  khung chat cho biết từ lúc đó trợ lý đã ngừng trả lời hộ — để bạn không tưởng nhầm là vẫn
  có người trực.

  *Lưu ý:* chỉ Facebook và Instagram có thêm 6 ngày này. Zalo vẫn 48 giờ, WhatsApp vẫn 24
  giờ rồi phải dùng mẫu đã duyệt — đó là quy định của từng nền tảng.

---

## Phiên bản 27/08/2026 — Sáu kênh chat trong một hộp thư

### ✨ Tính năng mới
- **Thêm WhatsApp và TikTok.** Hộp thư nay gom đủ sáu nơi khách hay nhắn: Zalo, Facebook,
  Instagram, WhatsApp, TikTok và Telegram — trả lời tất cả ở cùng một chỗ, không phải mở sáu
  ứng dụng.

  *Lưu ý:* WhatsApp cần tài khoản doanh nghiệp đã xác minh và một số điện thoại riêng; ngoài 24
  giờ kể từ tin của khách thì chỉ gửi được mẫu đã duyệt. TikTok cần ứng dụng doanh nghiệp đã
  được duyệt quyền nhắn tin, và chỉ gửi được chữ với ảnh — tệp thì gửi đường dẫn bằng tin chữ.
  Cả hai kênh này chưa chạy thử với tài khoản thật, nên hãy thử một cuộc trước khi giao cho cả
  đội dùng.
- **Tin nhắn Instagram vào thẳng hộp thư.** Nếu tài khoản Instagram của công ty đã liên kết với
  Trang Facebook, thì chỉ cần nối Trang như trước là xong — hệ thống tự nhận ra và nối luôn
  Instagram, **không phải bấm thêm nút nào**. Khách nhắn Direct hiện cạnh tin Zalo, Facebook và
  Telegram trong cùng một hộp thư, trả lời cùng một chỗ.

  *Lưu ý:* tài khoản Instagram phải là loại **Professional** và đã bật "Cho phép truy cập tin
  nhắn" trong cài đặt Instagram. Instagram cho trả lời trong **24 giờ** kể từ tin của khách,
  giống Facebook. Instagram chỉ báo "khách đã xem", không báo "đã nhận" — nên dấu tích ở kênh
  này nhảy thẳng từ một tích sang hai tích.
- **Nối bot Telegram chỉ còn dán một dòng mã.** Trước đây muốn đưa tin Telegram vào hộp thư,
  bạn phải tự nghĩ ra một chuỗi bí mật, chép địa chỉ trên màn hình, rồi tự gõ một câu lệnh ở
  ngoài trình duyệt — gần như không ai làm được nếu không có người kỹ thuật ngồi cạnh. Nay chỉ
  cần dán mã bot lấy từ BotFather rồi bấm **Lưu**: hệ thống tự kiểm tra mã, tự lo phần còn lại,
  và tự lấy tên bot làm tên gợi nhớ cho bạn. Gõ sai mã thì hiện đúng lý do thay vì chỉ báo
  "Lưu không được".
- **Khách Telegram nay được đối xử như khách Facebook.** Cảm xúc khách thả lên một tin hiện ngay
  trong hộp thư và gỡ ra thì mất theo; khách bấm nút thì bạn thấy đúng chữ trên nút họ bấm;
  khách nhìn thấy ba chấm trong lúc trợ lý soạn câu trả lời; và ảnh đại diện của khách hiện
  cạnh tin thay vì một ô chữ cái.
- **Biết khách Telegram đến từ đâu.** Nếu bạn phát liên kết chat có gắn dấu riêng cho từng chiến
  dịch (quảng cáo, mã QR tại quầy, đường dẫn trên website), hộp thư ghi lại nguồn đó ngay khi
  khách mở cuộc trò chuyện — thay vì nhận một câu lệnh khó hiểu như trước.

### 🔧 Đã khắc phục
- **Bản tin sáng tự chạy tiếp, không bắt bạn đăng nhập lại.** Trước đây nếu hệ thống không lấy
  được số liệu của bạn thì bản tin chỉ đơn giản không tới — không báo gì, và bạn không có cách
  nào biết. Nay hệ thống **tự lấy quyền của bạn** để chạy tiếp, bạn không phải làm gì cả.
  Chỉ khi tài khoản của bạn bị khoá hoặc xoá bên CRM thì mới cần người xử lý — lúc đó bạn nhận
  một thư nói rõ, đăng ký được tạm tắt, và chỗ **Tự động hoá → Bản tin của tôi** hiện luôn lý do
  cùng nút bật lại.
- **Đăng nhập một chạm từ CRM không còn bị văng ra sau mỗi lần cập nhật.** Ai vào Trav-ai bằng
  cách bấm thẳng từ CRM (không gõ mật khẩu) thì phiên làm việc bị mất mỗi lần hệ thống khởi
  động lại, phải quay về CRM bấm lại từ đầu. Nay phiên được giữ nguyên.
- **Bản tin sáng tới được cả người đăng nhập một chạm.** Đây là hệ quả nặng hơn của cùng một
  lỗi: những người này bị hệ thống xem như *chưa từng đăng nhập*, nên bản tin sáng lặng lẽ bỏ
  qua họ — không báo lỗi, không ai biết, chỉ là sáng ra không có bản tin. Kiểm trên dữ liệu
  thật: 6 phiên đang bị bỏ oan, nay nhận lại đủ.
- **Khách gửi video hoặc file nhạc qua Telegram không còn là một dòng trắng.** Trước đây những
  loại này lọt vào hộp thư thành một dòng trống: hội thoại nhảy lên đầu danh sách, báo có tin
  chưa đọc, mà mở ra không thấy gì — bạn không có cách nào biết khách vừa gửi cái gì.
- **Ảnh và tệp khách gửi qua Telegram không còn báo "chưa tải được".** Hệ thống lấy nhầm chìa
  của con bot dùng cho bản tin sáng thay vì bot của chính công ty bạn, nên mọi tệp đều mở không
  ra. Nay mở bình thường.
- **Gỡ kết nối bot Telegram nay báo cho Telegram ngừng gửi.** Trước đây gỡ xong Telegram vẫn gửi
  tin vào một địa chỉ đã bỏ, mãi mãi.

---

## Phiên bản 26/08/2026 — Hộp thư chat: tin hiện ngay, cuộn không sót

### ✨ Tính năng mới
- **Tin mới hiện ra ngay, không phải chờ.** Trước đây hộp thư tự tải lại vài giây một lần, nên tin
  khách vừa nhắn có thể mất tới bốn giây mới thấy — và máy vẫn tải lại đều đặn kể cả lúc chẳng có
  gì mới. Nay tin được đẩy tới ngay khi có: khách nhắn, đồng nghiệp trả lời, hay dấu tích chuyển
  sang "đã nhận"/"đã xem" đều hiện gần như tức thì. Bạn không phải làm gì thêm.
- **Cuộn danh sách hội thoại không còn lặp hay sót.** Khi hộp thư đang bận, danh sách cũ hay hiện
  lại một hội thoại bạn vừa đọc và bỏ qua một hội thoại chưa đọc. Nay cuộn tới đâu chắc tới đó, và
  có tin mới cũng không bị cuốn ngược về đầu danh sách giữa lúc bạn đang đọc.

- **Nối Facebook chỉ còn một nút bấm.** Trước đây muốn đưa tin nhắn Trang Facebook vào hộp thư,
  công ty phải tự tạo một ứng dụng bên Meta, lấy bốn loại mã khác nhau rồi tự bật nhận tin cho
  từng Trang. Nay bạn bấm **"Kết nối Facebook"**, đăng nhập Facebook, chọn Trang của công ty trong
  danh sách hiện ra — xong. Không phải nhập mã nào, cũng không phải vào trang quản trị của Meta.
  Quản trị nhiều Trang thì chọn xong Trang này có thể chọn tiếp Trang khác ngay trong cửa sổ đó.

- **Nối Zalo OA chỉ còn một nút bấm.** Trước đây mỗi công ty phải tự vào trang dành cho lập trình
  viên của Zalo, tạo một ứng dụng, tìm hai loại khoá bí mật rồi khai địa chỉ nhận tin — tám bước
  kỹ thuật trước khi nhắn được tin đầu tiên. Nay bạn chỉ bấm **"Kết nối Zalo OA"**, đăng nhập Zalo,
  chọn OA của công ty và bấm đồng ý. Không phải nhập mã nào cả.
- **Gắn nhãn và ghi chú cho khách.** Trong hồ sơ bên phải có thêm mục **"Nhãn"** và **"Ghi chú
  nội bộ"**. Nhãn theo khách chứ không theo cuộc trò chuyện, nên khách nhắn lại sau vài tháng bạn
  vẫn thấy nhãn cũ. Ghi chú **chỉ nhân viên đọc được, khách không bao giờ thấy**.
- Gõ nhãn có dấu cũng được — hệ thống tự bỏ dấu khi lưu, nên "Khách VIP" và "khach vip" là **một**
  nhãn chứ không thành hai.
- **Nối khách đang chat với hồ sơ khách trong CRM.** Mở hồ sơ bên phải, bấm **"Nối khách CRM"**,
  gõ tên hoặc số điện thoại rồi chọn. Trước đây mục này luôn hiện "chưa nối" vì không có cách nào
  nối cả. Nối nhầm thì bấm **"Gỡ nối"**. Bạn chỉ tìm được những khách mà tài khoản của bạn vốn
  được phép xem.
- **Xem được ai đã làm gì với một hội thoại.** Mở hồ sơ khách bên phải, cuộn xuống mục **"Nhật ký
  thao tác"**: ai nhận việc, ai chuyển cho ai, ai đóng hội thoại, ai tạm dừng trợ lý — kèm thời
  điểm. Trước đây những việc này không để lại dấu vết nào, nên khi khách thắc mắc thì không tra
  được. Nội dung tin nhắn **không** bị chép lại vào đây, tin vẫn nằm nguyên ở khung trò chuyện.

### 🔧 Đã khắc phục
- **Nút "Nhận việc" giờ mới thật sự nhận việc.** Trước đây bấm nút này không gán hội thoại cho
  bạn — thậm chí còn gỡ người đang phụ trách ra. Nút trông như chạy nên không ai để ý.
- **Công ty nào đã tự khai ứng dụng Zalo riêng vẫn dùng được như cũ**, và form khai tay nay tách
  rõ hai khoá bí mật mà Zalo cấp: một khoá để gửi tin, một khoá để nhận tin. Trước đây chỉ có một
  ô nên tuỳ bạn dán khoá nào mà một trong hai chiều lặng lẽ không chạy.
- **Dấu "chưa đọc" nay là của riêng bạn.** Trước đây đồng nghiệp mở một hội thoại là dấu chưa
  đọc biến mất với **tất cả mọi người** — bạn không hề mở mà vẫn mất dấu, nên tin của khách trôi
  qua mà không ai để ý. Nay mỗi người có dấu chưa đọc riêng. Các hội thoại cũ giữ nguyên trạng
  thái như trước, không tự bật đỏ lại.
- **Hai người không còn giẫm chân nhau khi cùng bấm nhận một hội thoại.** Trước đây ai bấm sau sẽ
  âm thầm giành mất việc của người bấm trước, mà **cả hai đều thấy "của tôi"** rồi cùng trả lời một
  khách — khách nhận hai câu khác nhau từ một công ty. Nay người bấm sau được báo rõ **ai đang xử
  lý** hội thoại đó.

### 📌 Lưu ý
- Nếu mạng chập chờn hoặc đường truyền bị chặn, hộp thư **tự quay về cách cũ** là thỉnh thoảng tải
  lại — chậm hơn một chút nhưng không đứng im.
- Nên mở hộp thư ở **một thẻ trình duyệt** thôi. Mở quá nhiều thẻ cùng lúc có thể làm các thao tác
  khác trong app chậm đi.

---

## Phiên bản 26/08/2026 — Báo giá dựng xong đưa thẳng thành đơn trên CRM

### ✨ Tính năng mới
- **Đưa báo giá lên CRM ngay từ bước cuối.** Ở bước "Xuất báo giá" nay có nút **Đồng bộ lên CRM**.
  Bạn chọn đơn thuộc loại **Tour đoàn** hay **Khách lẻ**, bấm một cái là đơn hiện luôn bên CRM —
  không phải nhập lại tay. Trước đây tour dựng ở đây chỉ nằm lại trong phần tính giá, xong rồi
  không biết đưa đi đâu.
- Với **Tour đoàn**, bạn điền tên và số điện thoại khách; hệ thống tự tìm khách trong CRM theo số
  điện thoại, chưa có thì tạo mới. Với **Khách lẻ**, đơn được tạo theo số chỗ và giá mỗi khách
  đúng như bảng giá bạn vừa chốt.
- Tour đã đưa lên CRM sẽ hiện thẻ **"Đã lên CRM"** kèm mã đơn. Bấm đồng bộ lần nữa hệ thống sẽ
  nhắc thay vì tạo thêm một đơn trùng.

### 🔧 Đã khắc phục
- **Tài khoản đại lý / cộng tác viên không còn tự dựng được báo giá.** Trước đây những tài khoản
  chỉ được phép đặt chỗ vẫn vào được phần Tính giá Tour và tạo báo giá. Nay phần này chỉ mở cho
  người có quyền tạo tour trên CRM — ai không có quyền sẽ không thấy mục này trong menu, và mở
  thẳng đường dẫn cũng không vào được.
- **Nút đóng không còn che đồng hồ đếm ngược** ở màn hình nạp thêm lượt AI. Trước đây dấu × nằm
  đè lên ô đếm ngược nên không đọc được còn bao nhiêu phút để chuyển khoản.

### 📌 Lưu ý
- Với đơn **Khách lẻ**, hệ thống tự suy ra điểm đón, điểm trả từ hành trình và đoán phương tiện
  theo lịch trình — bạn nên mở đơn trên CRM xem lại hai mục này trước khi chốt.
- Đơn **Khách lẻ** không gắn kèm thông tin khách hàng (đây là loại tour bán theo chỗ). Cần lưu
  khách vào CRM thì chọn **Tour đoàn**.
- Việc đồng bộ là **bạn chủ động bấm**, không tự chạy khi gửi báo giá cho khách.

---

## Phiên bản 25/08/2026 — Chặt hơn ở phần quản trị, và vài lỗi âm thầm

### 🔧 Đã khắc phục
- **Trang quản trị: chỉ người đã đăng nhập mới xem/nạp được lượt AI.** Trước đây có một đường
  cũ cho phép xem lượt của mọi công ty và cộng lượt mà không cần đăng nhập, miễn là biết đường
  dẫn. Nút "Nạp lượt" bạn vẫn dùng không đổi gì.
- **Lưu nháp báo giá tour không còn báo lỗi.** Một số trường hợp bấm lưu nháp bị báo "không lưu
  được" dù không thiếu thông tin gì.
- **Giờ nhận thư hiển thị đúng theo múi giờ.** Trước đây giờ nhận của thư được lưu lệch, chỉ
  tình cờ hiện đúng trên máy đặt giờ Việt Nam; xem từ máy đặt múi giờ khác thì sai.

### 📌 Lưu ý
- Mật khẩu đăng nhập trang quản trị nay có thể để ở **dạng đã mã hoá** trong cấu hình. Bạn đăng
  nhập vẫn như cũ, không phải nhớ gì thêm.

---

## Phiên bản 25/08/2026 — Gợi ý trạng thái quay lại khi cấu hình bản tin

### 🔧 Đã khắc phục
- **Ô "trạng thái nào còn phải làm" nay có gợi ý sẵn trở lại.** Khi bạn mở phần cài đặt bản tin
  và chọn trạng thái cho Cơ hội bán hàng, hệ thống sẽ tự đánh dấu trước những trạng thái mà nó
  cho là còn đang chạy, bạn chỉ việc soát lại. Trước đây ô này thường trống trơn, bấm "Phân loại
  lại" bao nhiêu lần cũng không ra gì, và không có thông báo nào cho biết vì sao — nên nhiều
  người tưởng tính năng bị bỏ. Phần cài đặt cho Công việc vẫn chạy bình thường từ trước, nên rất
  dễ nghĩ là do dữ liệu của mình chứ không phải do hệ thống.
- Càng nhiều trạng thái thì càng hay gặp: công ty nào có danh sách trạng thái dài chính là công
  ty gần như không bao giờ thấy gợi ý.

---

## Phiên bản 25/08/2026 — Biết khách đã nhận và đã đọc tin chưa

### ✨ Tính năng mới
- **Dấu tích cho biết tin đã tới đâu.** Tin bạn gửi cho khách nay hiện rõ: một dấu là đã gửi đi,
  hai dấu là đã tới máy khách, hai dấu đậm là khách đã mở đọc. Trước đây mọi tin đều dừng ở một
  dấu nên không biết khách đã thấy chưa.
- Áp dụng cho **Zalo** (báo khi khách đã đọc) và **Facebook Messenger** (báo cả hai mức).
  **Telegram** không cung cấp thông tin này nên tin gửi qua Telegram chỉ hiện "đã gửi" — di chuột
  lên dấu tích sẽ thấy giải thích, đây không phải lỗi.

### 📌 Lưu ý
- Dấu tích chỉ chạy **tiến**, không lùi. Nếu nền tảng báo tin về muộn hơn thứ tự thật, hệ thống
  giữ mức cao nhất đã biết thay vì hạ xuống — nên bạn sẽ không thấy tin "đang đọc rồi lại chưa đọc".

---

## Phiên bản 25/08/2026 — Hộp thư chat: không sót tin, trả lời nhanh bằng /lệnh

### ✨ Tính năng mới
- **Mẫu trả lời nhanh.** Những câu bạn phải gõ đi gõ lại cho khách — bảng giá, lịch khởi hành,
  lời chào — nay lưu thành mẫu. Trong ô soạn tin, gõ dấu `/` rồi vài chữ đầu là danh sách mẫu
  hiện ra, bấm một cái là nội dung điền sẵn, sửa thêm rồi gửi. Lệnh gọi **không cần bỏ dấu**:
  mẫu đặt tên "giá" thì gõ `/gia` vẫn ra, không phải dừng lại bật bộ gõ tiếng Việt.
  Cả đội trực chat dùng chung một bộ mẫu, một người sửa là mọi người thấy ngay.

### 🔧 Đã khắc phục
- **Tin của khách không còn nguy cơ biến mất.** Trước đây nếu máy chủ được khởi động lại hoặc
  cập nhật đúng lúc khách vừa nhắn, tin đó có thể mất hẳn mà không ai biết — kênh đã coi như
  gửi thành công nên không gửi lại. Nay tin được ghi lại ngay khi vừa tới, rồi mới xử lý; máy
  chủ có tắt giữa chừng thì bật lên vẫn xử lý tiếp đúng tin đó.
- **Khách nhắn nhanh hai lần không còn bị bot trả lời hai lần.** Khi kênh gửi trùng cùng một
  tin, hệ thống nay nhận ra và bỏ qua bản thứ hai.
- **Ô soạn tin hết bị hai viền chồng nhau** khi bạn bấm vào để gõ.
- **Các nút trong màn hình chat nay cùng một cỡ, một kiểu.** Trước đó mỗi nút một dáng, nhìn
  lộn xộn và khó biết nút nào là hành động chính.
- **Khai kết nối kênh gọn hơn hẳn.** Trước đây cả ba kênh đổ hết ra một màn hình, mỗi kênh lại
  bọc thêm khung nên phải cuộn nhiều và rối mắt. Nay là ba thẻ chuyển qua lại, mỗi lúc chỉ hiện
  kênh bạn đang khai. Các ô nhập mã, khoá bí mật đều có **ví dụ mờ sẵn trong ô** để bạn biết
  mình dán đúng thứ chưa.
- **Ảnh và tệp bạn gửi cho khách không còn bị mất.** Khi hệ thống lưu tệp trên máy chủ của bạn
  (thay vì trên đám mây), tệp có thể bị ghi nhầm ra ngoài thư mục ứng dụng — hậu quả là mở lại
  hội thoại cũ thì ảnh hiện ô vỡ, và cập nhật phiên bản là mất luôn. Nay tệp luôn nằm đúng chỗ.
  Ảnh cũ đã bị mất trước đó thì không khôi phục được, phải gửi lại.
- **Báo lỗi khi đặt mẫu trả lời đã đọc được.** Bỏ trống lệnh gọi thì trước đây câu báo lỗi kèm
  một đoạn chữ kỹ thuật khó hiểu.

---

## Phiên bản 25/08/2026 — Nhà cung cấp từ AI không còn làm lỗi trang web

### 🔧 Đã khắc phục
- **Nhà cung cấp tạo bằng AI làm lỗi phần thẻ hướng dẫn viên bên web.** Khi bạn nhập bảng giá
  bằng AI, thông tin người liên hệ của nhà cung cấp được lưu theo một kiểu khác với lúc bạn nhập
  tay bên web. Hậu quả là mở đúng nhà cung cấp đó bên web rồi thêm/sửa/xoá thẻ hướng dẫn viên,
  xuất giấy giới thiệu, hay xem thống kê thẻ thì trang báo lỗi và không làm gì được. Nay thông
  tin người liên hệ được lưu giống hệt như khi nhập tay, nên các chức năng trên chạy bình thường.

### 📌 Lưu ý
- Bản sửa này áp dụng cho nhà cung cấp tạo **từ nay trở đi**. Những nhà cung cấp đã tạo bằng AI
  trước đó vẫn giữ kiểu lưu cũ và vẫn gây lỗi — nhờ bộ phận kỹ thuật chạy dọn một lần là xong.

---

## Phiên bản 24/08/2026 — Hộp thư chat: nhiều tài khoản, gửi ảnh, bật/tắt gọn

### ✨ Tính năng mới
- **Một công ty nối được nhiều tài khoản cho mỗi kênh.** Trước đây mỗi kênh chỉ khai được một nơi
  nhận. Nay bạn thêm bao nhiêu Trang Facebook, Zalo OA hay bot Telegram cũng được — mỗi chi nhánh
  một Trang, mỗi đội sale một bot. Tin của khách luôn được trả lời bằng đúng tài khoản mà khách đã
  nhắn tới, không lẫn sang nơi khác.
- **Khai kết nối kênh chuyển thành cửa sổ riêng.** Bấm "Kết nối kênh" là mở ra một cửa sổ gọn, khai
  xong đóng lại — danh sách hội thoại không còn bị đẩy tụt xuống mỗi lần bạn mở phần cài đặt.
- **Gửi ảnh và tệp cho khách.** Ô soạn tin có thêm nút kẹp giấy: chọn ảnh hoặc tệp, xem trước rồi
  mới bấm gửi, kèm được lời chú thích. Chọn nhầm thì gỡ ra trước khi gửi.
- **Xem được ảnh và tệp khách gửi.** Trước đây khách gửi ảnh thì bạn chỉ thấy một ô trống. Nay ảnh
  hiện ngay trong khung chat, bấm vào xem cỡ đầy đủ; tệp thì hiện tên và dung lượng để tải về; khách
  gửi vị trí thì bấm ra bản đồ.
- **Chọn được nơi lưu ảnh/tệp**: trên máy chủ của bạn, hoặc trên dịch vụ lưu trữ đám mây. Không cần
  đăng ký dịch vụ nào vẫn dùng được ngay.

### 🔧 Đã khắc phục
- **Tắt tính năng chat giờ tắt sạch.** Trước đây tắt rồi mà gõ thẳng địa chỉ trang vẫn vào được, và
  vài chức năng bên trong vẫn đáp lại thay vì báo đã tắt. Nay tắt là menu ẩn, trang báo rõ "chưa mở",
  và mọi chức năng của hộp thư chat đều ngừng hẳn — trong khi Trợ lý số liệu vẫn chạy bình thường.
- **Chữ trên bong bóng tin nhắn của bạn đã dễ đọc hơn.** Nền cam trước đây quá sáng nên chữ trắng
  nhoè, nhìn cả ngày thì mỏi mắt.
- **Giờ trong tin nhắn không còn lặp lại ngày.** Mỗi ngày đã có một vạch ngăn ghi "Hôm nay"/"Hôm qua",
  nên trong từng tin chỉ hiện giờ phút.
- **Lý do không gửi được chỉ hiện một lần.** Trước đây khi hết hạn trả lời khách, câu giải thích hiện
  ở hai chỗ trông như hai lỗi khác nhau.
- **Tiêu đề trang không còn bị thanh công cụ che.**

### 📌 Lưu ý
- Ảnh và tệp gửi qua chat được các nền tảng (Zalo, Facebook, Telegram) tự tải về từ đường dẫn công
  khai. Vì vậy **đừng gửi giấy tờ nhạy cảm** qua đường này.

## Phiên bản 21/08/2026 — Hộp thư chat

### ✨ Tính năng mới
- **Khách nhắn ở đâu cũng về một chỗ.** Có thêm mục **"Hộp thư chat"**: tin khách nhắn tới
  **Zalo OA, Facebook Messenger hoặc Telegram** đều chảy về cùng một hộp thư, trợ lý trả lời trước,
  bạn đọc và tiếp quản khi cần. Bấm **"Kết nối kênh"** để lấy địa chỉ nhận tin rồi dán vào trang
  quản trị của từng kênh. Mỗi hội thoại giao được cho một nhân viên, đóng lại khi xong, và tạm dừng trợ lý nếu
  muốn tự trả lời.

  Vài điểm đáng biết:
  - **Trợ lý tự im khi bạn vào cuộc.** Bạn gửi một tin là trợ lý ngừng nói trong 30 phút, để không
    có chuyện khách nhận hai câu trả lời khác nhau. Kể cả khi bạn trả lời từ chính ứng dụng Zalo.
  - **Ba màu cho ba bên** — khách, trợ lý, và nhân viên — nên nhìn là biết câu nào do máy trả lời.
  - **Mỗi kênh một hạn trả lời khác nhau** — Zalo 48 giờ, Messenger 24 giờ kể từ tin cuối của
    khách; Telegram thì không giới hạn. Quá hạn thì ô soạn tự khoá kèm lời giải thích, thay vì để
    bạn gõ xong mới báo không gửi được.
  - **Trợ lý không bịa giá.** Chưa nối dữ liệu tour nên gặp câu hỏi về giá hay lịch khởi hành, nó
    hỏi thêm thông tin và hẹn báo lại — không tự nghĩ ra con số.

### 📌 Lưu ý
- Tính năng đang **tắt mặc định**, cần bật cho từng công ty.
- Để khách nhắn được vào đây, công ty phải khai địa chỉ nhận tin trong trang quản trị Zalo OA.
- Instagram, WhatsApp và chat trên web làm sau.

---

## Phiên bản 20/08/2026 — Cảnh báo tới đúng người

### ✨ Tính năng mới
- **Nghe bản tin ngay trong TRAVAI.** Trước đây muốn nghe bản tin sáng bạn phải vào Bảng tin —
  tức là phải ngồi trước màn hình mới bấm được, trong khi bản tin lại đúng là thứ hợp để nghe
  lúc đang lái xe hay vừa ngủ dậy. Nay trên trang trợ lý giọng nói có nút **"Bản tin"**: mở ra
  là thấy danh sách bản tin gần đây (của bạn và của công ty), chọn cái nào bấm **Nghe** cái đó,
  bấm lần nữa để dừng.

- **Trang hướng dẫn cho Nhà cung cấp.** Phần nhập bảng giá nhà cung cấp bằng AI trước đây nằm lẫn
  trong bài *Tính giá Tour*, nên mở mục lục Hướng dẫn không thấy dòng nào tên nó — nhiều người
  tưởng chưa có hướng dẫn. Nay có bài riêng **"Nhà cung cấp & Import giá"**, kèm ảnh minh hoạ đầy
  đủ cho từng bước: tra cứu trong danh sách, xem nhanh bảng giá đã lưu, tải file lên, sửa chỗ AI
  bóc chưa đúng, và lưu vào hệ thống.

### 🔧 Đã khắc phục
- **Thư nhắc thu tiền gửi tour của người khác cho bạn.** Trên Bảng tin, cảnh báo "tour sắp đi mà
  khách chưa trả đủ" vốn chỉ hiện với người phụ trách tour đó. Nhưng bản gửi qua email lại gộp
  *tất cả* tour vào một thư rồi gửi cho *mọi* người đã bật nhận email — kèm tên khách và số tiền
  còn thiếu. Nay **mỗi người nhận một thư riêng, chỉ chứa tour của mình**. Ai chưa khai email thì
  cảnh báo vẫn nằm trên Bảng tin, và phần tóm tắt lần chạy nói rõ còn bao nhiêu người như vậy để
  bạn nhắc họ khai. Muốn một bản đầy đủ cho kế toán hay quản lý thì gõ địa chỉ vào ô
  *"Gửi thêm tới"* — chỗ đó vẫn nhận trọn danh sách như cũ.

- **Nhân viên đọc được số liệu của cả công ty.** Những thẻ không gắn với ai cụ thể — điển hình là
  *"doanh thu tuần vừa rồi giảm bao nhiêu phần trăm"* — trước đây hiện trên Bảng tin của **mọi**
  tài khoản trong công ty. Nay chỉ tài khoản có quyền **Cấu hình hệ thống** mới thấy, giống như
  phần cài đặt cấp công ty ở trang Tự động hoá. Thẻ có người phụ trách thì không đổi gì: vẫn chỉ
  người đó thấy.

- **Trang Hướng dẫn hiện chữ lạ chèn giữa câu.** Nhiều đoạn trong tài liệu bị chèn ký tự
  `<br>` ngay giữa dòng, đọc rất khó chịu. Lỗi có ở cả 9 bài, nay đã sạch. Phần khung ghi chú có
  gạch đầu dòng cũng được trình bày thành danh sách cho dễ đọc, thay vì dính liền vào câu trên.

### 📌 Lưu ý
- Nếu công ty bạn đang bật *"Gửi thêm qua email"* cho tác vụ **Canh thanh toán**, hãy nhắc nhân
  viên bán tour vào **Tự động hoá → Nơi nhận của tôi** khai email. Chưa khai thì họ sẽ không nhận
  được thư — trước đây thư đi vòng qua người khác nên chuyện này bị che mất.

---

## Phiên bản 20/08/2026 — Mở CRM báo đúng lỗi

### 🔧 Đã khắc phục
- **Bấm "Danh sách Khách hàng CRM" hiện một câu lỗi không ai hiểu.** Câu hiện ra là
  *"Không mở được CRM: Unexpected token '<'…"* — nghe như lỗi của TRAV-AI, nhưng thật ra bên CRM mới
  là nơi trục trặc, còn lời giải thích của nó bị chặn lại giữa đường nên không tới được màn hình.
  Nay bạn đọc được **đúng nguyên nhân** ngay trên thông báo, thay vì một câu lỗi kỹ thuật vô nghĩa.
  Áp dụng cho mọi nút mở sang CRM: danh sách khách hàng, danh sách cơ hội, và mở một cơ hội cụ thể.

### 📌 Lưu ý
- Bản này làm lỗi **hiện ra đúng**. Nếu bấm nút mà vẫn chưa mở được CRM, thông báo giờ sẽ nói rõ
  vướng ở đâu — gửi nguyên câu đó cho bộ phận kỹ thuật là đủ để xử lý.

---

## Phiên bản 19/08/2026 — Bản tin sáng gửi đúng nội dung

### 🔧 Đã khắc phục
- **Bản tin sáng gửi đi phần AI đang nghĩ, không phải bản tin.** Thư nhận được là một đoạn AI tự nhủ
  — *"Chúng ta cần trả lời câu hỏi…"*, *"Yêu cầu: Chọn tối đa 7 việc…"* — rồi đứt ngang giữa câu.
  Nguyên nhân: hạn mức chữ cho AI quá chật, nó nghĩ chưa xong đã hết chỗ nên chẳng viết được câu nào,
  và phần nghĩ dở bị đem đi gửi. Nay hạn mức đã nới rộng, và **nếu AI vẫn chỉ kịp nghĩ thì hệ thống
  tự chuyển sang bản tin dạng bảng số** thay vì gửi chữ vô nghĩa. Áp dụng cho cả bản tin nhân viên
  bán hàng lẫn bản tin điều hành.

### 📌 Lưu ý
- Trang theo dõi hàng đợi gửi (dành cho quản trị) nay hiện thêm cột **Kênh** và **người nhận đúng
  theo kênh**. Trước đây tin Telegram/Zalo luôn hiện "— chưa có người nhận" vì trang chỉ biết đọc
  địa chỉ email — tức đúng những dòng gửi hỏng lại là những dòng không tra được đã gửi cho ai.

---

## Phiên bản 18/08/2026 (bản 5) — Trang Hướng dẫn: đủ ảnh, mở được mọi bài

### ✨ Tính năng mới
- **Bài hướng dẫn "Trợ lý số liệu" nay có ảnh minh hoạ.** Trước đây bài này **không có một ảnh nào**
  — đọc toàn chữ, khó hình dung nút nào ở đâu. Nay đủ 9 ảnh cho cả 8 bước: màn đăng nhập, giao diện
  2 cột, danh sách gợi ý câu hỏi, kết quả số liệu, nút đổi kiểu biểu đồ, chế độ so sánh 2 kỳ, ghi âm
  bằng giọng nói, và bảng "Cách vận hành".
- **Bổ sung ảnh cho các bước còn thiếu** ở *Chấm điểm khách hàng*, *AI phân tích Cơ hội* và *Hộp thư
  AI* — đều là những bước đang chạy dở (thanh tiến trình chấm hàng loạt, đồng bộ hộp thư, hộp xác
  nhận trước khi gửi thư cho khách), tức đúng lúc người dùng hay phân vân "thế này là đúng chưa".

### 🔧 Đã khắc phục
- **Bài "Nhắc chăm lại khách ngủ quên" không mở được.** Bài đã viết xong nhưng chưa được khai vào
  danh mục, nên không có trong cột bên trái và bấm link từ bài *Tự động hóa* sang cũng chỉ quay về
  trang mục lục. Nay bài đã nằm trong danh mục và mở bình thường.
- **Bấm link dẫn sang hướng dẫn khác thì bị quăng về trang mục lục.** Mọi liên kết giữa các bài đều
  hỏng theo cùng một kiểu. Nay bấm là sang đúng bài, ngay trong trang Hướng dẫn.
- **Hướng dẫn TRAVAI chỉ đường tới những nút không còn tồn tại.** Bài bảo bạn "chọn giọng đọc và
  bấm Nghe thử" — hai nút đó đã bỏ từ khi giọng đọc chuyển sang chạy ở máy chủ, nên ai làm theo
  cũng không tìm thấy. Bài cũng nói chế độ "Luôn nghe" **chỉ dùng được trên máy tính** trong khi
  điện thoại đã dùng được, và nói cần trình duyệt Edge mới có giọng tiếng Việt — nay không còn phụ
  thuộc trình duyệt nào. Đã sửa lại toàn bộ theo đúng màn hình hiện tại.
- **Trang hướng dẫn hiện lẫn ghi chú nội bộ.** Dưới nhiều ảnh có dòng *"📸 Cần chụp: …"* — lời dặn
  cho người đi chụp màn hình, không phải nội dung để bạn đọc. Có bài hiện tới 12 dòng như vậy. Nay
  chỗ nào đã có ảnh thì dòng đó tự ẩn; chỗ nào ảnh chưa chụp mới giữ lại làm chỗ trống.

---

## Phiên bản 18/08/2026 (bản 4) — Đọc được nguyên văn kết quả mỗi lần chạy

### 🔧 Đã khắc phục
- **Bảng "20 lần gần nhất" cắt cụt mất phần đáng đọc nhất.** Mỗi dòng chỉ hiện được một hàng chữ,
  phần còn lại thay bằng dấu ba chấm — mà đúng những thứ bạn cần lại nằm ở **cuối câu**: bao nhiêu
  khách bị bỏ vì vừa nhắc rồi, bao nhiêu tour bỏ qua vì chưa gán người phụ trách, và tỉ lệ khách
  được gọi lại sau khi nhắc. Muốn đọc phải rê chuột vào chờ dòng chú thích hiện ra, mà gần như
  không ai biết để rê. Nay tóm tắt tự xuống dòng, đọc thẳng trong bảng.
- **Trên điện thoại thì còn khó đọc hơn nữa.** Hai cột phụ (chạy tay hay theo lịch, mất mấy giây)
  chiếm gần hết bề ngang, chừa cho phần tóm tắt vài chữ mỗi hàng. Nay xem trên điện thoại sẽ ẩn hai
  cột đó đi và hiện trọn nội dung — vì trên màn cảm ứng không có cách nào rê chuột để đọc phần
  bị giấu.

---

## Phiên bản 18/08/2026 (bản 3) — Nhắc chăm khách: hết nhắc trùng, và đo được là có ai gọi không

### ✨ Tính năng mới
- **📞 Biết được lời nhắc có ai làm theo không.** Lịch sử chạy của tác vụ *Nhắc chăm lại khách ngủ
  quên* nay có thêm dòng: *"Trong 26 khách đã nhắc 30 ngày qua, 6 người đã được liên hệ sau đó
  (23%)"*. Đây là con số cho bạn biết tính năng có đáng bật hay không — trước đây không có cách nào
  trả lời.
- **⏸️ Nhắc rồi thì thôi, không nhắc lại mỗi sáng.** Hai ô cấu hình mới: *nhắc lại sau bao nhiêu
  ngày* (mặc định 7) và *mỗi khách nhắc tối đa mấy lần* (mặc định 3). Bộ đếm **tự về 0 khi khách
  được chăm sóc thật**, nên khách đã gọi rồi mà sau này ngủ quên lại vẫn được nhắc tiếp.

- **📤 Nhắc chăm khách gửi được ra ngoài Bảng tin.** Bật *"Gửi thêm tới kênh riêng của nhân viên"*
  thì danh sách gọi điện đi thẳng tới email/Telegram/Zalo mà chính nhân viên đó đã khai — khỏi phải
  nhớ mở phần mềm. Ai chưa khai vẫn nhận trong Bảng tin như thường.
- **🙋 Khách chưa có ai phụ trách cũng có chỗ báo.** Khai email/Telegram/Zalo ở ô *"Khách chưa có
  người phụ trách"* thì nhóm này được gửi riêng cho người bạn chỉ định — để họ **gán người phụ
  trách**, chứ không phải gọi hộ một lần rồi đâu lại vào đấy. **Không khai thì bỏ qua như hiện
  nay**, chỉ đếm trong lịch sử chạy.

### 🔧 Đã khắc phục
- **Cùng một khách bị nhắc lại mỗi sáng cho tới khi có người gọi.** Danh sách phình to dần, vài tuần
  là không ai buồn mở nữa — đúng lúc nó bắt đầu có khách thật sự cần gọi.
- **Tác vụ chỉ nhìn 200 khách mới nhất.** Trong khi công ty có hàng chục nghìn khách, và nhóm dễ ngủ
  quên lại là khách *cũ*. Nay hệ thống nhờ phần mềm lọc sẵn đúng nhóm "lâu chưa chăm sóc" rồi mới
  lấy về — phủ hết, mà không chậm hơn.
- **Danh sách đứng im sau vài ngày.** Khi 20 khách chi nhiều nhất đều đã được nhắc, tác vụ báo
  "không có ai mới" và không bao giờ chuyển sang khách tiếp theo. Nay lọc trước rồi mới cắt, kèm ghi
  rõ *"còn N khách để lượt sau"*.
- **Dòng tóm tắt lần chạy trộn hai mốc số liệu** nên đọc ra con số sai. Nay mọi số trong câu đều quy
  về được: *"131 tới hạn → bỏ 20 đã nhắc → lấy 20 → 6 thẻ cho 6 nhân viên"*.

---

## Phiên bản 18/08/2026 (bản 2) — Cảnh báo gửi đúng người, không còn "ai cũng thấy"

### ✨ Tính năng mới
- **🎯 Cảnh báo giờ tới đúng người phụ trách.** Trước đây *Canh thanh toán*, *Sẵn sàng khởi hành* và
  *Nhắc chăm khách* đều đổ chung vào Bảng tin cho **cả công ty** — ai cũng thấy nghĩa là ai cũng
  nghĩ người khác lo. Nay mỗi thẻ ghi đích danh người đang phụ trách tour hoặc khách đó, chỉ người
  ấy thấy.
- **📋 Nhắc chăm khách chia theo từng nhân viên.** Trước là một danh sách chung dài dằng dặc; nay
  mỗi người một thẻ, chỉ chứa khách của mình — mở ra là biết hôm nay phải gọi ai.
- **📨 Cảnh báo doanh thu bất thường gửi được ra ngoài.** Thêm ô khai người nhận qua **email,
  Telegram, Zalo** — nhiều người thì cách nhau bằng dấu phẩy hoặc xuống dòng. Để trống thì cảnh báo
  vẫn vào Bảng tin như cũ. Đây là số liệu tài chính cả công ty nên cố ý bắt khai đích danh, không
  tự gửi cho mọi người.

### 🔧 Đã khắc phục
- **Cảnh báo ghi "Phụ trách: ?" dù hệ thống biết là ai.** Với một số loại tour, tên người phụ trách
  không được lấy ra nên thẻ hiện dấu hỏi, người đọc tưởng tour chưa giao cho ai. Nay hiện đúng tên.

### 📌 Lưu ý
- **Tour ghép (GIT) phần lớn không có người phụ trách riêng** — một chuyến nhiều người cùng bán.
  Những tour đó nay **được bỏ qua** thay vì báo cho cả công ty, và số bị bỏ qua được ghi rõ trong
  lịch sử chạy của tác vụ để bạn biết. Nếu muốn theo dõi nhóm tour này, hãy gán người phụ trách cho
  tour trong phần mềm.
- Chạy lại một tác vụ trong cùng ngày sẽ báo **"đã nhắc hôm nay"** và không tạo thẻ mới — đó là
  chống trùng, không phải lỗi.

---

## Phiên bản 18/08/2026 — Trang Tự động hoá gọn lại, phần khai Zalo dễ hiểu hơn

### ✨ Tính năng mới
- **📥 "Nơi nhận của tôi" và "Zalo OA của công ty" nay gấp gọn lại.** Hai khối này chỉ khai một lần
  rồi gần như không đụng nữa, nhưng trước đây luôn mở toang ở đầu trang — mỗi lần vào bạn phải cuộn
  qua cả chục ô đã điền xong từ lâu. Nay chúng nằm gọn một dòng, bấm vào mới mở ra.
- **👀 Đóng lại vẫn biết mình đã khai xong chưa.** Ngay trên dòng tiêu đề có một dấu nhỏ nói rõ tình
  trạng: đang nhận qua đâu, hay *"Thiếu nơi nhận: Email"*, hay *"Chưa khai — kênh Zalo chưa gửi
  được"*. Bật một kênh mà bỏ trống địa chỉ là lỗi trước giờ không hề báo — sáng hôm sau không thấy
  bản tin tới thì mới biết. Nay nhìn một cái là thấy.

### 🔧 Đã khắc phục
- **Không còn phải chọn "dùng OA nào".** Ô chọn giữa OA riêng và OA của nhà cung cấp gây phân vân
  không cần thiết: hai kiểu đều khai đúng bốn thông tin như nhau, chỉ khác chỗ lấy giá trị ở đâu.
  Nay bỏ hẳn ô chọn, chỉ còn bốn ô nhập.
- **"Refresh token lần đầu" không ai hiểu là gì.** Nay ô đó ghi đúng tên Zalo in trên màn hình của
  họ — *Refresh Token* — kèm hướng dẫn bốn bước "lấy ở đâu", và một dòng giải thích vì sao hệ thống
  cần chuỗi này chứ không phải chuỗi còn lại, cùng việc bạn chỉ phải dán đúng một lần.
- **Mở phần khai Zalo ra là thấy ngay dòng báo lỗi đỏ** dù bạn chưa làm gì sai. Nay chỉ báo khi bạn
  đã điền dở, còn ô trống thì không bị coi là lỗi.
- **Trên điện thoại, các ô nhập phình cao gấp mấy lần bình thường** và những dòng ghi chú bị xé thành
  các cột chữ chồng lên nhau, đọc không ra nghĩa. Nay hiển thị đúng.

---

## Phiên bản 17/08/2026 (bản 2) — Trang giới thiệu để Google và Zalo đọc được

### ✨ Tính năng mới
- **🔍 Trang giới thiệu giờ tìm thấy được trên Google.** Trước đây nội dung trang chỉ hiện ra sau khi
  trình duyệt chạy xong, nên máy tìm kiếm nhìn vào thấy trang trắng. Nay chữ được gửi kèm ngay từ
  đầu: tiêu đề, giới thiệu, tên 9 tính năng, ba bước bắt đầu.
- **🔗 Dán link ra Zalo, Facebook, LinkedIn giờ hiện ảnh và tiêu đề.** Trước đây chia sẻ link ra chỉ
  được một dòng trống, vì các nền tảng đó không chạy được trang. Nay có tiêu đề, mô tả và ảnh xem trước.
- **📄 Tiêu đề riêng cho từng trang.** Trước đây mọi trang đều mang một tiêu đề tiếng Anh chung. Nay
  mỗi trang một tiêu đề tiếng Việt đúng nội dung — nhìn tab trình duyệt là biết đang mở gì.

### 🔧 Đã khắc phục
- **Màn hình làm việc nội bộ có thể lọt vào kết quả tìm kiếm.** Hộp thư, Tự động hoá, Trợ lý… đều
  đang mở cho máy tìm kiếm thu thập. Nay chúng được đánh dấu không đưa vào kết quả — người ngoài tìm
  Google sẽ ra trang giới thiệu, không phải một màn hình đăng nhập.
- **Địa chỉ không tồn tại vẫn trả về như trang bình thường.** Gõ một đường dẫn sai thì hệ thống báo
  "vẫn ổn", nên máy tìm kiếm lưu lại cả những địa chỉ rác. Nay báo đúng là không tìm thấy.
- **Hai địa chỉ khác nhau cùng dẫn tới trang giới thiệu** khiến điểm đánh giá bị chia đôi. Nay đã
  khai rõ đâu là địa chỉ chính.

### 📌 Lưu ý
- Sau khi lên bản này, nên khai địa chỉ trang trong Google Search Console và gửi sơ đồ trang
  (`/sitemap.xml`) để Google đọc lại sớm hơn.

---

## Phiên bản 17/08/2026 — Khai nơi nhận một lần, và tin Zalo mang tên công ty bạn

### ✨ Tính năng mới
- **📬 "Nơi nhận của tôi" — khai một lần, dùng cho tất cả.** Email, Telegram và số Zalo của bạn nay
  nằm ở một khối riêng ngay đầu mục *Theo người dùng* trong *Tự động hoá*. Trước đây chúng nằm lẫn
  trong thẻ bản tin sáng, nên mỗi khi có thêm loại thông báo mới là bạn lại phải khai địa chỉ thêm
  một lần nữa. Nay khai một lần, mọi thông báo đều dùng — bản tin sáng, cảnh báo, và những thứ ra sau.
- **💬 Tin Zalo gửi bằng tài khoản OA của chính công ty bạn.** Trước đây tin nhắn Zalo đi qua tài
  khoản chung của bên cung cấp dịch vụ, nghĩa là khách của bạn nhận tin mang tên một công ty khác.
  Nay công ty tự khai OA riêng trong *Tự động hoá → Theo tổ chức*, ngay dưới tài khoản tự động.
  Mỗi loại thông báo khai một mẫu tin riêng, vì Zalo duyệt mẫu theo nội dung. Chưa khai xong thì
  Zalo không gửi và nói rõ còn thiếu gì — hệ thống **không** tự gửi thay bằng tài khoản của bên khác.
- **📧 Cảnh báo thu tiền gửi được qua email.** Bật trong thẻ *Canh thanh toán trước khởi hành*: mỗi
  lần quét gửi **một thư gộp** mọi tour còn nợ tới những người đã khai email ở "Nơi nhận của tôi",
  chứ không phải mỗi tour một thư. Cần thêm hộp thư chung của kế toán thì điền riêng.
- **🔁 Giới hạn số lần nhắc.** Cũng trong thẻ đó: mỗi tour nhắc nhiều nhất một lần mỗi ngày, và dừng
  hẳn sau số lần bạn đặt (mặc định 3). Trước đây tour còn nợ bị nhắc lại **mỗi ngày cho tới lúc khởi
  hành** — nới cửa sổ lên 30 ngày là 30 lần cho cùng một tour.
- **⚙️ Chọn được loại tour cần quét, số ngày và mức nợ đáng nhắc.** Thẻ *Canh thanh toán* trước đây
  không có tuỳ chọn nào, mọi thứ cố định sẵn.

### 🔧 Đã khắc phục
- **Hai tác vụ về tour chỉ nhìn thấy một loại tour.** *Kiểm tra sẵn sàng khởi hành* và *Canh thanh
  toán* thực ra chỉ đọc được tour lẻ (FIT). Hậu quả: phần nhắc hồ sơ visa **chưa từng chạy lần nào**,
  phần canh chỗ ngồi không thấy tour ghép nào, và nợ của tour ghép không ai canh. Tệ nhất là nó
  **trông vẫn bình thường** — vẫn báo "đã quét N tour". Nay bạn tự chọn loại tour cần quét (mặc định
  tour lẻ + tour ghép), và nếu bật kiểm visa mà chưa chọn loại phù hợp thì hệ thống nói thẳng là
  phần đó không chạy.
- **Ô "loại tour cần visa" bắt gõ mã số khó hiểu.** Trước đây ô này yêu cầu nhập con số như `102` mà
  không ai ngoài người viết phần mềm biết nghĩa. Nay là danh sách chọn có tên thật, kèm giải thích
  rõ đây là "loại đơn nào được coi là hồ sơ visa" chứ không phải "tour nào đi nước ngoài".
- **"Còn nợ" nay tính đúng như bộ lọc trên màn hình tìm kiếm tour.** Trước đây tác vụ tự lấy doanh
  thu trừ đã thu, có thể khác con số bạn thấy khi bấm lọc *Chưa thu hết*. Nay dùng chính bộ lọc đó,
  nên cảnh báo và màn hình luôn nói cùng một con số.
- **Số tiền trên bảng tin hiện không thống nhất.** Hai thẻ cạnh nhau có thể hiện `7.350.000đ` và
  `7,350,000đ`, tuỳ máy chủ nào chạy. Nay thống nhất theo cách viết Việt Nam.
- **Sửa email trong bản tin có thể vô tình tắt đăng ký của bạn.** Nay chỗ khai địa chỉ tách riêng nên
  không còn đụng tới loại bản tin và giờ nhận.

### 📌 Lưu ý
- Cụm bản tin và cảnh báo vẫn nằm sau công tắc bật/tắt riêng, mặc định **chưa mở**. Chưa thấy trên
  máy bạn thì không phải hỏng.
- Muốn dùng Zalo, công ty cần chuẩn bị: OA riêng, mã ứng dụng, khoá bí mật, **mã làm mới lần đầu**
  (lấy khi cấp quyền cho ứng dụng trên trang quản lý OA), và mẫu tin đã được Zalo duyệt cho từng
  loại thông báo.

---

## Phiên bản 15/08/2026 — Ghi chú khách hàng hiện đúng tiếng Việt

### ✨ Tính năng mới
- **📉 Tự canh doanh thu bất thường.** Bật **Canh doanh thu bất thường** trong *Tự động hoá*: mỗi lần
  chạy, hệ thống so doanh thu tuần vừa rồi với mức thường của mấy tuần trước. Lệch quá ngưỡng bạn đặt
  thì báo vào **Bảng tin** — *"tuần vừa rồi giảm 42% so với mức thường 820 triệu/tuần của 4 tuần trước"*.
  Bạn tự chọn lấy mấy tuần làm mức thường và lệch bao nhiêu phần trăm thì báo, vì ngành tour lên xuống
  theo mùa — đặt thấp quá thì tuần nào cũng có cảnh báo, mà cảnh báo tuần nào cũng có thì không còn là
  cảnh báo. Tăng vọt cũng được báo (ở mức thông tin, không tô đỏ) và tắt riêng được nếu bạn chỉ muốn
  nghe tin xấu. **Không tốn lượt AI.**
- **📞 Nhắc gọi lại khách cũ đã lâu không ai liên hệ.** Bật **Nhắc chăm lại khách ngủ quên**: hệ thống
  tìm những khách đã từng mua nhưng lâu rồi không ai chăm, gom thành **một** danh sách trong Bảng tin —
  kèm hạng khách, số tiền họ đã chi, số ngày im lặng và **số điện thoại để gọi ngay**. Khách chi nhiều
  xếp lên trước, vì người gọi chỉ đọc mấy dòng đầu.
  Bạn đặt bao lâu thì coi là "ngủ quên", chỉ nhắc hạng nào, và mỗi lần tối đa mấy khách.
  **Hệ thống KHÔNG gửi gì cho khách hàng** — chỉ nhắc bạn, còn gọi ai và nói gì là bạn quyết.
  **Không tốn lượt AI.**
- **📈 Bản tin điều hành nói luôn "đà này có kịp kế hoạch không".** Khai chỉ tiêu doanh thu tháng
  trong phần cài đặt của *Bản tin điều hành*, mỗi sáng bạn sẽ thấy thêm một dòng: theo tốc độ bán từ
  đầu tháng tới nay thì cả tháng ước đạt bao nhiêu, bằng bao nhiêu phần trăm kế hoạch, và một câu
  nhận định — *vượt kế hoạch* / *hụt nhẹ, còn kịp bù* / *khó đạt*.
  Số thực đạt và chỉ tiêu luôn in kèm để bạn tự đối chiếu.
  **Bốn ngày đầu tháng hệ thống chỉ báo số thực đạt chứ không ước** — sớm quá thì một hợp đồng lớn
  cũng đủ làm con số sai lệch nhiều lần, mà đây là bản tin gửi giám đốc.
  Chưa khai chỉ tiêu thì bản tin giữ nguyên như cũ, không hiện gì thêm.
- **🎟️ Biết tour nào sắp đầy để đẩy bán nốt.** *Kiểm tra sẵn sàng khởi hành* trước đây chỉ báo cái
  còn thiếu. Nay tour ghép gần kín chỗ cũng được nhắc — *"đã kín 17/20 chỗ, còn 3 chỗ"* — để bạn dồn
  sức bán nốt vài chỗ cuối thay vì để trống. Đây là tin vui nên thẻ hiện ở mức thông tin, không tô đỏ
  như cảnh báo. Tour đã đầy hẳn thì không nhắc, vì chẳng còn gì để bán.
- **📅 Chuyện chỗ ngồi được soát sớm hơn hẳn.** Phần tiền và hồ sơ visa vẫn nhắc ở mốc 7 / 3 / 1 ngày
  trước khi đi. Riêng chuyện chỗ ngồi nay soát từ **3 tuần trước** (21 / 14 / 7 ngày) — vì bán nốt
  chỗ cuối hay quyết định dồn chuyến mà tới sát ngày mới biết thì đã hết đường xoay. Bạn tự đổi được
  các mốc này trong phần cài đặt của tác vụ.

### 🔧 Đã khắc phục
- **📝 Ghi chú chăm sóc không còn hiện thành chữ lạ.** Ghi chú bạn gõ trong CRM có dấu tiếng Việt —
  ví dụ *"không có nhu cầu"* — khi hiện lại trong TRAV-AI bị biến thành `khong c&oacute; nhu cầu`,
  đọc không ra. Nay hiện đúng như lúc bạn gõ.
  Chỗ nào cũng được sửa: **thẻ chuẩn bị gặp khách**, **bản chấm hạng khách hàng** và **chấm điểm cơ
  hội bán hàng**. Đáng nói hơn phần nhìn: trước đây trợ lý cũng phải đọc bản chữ méo đó, nên có lúc
  hiểu sai ý ghi chú và khuyên lệch — nay nó đọc đúng thứ bạn viết.

- **👥 Hết báo nhầm "chưa đủ khách" cho tour đã đủ.** Khi đếm khách của một tour, hệ thống bỏ sót
  những chỗ đang **giữ**. Tour đã kín 7 trên 20 chỗ bị tính thành 6, nên công ty đặt mức tối thiểu 7
  vẫn nhận cảnh báo "chưa đủ khách" — trong khi tour đã đủ. Nay chỗ đang giữ được tính là đã chiếm,
  và thẻ nói rõ *"7/20 chỗ (6 đã đặt + 1 giữ chỗ)"* để bạn biết phần nào còn có thể rơi.

- **🔊 Nghe bản tin dài không còn bị cụt giữa chừng mà không biết.** Nút **Nghe** đọc được một độ dài
  nhất định. Trước đây bản tin vượt quá thì tiếng nói dừng giữa từ, không báo gì — bạn nghe xong tưởng
  bản tin chỉ có thế. Nay chỗ dừng rơi vào cuối câu, và có dòng nhắc *"bản tin dài, chỉ đọc được phần
  đầu"* để bạn biết phần còn lại cần đọc bằng mắt. Bản tin dài bình thường vẫn đọc trọn như cũ.

### 📌 Lưu ý
- Vì nội dung ghi chú nay khác trước, những khách đã chấm hạng sẽ được **chấm lại một lượt** cho khớp
  với chữ đã sửa — mỗi công ty khoảng một, hai trăm khách, xong là thôi. Bạn không cần làm gì cả.

- **Hai tính năng mới mở dần theo từng công ty.** *Kiểm tra sẵn sàng khởi hành* và *Hỏi trợ lý trước
  khi đi gặp khách* (giới thiệu ở bản 14/08) được bật riêng cho từng công ty, không mở đại trà ngay.
  Nên nếu bạn vào *Tự động hoá* mà chưa thấy thẻ **Kiểm tra sẵn sàng khởi hành**, hoặc hỏi trợ lý
  *"chuẩn bị giúp tôi gặp khách A"* mà nó trả lời như tra cứu khách thường — **không phải hỏng**, chỉ
  là công ty bạn chưa được mở. Nhắn cho bên hỗ trợ là bật được ngay, không mất dữ liệu gì.

---

## Phiên bản 14/08/2026 — Nhắc trước ngày đi và chuẩn bị trước khi gặp khách

### ✨ Tính năng mới
- **🛫 Nhắc kiểm tra tour trước ngày khởi hành.** Bật **Kiểm tra sẵn sàng khởi hành** trong *Tự động hoá*,
  hệ thống sẽ soát các tour sắp đi và báo vào **Bảng tin** những tour còn thiếu điều kiện: chưa thu đủ
  tiền, chưa đủ khách tối thiểu, hoặc là tour cần visa nên nhớ soát lại hồ sơ. Mỗi tour chỉ nhắc **ba
  lần** — còn 7 ngày (còn kịp xoay), còn 3 ngày (cảnh báo) và ngày cuối — thay vì nhắc lại mỗi sáng cho
  tới lúc bạn không buồn đọc nữa. Tour nào đủ điều kiện thì im lặng, không làm phiền.
  Bạn tự chọn mốc nhắc, mục nào cần soát, và mức khách tối thiểu của công ty mình.
- **🤝 Hỏi trợ lý trước khi đi gặp khách.** Nói với trợ lý *"chiều nay tôi gặp khách A, chuẩn bị giúp
  tôi"* — trợ lý gom lại khách đó là ai, đã đi những tour nào, từng phàn nàn gì, thư gần nhất đã được
  trả lời chưa, rồi gợi ý **nên nói gì** và **cần tránh gì** trong một phút. Bảng bên phải hiện đúng
  những dữ liệu đã dùng, để bạn tra lại con số nào lấy từ đâu.
  Chỉ chạy khi bạn hỏi, nên không tốn lượt cho những cuộc gặp bạn không cần chuẩn bị.

### 🔧 Đã khắc phục
- **📬 Thư giống hệt nhau không còn bị xếp vào các nhóm khác nhau.** Trước đây cùng một loại thông báo
  gửi đi gửi lại có lần bị coi là *Spam*, có lần *Khác*, có lần *Xác nhận* — mở Hộp thư AI ra thấy lộn
  xộn không hiểu vì sao. Nay hệ thống được chỉ rõ từng nhóm nghĩa là gì và xử lý thế nào khi phân vân,
  nên cùng một thư sẽ luôn vào cùng một nhóm.
- **📥 Thông báo của hệ thống không còn bị coi là spam.** Thư nội bộ, biên nhận, cảnh báo bảo mật — kể
  cả **bản tin sáng của chính TRAV-AI** — từng bị xếp vào *Spam* và nằm im ở đó. Nay chúng vào *Khác*
  để bạn vẫn nhìn thấy. Ngược lại, thư quảng cáo vẫn nằm đúng chỗ trong *Spam*.
- **🔁 Sửa được nhóm của một email đã phân loại sai.** Trước đây mỗi email chỉ được phân loại đúng một
  lần lúc tải về, xếp nhầm là chịu. Nay mở email lên, bấm **↻ Phân loại lại** ngay cạnh tên nhóm.
- **📨 Email chuyển tiếp không còn mở ra trắng trơn.** Khi ai đó chuyển tiếp thư cho bạn theo kiểu
  *đính kèm nguyên thư gốc* (Outlook và nhiều phần mềm công ty làm vậy), Hộp thư AI chỉ hiện phần vỏ —
  thường là trống — nên nhìn như email lỗi. Nay nội dung thư gốc được hiện đầy đủ bên dưới một dòng
  phân cách ghi rõ ai gửi, ngày nào, tiêu đề gì; thư chuyển tiếp qua nhiều người vẫn đọc được tới thư
  trong cùng.
- **📎 Biết được email có tệp đính kèm và tên tệp là gì.** Trước đây email mà toàn bộ nội dung nằm
  trong tệp Excel/PDF (báo cáo, hợp đồng, bảng giá) mở lên chỉ thấy mấy dòng chữ ký — không có dấu
  hiệu nào cho biết là có tệp, nên trông như email lỗi. Nay cuối thư có dòng liệt kê tên các tệp, tính
  cả tệp nằm trong thư được chuyển tiếp. Logo trong chữ ký không bị tính là tệp đính kèm.

### 📌 Lưu ý
- Nếu trợ lý chưa viết được phần gợi ý (mạng lỗi, hết lượt AI), bạn vẫn thấy đầy đủ dữ liệu thô về
  khách — sắp bước vào gặp mà báo lỗi trắng thì không giúp được gì.
- Những email đã tải về từ trước vẫn giữ nhóm cũ. Muốn sửa thì cho phân loại lại từng email — hệ thống
  cố ý không tự chạy lại toàn bộ hộp thư vì mỗi email tốn một lượt AI.
- Email chuyển tiếp **đã nằm sẵn trong hộp thư** thì bấm **↻ Đọc lại nội dung** ở đầu trang một lần —
  hệ thống tải lại nội dung từ Gmail cho các email cũ. Nhóm phân loại, trạng thái xử lý và nháp bạn
  đang soạn được giữ nguyên, và thao tác này **không tốn lượt AI** nào.
- Hộp thư AI **chưa mở/tải được tệp đính kèm** — mới chỉ cho biết là có tệp và tên tệp. Nội dung nằm
  trong tệp Excel/PDF thì bạn vẫn phải mở bằng Gmail.

---

## Phiên bản 14/08/2026 — Bản tin đến đúng giờ và tự cấu hình được

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
- **🎛️ Tự chọn bản tin sáng gồm những mục nào.** Trong *Tự động hoá* → thẻ **Bản tin sáng cho nhân viên
  bán hàng**, mỗi mục (cơ hội cần gọi lại, cơ hội cần dọn hồ sơ, báo giá bỏ dở, lịch hẹn, việc cần làm,
  tour thiếu tiền, khách quen, hộp thư) nay có công tắc riêng — không cần thì tắt, bản tin gọn lại đúng
  thứ bạn quan tâm. Mục nào có ngưỡng riêng (bao nhiêu ngày thì coi là "im lặng", "kẹt", "bỏ dở") thì
  ô chỉnh nằm ngay dưới công tắc của mục đó.
- **🏷️ Chọn đúng trạng thái cơ hội cần chăm.** Mỗi công ty đặt tên trạng thái một kiểu, nên thay vì để
  hệ thống đoán, bạn chọn thẳng những trạng thái mà nhân viên CÒN phải chăm (ví dụ "Đang tư vấn", "Đã
  báo giá"). Để trống thì hệ thống vẫn tự bỏ qua các cơ hội đã hủy / đã chốt như trước.
- **📋 Bản tin điều hành cho biết còn bao nhiêu việc chưa xong.** Thêm mục *Công việc chưa hoàn thành*:
  tổng số việc của cả công ty còn treo, kèm số việc đã quá hạn. Trạng thái nào tính là "chưa xong" thì
  bạn tự chọn — mỗi công ty đặt tên khác nhau, có nơi "Đang kiểm tra" nghĩa là đã làm xong chờ duyệt.
- **🤖 AI viết lại bản tin cho dễ đọc.** Số liệu vẫn do hệ thống tính, AI chỉ sắp xếp và diễn đạt lại
  cho mạch lạc, nêu rõ nên làm gì trước. Có thể tắt trong cấu hình nếu bạn thích bản liệt kê thuần.
- **🔎 Xem ngay hệ thống đang hiểu trạng thái của bạn thế nào.** Trong *Tự động hoá* → **Theo tổ chức**
  → *Luật chung của bản tin* có khối **“Cách hiểu trạng thái của công ty bạn”**: bấm **Xem cách hiểu** để
  thấy từng trạng thái đang được xếp là *còn phải làm* hay *đã xong*, và **Phân loại lại** để nhờ AI đọc
  lại — không cần mở từng mục cấu hình đi tìm.
- **🧠 Hệ thống tự hiểu tên trạng thái của công ty bạn.** Mỗi nơi đặt tên một kiểu — "Kết thúc", "Đã
  bàn giao", "Chốt đơn" — nên lần đầu mở phần cấu hình, hệ thống tự đọc danh sách trạng thái của chính
  công ty bạn và chọn sẵn những trạng thái *còn phải chăm*. Bạn xem được nó đang hiểu từng trạng thái
  ra sao, sửa lại nếu chưa đúng, hoặc bấm **Phân loại lại**. Chỉ chạy một lần rồi nhớ, đổi tên trạng
  thái trong CRM thì tự nhận ra và làm lại.
- **✅ Chọn thế nào là việc "chưa xong".** Mục *Việc cần làm hôm nay* nay cho bạn chỉ định những trạng
  thái còn phải làm — ví dụ có công ty coi "Đang kiểm tra" là đã xong, chờ duyệt.
- **📊 Bản tin điều hành cũng cấu hình được.** Trước đây bản tin cho giám đốc cố định một khuôn. Nay
  chọn được **kỳ so sánh** (cùng kỳ tháng trước / cùng kỳ **năm** trước / không so sánh — du lịch theo
  mùa nên so tháng trước dễ đánh lừa), bật/tắt từng mục (top nhân viên và lấy mấy người, cơ hội mới,
  lịch hẹn, cảnh báo thanh toán), và chọn có để AI viết lời hay in thẳng bảng số.
- **🔀 Chọn loại bản tin bằng một ô chọn.** Trước đây hai loại bản tin bày thành hai thẻ cạnh nhau,
  dễ tưởng phải khai cả hai. Nay chỉ còn một thẻ, chọn loại ở ô trên cùng.
- **🧭 Tách rõ phần của bạn và phần của công ty.** Trong *Tự động hoá*: mục **Theo người dùng** chỉ còn
  đăng ký nhận của riêng bạn (nhận hay không, mấy giờ, ở đâu); còn các luật áp cho mọi người — đưa mục
  nào vào bản tin, ngưỡng bao nhiêu ngày, trạng thái nào còn phải chăm — chuyển xuống khối **Luật chung
  của bản tin** trong mục *Theo tổ chức*, một người khai một lần.
- **🔘 Bật nhận là đủ.** Trước đây phải bật hai chỗ mới nhận được tin: đăng ký của bạn và lịch gửi của
  công ty. Nay bạn bật nhận là hệ thống tự lo phần còn lại.

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
- **Bản tin sáng bảo gọi lại cơ hội đã hủy.** Trước đây mục "cơ hội cần gọi lại" gom cả những cơ hội đã
  hủy hoặc đã chốt xong — nhân viên mở ra thấy một danh sách dài toàn việc không cần làm. Nay chỉ còn
  cơ hội đang thực sự theo đuổi.
- **Một cơ hội xuất hiện ở hai mục cùng lúc.** Cùng một cơ hội vừa nằm ở "cần gọi lại" vừa ở "cần dọn hồ
  sơ", đọc xong tưởng có hai việc. Nay mỗi cơ hội chỉ nằm ở đúng một mục.
- **Form cấu hình khó đọc, chỉnh một mục phải tìm ở ba chỗ.** Trước đây các ô được xếp theo kiểu dữ liệu
  nên công tắc một chỗ, ngưỡng ngày một chỗ khác. Nay mọi thứ của cùng một mục nằm liền nhau, và nhãn
  với ô nhập đứng cùng một dòng thay vì mỗi ô một dòng như cũ — nhìn hết cấu hình mà không phải cuộn dài.
- **Không tìm thấy chỗ đặt giờ nhận bản tin.** Khối *Bản tin của tôi* — nơi chọn giờ nhận và nơi nhận —
  bị mất khỏi màn hình từ một bản cập nhật trước đó trong ngày, không báo lỗi gì cả nên chỉ đơn giản là
  không còn chỗ nào để đặt. Nay đã trở lại, và nếu vì lý do nào đó không nạp được thì màn hình sẽ nói rõ
  thay vì im lặng bỏ trống.
- **Bản tin nhắc đi gặp khách mà cuộc hẹn đã xong.** Mục *Lịch hẹn hôm nay* và *Việc cần làm* trước đây
  gom cả cuộc hẹn đã đánh dấu xong và việc đã hoàn thành/đã huỷ. Nay chỉ còn thứ thật sự phải làm.
- **Bản tin nhắc lại cuộc hẹn đã trôi qua.** Hẹn quá ngày thì không làm bù được nữa, nhắc chỉ làm bản
  tin dài thêm — nay bỏ hẳn. Việc quá hạn thì vẫn nhắc, vì vẫn làm được.
- **Không hiểu ô "tần suất kiểm tra" để làm gì.** Nay ô này đổi tên thành *Kiểm tra ai đến giờ, mỗi…* và
  nói rõ: bản tin không gửi theo giờ đặt ở đó, mỗi người tự chọn giờ nhận riêng; con số này chỉ quyết
  định bản tin có thể lệch giờ tối đa bao nhiêu.
- **Nút bật/tắt nơi nhận trông khác phần còn lại của trang.** Phần *Nơi nhận* dùng ô tick mặc định của
  trình duyệt trong khi cả trang dùng công tắc — nay đã thống nhất.
- **Bản tin điều hành ghi sai đơn vị tiền.** Có lúc bản tin viết "lỗ 644 tỷ" trong khi con số thật là
  644 triệu — lệch đúng một bậc. Nguyên nhân: phần viết lời tự quy đổi sang tỷ/triệu. Nay mọi cách đọc
  đều do hệ thống tính sẵn, phần viết lời không được tự đổi đơn vị nữa.
- **Bản tin điều hành nói "hôm nay" cho số của cả nửa tháng.** Ba con số doanh thu – chi phí – lợi nhuận
  là luỹ kế từ đầu tháng, nhưng bản tin mở đầu bằng "tình hình hôm nay…". Nay nêu rõ khoảng ngày.
- **Lãi chuyển thành lỗ chỉ hiện ra một con số phần trăm khó hiểu.** Trước đây ghi "-101%" — vừa vô
  nghĩa vừa đọc nhẹ hơn thực tế, trong khi đó mới là điều đáng lưu ý nhất. Nay ghi thẳng *chuyển từ lãi
  sang lỗ*; lỗ nặng thêm cũng ghi rõ là *lỗ nặng thêm bao nhiêu %* thay vì một dấu cộng gây hiểu ngược.
- **Số lỗ dễ bị đọc nhầm thành lãi.** Dòng "Lợi nhuận: -644.211.149đ" bắt người đọc tự để ý dấu trừ giữa
  một dãy chữ số. Nay ghi thẳng **Lỗ: 644.211.149đ**.
- **Bản tin đem doanh số nhân viên so với doanh thu công ty.** Hai con số này đo theo hai cách khác nhau
  nên cộng lại không khớp, đọc xong tưởng số liệu mâu thuẫn. Nay bản tin không đặt chúng cạnh nhau nữa.
- **Bản tin kết luận "kinh doanh khó khăn" từ số liệu chưa kiểm.** Khi một chỉ số biến động quá mạnh
  (thường là do dữ liệu chưa nhập đủ), bản tin nay nhắc *cần kiểm tra lại số liệu* thay vì khẳng định
  công ty đang tốt hay xấu.
- **Bản tin điều hành chỉ đọc lại bảng số bằng lời.** Nay luôn kết bằng 1–2 câu khuyến nghị nên làm gì
  tiếp, bám đúng số đang có.
- **Chưa nhập chi phí thì bản tin trông như đang lãi trọn doanh thu.** Công ty chưa ghi chi phí vào hệ
  thống sẽ thấy "Chi phí: 0đ" và dòng lợi nhuận bằng đúng doanh thu — đọc lướt là tưởng lãi hết. Nay ghi
  rõ *Chi phí: chưa ghi nhận trong hệ thống* và *Lợi nhuận (CHƯA trừ chi phí)*.
- **Kết luận "kinh doanh khả quan" từ mức tăng phần trăm trên nền doanh thu nhỏ.** Vài chục triệu thì
  "+21%" chỉ là vài triệu. Nay bản tin nói con số tuyệt đối thay vì nhấn vào phần trăm.
- **Số đếm không có dấu phân cách nghìn.** "2345 cuộc hẹn quá hạn" nay là "2.345 cuộc", khớp với cách
  hiển thị của mọi số tiền quanh nó.
- **Bản tin sáng có lúc bỏ sót nguyên một mục.** Đã gặp: 47–61 việc cần làm — trong đó có việc ưu
  tiên cao — biến mất khỏi bản tin, mà dòng tổng kết cuối thư cũng không nhắc, nên đọc xong tưởng hôm
  nay không có việc gì. Nay mục nào có dữ liệu thì bản tin luôn nhắc tới ít nhất một lần; riêng *lịch
  hẹn hôm nay* và *tour sắp đi còn thiếu tiền* thì bắt buộc liệt kê — đó là loại trễ một ngày là mất.
  Dòng tổng kết cuối thư cũng đã phủ đủ mọi mục thay vì chỉ một nửa.
- **Hết lượt AI làm hỏng cả phần chọn sẵn trạng thái, mà không nói vì sao.** Công ty hết lượt AI sẽ
  không được phân loại trạng thái, hệ thống lặng lẽ quay về cách đoán theo từ khoá quen thuộc (Hủy, Đã
  chốt…) — với công ty đặt tên trạng thái theo quy trình riêng thì cách đoán đó gần như không lọc được
  gì, dẫn tới bản tin nhắc cả cơ hội đã đóng. Nay: **việc chọn sẵn trạng thái không còn tính vào số lượt
  AI** (đây là bước cài đặt, không phải bạn dùng AI), nên công ty nào cũng chạy được ngay. Nếu vì lý do
  khác mà chưa có kết quả, màn hình nói rõ nguyên nhân thay vì im lặng.
- **Số việc quá hạn lớn hơn cả tổng số việc chưa xong.** Bản tin có lúc ghi "335 việc chưa hoàn thành,
  trong đó 591 việc đã quá hạn" — hai con số đếm theo hai cách khác nhau nên đặt cạnh nhau thành vô lý.
  Nay cả hai đếm trên đúng những trạng thái bạn đã chọn.

### 📌 Lưu ý
- Ô **"Trong app"** trong phần *Bản tin của tôi* nay luôn bật và không tắt được — đó là nơi lưu bản tin
  để bạn xem/nghe lại, không phải một kênh gửi.
- Tin nhắn Zalo là **lời nhắc ngắn** kèm ngày, không phải toàn bộ bản tin — đây là giới hạn của Zalo.
  Nội dung đầy đủ bạn đọc ở tab **Bảng tin**.
- **Công ty phải khai luật chung trước thì mọi người mới đăng ký nhận được.** Chưa khai mà cho bật thì
  bản tin sẽ chạy bằng thiết lập mặc định chưa ai xem qua, nhắc theo ngưỡng phỏng đoán — thà chặn lại và
  chỉ đúng chỗ cần khai. Nếu bạn thấy ô *Nhận bản tin này* bị mờ, nhờ người phụ trách vào *Tự động hoá*
  → **Theo tổ chức** → *Luật chung của bản tin* khai rồi bấm **Lưu cấu hình**.
- Danh sách trạng thái chọn sẵn là **phỏng đoán theo tên** — hệ thống không có cách nào biết chắc trạng
  thái nào của công ty bạn nghĩa là "đã xong". Xem lại một lượt rồi bấm Lưu; sau đó lời nhắc sẽ tự mất.

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
