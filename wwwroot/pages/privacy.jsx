// pages/privacy.jsx — Chính sách bảo mật + hướng dẫn xoá dữ liệu.
//
// Vì sao có trang này: Meta BẮT BUỘC hai đường dẫn công khai trước khi cho ứng dụng chuyển sang
// Live — "Privacy Policy URL" và "Data Deletion Instructions". Thiếu một trong hai là App Review
// bị trả về, và trước đó thì mọi tài khoản Facebook không có vai trò trong ứng dụng đều gặp màn
// "Ứng dụng không hoạt động".
//
// Trang phải mở được KHÔNG cần đăng nhập: người duyệt của Meta không có tài khoản trong hệ.
// Vì thế nó khai ở app.jsx cùng nhánh với /landing, không nằm sau LoginGate.
//
// ⚠️ Nội dung ở đây là mô tả THẬT cách hệ thống đang chạy, không phải văn mẫu. Sửa luồng dữ liệu
// (thêm kênh, đổi nơi lưu, đổi nhà cung cấp AI) thì phải sửa cả trang này — người duyệt đối chiếu
// với những gì ứng dụng thật sự xin quyền.

function PrivacyPage() {
  const M = ({ id, tieuDe, children }) => (
    <section id={id} className="pv-muc">
      <h2>{tieuDe}</h2>
      {children}
    </section>
  );

  return (
    <div className="pv-trang">
      <header className="pv-dau">
        <h1>Chính sách bảo mật</h1>
        <p className="pv-phu">
          TRAV-AI · Cập nhật ngày 28/08/2026
        </p>
      </header>

      <M id="thu-thap" tieuDe="1. Chúng tôi nhận những dữ liệu gì">
        <p>
          TRAV-AI là công cụ dành cho <b>công ty du lịch</b>, giúp nhân viên trả lời khách hàng
          từ nhiều kênh nhắn tin trong một hộp thư chung. Khi một công ty kết nối kênh của họ
          (Facebook Messenger, Instagram, WhatsApp, Zalo, Telegram, TikTok), chúng tôi nhận:
        </p>
        <ul>
          <li><b>Nội dung tin nhắn</b> khách gửi tới trang/tài khoản của công ty đó, và tin công
            ty trả lời.</li>
          <li><b>Tên hiển thị và ảnh đại diện</b> của người nhắn, do chính nền tảng cung cấp.</li>
          <li><b>Mã người dùng</b> do nền tảng cấp (ví dụ PSID của Facebook) — mã này chỉ dùng
            được với riêng trang đó, không phải số điện thoại hay email.</li>
          <li><b>Ảnh và tệp</b> khách gửi kèm trong cuộc trò chuyện.</li>
        </ul>
        <p>
          Chúng tôi <b>không</b> thu thập danh bạ bạn bè, dòng thời gian, hay bất kỳ dữ liệu nào
          ngoài cuộc trò chuyện giữa khách và công ty đã kết nối.
        </p>
      </M>

      <M id="su-dung" tieuDe="2. Dùng để làm gì">
        <ul>
          <li>Hiện cuộc trò chuyện trong hộp thư để nhân viên công ty đọc và trả lời.</li>
          <li>Trợ lý AI soạn câu trả lời gợi ý. Nhân viên vẫn là người quyết định gửi hay không.</li>
          <li>Thống kê nội bộ của chính công ty đó: số hội thoại, thời gian phản hồi.</li>
        </ul>
        <p>
          Chúng tôi <b>không bán</b> dữ liệu, <b>không dùng để quảng cáo</b>, và không dùng nội
          dung tin nhắn của công ty này cho công ty khác.
        </p>
      </M>

      <M id="chia-se" tieuDe="3. Ai được chạm vào dữ liệu">
        <ul>
          <li><b>Nhân viên của chính công ty</b> đã kết nối kênh — họ là người trả lời khách.</li>
          <li><b>Nhà cung cấp mô hình AI</b>: nội dung tin được gửi tới nhà cung cấp AI để sinh
            câu trả lời gợi ý. Chúng tôi chọn nhà cung cấp có cam kết không dùng dữ liệu khách
            hàng để huấn luyện mô hình.</li>
          <li><b>Hạ tầng lưu trữ</b> mà chúng tôi thuê để chạy hệ thống.</li>
        </ul>
        <p>Ngoài ba nhóm trên, không ai khác được truy cập.</p>
      </M>

      <M id="luu-tru" tieuDe="4. Lưu ở đâu, bao lâu">
        <p>
          Dữ liệu lưu trên máy chủ thuê tại trung tâm dữ liệu thương mại, truyền qua kết nối mã
          hoá. Khoá kết nối kênh được mã hoá khi lưu.
        </p>
        <p>
          Lịch sử hội thoại giữ trong suốt thời gian công ty còn dùng dịch vụ, vì đó là hồ sơ
          nghiệp vụ của họ (tra cứu khi khách khiếu nại, bàn giao giữa nhân viên). Công ty ngừng
          dùng dịch vụ thì dữ liệu được xoá theo yêu cầu — xem mục 6.
        </p>
      </M>

      <M id="quyen" tieuDe="5. Quyền của người nhắn tin">
        <p>
          Nếu bạn là khách hàng đã nhắn tin cho một công ty dùng TRAV-AI, bạn có quyền yêu cầu
          xem, sửa hoặc xoá dữ liệu của mình. Cách nhanh nhất là liên hệ trực tiếp công ty bạn đã
          nhắn — họ là bên quyết định dữ liệu đó. Bạn cũng có thể liên hệ thẳng chúng tôi theo
          mục 7 và chúng tôi sẽ chuyển tiếp.
        </p>
      </M>

      <M id="xoa-du-lieu" tieuDe="6. Hướng dẫn xoá dữ liệu">
        <p>
          Để yêu cầu xoá toàn bộ dữ liệu liên quan tới bạn, gửi email tới địa chỉ ở mục 7 với
          tiêu đề <b>“Yêu cầu xoá dữ liệu”</b>, kèm:
        </p>
        <ul>
          <li>Tên trang hoặc tài khoản bạn đã nhắn tin tới.</li>
          <li>Tên hiển thị của bạn trên nền tảng đó.</li>
          <li>Khoảng thời gian bạn đã nhắn (nếu nhớ) — giúp tìm đúng cuộc trò chuyện.</li>
        </ul>
        <p>
          Chúng tôi xác minh rồi xoá trong vòng <b>30 ngày</b> và trả lời xác nhận bằng email.
          Việc xoá bao gồm nội dung tin nhắn, tên, ảnh đại diện và tệp đính kèm đã lưu.
        </p>
        <p>
          <b>Với Facebook và Instagram còn có đường nhanh hơn:</b> vào phần cài đặt tài khoản
          Facebook của bạn, gỡ ứng dụng TourKit AI và chọn xoá dữ liệu. Facebook báo thẳng sang
          chúng tôi, hệ thống xoá <b>ngay lập tức</b> rồi trả về một mã tra cứu — mở mã đó là thấy
          đã xoá bao nhiêu hội thoại, bao nhiêu tin nhắn.
        </p>
        <p className="pv-luu-y">
          Lưu ý: chúng tôi chỉ xoá được bản lưu trong hệ thống của mình. Bản nằm trên Facebook,
          Zalo, Telegram… thuộc quyền các nền tảng đó — bạn cần yêu cầu riêng với họ.
        </p>
      </M>

      <M id="lien-he" tieuDe="7. Liên hệ">
        <p>
          Email: <a href="mailto:hotro@tourkit.vn">hotro@tourkit.vn</a>
        </p>
      </M>

      {/* Người duyệt của Meta thường không đọc được tiếng Việt. Bản tóm tắt tiếng Anh ở đây là để
          họ đối chiếu nhanh với các quyền ứng dụng xin, không phải bản dịch đầy đủ. */}
      <M id="english" tieuDe="English summary (for reviewers)">
        <p>
          TRAV-AI is a shared inbox for Vietnamese travel agencies. When an agency connects its
          own Facebook Page, Instagram, WhatsApp, Zalo, Telegram or TikTok account, we receive the
          messages customers send to that account, together with the sender’s display name, avatar
          and platform-scoped user id.
        </p>
        <p>
          This data is used solely to show the conversation to that agency’s staff and to let an
          AI assistant draft suggested replies. We do not sell data, do not use it for
          advertising, and never share one agency’s conversations with another.
        </p>
        <p>
          To request deletion, email <a href="mailto:hotro@tourkit.vn">hotro@tourkit.vn</a> with
          the subject “Data deletion request”, naming the page you messaged and your display name.
          We verify and delete within 30 days, then confirm by email.
        </p>
      </M>
    </div>
  );
}

window.PrivacyPage = PrivacyPage;
