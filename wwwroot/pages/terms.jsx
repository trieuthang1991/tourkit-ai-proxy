// pages/terms.jsx — Điều khoản dịch vụ.
//
// Meta để trường "Terms of Service URL" là TUỲ CHỌN, nhưng hồ sơ App Review có điều khoản rõ ràng
// thì qua dễ hơn: người duyệt đọc nó để đối chiếu lời khai về cách xử lý dữ liệu.
//
// Trang phải mở được KHÔNG cần đăng nhập — người duyệt không có tài khoản trong hệ. Khai ở
// app.jsx cùng nhánh với /privacy, nằm TRƯỚC cổng đăng nhập.
//
// ⚠️ Viết đúng những gì hệ THẬT SỰ làm. Chỗ nhạy nhất là mục về trợ lý AI: nó CÓ THỂ tự trả lời
// khách chứ không chỉ gợi ý cho nhân viên duyệt. Viết mềm đi thành "chỉ gợi ý" là sai sự thật, và
// sai ở đúng chỗ người duyệt soi kỹ nhất.

function TermsPage() {
  const M = ({ id, tieuDe, children }) => (
    <section id={id} className="pv-muc">
      <h2>{tieuDe}</h2>
      {children}
    </section>
  );

  return (
    <div className="pv-trang">
      <header className="pv-dau">
        <h1>Điều khoản dịch vụ</h1>
        <p className="pv-phu">TRAV-AI · Cập nhật ngày 29/08/2026</p>
      </header>

      <M id="dich-vu" tieuDe="1. Dịch vụ này là gì">
        <p>
          TRAV-AI là phần mềm dành cho <b>công ty du lịch</b>: gom tin nhắn khách hàng từ nhiều
          kênh (Facebook Messenger, Instagram, WhatsApp, Zalo, Telegram, TikTok) về một hộp thư
          chung để nhân viên đọc và trả lời, kèm trợ lý AI hỗ trợ soạn câu trả lời.
        </p>
        <p>
          Dịch vụ dành cho <b>tổ chức</b>, không dành cho cá nhân dùng riêng.
        </p>
      </M>

      <M id="tai-khoan" tieuDe="2. Tài khoản">
        <p>
          Công ty tự chịu trách nhiệm giữ tài khoản và mật khẩu của nhân viên mình, và chịu trách
          nhiệm cho mọi thao tác thực hiện từ các tài khoản đó. Phát hiện tài khoản bị lộ thì báo
          ngay cho chúng tôi theo mục 9.
        </p>
      </M>

      <M id="trach-nhiem" tieuDe="3. Trách nhiệm của công ty sử dụng">
        <p>
          Khi công ty kết nối kênh của mình, <b>công ty là bên quyết định dữ liệu</b> của khách
          hàng mình; TRAV-AI chỉ là bên xử lý theo yêu cầu. Nghĩa là công ty phải:
        </p>
        <ul>
          <li>Có cơ sở hợp pháp để thu thập và xử lý tin nhắn của khách.</li>
          <li>Tuân thủ chính sách của từng nền tảng (Facebook, Zalo…) khi nhắn tin cho khách.</li>
          <li><b>Không</b> dùng dịch vụ để gửi tin rác, quảng cáo hàng loạt cho người chưa đồng ý,
            hoặc bất kỳ nội dung nào vi phạm pháp luật.</li>
          <li>Trả lời các yêu cầu của khách về dữ liệu của họ.</li>
        </ul>
      </M>

      <M id="tro-ly" tieuDe="4. Về trợ lý AI">
        <p>
          Trợ lý AI có thể <b>tự động trả lời khách</b> khi công ty bật tính năng đó. Công ty bật,
          tắt, hoặc tạm dừng trợ lý theo từng cuộc trò chuyện, và <b>chịu trách nhiệm về mọi nội
          dung gửi tới khách</b> — kể cả nội dung do trợ lý soạn.
        </p>
        <p>
          Trợ lý được đặt sẵn luật không tự báo giá, không hứa giữ chỗ và không cam kết lịch khởi
          hành. Dù vậy, nội dung do AI sinh ra <b>có thể sai</b>. Công ty nên bố trí người theo dõi
          hộp thư, nhất là với những cuộc trò chuyện liên quan tới tiền hoặc cam kết.
        </p>
        <p className="pv-luu-y">
          Chúng tôi không bảo đảm câu trả lời của trợ lý là chính xác, đầy đủ hay phù hợp cho một
          tình huống cụ thể.
        </p>
      </M>

      <M id="nen-tang" tieuDe="5. Phụ thuộc vào nền tảng bên thứ ba">
        <p>
          Dịch vụ hoạt động dựa trên API của Facebook, Zalo, Telegram và các nền tảng khác. Các
          nền tảng này có thể đổi chính sách, giới hạn quyền hoặc ngừng cung cấp bất cứ lúc nào,
          ngoài tầm kiểm soát của chúng tôi.
        </p>
        <p>
          Ví dụ có thật: mỗi nền tảng đặt một <b>cửa sổ thời gian</b> để trả lời khách (Facebook và
          Instagram 24 giờ cho trợ lý tự động, Zalo 48 giờ); hết cửa sổ thì chỉ gửi được mẫu tin đã
          được nền tảng duyệt. Đó là quy định của họ, không phải giới hạn của chúng tôi.
        </p>
      </M>

      <M id="gioi-han" tieuDe="6. Giới hạn trách nhiệm">
        <p>
          Chúng tôi cố gắng giữ dịch vụ chạy liên tục nhưng <b>không cam kết không gián đoạn</b>:
          bảo trì, sự cố hạ tầng hoặc thay đổi từ nền tảng bên thứ ba đều có thể làm gián đoạn.
        </p>
        <p>
          Chúng tôi không chịu trách nhiệm cho thiệt hại gián tiếp phát sinh từ việc dùng dịch vụ,
          bao gồm mất cơ hội kinh doanh do tin nhắn không được trả lời kịp.
        </p>
      </M>

      <M id="ngung" tieuDe="7. Ngừng dịch vụ và xoá dữ liệu">
        <p>
          Công ty có thể gỡ kết nối kênh bất cứ lúc nào — làm vậy là chúng tôi ngừng nhận dữ liệu
          mới của công ty đó ngay lập tức.
        </p>
        <p>
          Yêu cầu xoá dữ liệu đã lưu: xem <a href="/privacy#xoa-du-lieu">Chính sách bảo mật, mục 6</a>.
        </p>
        <p>
          Chúng tôi có quyền tạm ngừng tài khoản vi phạm mục 3, và sẽ báo trước trừ khi vi phạm
          gây hại tức thời.
        </p>
      </M>

      <M id="thay-doi" tieuDe="8. Thay đổi điều khoản">
        <p>
          Điều khoản có thể được cập nhật. Thay đổi quan trọng sẽ được báo qua email liên hệ của
          công ty trước khi có hiệu lực.
        </p>
      </M>

      <M id="lien-he" tieuDe="9. Liên hệ">
        <p>Email: <a href="mailto:hotro@tourkit.vn">hotro@tourkit.vn</a></p>
      </M>

      <M id="english" tieuDe="English summary (for reviewers)">
        <p>
          TRAV-AI is B2B software for Vietnamese travel agencies. Each agency connects its own
          messaging channels and remains the data controller for its customers' conversations;
          TRAV-AI acts as a processor.
        </p>
        <p>
          An AI assistant may reply to customers automatically when the agency enables it. The
          agency can enable, disable or pause it per conversation, and is responsible for all
          content sent to its customers, including AI-drafted content.
        </p>
        <p>
          Agencies may disconnect a channel at any time, which stops all further data collection
          immediately. Deletion of stored data is described in the{' '}
          <a href="/privacy#xoa-du-lieu">Privacy Policy</a>.
        </p>
      </M>

      {/* Khối pháp nhân — KHÔNG phải trang trí.
          Xác minh doanh nghiệp của Meta là người thật mở website ra, đối chiếu tên pháp nhân và
          địa chỉ với ĐKKD mình nộp. Hai trang này chính là hai đường dẫn khai trong Settings →
          Basic, nên là chỗ chắc chắn người duyệt bấm vào. Thiếu khối này thì hồ sơ bị trả về với
          lý do "không xác nhận được doanh nghiệp qua website" — mà lý do đó không nói rõ thiếu gì,
          nên rất dễ nộp lại y nguyên rồi trượt lần nữa.

          ⚠️ Tên và địa chỉ trụ sở chép Y HỆT ĐKKD, kể cả kiểu viết hoa và chữ "Số nhà". Meta so
          máy móc: "P.9" với "Phường 9", "Tòa nhà" với "Số nhà" là đủ để trả về. Ai thấy khối này
          viết hoa toàn bộ mà định "sửa cho đẹp" thì đừng — nó cố ý.

          Không đưa người đại diện và số tài khoản ngân hàng lên đây: Meta không cần, mà để công
          khai thì mở đường cho lừa đảo mạo danh công ty đi thu tiền. */}
      <footer className="pv-phap-nhan">
        <p className="pv-phap-nhan-ten">CÔNG TY CỔ PHẦN TOURKIT VIỆT NAM</p>
        <p>Trụ sở chính: Tầng 3, Số nhà 242 Nguyễn Văn Lộc, Phường Hà Đông, Thành phố Hà Nội, Việt Nam</p>
        <p>Mã số thuế: 0111219654</p>
        <p>Văn phòng Hồ Chí Minh: Số 1, Đặng Văn Sâm, Phường 9, Quận Phú Nhuận</p>
        <p>
          <a href="tel:0383202404">0383.202.404</a>
          {' · '}
          <a href="mailto:hotro@tourkit.vn">hotro@tourkit.vn</a>
        </p>
      </footer>
    </div>
  );
}

window.TermsPage = TermsPage;
