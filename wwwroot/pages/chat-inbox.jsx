// pages/chat-inbox.jsx — Hộp thư chat đa kênh (route /chat-inbox).
//
// BỐN vùng: dải kênh | danh sách hội thoại | khung chat | hồ sơ khách.
// Dải kênh tách riêng khỏi bộ lọc trạng thái vì đó là hai câu hỏi khác nhau: "khách nhắn từ đâu"
// và "việc này xử lý tới đâu". Trộn chung một cột thì cứ đổi kênh là mất bộ lọc trạng thái.
//
// Ba điều KHÔNG được bỏ:
//   1. Bong bóng phân biệt BA bên (khách / AI / nhân viên), không phải hai. Người đọc cần biết
//      câu nào do máy trả lời — nhất là khi phải sửa lại lời máy nói với khách.
//   2. Hết cửa sổ gửi thì KHOÁ ô soạn kèm lý do. Để bấm gửi rồi mới báo hỏng là đã muộn: nhân
//      viên gõ xong cả đoạn mới biết không gửi được.
//   3. Thời hạn trả lời hiện THƯỜNG TRỰC ngay dưới tên khách, không đợi sắp hết mới báo. Đây là
//      dữ kiện duy nhất trong trang mà chờ đợi sẽ mất — thấy sớm mới kịp làm gì đó.
(function () {
  'use strict';

  const { useState, useEffect, useRef, useCallback, useMemo } = React;
  const authedFetch = (...a) => window.tourkitAuth.authedFetch(...a);
  const fmtAgo = (t) => (window.tourkitUtil?.fmtAgo ? window.tourkitUtil.fmtAgo(t) : t || '');
  const fmtDate = (t, o) => (window.tourkitUtil?.fmtDate ? window.tourkitUtil.fmtDate(t, o) : t || '');

  // Chữ viết tắt thay cho biểu tượng thương hiệu: ba kênh ba chữ khác nhau nên phân biệt được
  // ngay, mà không phải kéo logo của bên thứ ba về.
  // Mỗi kênh dùng ĐÚNG dấu hiệu thật của nó. Zalo và Facebook nhận diện bằng CHỮ (Z, f) — đó là
  // chữ ký thương hiệu, không phải viết tắt. Bốn kênh còn lại nhận diện bằng HÌNH.
  //
  // Bản đầu viết tắt 'ig' / 'wa' / 'tt' cho ba kênh mới: hai chữ thường, nhỏ hơn hẳn chữ ký thật
  // bên cạnh, và không ai nhận ra đó là kênh nào. Trộn chữ ký thật với viết tắt tự bịa là chỗ
  // dải kênh trông lệch.
  const KENH = {
    0: { ten: 'Zalo', chu: 'Z' },
    1: { ten: 'Messenger', chu: 'f' },
    4: { ten: 'Instagram', hinh: 'instagram' },
    5: { ten: 'WhatsApp', hinh: 'whatsapp' },
    6: { ten: 'TikTok', hinh: 'tiktok' },
    2: { ten: 'Web', chu: 'W' },
    3: { ten: 'Telegram', hinh: 'telegram' },
  };
  const KENH_SONG = [0, 1, 4, 5, 6, 3];   // kênh đã nối thật; Web chỉ hiện khi có dữ liệu

  const TRANG_THAI = [
    { v: null, nhan: 'Tất cả' },
    { v: 0, nhan: 'Mới' },
    { v: 1, nhan: 'Đang xử lý' },
    { v: 2, nhan: 'Đã đóng' },
  ];
  const TEN_TRANG_THAI = { 0: 'Mới', 1: 'Đang xử lý', 2: 'Đã đóng' };

  // ── Định dạng nhỏ ────────────────────────────────────────────────────────

  function chuDau(ten) {
    const s = (ten || '').trim();
    if (!s) return '?';
    const tu = s.split(/\s+/).filter(Boolean);
    if (tu.length === 1) return tu[0].slice(0, 2).toUpperCase();
    return (tu[0][0] + tu[tu.length - 1][0]).toUpperCase();
  }

  // Giờ trong danh sách phải NGẮN: cột chỉ rộng 300px, "21 phút trước" đẩy tên khách xuống dòng.
  function gioNgan(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    const nay = new Date();
    if ((nay - d) / 1000 < 60) return 'vừa xong';
    if (d.toDateString() === nay.toDateString())
      return d.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' });
    const homQua = new Date(nay); homQua.setDate(nay.getDate() - 1);
    if (d.toDateString() === homQua.toDateString()) return 'Hôm qua';
    if (d.getFullYear() === nay.getFullYear())
      return d.toLocaleDateString('vi-VN', { day: '2-digit', month: '2-digit' });
    return fmtDate(iso);
  }

  function nhanNgay(iso) {
    const d = new Date(iso);
    const nay = new Date();
    if (d.toDateString() === nay.toDateString()) return 'Hôm nay';
    const homQua = new Date(nay); homQua.setDate(nay.getDate() - 1);
    if (d.toDateString() === homQua.toDateString()) return 'Hôm qua';
    return fmtDate(iso);
  }

  function ngayCua(iso) { return new Date(iso).toDateString(); }

  // Giờ trong bong bóng LUÔN là HH:mm. Ngày đã có vạch ngăn ngay phía trên, in thêm "Hôm qua"
  // vào từng bong bóng là bắt người đọc đọc hai lần cùng một thứ.
  function gioPhut(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' });
  }

  // Giờ ĐẦY ĐỦ cho tooltip khi rê chuột. Bong bóng không in giờ nữa (xem ghi chú ở BongBong),
  // nên đây là đường duy nhất để tra chính xác một tin gửi lúc nào.
  function gioDayDu(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleString('vi-VN', {
      weekday: 'short', day: '2-digit', month: '2-digit', year: 'numeric',
      hour: '2-digit', minute: '2-digit',
    });
  }

  // 41.3 giờ thành "41 giờ 18 phút". Số giờ lẻ thập phân không nói lên gì với người trực máy.
  function dienGio(gio) {
    if (gio == null) return '';
    const tong = Math.max(0, Math.round(gio * 60));
    const g = Math.floor(tong / 60), p = tong % 60;
    if (g <= 0) return p + ' phút';
    return p ? g + ' giờ ' + p + ' phút' : g + ' giờ';
  }

  // ── Mảnh dùng lại ────────────────────────────────────────────────────────

  function AnhDaiDien({ ten, url, co }) {
    const style = co ? { width: co, height: co, fontSize: Math.round(co * 0.38) } : null;
    if (url) return <img className="ci-avt" style={style} src={url} alt="" />;
    return <span className="ci-avt" style={style}>{chuDau(ten)}</span>;
  }

  function HuyHieuKenh({ kenh, day }) {
    const k = KENH[kenh];
    if (!k) return null;
    // Hình vẽ theo cỡ chữ quanh nó nên nằm cùng hàng với chữ ký Z/f, không nhảy dòng.
    return (
      <span className={'ci-hh' + (day ? ' day' : '') + (k.hinh ? ' hinh' : '')} title={k.ten}>
        {k.hinh ? <window.Icon name={k.hinh} size={13} stroke={1.9} /> : k.chu}
      </span>
    );
  }

  // Dấu tích trạng thái gửi. Ghép từ biểu tượng "check" có sẵn thay vì vẽ tay đường SVG mới.
  // Telegram không bao giờ báo lại đã nhận/đã xem (Bot API không có). Không nói rõ thì nhân viên
  // nhìn hai hội thoại cạnh nhau sẽ kết luận sai "khách Telegram không đọc tin" — hiểu nhầm do
  // MÌNH tạo ra, tệ hơn là không hiện gì.
  function DauGui({ state, kenh }) {
    if (state === 0) return <span className="ci-tich cho">đang gửi…</span>;
    if (state === 4) return null;   // lỗi có dòng riêng, màu đỏ, không nhét vào đây
    const khongBao = kenh === 3;    // Telegram
    const nhan = khongBao
      ? 'Đã gửi — kênh này không báo lại việc khách đã nhận hay đã xem'
      : state >= 3 ? 'Khách đã xem' : state === 2 ? 'Đã tới máy khách' : 'Đã gửi';
    return (
      <span className={'ci-tich' + (state >= 3 && !khongBao ? ' xem' : '')} title={nhan} aria-label={nhan}>
        <window.Icon name="check" size={11} stroke={2.6} />
        {state >= 2 && !khongBao && <window.Icon name="check" size={11} stroke={2.6} />}
      </span>
    );
  }

  function coCho(byte) {
    if (!byte) return '';
    if (byte < 1024) return byte + ' B';
    if (byte < 1024 * 1024) return Math.round(byte / 1024) + ' KB';
    return (byte / 1024 / 1024).toFixed(1) + ' MB';
  }

  // Đính kèm khách gửi. Máy chủ đã chuẩn hoá về cùng hình dạng cho cả ba kênh (xem ChatAttachment),
  // nên ở đây KHÔNG có chỗ nào phải biết Zalo/Messenger/Telegram gói tệp khác nhau ra sao.
  function DinhKem({ tin }) {
    const ds = tin.files || [];
    if (!ds.length) return null;
    return (
      <div className="ci-dinhkem">
        {ds.map((f, i) => {
          if (f.lat != null && f.lon != null) return (
            <a key={i} className="ci-tep" target="_blank" rel="noopener noreferrer"
               href={`https://www.google.com/maps?q=${f.lat},${f.lon}`}>
              <window.Icon name="pin" size={14} />
              <span>Vị trí khách gửi</span>
            </a>
          );
          if (!f.url) return (
            // Có đính kèm nhưng không lấy được đường tải (kênh chưa khai đủ khoá, tệp quá hạn…).
            // Nói thẳng thay vì hiện ảnh vỡ — nhân viên biết mà hỏi lại khách.
            <div key={i} className="ci-tep hong">
              <window.Icon name="warning" size={14} />
              <span>{f.ten || 'Tệp đính kèm'} — chưa tải được</span>
            </div>
          );
          // Ảnh: hiện thẳng, bấm để mở cỡ đầy đủ. loading="lazy" vì một hội thoại có thể có
          // hàng chục ảnh mà nhân viên chỉ nhìn vài cái gần nhất.
          // kind 1 = ảnh, kind 4 = sticker. Cùng một thẻ nhưng KHÁC cỡ: sticker là biểu cảm,
          // vẽ to bằng tấm ảnh khách chụp hộ chiếu là sai thứ tự quan trọng.
          if (tin.kind === 1 || tin.kind === 4) return (
            <a key={i} href={f.url} target="_blank" rel="noopener noreferrer"
               className={'ci-anh' + (tin.kind === 4 ? ' sticker' : '')}>
              <img src={f.url} alt={f.ten || (tin.kind === 4 ? 'Sticker' : 'Ảnh khách gửi')}
                   loading="lazy" />
            </a>
          );
          return (
            <a key={i} className="ci-tep" href={f.url} target="_blank" rel="noopener noreferrer">
              <window.Icon name="paperclip" size={14} />
              <span>{f.ten || 'Tệp đính kèm'}</span>
              {f.kich > 0 && <em>{coCho(f.kich)}</em>}
            </a>
          );
        })}
      </div>
    );
  }

  // Kênh lấy từ HỘI THOẠI, không phải từ tin: bảng chat_messages có cột channel nhưng lớp
  // ChatMessage không map cột đó nên API không trả về — viết tin.channel sẽ ra undefined và mọi
  // tin đều bị coi là Zalo.
  // Tên nguồn của Meta viết hoa toàn chữ Anh. Dịch sang chữ người dùng đọc được; nguồn lạ thì
  // hiện nguyên văn còn hơn nuốt mất — biết "đến từ đâu đó không rõ" vẫn hơn không biết gì.
  const NGUON_KHACH = {
    ADS: 'Quảng cáo Facebook',
    SHORTLINK: 'Liên kết m.me',
    CUSTOMER_CHAT_PLUGIN: 'Khung chat trên website',
    MESSENGER_CODE: 'Mã QR Messenger',
    DISCOVER_TAB: 'Mục Khám phá',
  };

  // ── Gộp tin liên tiếp thành CỤM ───────────────────────────────────────────
  //
  // Khách hay nhắn dồn ba bốn câu trong một phút. In giờ dưới TỪNG câu thì năm tin liên tiếp
  // thành năm dòng "22:28" — không nói thêm gì mà cắt vụn dòng đọc và đẩy nội dung thật ra xa
  // nhau. Messenger, Zalo, WhatsApp đều gộp; mình cũng vậy.
  //
  // Luật ở GIAO DIỆN chứ không ở ChatRules: file đó tự dặn chỉ chứa luật "sai là hỏng thật, không
  // phải chuyện đẹp xấu". Gộp sai một cụm thì xấu, chứ không mất tin của ai.

  const CUM_MS = 5 * 60 * 1000;    // trong 5 phút thì còn là một cụm
  const MOC_MS = 20 * 60 * 1000;   // cách trên 20 phút thì chèn một mốc giờ

  function cungCum(a, b) {
    if (!a || !b) return false;
    const ta = new Date(a.createdUtc).getTime();
    const tb = new Date(b.createdUtc).getTime();
    return a.senderKind === b.senderKind
      && a.direction === b.direction
      // Cùng NGƯỜI nữa, không chỉ cùng vai: hai nhân viên trả lời nối nhau là hai cụm, gộp thì
      // tên ở đầu cụm nói sai ai đã nói câu nào.
      && (a.senderUsername || '') === (b.senderUsername || '')
      // Lùi giờ = dữ liệu lệch (nhập lịch sử, lệch đồng hồ). Gộp thì dấu giờ ở cuối cụm nói sai
      // thời điểm của cả cụm — thà tách ra.
      && tb >= ta && tb - ta <= CUM_MS;
  }

  // Gộp cụm làm mất bớt dấu giờ, nên phải trả ngữ cảnh lại ở chỗ nó thật sự có nghĩa: lúc hội
  // thoại đứt quãng lâu. Không có mốc này thì đọc lại một hội thoại dài không biết khách im ba
  // tiếng hay trả lời ngay. Đổi NGÀY đã có dải ngày riêng lo.
  function canMocGio(a, b) {
    if (!a || !b) return false;
    if (ngayCua(a.createdUtc) !== ngayCua(b.createdUtc)) return false;
    return new Date(b.createdUtc) - new Date(a.createdUtc) >= MOC_MS;
  }

  /**
   * Toạ độ mở một menu neo theo nút vừa bấm, dùng cho position:fixed.
   *
   * Tự LẬT LÊN khi dưới nút không đủ chỗ — nếu không thì mấy dòng cuối danh sách (và mấy tin
   * cuối khung chat) mở menu ra là nó chui xuống dưới khung nhìn, phải cuộn mới bấm được.
   * Chỉ lật khi phía trên THẬT SỰ rộng hơn phía dưới, chứ không lật mù.
   *
   * Dùng fixed chứ không absolute vì cả hai menu đều nằm trong khung CUỘN: absolute thì bị
   * khung cắt, nong chiều ngang, và bị phần tử sau đè lên.
   */
  function viTriMenu(nut, caoUocLuong) {
    const o = nut.getBoundingClientRect();
    const cao = window.innerHeight, rong = window.innerWidth;
    const duoi = cao - o.bottom;
    const lat = duoi < caoUocLuong && o.top > duoi;

    // KẸP vào khung nhìn. Nút neo có thể nằm ngoài màn hình (danh sách vừa cuộn, hoặc cửa sổ
    // thấp), lúc đó menu bám sát nút sẽ chui ra ngoài và không bấm được — đã thấy khi chạy thử.
    const y = lat
      ? Math.min(o.top - 6, cao - 8)                       // lật lên: y là ĐÁY menu
      : Math.max(8, Math.min(o.bottom + 6, cao - caoUocLuong - 8));
    // x là mép PHẢI menu (có translateX(-100%)), nên phải chừa đủ bề ngang menu bên trái.
    const x = Math.max(198, Math.min(o.right, rong - 8));

    // Chỗ đặt mũi nhọn, đo từ mép PHẢI menu vào. Tính theo tâm nút thật chứ không đặt cứng: khi
    // x bị kẹp ở trên thì mép menu không còn trùng mép nút nữa, đặt cứng là mũi nhọn chỉ vào chỗ
    // trống. Kẹp lại trong thân menu để nó không thò ra ngoài góc bo.
    const nhon = Math.max(8, Math.min(168, x - (o.left + o.width / 2) - 5));
    return { x, y, lat, nhon };
  }

  /** Telegram — kênh DUY NHẤT thu hồi thật được (bot xoá tin của chính nó trong 48 giờ). */
  const KENH_TELEGRAM = 3;
  const THU_HOI_TELEGRAM_MS = 48 * 60 * 60 * 1000;

  /**
   * Còn mấy giây nữa tin rời máy chủ. 0 = đã đi (hoặc không hoãn).
   *
   * Mốc lấy từ MÁY CHỦ (`send_after`), không tự cộng từ `createdUtc` + số giây trong cấu hình:
   * quản trị đổi số giây, hoặc đồng hồ máy khách chạy sai, là con số suy ra sai ngay — mà sai ở
   * đây nghĩa là nút Thu hồi hiện ra sau khi tin đã đi, bấm vào báo lỗi.
   */
  function giayConLai(mocIso) {
    if (!mocIso) return 0;
    return Math.max(0, Math.ceil((new Date(mocIso).getTime() - Date.now()) / 1000));
  }

  function BongBong({ tin, kenh, ten0, dauCum = true, cuoiCum = true, onXoa, onSua, onThuHoi }) {
    // 0=khách 1=AI 2=nhân viên 3=hệ thống
    const ben = tin.senderKind;
    const cuaMinh = tin.direction === 1;
    const lop = ben === 0 ? 'ci-khach' : ben === 1 ? 'ci-ai' : ben === 3 ? 'ci-hethong' : 'ci-nv';
    // Nhãn người gửi chỉ ở ĐẦU cụm — lặp lại dưới mỗi bong bóng của cùng một người là thừa.
    const nhan = !dauCum ? null
      : ben === 1 ? 'AI trả lời' : ben === 2 ? (tin.senderUsername || 'Nhân viên') : null;

    // Toạ độ mở menu của tin, hoặc null. Lưu TOẠ ĐỘ chứ không phải cờ bật/tắt: menu vẽ bằng
    // position:fixed nên nó không nằm trong luồng của khung chat — không nong chiều ngang,
    // không bị khung cuộn cắt, và không bị tin bên dưới đè lên.
    const [moTin, setMoTin] = React.useState(null);

    // Đếm ngược cửa sổ thu hồi. Dừng hẳn khi về 0 — để chạy tiếp là mỗi tin cũ trong hội thoại
    // giữ một bộ đếm vô ích, mở một hội thoại dài là hàng trăm cái.
    const [conLai, setConLai] = React.useState(() => giayConLai(tin.sendAfterUtc));
    React.useEffect(() => {
      setConLai(giayConLai(tin.sendAfterUtc));
      if (!tin.sendAfterUtc) return;
      const t = setInterval(() => {
        const n = giayConLai(tin.sendAfterUtc);
        setConLai(n);
        if (n === 0) clearInterval(t);
      }, 500);
      return () => clearInterval(t);
    }, [tin.sendAfterUtc]);
    const coTep = (tin.files || []).length > 0;
    // Tin CHỈ có ảnh/nhãn dán, không kèm chữ. Bong bóng bọc quanh một tấm ảnh — nhất là nhãn dán
    // nền trong suốt — trông như cái khung thừa; mọi app chat đều để media trôi tự do.
    const chiMedia = coTep && !tin.body;

    // Ba dạng trình bày KHÁC NHAU, không phải một bong bóng đổi màu:
    //   khách  — bong bóng trắng, có ảnh đại diện, giờ nằm DƯỚI bóng
    //   bot    — KHÔNG bong bóng, chỉ một dải mảnh bên trái. Bot nói nhiều; để nó cũng thành
    //            bong bóng thì khung chat đặc kín và mắt không phân biệt nổi đâu là người thật
    //   mình   — bong bóng đậm, nhãn người gửi nằm TRONG bóng
    // Tin đã xoá khỏi hộp thư: hiện một dòng nhạt thay cho nội dung, KHÔNG cho biến mất hẳn.
    // Biến mất thì người trực tưởng mình nhớ nhầm, và cụm tin quanh nó mất mạch.
    const noiDung = tin.deleted ? (
      <div className="ci-noidung ci-da-xoa"><i>Tin đã bị xoá khỏi hộp thư</i></div>
    ) : (
      <>
        <DinhKem tin={tin} />
        {/* Có đính kèm thì chữ là CHÚ THÍCH, vắng chữ là bình thường — đừng in "(không có chữ)"
            dưới một tấm ảnh, vừa thừa vừa trông như lỗi. */}
        {(tin.body || !coTep) && (
          <div className="ci-noidung">{tin.body || <i>(không có chữ)</i>}</div>
        )}
      </>
    );
    // ⚠️ KHÔNG in giờ dưới bong bóng nữa — đây là cách Messenger làm, và nó gọn hơn hẳn.
    //
    // Toàn bộ thông tin thời gian dồn vào DẢI NGĂN giữa dòng (dải ngày + mốc giờ), chỉ xuất
    // hiện khi hội thoại thật sự đứt quãng. Giờ chính xác của TỪNG tin vẫn tra được: rê chuột
    // lên bong bóng là hiện đầy đủ cả thứ, ngày, giờ.
    //
    // Bản trước in giờ dưới mỗi CỤM. Đỡ hơn in dưới mỗi tin, nhưng khách nhắn rải rác vẫn ra
    // một cột giờ chạy dọc khung chat, lặp gần như cùng một con số.
    //
    // Còn lại ở hàng này: dấu ĐÃ GỬI/ĐÃ XEM (chỉ tin mình gửi) và báo GỬI HỎNG. Báo hỏng luôn
    // hiện dù nằm giữa cụm — giấu nó cho gọn mắt là giấu mất một tin khách không bao giờ nhận.
    const coDauGui = cuaMinh && cuoiCum;
    const gio = (coDauGui || tin.state === 4) && (
      <div className="ci-gio">
        {coDauGui && <DauGui state={tin.state} kenh={kenh} />}
        {tin.state === 4 && <span className="ci-loi" title={tin.errorMessage}>gửi hỏng</span>}
      </div>
    );
    const camXuc = (tin.reactions || []).length > 0 && (
      <div className="ci-camxuc">
        {tin.reactions.map(r => (
          <span key={r.emoji} className="ci-camxuc-mot">
            {r.emoji}{r.count > 1 && <b>{r.count}</b>}
          </span>
        ))}
      </div>
    );

    if (ben === 1) return (
      <div className={'ci-ai' + (dauCum ? '' : ' lien')} title={gioDayDu(tin.createdUtc)}>
        {dauCum && (
          <div className="ci-nhan"><window.Icon name="sparkle" size={11} />Bot đã trả lời</div>
        )}
        {noiDung}
        {gio}
        {camXuc}
      </div>
    );

    if (!cuaMinh) return (
      <div className={'ci-dong ci-trai' + (cuoiCum ? '' : ' lien')}>
        {/* Ảnh đại diện MỘT LẦN cho cả cụm, đặt ở tin CUỐI — đó là chỗ Messenger đặt, và mắt
            đọc từ trên xuống nên nó đóng cụm lại. Các tin trên giữ một ô trống cùng bề ngang
            để bong bóng không bị lệch trái. */}
        {cuoiCum
          ? <AnhDaiDien ten={tin.senderUsername || ten0} co={26} />
          : <span className="ci-avt-cho" aria-hidden="true" />}
        <div style={{ minWidth: 0 }}>
          <div className={'ci-bong ci-khach' + (chiMedia ? ' tran' : '')}
               title={gioDayDu(tin.createdUtc)}>{noiDung}</div>
          {gio}
          {camXuc}
        </div>
      </div>
    );

    // Thao tác trên tin CỦA MÌNH. Nút Sửa chỉ hiện với tin chưa ra khỏi máy (chờ gửi = 0,
    // gửi hỏng = 4) — tin đã gửi thì khách đã thấy bản gốc vĩnh viễn, sửa bản của mình là làm
    // hộp thư nói dối. Ẩn hẳn nút chứ không hiện rồi báo lỗi.
    const suaDuoc = onSua && (tin.state === 0 || tin.state === 4);
    // Telegram thu hồi được cả tin ĐÃ gửi, trong 48 giờ — kênh duy nhất làm được. Kênh khác thì
    // hết đếm ngược là chỉ còn "Xoá", và câu xác nhận của nó nói rõ khách vẫn thấy.
    const thuHoiDuoc = onThuHoi && conLai === 0 && kenh === KENH_TELEGRAM && tin.state >= 1
      && Date.now() - new Date(tin.createdUtc).getTime() < THU_HOI_TELEGRAM_MS;

    // Học Messenger: một nút tròn "⋯" NỔI cạnh bong bóng khi rê chuột, mở ra menu — chứ không
    // phải dải chữ gạch chân nằm dưới tin.
    //
    // Hai lý do, đều thấy ngay trên màn hình: dải chữ nằm trong luồng nên lúc hiện ra nó ĐẨY mọi
    // tin bên dưới nhích xuống, đọc một hội thoại dài mà rê chuột qua là cả khung nhảy; và chữ
    // gạch chân trông như liên kết, không như thao tác.
    const thaoTac = !tin.deleted && (onXoa || suaDuoc || thuHoiDuoc) && (
      <div className="ci-tin-menu">
        <button className="ci-tin-cham" title="Thao tác với tin này" aria-label="Thao tác với tin này"
                onClick={e => setMoTin(moTin ? null : viTriMenu(e.currentTarget, 130))}>
          <window.Icon name="more" size={14} />
        </button>
        {moTin && (
          <>
            <div className="ci-menu-nen" onClick={() => setMoTin(null)} />
            <div className={'ci-menu ci-menu-tin ' + (moTin.lat ? 'nhon-duoi' : 'nhon-tren')}
                 role="menu"
                 style={{ left: moTin.x, top: moTin.y, '--nhon': moTin.nhon + 'px',
                          transform: moTin.lat ? 'translate(-100%, -100%)' : 'translateX(-100%)' }}>
              {suaDuoc && (
                <button role="menuitem" onClick={() => { setMoTin(null); onSua(tin); }}>Sửa lại</button>
              )}
              {thuHoiDuoc && (
                <button role="menuitem" onClick={() => { setMoTin(null); onThuHoi(tin); }}>
                  Thu hồi cả phía khách
                </button>
              )}
              {/* ⚠️ Nhãn ngắn gọn "Gỡ" theo lối Messenger, nhưng bên đó "Gỡ" nghĩa là thu hồi
                  CẢ PHÍA KHÁCH — còn đây chỉ gỡ trong hộp thư của mình. Vì thế câu xác nhận
                  trong onXoa PHẢI giữ nguyên phần nói rõ khách vẫn thấy; bỏ nó đi là nhãn này
                  thành lời hứa sai. */}
              {onXoa && (
                <button role="menuitem" className="nguy-hiem"
                        onClick={() => { setMoTin(null); onXoa(tin); }}>
                  Gỡ
                </button>
              )}
            </div>
          </>
        )}
      </div>
    );

    return (
      <div className={'ci-dong ci-phai' + (cuoiCum ? '' : ' lien')}>
        <div>
          <div className={'ci-bong ' + lop + (chiMedia ? ' tran' : '')}
               title={gioDayDu(tin.createdUtc)}>
            {nhan && <div className="ci-nhan">{nhan}</div>}
            {noiDung}
            {/* Nút ĐÃ GỬI kèm tin. Vẽ lại để đọc hội thoại là thấy đúng thứ khách nhìn thấy —
                không vẽ thì dòng tin chỉ còn chữ, và không ai hiểu vì sao khách trả lời gọn lỏn
                "Nhật Bản" giữa chừng.

                CỐ Ý không bấm được: đây là bản ghi lại, không phải nút thật. Cho bấm thì nhân
                viên tưởng mình đang thay khách chọn. Nút mở liên kết thì mở được — nó chỉ dẫn
                tới một trang, và nhân viên xem thử khách sẽ thấy gì là việc chính đáng. */}
            {tin.buttons?.length > 0 && (
              <div className="ci-nut-tin">
                {tin.buttons.map((b, i) => b.url
                  ? <a key={i} href={b.url} target="_blank" rel="noopener noreferrer">{b.chu}</a>
                  : <span key={i}>{b.chu}</span>)}
              </div>
            )}
            {gio}
          </div>
          {camXuc}
          {/* Cửa sổ thu hồi THẬT: tin còn nằm trong hàng đợi, chưa rời máy chủ. Hết giây là nó
              đã đi và không kênh nào (trừ Telegram) rút lại được nữa. */}
          {conLai > 0 && onThuHoi && (
            <div className="ci-thu-hoi">
              <span>Đang gửi sau {conLai}s</span>
              <button onClick={() => onThuHoi(tin)}>Thu hồi</button>
            </div>
          )}
          {thaoTac}
        </div>
      </div>
    );
  }

  // ── Tin mẫu đã duyệt ──────────────────────────────────────────────────────
  //
  // Chỉ hiện khi cửa sổ trả lời tự do ĐÃ ĐÓNG — lúc đó đây là đường duy nhất còn lại. Mở nó ra
  // lúc vẫn nhắn tự do được là dụ người dùng tiêu một tin trả phí cho việc gõ tay vẫn làm được.
  function BangTinMau({ hoiThoai, onDong, onGuiXong, pushToast }) {
    const [ds, setDs] = React.useState(null);      // null = đang tải
    const [chan, setChan] = React.useState(null);  // lý do kênh này không gửi mẫu được
    const [chonMau, setChonMau] = React.useState(null);
    const [oDien, setODien] = React.useState({});
    const [dangGui, setDangGui] = React.useState(false);

    React.useEffect(() => {
      let huy = false;
      (async () => {
        // try/catch bao TRỌN, không chỉ kiểm r.ok: mạng đứt hay máy chủ chết giữa chừng thì
        // authedFetch NÉM chứ không trả về phản hồi hỏng — và lời hứa bị từ chối ở đây không
        // chạm tới setDs nào cả, nên bảng kẹt mãi ở "Đang đọc danh sách mẫu…". Người dùng ngồi
        // chờ một thứ không bao giờ tới, mà cũng không có lỗi nào hiện ra để họ biết mà đóng.
        try {
          const r = await authedFetch('/api/v1/chat/conversations/' + hoiThoai + '/templates');
          if (huy) return;
          if (!r.ok) { setDs([]); setChan('Không đọc được danh sách mẫu. Thử lại sau ít phút.'); return; }
          const j = await r.json();
          setChan(j.supported === false || j.blocked ? j.reason : null);
          setDs(j.items || []);
        } catch {
          if (huy) return;
          setDs([]);
          setChan('Không đọc được danh sách mẫu. Thử lại sau ít phút.');
        }
      })();
      return () => { huy = true; };
    }, [hoiThoai]);

    // Điền sẵn ví dụ nền tảng kèm theo. Nhân viên sửa nhanh hơn gõ từ đầu, và nhìn ví dụ mới
    // đoán ra ô đó là gì — Meta không đặt tên ô, chỉ đánh số.
    function chon(m) {
      setChonMau(m);
      const d = {};
      (m.slots || []).forEach(o => { d[o.key] = o.sample || ''; });
      setODien(d);
    }

    async function gui() {
      setDangGui(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + hoiThoai + '/send-template', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ templateId: chonMau.id, values: oDien }),
        });
        let j = null; try { j = await r.json(); } catch {}
        if (!r.ok) { pushToast(j?.error || 'Gửi không được', 'error'); return; }
        pushToast('Đã gửi tin mẫu', 'success');
        await onGuiXong();
      } finally { setDangGui(false); }
    }

    const thieuO = (chonMau?.slots || []).some(o => !(oDien[o.key] || '').trim());

    return (
      <div className="ci-mau-tin">
        <div className="ci-mau-tin-dau">
          <b>Tin mẫu đã duyệt</b>
          <button className="ci-nut-icon" onClick={onDong} aria-label="Đóng">
            <window.Icon name="close" size={14} />
          </button>
        </div>

        {ds === null && <div className="ci-mau-tin-trong">Đang đọc danh sách mẫu…</div>}
        {chan && <div className="ci-mau-tin-trong">{chan}</div>}

        {/* Chưa đăng ký mẫu nào là trạng thái BÌNH THƯỜNG của công ty mới, không phải lỗi —
            nên nói cách làm tiếp, đừng chỉ báo trống. */}
        {ds !== null && !chan && ds.length === 0 && (
          <div className="ci-mau-tin-trong">
            Kênh này chưa có mẫu nào được duyệt. Đăng ký mẫu trong trang quản trị của nền tảng
            (Zalo ZNS · Meta Business), duyệt xong là nó tự hiện ở đây.
          </div>
        )}

        {ds !== null && !chan && ds.length > 0 && !chonMau && (
          <ul className="ci-mau-tin-ds">
            {ds.map(m => (
              <li key={m.id}>
                <button type="button" disabled={!m.ready} onClick={() => chon(m)}>
                  <span className="ci-mau-tin-ten">{m.name}</span>
                  {/* Mẫu chờ duyệt vẫn hiện, mờ đi. Giấu hẳn thì người dùng tưởng mẫu bị mất
                      rồi đăng ký lại một mẫu trùng — và Meta tính lượt duyệt. */}
                  {!m.ready && <span className="ci-mau-tin-tt">{m.status}</span>}
                  {m.preview && <span className="ci-mau-tin-xem">{m.preview}</span>}
                </button>
              </li>
            ))}
          </ul>
        )}

        {chonMau && (
          <div className="ci-mau-tin-dien">
            <button type="button" className="ci-lienket" onClick={() => setChonMau(null)}>
              ← Chọn mẫu khác
            </button>
            <div className="ci-mau-tin-ten">{chonMau.name}</div>
            {chonMau.preview && <div className="ci-mau-tin-xem">{chonMau.preview}</div>}

            {(chonMau.slots || []).map(o => (
              <label key={o.key} className="ci-o">
                {o.label}
                <input value={oDien[o.key] || ''} placeholder={o.sample || ''}
                       onChange={e => setODien(p => ({ ...p, [o.key]: e.target.value }))} />
              </label>
            ))}

            <div className="ci-tk-nut">
              <button className="ci-nut chinh" disabled={dangGui || thieuO} onClick={gui}>
                {dangGui ? 'Đang gửi…' : 'Gửi cho khách'}
              </button>
              {/* Nói TRƯỚC khi bấm: mẫu Zalo tính tiền từng tin, và tin đã gửi thì không thu lại
                  được. Báo sau khi gửi là quá muộn. */}
              <span className="ci-mau-tin-luuy">Tin mẫu có thể tính phí và không thu hồi được.</span>
            </div>
          </div>
        )}
      </div>
    );
  }

  // Thời hạn trả lời. Ba mức: còn nhiều / sắp hết / đã đóng, dùng đúng bộ màu cảnh báo của app.
  function ThanhCuaSo({ cuaSo, kenh }) {
    if (!cuaSo) return null;
    const ten = KENH[kenh]?.ten || 'kênh này';
    // Hết cửa sổ thì KHÔNG in ở đây: ô soạn phía dưới đã thay bằng đúng lý do đó, mà ô soạn mới
    // là chỗ nhân viên định gõ. In hai nơi cùng một câu chỉ tổ khiến người đọc nghĩ là hai chuyện.
    if (!cuaSo.open) return null;
    if (cuaSo.hoursLeft == null) return (
      <div className="ci-cuaso mo">
        <window.Icon name="checkCircle" size={14} />
        <span>{ten} không giới hạn thời gian trả lời.</span>
      </div>
    );
    // Quá 24 giờ nhưng vẫn trong 7 ngày: Messenger/Instagram cho NGƯỜI THẬT nhắn tiếp, trợ lý
    // thì không. Phải nói ra — nhân viên đang quen có bot trực hộ, không nói thì họ đóng máy về
    // và tưởng khách vẫn được trả lời.
    if (cuaSo.lateHumanReply) return (
      <div className="ci-cuaso muon" style={{ '--ci-con': '100%' }}>
        <window.Icon name="user" size={12} />
        <span>Quá 24 giờ — giờ chỉ <b>bạn</b> trả lời được, trợ lý thì không.</span>
        <span className="ci-cuaso-phu">
          còn {dienGio(cuaSo.hoursLeft)} trước khi {ten} đóng hẳn
        </span>
      </div>
    );
    const sap = cuaSo.hoursLeft < 6;
    // Vạch ở mép dưới cho thấy còn BAO NHIÊU so với cả cửa sổ, không chỉ con số. Mốc lấy theo
    // cửa sổ dài nhất của kênh (Zalo 48h, Messenger 24h) — đọc bằng mắt nhanh hơn đọc số.
    // Zalo 48h; Messenger, Instagram 24h. Telegram/web không giới hạn nên không tới đây.
    const tron = kenh === 0 ? 48 : 24;
    const con = Math.max(2, Math.min(100, Math.round(cuaSo.hoursLeft / tron * 100)));
    return (
      // Một dòng, không hai. Câu giải thích "hết hạn thì phải chờ khách nhắn lại" là thứ đọc MỘT
      // lần rồi thuộc, nhưng bản trước in nó ra ở mọi hội thoại và chiếm nguyên nửa dải. Đưa vào
      // tooltip: người mới vẫn tra được, người quen việc không phải nhìn lại mỗi ngày.
      //
      // Chỉ khi sắp hết (dưới 6 giờ) mới bung câu nhắc ra ngoài — lúc đó nó là cảnh báo thật.
      <div className={'ci-cuaso ' + (sap ? 'sap' : 'mo')} style={{ '--ci-con': con + '%' }}
           title={'Còn ' + dienGio(cuaSo.hoursLeft) + ' để trả lời trên ' + ten
                  + '. Hết hạn thì phải chờ khách nhắn lại mới gửi được, hoặc dùng tin mẫu đã duyệt.'}>
        <window.Icon name="clock" size={12} />
        <span>{ten} còn <b>{dienGio(cuaSo.hoursLeft)}</b></span>
        {sap && <span className="ci-cuaso-phu">sắp hết — trả lời sớm</span>}
      </div>
    );
  }

  // ── Hồ sơ khách (vùng 4) ─────────────────────────────────────────────────

  function Dong({ nhan, children }) {
    if (children == null || children === '') return null;
    return <div className="ci-hs-dong"><span>{nhan}</span><b>{children}</b></div>;
  }

  // Nhãn của từng hành động trong nhật ký. Thêm hành động mới ở máy chủ thì thêm một dòng ở đây;
  // thiếu thì hiện nguyên mã hành động — xấu nhưng KHÔNG giấu mất dòng nhật ký.
  const TEN_HANH_DONG = {
    'nhan-viec': 'nhận việc',
    'nha-viec': 'nhả việc',
    'chuyen-viec': 'chuyển việc',
    'doi-trang-thai': 'đổi trạng thái',
    'tam-dung-bot': 'chỉnh trợ lý',
    'theo-doi': 'theo dõi',
    'bo-theo-doi': 'bỏ theo dõi',
    'chan-khach': 'chặn khách',
    'bo-chan-khach': 'bỏ chặn khách',
    'xoa-tin': 'xoá tin',
    'sua-tin': 'sửa tin',
    'thu-hoi-tin': 'thu hồi tin',
    'go-ket-noi': 'gỡ kết nối kênh',
    'danh-dau-chua-doc': 'đánh dấu chưa đọc',
    // HAI đường TỰ ĐỘNG. Chúng vốn thiếu nhãn nên nhật ký in ra mã trần ("Hệ thống xoay-vong"),
    // đúng hai dòng người đọc cần nhất khi hỏi "ai giao việc này, máy hay người?".
    'xoay-vong': 'tự động chia việc',
    'tu-nhan-khi-tra-loi': 'tự nhận khi trả lời',
    // Quản trị bấm nút chia lại — KHÁC 'xoay-vong' ở chỗ có người ra lệnh, nên nhật ký ghi tên
    // người đó chứ không ghi "Hệ thống". Khi tra lại thì "ai bấm" là câu hỏi đầu tiên.
    'chia-lai': 'chia lại theo vòng quay',
  };

  // Mã người → tên. Nhật ký lưu MÃ (đặc tả mục 4b: quyết định bằng mã, hiển thị bằng tên), còn
  // `staffs` là danh sách màn hình đã nạp sẵn cho ô phân công — dùng lại, không gọi thêm lượt nào.
  // Tra không ra thì hiện #mã: người đọc còn biết là có một ai đó và có cái để đi tra, chứ ô
  // trống thì họ tưởng hỏng.
  const tenNhanVien = (staffs, ma) =>
    (staffs || []).find(nv => nv.id === ma)?.name || ('#' + ma);

  // `staffs` là danh sách nhân viên màn hình đã nạp sẵn cho ô phân công — dùng lại, không gọi thêm
  // lượt nào. Nhật ký lưu MÃ người (đặc tả mục 4b: quyết định bằng mã, hiển thị bằng tên).
  function MotDongNhatKy({ d, staffs }) {
    // null nghĩa là HỆ THỐNG (vòng quay tự chia việc), KHÔNG phải "không rõ ai" — hai thứ đó khác
    // nhau khi tra lại một hội thoại bị đóng nhầm. Tra không ra tên thì hiện #mã: người dùng còn
    // biết là có ai đó, còn ô trống thì họ tưởng hỏng.
    const tenNguoi = d.userId == null ? 'Hệ thống' : tenNhanVien(staffs, d.userId);
    let ct = null;
    try { ct = d.chiTiet ? JSON.parse(d.chiTiet) : null; } catch {}
    const them =
      // Cả giao tay lẫn tự động chia đều ghi {"cho": <mã người>}. Bản trước in thẳng con số ra
      // ("Hệ thống xoay-vong cho 2") — đúng lớp lỗi đã sửa ở bản tin sáng cùng ngày, và ở đây
      // còn dễ sửa hơn vì `staffs` đã nằm sẵn trong tay.
      ['chuyen-viec', 'xoay-vong', 'chia-lai'].includes(d.hanhDong) && ct?.cho
        ? ' cho ' + tenNhanVien(staffs, ct.cho)
      : d.hanhDong === 'doi-trang-thai' && ct?.trangThai != null ? ' → ' + (TEN_TRANG_THAI[ct.trangThai] || ct.trangThai)
      : d.hanhDong === 'tam-dung-bot' ? (ct?.phut ? ' (tạm dừng ' + ct.phut + ' phút)' : ' (cho chạy lại)')
      : '';
    return (
      <div className="ci-hs-dong nk">
        {/* "3 giờ trước" đủ để lướt, nhưng nhật ký sinh ra để TRA LẠI — mà tra thì cần mốc
            thật để đối chiếu với hộp thư, với lịch sử CRM, với lời khách kể. Rê chuột là ra. */}
        <span title={fmtDate(d.createdUtc, { time: true })}>{fmtAgo(d.createdUtc)}</span>
        <b>{tenNguoi}</b> {TEN_HANH_DONG[d.hanhDong] || d.hanhDong}{them}
      </div>
    );
  }

  // Nối khách chat với khách CRM — NỐI TAY, không đoán tự động. Ghép theo tên sai thường xuyên
  // (trùng tên là chuyện bình thường ở khách du lịch), còn Zalo/Messenger thì không cho biết số
  // điện thoại trừ khi khách tự nhắn. Nối tay đúng 100% và dùng được ngay.
  // Nhãn và ghi chú gắn theo KHÁCH, không theo hội thoại: khách nhắn lại sau ba tháng vẫn còn
  // nhãn cũ. Gắn theo hội thoại thì mỗi lần mở hội thoại mới là mất hết — đúng lúc cần nhất.
  /**
   * Thanh nhãn ngay trong khung chat — bấm chọn, bấm bỏ, thêm nhãn mới tại chỗ.
   *
   * TRƯỚC ĐÂY nhãn nằm trong panel hồ sơ bên phải và chỉ có ô GÕ TỰ DO. Hai hệ quả:
   *   1. Panel đó đóng được (và luôn đóng ở điện thoại) — nhãn coi như không tồn tại.
   *   2. Gõ tự do sinh ra "khach-vip", "vip", "khachvip" cho cùng một ý. Không ai biết bộ nhãn
   *      của công ty gồm những gì, nên lọc theo nhãn không bao giờ ra đủ.
   *
   * Nay danh mục nhãn là một bảng thật (chat_tag_catalog): người trực CHỌN từ danh sách, và nhãn
   * nào gõ mới cũng tự vào danh mục cho lần sau. Chữ hiện ra là tên có dấu, không phải slug —
   * bản trước bày thẳng "khach-kho-tinh" cho người dùng đọc.
   */
  function ThanhNhan({ chiTiet, pushToast }) {
    const id = chiTiet?.conversation?.id;
    const [dangMang, setDangMang] = useState(null);   // slug[] của hội thoại này
    const [danhMuc, setDanhMuc] = useState([]);       // {id, slug, name}[]
    const [mo, setMo] = useState(false);
    const [tim, setTim] = useState('');
    const [dangLam, setDangLam] = useState(false);
    const boc = useRef(null);

    const chuanHoa = (window.ChonNguoiUtil && window.ChonNguoiUtil.chuanHoa)
      || (s => String(s || '').toLowerCase());

    const tai = useCallback(async () => {
      if (!id) return;
      const [a, b] = await Promise.all([
        authedFetch('/api/v1/chat/conversations/' + id + '/tags')
          .then(r => (r.ok ? r.json() : { items: [] })).catch(() => ({ items: [] })),
        authedFetch('/api/v1/chat/tags')
          .then(r => (r.ok ? r.json() : { items: [] })).catch(() => ({ items: [] })),
      ]);
      setDangMang(a.items || []);
      setDanhMuc(b.items || []);
    }, [id]);

    useEffect(() => { setDangMang(null); setMo(false); setTim(''); tai(); }, [tai]);

    useEffect(() => {
      if (!mo) return;
      const ngoai = e => { if (boc.current && !boc.current.contains(e.target)) setMo(false); };
      document.addEventListener('mousedown', ngoai);
      return () => document.removeEventListener('mousedown', ngoai);
    }, [mo]);

    // Tên hiện ra tra từ danh mục; nhãn cũ chưa có trong danh mục thì đành hiện slug — thà xấu
    // còn hơn giấu một nhãn đang thật sự gắn trên khách.
    const ten = slug => (danhMuc.find(n => n.slug === slug) || {}).name || slug;

    async function bat(slug, ten) {
      setDangLam(true);
      try {
        const co = (dangMang || []).includes(slug);
        const r = co
          ? await authedFetch('/api/v1/chat/conversations/' + id + '/tags/' + encodeURIComponent(slug),
                              { method: 'DELETE' })
          : await authedFetch('/api/v1/chat/conversations/' + id + '/tags', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ tag: ten || slug }),
            });
        if (!r.ok) { pushToast('Không đổi được nhãn', 'error'); return; }
        await tai();
      } finally { setDangLam(false); }
    }

    async function themMoi(e) {
      e.preventDefault();
      const t = tim.trim();
      if (!t) return;
      // Gắn thẳng cho hội thoại — máy chủ tự đưa nhãn vào danh mục. Bắt người dùng tạo nhãn
      // trước rồi mới quay lại gắn là hai bước cho một ý định.
      await bat(chuanHoa(t).replace(/\s+/g, '-'), t);
      setTim('');
    }

    if (!id || dangMang === null) return null;

    const conLai = danhMuc.filter(n => !(dangMang || []).includes(n.slug));
    const locDuoc = tim.trim()
      ? conLai.filter(n => chuanHoa(n.name).includes(chuanHoa(tim)) || n.slug.includes(chuanHoa(tim)))
      : conLai;
    const trungHet = danhMuc.some(n => n.slug === chuanHoa(tim).replace(/\s+/g, '-'));

    return (
      <div className="ci-tn" ref={boc}>
        <window.Icon name="tag" size={13} />
        {dangMang.length === 0 && <span className="ci-tn-trong">Chưa gắn nhãn nào</span>}
        {dangMang.map(s => (
          <span key={s} className="ci-tn-chip">
            {ten(s)}
            <button onClick={() => bat(s)} disabled={dangLam}
                    title={'Bỏ nhãn ' + ten(s)} aria-label={'Bỏ nhãn ' + ten(s)}>
              <window.Icon name="close" size={11} />
            </button>
          </span>
        ))}

        <div className="ci-tn-boc">
          <button className="ci-tn-them" onClick={() => setMo(x => !x)}
                  aria-expanded={mo} aria-haspopup="listbox">
            <window.Icon name="plus" size={11} /> Nhãn
          </button>

          {mo && (
            <div className="ci-tn-hop" role="listbox" aria-label="Chọn nhãn">
              <form className="ci-tn-tim" onSubmit={themMoi}>
                <window.Icon name="search" size={13} />
                <input value={tim} onChange={e => setTim(e.target.value)} autoFocus
                       placeholder="Tìm hoặc gõ nhãn mới…" aria-label="Tìm hoặc tạo nhãn" />
              </form>

              <div className="ci-tn-ds">
                {locDuoc.map(n => (
                  <button key={n.id} type="button" role="option" aria-selected="false"
                          className="ci-tn-dong" disabled={dangLam}
                          onClick={() => { bat(n.slug, n.name); setMo(false); setTim(''); }}>
                    <span>{n.name}</span>
                    {/* Số khách đang mang — nhãn dùng nhiều đáng chọn hơn nhãn ai đó tạo rồi bỏ. */}
                    {n.usageCount > 0 && <i>{n.usageCount}</i>}
                  </button>
                ))}
                {locDuoc.length === 0 && !tim.trim() && (
                  <div className="ci-tn-rong">Đã gắn hết nhãn trong danh mục.</div>
                )}
              </div>

              {/* Gõ một chữ chưa có trong danh mục thì mời tạo luôn, ngay tại chỗ vừa gõ. */}
              {tim.trim() && !trungHet && (
                <button type="button" className="ci-tn-tao" disabled={dangLam}
                        onClick={() => { themMoi({ preventDefault() {} }); setMo(false); }}>
                  <window.Icon name="plus" size={12} /> Tạo nhãn “{tim.trim()}”
                </button>
              )}
            </div>
          )}
        </div>
      </div>
    );
  }

  /**
   * Bảng quản lý DANH MỤC nhãn — một mục trong hộp cài đặt hộp thư.
   *
   * Danh mục này RIÊNG TỪNG CÔNG TY: mọi câu lệnh kẹp tenant_id, khoá duy nhất là (tenant_id, slug),
   * và công ty lấy từ PHIÊN chứ không từ phía người gọi. Xoá nhãn của công ty khác trả 404.
   *
   * Slug đi NGAY SAU tên trên cùng một dòng, không xuống dòng riêng: nó là thứ thật sự ghi lên
   * khách và đi trên đường dẫn nên phải thấy, nhưng nó là chi tiết phụ, không đáng ăn gấp đôi
   * chiều cao mỗi dòng.
   *
   * ĐÃ BỎ CỘT ID (09/09/2026). Mã số dòng không nói gì với người quản trị — cùng lý do ô chọn
   * người chỉ hiện mã ở dòng trùng tên. Nó chiếm cột đầu bảng, kéo mắt về trái, rồi không
   * dùng vào việc gì.
   */
  function QuanLyNhan({ pushToast }) {
    const [ds, setDs] = useState(null);
    const [ten, setTen] = useState('');
    const [loc, setLoc] = useState('');
    const [dangLam, setDangLam] = useState(false);

    const tai = useCallback(async () => {
      const r = await authedFetch('/api/v1/chat/tags')
        .then(x => (x.ok ? x.json() : { items: [] })).catch(() => ({ items: [] }));
      setDs(r.items || []);
    }, []);
    useEffect(() => { tai(); }, [tai]);

    async function them(e) {
      e.preventDefault();
      const t = ten.trim();
      if (!t) return;
      setDangLam(true);
      try {
        const r = await authedFetch('/api/v1/chat/tags', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name: t }),
        });
        if (!r.ok) { pushToast('Tên nhãn không hợp lệ', 'error'); return; }
        setTen(''); await tai();
      } finally { setDangLam(false); }
    }

    async function xoa(n) {
      // Hỏi lại VÀ nói rõ hậu quả kèm CON SỐ: xoá nhãn không chỉ xoá một dòng bảng, nó gỡ nhãn
      // khỏi tất cả khách đang mang. Không có con số thì "bạn có chắc không" chẳng giúp gì.
      const cau = n.usageCount > 0
        ? 'Xoá nhãn “' + n.name + '” sẽ gỡ nó khỏi ' + n.usageCount + ' khách. Không hoàn tác được.'
        : 'Xoá nhãn “' + n.name + '”? Chưa khách nào mang nhãn này.';
      const ok = window.appConfirm
        ? await window.appConfirm(cau, { title: 'Xoá nhãn', confirmLabel: 'Xoá nhãn', danger: true })
        : window.confirm(cau);
      if (!ok) return;

      setDangLam(true);
      try {
        const r = await authedFetch('/api/v1/chat/tags/' + n.id, { method: 'DELETE' });
        if (!r.ok) { pushToast('Không xoá được nhãn', 'error'); return; }
        const j = await r.json().catch(() => ({}));
        pushToast(j.removedFrom > 0 ? 'Đã xoá nhãn và gỡ khỏi ' + j.removedFrom + ' khách'
                                    : 'Đã xoá nhãn', 'success');
        await tai();
      } finally { setDangLam(false); }
    }

    if (ds === null) return <div className="ci-pc-dangtai">Đang tải…</div>;

    // Lọc BỎ DẤU, dùng chung hàm chuẩn hoá của ô chọn người: gõ "khach vip" phải ra
    // “Khách VIP”. Không ai gõ đủ dấu khi đang tìm nhanh.
    const chuanHoaNhan = (window.ChonNguoiUtil && window.ChonNguoiUtil.chuanHoa)
      || (x => String(x || '').toLowerCase());
    const qLoc = chuanHoaNhan(loc);
    const hienThi = qLoc ? ds.filter(n => chuanHoaNhan(n.name + ' ' + n.slug).includes(qLoc)) : ds;

    return (
      <div className="ci-qn">
        <form className="ci-qn-them" onSubmit={them}>
          <input value={ten} onChange={e => setTen(e.target.value)}
                 placeholder="Tên nhãn mới, vd: Khách VIP" aria-label="Tên nhãn mới" />
          <button className="ci-nut chinh" type="submit" disabled={dangLam || !ten.trim()}>
            Thêm nhãn
          </button>
        </form>
        <p className="ci-pc-phu">
          Người trực cũng tạo được nhãn ngay trên khung chat — nhãn nào gõ mới cũng tự vào bảng này.
        </p>

        {ds.length === 0 ? (
          <div className="ci-qn-rong">
            Chưa có nhãn nào. Thêm vài nhãn hay dùng (“Khách VIP”, “Chờ báo giá”) để người trực
            bấm chọn thay vì gõ tay mỗi lần.
          </div>
        ) : (
          <>
            {/* Ô lọc CHỈ mọc ra khi danh sách đủ dài để phải tìm. Dưới ngưỡng đó mắt quét hết
                được, mà một ô tìm luôn hiện thì chỉ là thêm một thứ chiếm chỗ. */}
            <div className="ci-qn-dau">
              <span className="ci-qn-dem"><b>{ds.length}</b> nhãn</span>
              {ds.length > 8 && (
                <label className="ci-qn-loc">
                  <Icon name="search" size={13} />
                  <input value={loc} onChange={e => setLoc(e.target.value)}
                         placeholder="Lọc theo tên…" aria-label="Lọc nhãn" />
                </label>
              )}
            </div>

            {/* Trần chiều cao + cuộn riêng: công ty dùng nhiều nhãn thì danh sách dài bằng mấy màn
                hình, đẩy ô thêm nhãn và cả các mục khác ra khỏi tầm nhìn. */}
            <div className="ci-qn-ds">
              {hienThi.length === 0 && (
                <div className="ci-qn-rong-loc">Không nhãn nào khớp “{loc}”.</div>
              )}
              {hienThi.map(n => (
                <div key={n.id} className="ci-qn-dong">
                  <b>{n.name}</b>
                  <code>{n.slug}</code>
                  {/* Nhãn chưa ai mang thì để TRỐNG, không in gạch ngang: một cột gạch ngang chạy
                      dọc danh sách là nhiễu thuần tuý, trong khi ô trống đã nói đúng điều đó. */}
                  <span className="ci-qn-gan">
                    {n.usageCount > 0 ? n.usageCount + ' khách' : ''}
                  </span>
                  {/* Nút xoá là BIỂU TƯỢNG mờ, đỏ lên khi trỏ tới. Mỗi dòng một nút đỏ như cũ thì cả
                      danh sách đỏ rực, trong khi xoá nhãn là việc hiếm nhất ở màn này. Hộp hỏi lại kèm
                      con số khách vẫn giữ nguyên — đó mới là chốt chặn thật. */}
                  <button type="button" className="ci-qn-xoa" disabled={dangLam}
                          title={'Xoá nhãn ' + n.name} aria-label={'Xoá nhãn ' + n.name}
                          onClick={() => xoa(n)}>
                    <Icon name="trash" size={13} />
                  </button>
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    );
  }

  /** Ghi chú nội bộ về khách. Nhãn đã tách sang ThanhNhan ở khung chat. */
  function NhanVaGhiChu({ chiTiet, pushToast }) {
    const id = chiTiet?.conversation?.id;
    const [ghiChu, setGhiChu] = useState(null);
    const [ghiChuMoi, setGhiChuMoi] = useState('');
    const [dangLam, setDangLam] = useState(false);

    const tai = useCallback(async () => {
      if (!id) return;
      const b = await authedFetch('/api/v1/chat/conversations/' + id + '/notes')
        .then(r => (r.ok ? r.json() : { items: [] })).catch(() => ({ items: [] }));
      setGhiChu(b.items || []);
    }, [id]);

    useEffect(() => { setGhiChu(null); tai(); }, [tai]);

    async function themGhiChu(e) {
      e.preventDefault();
      const t = ghiChuMoi.trim();
      if (!t) return;
      setDangLam(true);
      try {
        // ⚠️ KHOÁ PHẢI LÀ `body`, không phải `noiDung`. Máy chủ nhận NoteReq(string? Body) và
        // trả về cũng bằng khoá `body`. Trước 08/09/2026 giao diện gửi `noiDung` ở đây và đọc
        // `g.noiDung` khi vẽ — sai cả hai chiều, nên LƯU GHI CHÚ CHƯA BAO GIỜ CHẠY: mọi lượt
        // gửi đều nhận 400 "Chưa nhập nội dung ghi chú", và kể cả có lưu được thì ô nội dung
        // cũng vẽ ra rỗng. Không test nào bắt được vì cả hai bộ đều không chạm tới đường này.
        const r = await authedFetch('/api/v1/chat/conversations/' + id + '/notes', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ body: t }),
        });
        if (!r.ok) { pushToast('Không lưu được ghi chú', 'error'); return; }
        setGhiChuMoi(''); await tai();
      } finally { setDangLam(false); }
    }

    return (
      <>
        {/* ⚠️ KHỐI "NHÃN" ĐÃ CHUYỂN RA KHUNG CHAT (08/09/2026) — xem component ThanhNhan.
            Nhãn là thứ vừa liếc vừa bấm trong lúc đang đọc tin khách, nên nó thuộc về khung chat
            chứ không phải một panel bên cạnh mà người ta hay đóng lại. Quan trọng hơn: để cả hai
            chỗ cùng gắn/gỡ nhãn thì hai chỗ cùng giữ một bản sao danh sách, và bản nào cũng có
            thể cũ — sửa bên này bên kia vẫn hiện nhãn đã gỡ. Một dữ liệu thì một chỗ sửa. */}
        <div className="ci-hs-muc">
          {/* Nhãn "chỉ nội bộ" nằm NGAY CẠNH tiêu đề chứ không phải một dòng chú thích riêng bên
              dưới. Trước đây câu "Chỉ nhân viên đọc được…" chiếm nguyên một dòng và vẫn bị đọc
              lướt qua; gắn vào tiêu đề thì nó đi cùng ánh mắt lúc người ta đọc chữ "Ghi chú",
              đúng lúc cần biết điều đó. */}
          <h4 className="ci-gc-dau">
            Ghi chú nội bộ
            <span className="ci-gc-kin" title="Khách không bao giờ thấy nội dung ở đây">
              <window.Icon name="shield" size={10} /> chỉ nội bộ
            </span>
          </h4>

          {/* Ghi chú CŨ đứng trước ô soạn — đọc trước rồi mới viết thêm, đúng thứ tự người ta làm.
              Bản trước ô soạn đứng trên, ghi chú cũ nằm dưới nút Lưu, nên muốn xem người trước đã
              dặn gì thì phải đọc qua cả cái form. */}
          {ghiChu !== null && ghiChu.length === 0 && (
            <div className="ci-gc-rong">
              <window.Icon name="edit" size={15} />
              <span>Chưa có ghi chú nào. Ghi lại điều người trực sau cần biết —
                    “khách đã có báo giá”, “đừng gọi trước 9h”.</span>
            </div>
          )}
          {(ghiChu || []).map(g => (
            <div key={g.id} className="ci-gc">
              <span className="ci-gc-tron" aria-hidden="true">
                {window.ChonNguoiUtil ? window.ChonNguoiUtil.chuDau(g.username) : '•'}
              </span>
              <div className="ci-gc-than">
                <span className="ci-gc-meta">
                  <b>{g.username}</b>
                  <time dateTime={g.createdUtc}>{fmtAgo(g.createdUtc)}</time>
                </span>
                <p>{g.body}</p>
              </div>
            </div>
          ))}

          {/* Ô soạn nở ra khi có chữ, và nút Lưu chỉ mọc khi thật sự có gì để lưu — giống hệt lối
              nút "Gán" ở khối phụ trách. Bày sẵn một nút xám mờ chỉ tốn chỗ trong cột 312px. */}
          <form className="ci-gc-soan" onSubmit={themGhiChu}>
            <textarea value={ghiChuMoi} onChange={e => setGhiChuMoi(e.target.value)}
                      rows={ghiChuMoi.trim() ? 3 : 1} aria-label="Ghi chú mới"
                      placeholder="Thêm ghi chú…" />
            {ghiChuMoi.trim() && (
              <div className="ci-gc-nut">
                <button className="ci-nut chinh nho" type="submit" disabled={dangLam}>
                  {dangLam ? 'Đang lưu…' : 'Lưu ghi chú'}
                </button>
                <button className="ci-nut nho" type="button" disabled={dangLam}
                        onClick={() => setGhiChuMoi('')}>Thôi</button>
              </div>
            )}
          </form>
        </div>
      </>
    );
  }

  function NoiCrm({ chiTiet, pushToast }) {
    const v = chiTiet?.conversation;
    const lh = chiTiet?.contact;
    const [mo, setMo] = useState(false);
    const [tim, setTim] = useState('');
    const [ds, setDs] = useState(null);
    const [dangLam, setDangLam] = useState(false);

    // Chờ người dùng ngừng gõ rồi mới hỏi: mỗi phím một lượt gọi CRM là vừa chậm vừa tốn quota
    // của chính công ty khách.
    useEffect(() => {
      if (!mo || !v?.id) return;
      const q = tim.trim();
      if (q.length < 2) { setDs(null); return; }
      let song = true;
      const hen = setTimeout(async () => {
        try {
          const r = await authedFetch('/api/v1/chat/conversations/' + v.id
            + '/crm-search?q=' + encodeURIComponent(q));
          const j = r.ok ? await r.json() : { items: [] };
          if (song) setDs(j.items || []);
        } catch { if (song) setDs([]); }
      }, 350);
      return () => { song = false; clearTimeout(hen); };
    }, [mo, tim, v?.id]);

    async function doiNoi(customerId) {
      setDangLam(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + v.id + '/link-crm', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(customerId ? { customerId: Number(customerId) } : {}),
        });
        if (!r.ok) { pushToast('Không lưu được', 'error'); return; }
        pushToast(customerId ? 'Đã nối với khách CRM' : 'Đã gỡ nối khách CRM', 'success');
        setMo(false); setTim(''); setDs(null);
      } finally { setDangLam(false); }
    }

    if (lh?.crmCustomerId && !mo) {
      return (
        <div className="ci-hs-crm">
          Đã nối với khách <b>#{lh.crmCustomerId}</b>
          <div className="ci-hs-crm-nut">
            <button className="ci-nut nho" onClick={() => setMo(true)}>Đổi</button>
            <button className="ci-nut nho" disabled={dangLam} onClick={() => doiNoi(null)}>Gỡ nối</button>
          </div>
        </div>
      );
    }

    if (!mo) {
      return (
        <div className="ci-hs-trong">
          Chưa nối với khách hàng trong CRM. Bot đang trả lời bằng kiến thức chung, không đọc
          lịch sử mua hay bảng giá của khách này.
          <div className="ci-hs-crm-nut">
            <button className="ci-nut nho" onClick={() => setMo(true)}>Nối khách CRM</button>
          </div>
        </div>
      );
    }

    return (
      <div className="ci-hs-crm-tim">
        <input value={tim} onChange={e => setTim(e.target.value)} autoFocus
               placeholder="Tên, số điện thoại hoặc mã khách…" />
        {tim.trim().length >= 2 && ds === null && <div className="ci-hs-trong">Đang tìm…</div>}
        {ds !== null && ds.length === 0 && <div className="ci-hs-trong">Không thấy khách nào khớp.</div>}
        {(ds || []).map(k => (
          <button key={k.id} className="ci-hs-crm-kq" disabled={dangLam} onClick={() => doiNoi(k.id)}>
            <b>{k.name}</b>
            <span>{[k.code, k.phone].filter(Boolean).join(' · ') || '#' + k.id}</span>
          </button>
        ))}
        <div className="ci-hs-crm-nut">
          <button className="ci-nut nho" onClick={() => { setMo(false); setTim(''); setDs(null); }}>Thôi</button>
        </div>
      </div>
    );
  }

  /**
   * Khối "Phụ trách" — đứng ngay dưới ci-hs-dau trong hồ sơ khách.
   *
   * ⚠️ HAI VIỆC KHÁC NHAU, TRƯỚC ĐÂY BỊ TRỘN LÀM MỘT trên thanh tiêu đề chật:
   *
   *   • NHẬN CHĂM SÓC — nhận về CHÍNH MÌNH. Một chạm, không phải chọn ai. Đây là việc người
   *     trực làm mấy chục lần một ngày, nên nó là nút to nhất và không có bước trung gian.
   *   • GÁN NGƯỜI CHĂM SÓC — giao cho NGƯỜI KHÁC. Phải chọn đúng một người trong 108, nên nó
   *     là hai bước: chọn xong rồi mới bấm Gán. Bản trước ô chọn gán NGAY khi đổi lựa chọn —
   *     trượt tay một dòng trong hộp thả xuống là hội thoại của khách đang nói chuyện nhảy
   *     sang người khác, không hỏi lại, không hoàn tác được.
   *
   * Nút cũ còn nói SAI: hễ hội thoại có người là ghi "Đã nhận chăm sóc", kể cả khi người đó là
   * đồng nghiệp. Đọc thành "mình đã nhận" trong khi việc là của người khác. Nay ba trạng thái
   * nói ba câu khác nhau, dựa trên phanCong.meId.
   */
  function KhoiPhuTrach({ v, phanCong, chonDuoc, onNhan, onGiao, onNha }) {
    const [moChon, setMoChon] = useState(false);
    const [dinh, setDinh] = useState(null);        // người vừa chọn, CHƯA bấm Gán
    const [dangLam, setDangLam] = useState(false);

    const maPT = v.assignedUserId || null;
    const nguoiPT = (phanCong.staffs || []).find(nv => nv.id === maPT) || null;
    // Dòng cũ chỉ có tên đăng nhập, dòng mới chỉ có mã — đọc cả hai, không thì hội thoại cũ hiện
    // ra như chưa ai nhận.
    const tenPT = nguoiPT?.name || v.assignedUsername || (maPT ? '#' + maPT : null);
    const coNguoi = !!(maPT || v.assignedUsername);
    const laToi = maPT != null && phanCong.meId != null && maPT === phanCong.meId;
    // Nhân viên thường khi đang kẹp quyền xem: hội thoại họ mở được thì đã là của họ, nút nhận
    // bấm không ra gì — để nguyên là trông như lỗi.
    const duocGiao = phanCong.isAdmin || !phanCong.scopeOwnOnly;

    async function chay(fn) {
      setDangLam(true);
      try { await fn(); } finally { setDangLam(false); }
    }

    function huy() { setMoChon(false); setDinh(null); }

    return (
      <div className="ci-hs-muc ci-pt">
        <h4>Phụ trách</h4>

        <div className={'ci-pt-the' + (coNguoi ? '' : ' trong')}>
          {coNguoi
            ? <>
                <span className="ci-pt-tron" aria-hidden="true">
                  {window.ChonNguoiUtil ? window.ChonNguoiUtil.chuDau(tenPT) : '•'}
                </span>
                <span className="ci-pt-ten">
                  <b>{laToi ? 'Bạn' : tenPT}</b>
                  <span>đang phụ trách</span>
                </span>
              </>
            : <>
                <span className="ci-pt-cham" aria-hidden="true" />
                <span className="ci-pt-ten">
                  <b>Chưa ai phụ trách</b>
                  <span>khách đang chờ người nhận</span>
                </span>
              </>}
        </div>

        {/* ⚠️ KHÔNG có nút "Nhận chăm sóc" ở đây. Nó đã đứng sẵn trên thanh tiêu đề — bày lại
            lần nữa cách nhau vài trăm pixel là hai nút giống hệt trên cùng một màn hình, người
            dùng phải dừng lại đoán xem hai cái có khác nhau không (chủ dự án bắt được 08/09/2026).
            Panel này lo ĐÚNG MỘT việc: giao cho người khác.

            Danh sách hiện SẴN, không giấu sau một nút "Gán người khác". Giấu đi thì việc này tốn
            hai lần bấm mà chẳng che được gì — panel còn nguyên chỗ trống bên dưới. */}
        {duocGiao && (
          chonDuoc.length === 0 ? (
            /* HAI NGUYÊN NHÂN, MỘT TRIỆU CHỨNG — phải nói đúng cái nào, vì hai cách sửa khác hẳn.
               Máy chủ đã tách hai ca này trong log từ 08/09/2026 (hai câu cảnh báo riêng), nhưng
               giao diện thì vẫn đổ chung một câu "đội trực còn trống". Đo trên staging sáng
               09/09: CRM trả về 0 nhân viên (statuses=10, sources=4 vẫn về bình thường nên không
               phải lỗi đọc dữ liệu) — màn hình lúc đó giục quản trị đi thêm người vào đội trực,
               trong khi đội trực chẳng liên quan gì và có thêm cũng không hết lỗi. */
            (phanCong.staffs || []).length === 0 ? (
              <p className="ci-pt-nhac">
                Không lấy được danh sách nhân viên từ CRM — không phải công ty chưa có ai. Thử tải
                lại trang; còn nguyên thì báo kỹ thuật xem log máy chủ.
              </p>
            ) : (
              <p className="ci-pt-nhac">
                Chưa có ai để giao. Đội trực chat còn trống — nhờ quản trị thêm người trong
                <b> Phân công</b> ở đầu hộp thư.
              </p>
            )
          ) : (
            <div className="ci-pt-giao">
              <window.ChonNguoi danhSach={chonDuoc} giaTri={dinh} khoa={dangLam}
                                onChon={setDinh} nhan="Chọn người để giao…" />

              {/* Nút gán CHỈ mọc ra khi đã chọn được một người KHÁC người đang giữ. Bày sẵn một
                  nút xám mờ thì nó vừa chiếm chỗ vừa không nói được là còn thiếu bước nào; còn
                  bày nút sáng mà chưa chọn ai thì bấm vào chẳng ra gì. */}
              {dinh && dinh !== maPT && (
                <div className="ci-pt-nut">
                  <button className="ci-nut chinh" disabled={dangLam}
                          onClick={() => chay(async () => { await onGiao(dinh); setDinh(maPT); })}>
                    {dangLam
                      ? 'Đang gán…'
                      : 'Gán cho ' + ((chonDuoc.find(nv => nv.id === dinh) || {}).name || 'người này')}
                  </button>
                  <button className="ci-nut nho" disabled={dangLam} onClick={() => setDinh(maPT)}>
                    Thôi
                  </button>
                </div>
              )}

              {coNguoi && !(dinh && dinh !== maPT) && (
                <div className="ci-pt-nut">
                  {/* "Dừng chăm sóc" chứ không phải "Nhả việc": người dùng đọc màn hình này bằng
                      từ "chăm sóc" ở khắp nơi (nhận chăm sóc, người chăm sóc), còn "nhả việc" là
                      tiếng của người viết mã. Cùng một hành động thì phải cùng một từ. */}
                  <button className="ci-nut nguyhiem nho" disabled={dangLam}
                          onClick={() => chay(async () => { await onNha(); setDinh(null); })}>
                    Dừng chăm sóc
                  </button>
                </div>
              )}
            </div>
          )
        )}
      </div>
    );
  }

  function HoSo({ chiTiet, phanCong, chonDuoc, onDong, pushToast, onNhan, onGiao, onNha }) {
    const v = chiTiet?.conversation;
    const lh = chiTiet?.contact;
    const [nhatKy, setNhatKy] = useState(null);
    // Thẻ đang xem. Mở lại luôn về "Chăm sóc": đó là việc người trực làm, còn hai thẻ kia là tra
    // cứu — nhớ thẻ cũ thì mở hội thoại tiếp theo lại rơi vào màn nhật ký của người trước.
    const [tab, setTab] = useState('chamsoc');
    // Nhật ký hiện MẤY DÒNG. Máy chủ đã chặn ở 50, nhưng đổ cả 50 ra một lượt thì thẻ dài lê thê
    // trong khi thứ người ta cần gần như luôn là vài thao tác gần nhất.
    const [soNhatKy, setSoNhatKy] = useState(6);

    // Tải RIÊNG, không nhét vào /conversations/{id}: nhật ký chỉ xem khi mở panel hồ sơ, còn
    // hội thoại thì tải lại mỗi lần có sự kiện — gộp vào là kéo thêm một bảng nữa mỗi tin mới.
    useEffect(() => {
      if (!v?.id) return;
      let song = true;
      setNhatKy(null);
      authedFetch('/api/v1/chat/conversations/' + v.id + '/audit')
        .then(r => (r.ok ? r.json() : { items: [] }))
        .then(j => { if (song) setNhatKy(j.items || []); })
        .catch(() => { if (song) setNhatKy([]); });
      return () => { song = false; };
    }, [v?.id]);

    if (!v) return null;
    const ten = v.displayName || v.contactExternalId;

    async function chepMa() {
      const ok = await window.tourkitUtil.copyText(v.contactExternalId);
      pushToast(ok ? 'Đã chép mã người dùng' : 'Trình duyệt không cho chép', ok ? 'success' : 'error');
    }

    return (
      <aside className="ci-hoso">
        <div className="ci-hs-dau">
          <AnhDaiDien ten={ten} url={lh?.avatarUrl} co={34} />
          <div className="ci-hs-ten">
            <b>{ten}</b>
            <span>{KENH[v.channel]?.ten}</span>
          </div>
          <button className="ci-nut-icon" onClick={onDong} title="Đóng hồ sơ" aria-label="Đóng hồ sơ">
            <window.Icon name="close" size={15} />
          </button>
        </div>

        {/* ── Ba thẻ, chia theo VIỆC ĐANG LÀM ───────────────────────────────────────────
            Trước 08/09/2026 panel này là SÁU khối xếp dọc trong một cuộn duy nhất: phụ trách,
            xử lý, khách hàng CRM, thông tin, nhãn + ghi chú, nhật ký. Cao gấp ba màn hình, và
            mọi thứ cùng một cỡ chữ nên không có gì nổi lên trước — muốn xem số điện thoại thì
            phải cuộn qua cả cụm giao việc, muốn xem nhật ký thì cuộn qua tất.

            Chia theo việc, không theo nguồn dữ liệu:
              • Chăm sóc  — thứ ĐANG làm với hội thoại này: giao ai, trạng thái, nhãn, ghi chú.
              • Khách hàng — biết gì về người bên kia: hồ sơ CRM, liên hệ, đến từ đâu.
              • Nhật ký   — ai đã làm gì, xem khi cần truy lại.
            Nhật ký tách riêng vì nó là thứ hiếm mở nhất mà lại dài nhất — để chung là nó đẩy
            mọi thứ khác xuống dưới màn hình. */}
        <div className="ci-hs-tab" role="tablist" aria-label="Mục hồ sơ">
          {[['chamsoc', 'Chăm sóc'], ['khach', 'Khách hàng'], ['nhatky', 'Nhật ký']].map(([ma, ten]) => (
            <button key={ma} role="tab" aria-selected={tab === ma}
                    className={'ci-hs-tab-nut' + (tab === ma ? ' on' : '')}
                    onClick={() => setTab(ma)}>{ten}</button>
          ))}
        </div>

        {tab === 'chamsoc' && (
          <>
            {/* Giao việc đứng ĐẦU thẻ — câu hỏi đầu tiên khi mở một hội thoại lạ là "việc này
                của ai?", và đây cũng là việc duy nhất trong panel có thao tác đi kèm. */}
            <KhoiPhuTrach v={v} phanCong={phanCong} chonDuoc={chonDuoc}
                          onNhan={onNhan} onGiao={onGiao} onNha={onNha} />

            {/* Dòng "Phụ trách" ĐÃ BỎ khỏi thẻ này: nó vừa lặp lại khối trên vừa là bản chỉ-đọc
                của cùng một dữ kiện, mà chỗ sửa lại nằm nơi khác — người dùng đọc dòng đó rồi
                đi tìm chỗ đổi ngay bên cạnh mà không thấy. */}
            <div className="ci-hs-muc">
              <h4>Trạng thái</h4>
              <div className="ci-hs-the">
                <div className="ci-hs-dong">
                  <span>Hội thoại</span>
                  <span className="cham"><i />{TEN_TRANG_THAI[v.status]}</span>
                </div>
                <div className="ci-hs-dong">
                  <span>Trợ lý bot</span>
                  <span>{v.botPaused ? 'đang tạm dừng' : 'đang trả lời'}</span>
                </div>
              </div>
            </div>

            <NhanVaGhiChu chiTiet={chiTiet} pushToast={pushToast} />
          </>
        )}

        {tab === 'khach' && (
          <>
            <div className="ci-hs-muc">
              <h4>Khách hàng CRM</h4>
              <NoiCrm chiTiet={chiTiet} pushToast={pushToast} />
            </div>

            <div className="ci-hs-muc">
              <h4>Liên hệ</h4>
              {/* Gom vào MỘT thẻ có viền như khối trạng thái, thay vì các dòng trôi nổi cạnh
                  nhau: cùng một kiểu trình bày cho cùng một kiểu nội dung (nhãn — giá trị) thì
                  mắt không phải học lại cách đọc ở mỗi khối. */}
              <div className="ci-hs-the">
                <div className="ci-hs-dong ma">
                  <span>Mã người dùng</span>
                  <button onClick={chepMa} title="Chép mã người dùng">
                    {v.contactExternalId}
                    <window.Icon name="copy" size={12} />
                  </button>
                </div>
                <Dong nhan="Số điện thoại">{lh?.phone}</Dong>
                <Dong nhan="Email">{lh?.email}</Dong>
                <Dong nhan="Nhắn lần đầu">{lh?.createdUtc ? fmtDate(lh.createdUtc) : null}</Dong>
                <Dong nhan="Nhắn gần nhất">
                  {v.contactRepliedAt ? fmtAgo(v.contactRepliedAt) : 'chưa nhắn lần nào'}
                </Dong>
              </div>
            </div>

            {/* Khách đến từ đâu. Kênh chỉ nói MỘT LẦN lúc khách mở cuộc trò chuyện nên đây là bản
                ghi duy nhất — không tra lại được ở đâu khác. Cả khối chỉ hiện khi có, đừng bày
                một tiêu đề trống. */}
            {v.referral && (
              <div className="ci-hs-muc">
                <h4>Đến từ</h4>
                <div className="ci-hs-the">
                  <Dong nhan="Nguồn">{NGUON_KHACH[v.referral.source] || v.referral.source}</Dong>
                  <Dong nhan="Mã liên kết">{v.referral.gtRef}</Dong>
                  <Dong nhan="Mã quảng cáo">{v.referral.adId}</Dong>
                </div>
              </div>
            )}
          </>
        )}

        {tab === 'nhatky' && (
          <div className="ci-hs-muc">
            <h4>Nhật ký thao tác</h4>
            {nhatKy === null
              ? <div className="ci-hs-trong">Đang tải…</div>
              : nhatKy.length === 0
                ? <div className="ci-hs-trong">Chưa có thao tác nào được ghi lại.</div>
                : <>
                    {nhatKy.slice(0, soNhatKy).map(d =>
                      <MotDongNhatKy key={d.id} d={d} staffs={phanCong?.staffs} />)}
                    {/* Nói rõ CÒN BAO NHIÊU, không chỉ "Xem thêm": biết còn 3 hay còn 44 thì mới
                        quyết được có đáng bấm không. Máy chủ chặn ở 50 nên con số này có trần. */}
                    {nhatKy.length > soNhatKy && (
                      <button className="ci-nut nho ci-hs-them-nut"
                              onClick={() => setSoNhatKy(n => n + 20)}>
                        Xem thêm {Math.min(20, nhatKy.length - soNhatKy)} thao tác
                        <span className="ci-hs-con">còn {nhatKy.length - soNhatKy}</span>
                      </button>
                    )}
                  </>}
          </div>
        )}
      </aside>
    );
  }

  // ── Khai kết nối kênh ────────────────────────────────────────────────────

  // Popup thay vì khối chèn giữa trang: khai kênh là việc làm MỘT LẦN lúc cài đặt, còn hộp thư là
  // việc làm hằng ngày. Đẩy khối cấu hình vào giữa làm danh sách hội thoại tụt xuống mỗi lần mở.
  //
  // Form TỰ VẼ theo danh sách ô mà máy chủ trả về: thêm kênh mới ở backend là giao diện tự có ô
  // nhập, không phải sửa hai nơi rồi lệch.
  // ⚠️ PHẢI ở tầng module, KHÔNG được lồng trong KhaiKenh.
  //
  // Hàm khai báo bên trong một component là một **kiểu component MỚI ở mỗi lần vẽ lại**. React
  // thấy kiểu khác thì tháo cả nhánh cũ ra rồi dựng nhánh mới — thẻ <input> thành một nút DOM
  // khác hẳn, nên con trỏ nhảy ra ngoài. Gõ một ký tự → setNhap → vẽ lại → mất focus: người dùng
  // phải bấm lại vào ô sau MỖI chữ cái. Nhìn thì như "trang bị đơ", không ai nghĩ tới React.
  //
  // Có test canh việc này (ChatUiGuardTests) — đừng đẩy ngược vào trong cho gọn.
  // Chữ trong form kênh do MÁY CHỦ mô tả, nên nó phải chở được liên kết và chữ đậm mà không
  // cần mỗi kênh một đoạn JSX riêng. Hai cú pháp, đúng hai cái cần: [chữ](đường dẫn) và **đậm**.
  // Cố ý KHÔNG dùng thư viện markdown: nhận HTML từ chuỗi cấu hình là mở cửa cho chèn thẻ.
  function chuCoLienKet(raw) {
    const ra = [];
    const mau = /\[([^\]]+)\]\(([^)]+)\)|\*\*([^*]+)\*\*/g;
    let cuoi = 0, m, i = 0;
    while ((m = mau.exec(raw)) !== null) {
      if (m.index > cuoi) ra.push(raw.slice(cuoi, m.index));
      if (m[1]) {
        // Chỉ nhận http(s): chuỗi cấu hình không được mở ra javascript:
        const an = /^https?:\/\//i.test(m[2]);
        ra.push(an
          ? <a key={i++} href={m[2]} target="_blank" rel="noopener noreferrer">{m[1]}</a>
          : m[1]);
      } else {
        ra.push(<b key={i++}>{m[3]}</b>);
      }
      cuoi = mau.lastIndex;
    }
    if (cuoi < raw.length) ra.push(raw.slice(cuoi));
    return ra;
  }

  function ONhap({ truong, daKhai, giaTri, onDoi }) {
    if (truong.type === 'note') return <div className="ci-ghichu">{chuCoLienKet(truong.label)}</div>;

    // Các bước lấy khoá — ngăn cách bằng |. Đặt TRƯỚC ô nhập trong danh sách trường thì nó hiện
    // trước, đúng thứ tự người ta làm: đọc cách lấy rồi mới có cái để dán.
    if (truong.type === 'steps') return (
      <ol className="ci-buoc">
        {truong.label.split('|').map((b, i) => (
          <li key={i}><span>{i + 1}</span><span>{chuCoLienKet(b.trim())}</span></li>
        ))}
      </ol>
    );
    const biMat = truong.type === 'secret';

    // Ô BÍ MẬT ở form XEM LẠI: hiện trạng thái, không hiện ô nhập.
    //
    // Ô che sao kèm chữ mờ "để trống = giữ nguyên" vừa mời người ta gõ vào, vừa bắt họ đoán
    // nghĩa của việc để trống — mà đoán sai ở đây là mất khoá đăng nhập của cả kênh. Người mở
    // form này gần như luôn chỉ định đổi tên gợi nhớ; đổi khoá là việc hiếm và phải cố ý.
    if (biMat && daKhai) return (
      <OBiMatDaLuu truong={truong} giaTri={giaTri} onDoi={onDoi} />
    );

    return (
      <label className="ci-o">
        {truong.label}
        <input type={biMat ? 'password' : 'text'}
               placeholder={truong.hint || ''}
               value={giaTri}
               onChange={e => onDoi(e.target.value)} />
      </label>
    );
  }

  /// Khoá đã lưu: một dòng trạng thái, và ô nhập chỉ bung ra khi người dùng CHỦ ĐỘNG bấm đổi.
  function OBiMatDaLuu({ truong, giaTri, onDoi }) {
    const [dangDoi, setDangDoi] = useState(false);

    // Bấm Thôi phải XOÁ luôn cái vừa gõ dở. Ẩn ô mà giữ giá trị thì lượt Lưu tới vẫn ghi đè
    // khoá cũ bằng một chuỗi người dùng tưởng mình đã bỏ.
    function thoi() { onDoi(''); setDangDoi(false); }

    if (!dangDoi) return (
      <div className="ci-bimat">
        <span className="ci-bimat-ten">{truong.label}</span>
        <span className="ci-bimat-tt">đã lưu</span>
        <button type="button" className="ci-lienket" onClick={() => setDangDoi(true)}>
          Đổi
        </button>
      </div>
    );

    return (
      <div className="ci-bimat-doi">
        <label className="ci-o">
          {truong.label} mới
          <input type="password" autoFocus placeholder={truong.hint || ''}
                 value={giaTri} onChange={e => onDoi(e.target.value)} />
        </label>
        <div className="ci-bimat-doi-duoi">
          <span>Bấm <b>Lưu</b> để thay khoá cũ.</span>
          <button type="button" className="ci-lienket" onClick={thoi}>Thôi</button>
        </div>
      </div>
    );
  }

  // ── Cài đặt trợ lý ────────────────────────────────────────────────────────
  //
  // Trước 28/08/2026 mọi công ty dùng chung MỘT lời dặn nằm trong file cấu hình máy chủ — không
  // công ty nào khai được "bên em chuyên tour Nhật, giọng trang trọng". Màn hình này mở chỗ đó ra.
  function CaiDatTroLy({ pushToast }) {
    const [v, setV] = React.useState(null);
    const [dangLuu, setDangLuu] = React.useState(false);

    React.useEffect(() => {
      let huy = false;
      (async () => {
        try {
          const r = await authedFetch('/api/v1/chat/bot-settings');
          if (huy) return;
          setV(r.ok ? await r.json() : { error: true });
        } catch { if (!huy) setV({ error: true }); }
      })();
      return () => { huy = true; };
    }, []);

    async function luu() {
      setDangLuu(true);
      try {
        const r = await authedFetch('/api/v1/chat/bot-settings', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            enabled: v.enabled, persona: v.persona, greeting: v.greeting,
            muteMinutes: v.muteMinutes, historyTurns: v.historyTurns,
          }),
        });
        let j = null; try { j = await r.json(); } catch {}
        if (!r.ok) { pushToast(j?.error || 'Lưu không được', 'error'); return; }
        pushToast('Đã lưu cài đặt trợ lý', 'success');
      } catch (e) { pushToast('Lưu không được: ' + e.message, 'error'); }
      finally { setDangLuu(false); }
    }

    if (!v) return <div className="ci-trong">Đang tải…</div>;
    if (v.error) return <div className="ci-trong">Không đọc được cài đặt trợ lý.</div>;

    const gioiHan = v.limits || {};
    return (
      <div className="ci-tk-form">
        {/* Công tắc đứng ĐẦU: tắt bot là mọi ô còn lại thành vô nghĩa, nên nó phải là thứ đọc
            được trước tiên. */}
        <label className="ci-bat">
          <input type="checkbox" checked={v.enabled}
                 onChange={e => setV(p => ({ ...p, enabled: e.target.checked }))} />
          <span>
            <b>Trợ lý tự trả lời khách</b>
            <em>Tắt thì tin vẫn vào hộp thư, chỉ là không ai trả lời hộ — nhân viên trực toàn bộ.</em>
          </span>
        </label>

        <label className="ci-o">
          Trợ lý cần biết gì về công ty bạn
          <textarea rows={7} maxLength={gioiHan.personaChars}
                    value={v.persona || ''}
                    placeholder={'vd: Bên em chuyên tour Nhật – Hàn – Đài, khởi hành từ Hà Nội và '
                      + 'TP.HCM.\nXưng "em", gọi khách là "anh/chị".\nKhông nhận đoàn dưới 10 khách.\n'
                      + 'Khách hỏi visa thì hướng dẫn nộp hồ sơ trước 20 ngày.'}
                    onChange={e => setV(p => ({ ...p, persona: e.target.value }))} />
        </label>
        {/* Nói rõ giới hạn của công cụ. Không nói thì công ty viết "báo giá tour Nhật 25 triệu"
            vào đây rồi tưởng bot sẽ báo giá — mà nó sẽ KHÔNG, vì luật chống bịa luôn thắng. */}
        <div className="ci-ghichu">
          Phần này <b>thêm vào</b> chứ không thay thế các luật an toàn có sẵn. Trợ lý vẫn
          <b> không bao giờ tự báo giá, lịch khởi hành hay số chỗ còn</b> — nó chưa đọc dữ liệu
          thật của công ty, nên gặp câu hỏi cần số liệu thì nó hẹn kiểm tra rồi báo lại.
        </div>

        <label className="ci-o">
          Câu chào khách nhắn lần đầu
          <input value={v.greeting || ''} placeholder="Bỏ trống = không chào, vào thẳng trả lời"
                 onChange={e => setV(p => ({ ...p, greeting: e.target.value }))} />
        </label>

        <div className="ci-doi-o">
          <label className="ci-o">
            Nhân viên trả lời xong thì trợ lý im (phút)
            <input type="number" min={0} max={1440} value={v.muteMinutes}
                   onChange={e => setV(p => ({ ...p, muteMinutes: +e.target.value }))} />
          </label>
          <label className="ci-o">
            Trợ lý nhớ lại bao nhiêu tin gần nhất
            <input type="number" min={gioiHan.minHistory} max={gioiHan.maxHistory}
                   value={v.historyTurns}
                   onChange={e => setV(p => ({ ...p, historyTurns: +e.target.value }))} />
          </label>
        </div>
        <div className="ci-ghichu">
          Nhớ ít thì trợ lý không hiểu câu hỏi nối tiếp ("thế còn tháng 10?"); nhớ nhiều thì tốn
          lượt AI hơn và dễ bám vào chuyện cũ. {gioiHan.minHistory}–{gioiHan.maxHistory} tin.
        </div>

        <div className="ci-tk-nut">
          <button className="ci-nut chinh" disabled={dangLuu} onClick={luu}>
            {dangLuu ? 'Đang lưu…' : 'Lưu'}
          </button>
        </div>
      </div>
    );
  }

  // ── Quản lý mẫu trả lời nhanh ─────────────────────────────────────────────
  //
  // Trước 28/08/2026 KHÔNG có màn hình nào — mẫu chỉ tạo được bằng gọi API tay, nên tính năng
  // này gần như không ai dùng dù đã chạy từ lâu.
  function QuanLyMau({ pushToast }) {
    const [ds, setDs] = React.useState(null);
    const [sua, setSua] = React.useState(null);   // {trigger, body, buttons[]} — null = đang không sửa
    const [dangLuu, setDangLuu] = React.useState(false);

    const tai = React.useCallback(async () => {
      try {
        const r = await authedFetch('/api/v1/chat/quick-replies');
        setDs(r.ok ? (await r.json()).items || [] : []);
      } catch { setDs([]); }
    }, []);
    React.useEffect(() => { tai(); }, [tai]);

    async function luu() {
      const tg = (sua.trigger || '').trim();
      const noi = (sua.body || '').trim();
      if (!tg || !noi) { pushToast('Cần cả lệnh gọi và nội dung', 'error'); return; }
      setDangLuu(true);
      try {
        const r = await authedFetch('/api/v1/chat/quick-replies', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            trigger: tg, body: noi,
            buttons: (sua.buttons || []).map(b => ({ label: b.chu, url: b.url })),
          }),
        });
        let j = null; try { j = await r.json(); } catch {}
        if (!r.ok) { pushToast(j?.error || 'Lưu không được', 'error'); return; }
        pushToast('Đã lưu mẫu', 'success');
        setSua(null);
        await tai();
      } finally { setDangLuu(false); }
    }

    async function xoa(m) {
      if (!window.confirm(`Xoá mẫu "/${m.trigger}"?`)) return;
      const r = await authedFetch('/api/v1/chat/quick-replies/' + m.id, { method: 'DELETE' });
      if (!r.ok) { pushToast('Xoá không được', 'error'); return; }
      await tai();
    }

    if (ds === null) return <div className="ci-trong">Đang tải…</div>;

    return (
      <div>
        <div className="ci-tk-dau">
          <span>{ds.length} mẫu</span>
          <button className="ci-nut nho chinh"
                  onClick={() => setSua(sua ? null : { trigger: '', body: '', buttons: [] })}>
            {sua ? 'Thôi' : '+ Thêm mẫu'}
          </button>
        </div>

        {sua && (
          <div className="ci-tk-form">
            <label className="ci-o">
              Gõ gì để gọi mẫu
              <input value={sua.trigger} placeholder="vd: gia — nhân viên gõ /gia trong ô soạn"
                     onChange={e => setSua(p => ({ ...p, trigger: e.target.value }))} />
            </label>
            <label className="ci-o">
              Nội dung
              <textarea rows={4} value={sua.body}
                        onChange={e => setSua(p => ({ ...p, body: e.target.value }))} />
            </label>

            <BoNut nut={sua.buttons || []} onDoi={n => setSua(p => ({ ...p, buttons: n }))} />

            <div className="ci-tk-nut">
              <button className="ci-nut chinh" disabled={dangLuu} onClick={luu}>
                {dangLuu ? 'Đang lưu…' : 'Lưu mẫu'}
              </button>
            </div>
          </div>
        )}

        {ds.length === 0 && !sua && (
          <div className="ci-trong">
            Chưa có mẫu nào. Mẫu giúp nhân viên gõ <b>/gia</b> là ra sẵn cả câu trả lời, kèm nút
            bấm nếu bạn muốn — đỡ phải gõ lại những câu nói suốt ngày.
          </div>
        )}

        {ds.map(m => (
          <div key={m.id} className="ci-tk">
            <div className="ci-mau-dong">
              <b>/{m.trigger}</b>
              <span>{m.body}</span>
              {m.buttons?.length > 0 && (
                <span className="ci-mau-so-nut">{m.buttons.length} nút</span>
              )}
              <button className="ci-lienket"
                      onClick={() => setSua({ trigger: m.trigger, body: m.body, buttons: m.buttons || [] })}>
                Sửa
              </button>
              <button className="ci-lienket nguyhiem" onClick={() => xoa(m)}>Xoá</button>
            </div>
          </div>
        ))}
      </div>
    );
  }

  /// Bộ đặt nút dùng chung cho màn hình mẫu. Cùng ý nghĩa hai kiểu nút như ở ô soạn.
  function BoNut({ nut, onDoi }) {
    const [them, setThem] = React.useState({ chu: '', url: '' });
    return (
      <div className="ci-bo-nut">
        <div className="ci-o">Nút gắn kèm (không bắt buộc)</div>
        {nut.length > 0 && (
          <div className="ci-nut-soan">
            {nut.map((b, i) => (
              <button key={i} type="button" title="Bỏ nút này"
                      onClick={() => onDoi(nut.filter((_, j) => j !== i))}>
                {b.chu}
                <window.Icon name="close" size={11} />
              </button>
            ))}
          </div>
        )}
        <div className="ci-nut-them">
          <input value={them.chu} placeholder="Chữ trên nút"
                 onChange={e => setThem(p => ({ ...p, chu: e.target.value }))} />
          <input value={them.url} placeholder="Đường dẫn (bỏ trống = trả lời nhanh)"
                 onChange={e => setThem(p => ({ ...p, url: e.target.value }))} />
          <button type="button" className="ci-lienket" onClick={() => {
            const chu = them.chu.trim();
            if (!chu) return;
            onDoi([...nut, { chu, url: them.url.trim() || undefined }]);
            setThem({ chu: '', url: '' });
          }}>Thêm nút</button>
        </div>
        {/* Mẫu dùng chung cho cả sáu kênh mà mỗi kênh một giới hạn, nên KHÔNG cắt ở đây — cắt
            lúc gửi, khi đã biết hội thoại thuộc kênh nào. Nói trước để khỏi bất ngờ. */}
        <div className="ci-ghichu">
          Mỗi kênh nhận số nút khác nhau (Facebook 13 · Zalo 5 · WhatsApp 3 · Telegram 8 ·
          TikTok không có). Soạn dư thì lúc gửi hệ thống bỏ bớt và báo lại.
        </div>
      </div>
    );
  }

  function KhaiKenh({ pushToast, onDong, mucDau, onLuuPhanCong }) {
    const [ds, setDs] = useState(null);
    const [dangLuu, setDangLuu] = useState(null);
    const [nhap, setNhap] = useState({});     // { "kenh:accountId" | "kenh:moi" -> {field: value} }
    const [tab, setTab] = useState(0);        // số của kênh đang xem
    // Đang mở cấu hình của tài khoản nào: "kênh:accountId" hoặc "kênh:moi". Một lúc MỘT —
    // mở hết cùng lúc thì khai ba OA là hộp thoại dài bằng ba màn hình.
    const [mo, setMo] = useState(null);
    // Mục cài đặt đang xem: kenh | troly | mau | phancong. Mục mở sẵn do NÚT bấm quyết định —
    // "Kết nối kênh" vào thẳng mục Kênh, "Phân công" vào thẳng mục Phân công — chứ không bắt
    // người dùng mở hộp rồi tự đi tìm tab.
    const [muc, setMuc] = useState(mucDau || "kenh");

    const taiLai = useCallback(async () => {
      try {
        const r = await authedFetch('/api/v1/chat/channels');
        if (!r.ok) { setDs(r.status === 403 ? 'cam' : []); return; }
        const j = await r.json();
        setDs(j.items || []);
      } catch { setDs([]); }
    }, []);

    useEffect(() => { taiLai(); }, [taiLai]);

    // Đóng bằng Esc — popup nào cũng nên đóng được mà không phải rê chuột lên nút X.
    useEffect(() => {
      const f = (e) => { if (e.key === 'Escape') onDong(); };
      window.addEventListener('keydown', f);
      return () => window.removeEventListener('keydown', f);
    }, [onDong]);

    function o(kenh, accId) { return nhap[kenh + ':' + (accId || 'moi')] || {}; }
    function dat(kenh, accId, key, val) {
      const k = kenh + ':' + (accId || 'moi');
      setNhap(p => ({ ...p, [k]: { ...(p[k] || {}), [key]: val } }));
    }

    // Trường thường thì ĐIỀN SẴN giá trị đang lưu — không thấy giá trị cũ thì không kiểm được
    // mình khai đúng Trang/OA nào. Bí mật thì máy chủ không trả về, để trống = giữ nguyên.
    function giaTriO(kenh, accId, truong, sanCo) {
      const dangGo = o(kenh, accId)[truong.key];
      if (dangGo !== undefined) return dangGo;
      return truong.type === 'secret' ? '' : (sanCo?.[truong.key] || '');
    }

    async function luu(kenh, accId) {
      const khoa = kenh + ':' + (accId || 'moi');
      const than = nhap[khoa] || {};
      if (!accId && Object.values(than).every(v => !String(v || '').trim())) {
        pushToast('Chưa nhập gì để thêm', 'error'); return;
      }
      setDangLuu(khoa);
      try {
        const duong = accId
          ? '/api/v1/chat/channels/' + kenh + '/accounts/' + accId
          : '/api/v1/chat/channels/' + kenh + '/accounts';
        const r = await authedFetch(duong, {
          method: accId ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(than),
        });
        if (!r.ok) {
          // Máy chủ trả câu lỗi CỤ THỂ (token sai, Telegram từ chối đăng ký địa chỉ nhận tin,
          // địa chỉ không phải https công khai…). Nuốt mất rồi hiện "Lưu không được" thì người
          // khai không có manh mối nào để sửa — đúng thứ vừa mất một buổi khi nối Facebook.
          let cau = 'Lưu không được';
          try { const j = await r.json(); if (j && j.error) cau = j.error; } catch (e) {}
          pushToast(cau, 'error'); return;
        }
        pushToast(accId ? 'Đã cập nhật tài khoản' : 'Đã thêm tài khoản', 'success');
        setNhap(p => ({ ...p, [khoa]: {} }));
        await taiLai();
      } finally { setDangLuu(null); }
    }

    // Zalo KHÔNG cho copy Refresh Token từ giao diện của họ — phải đi một vòng OAuth. Máy chủ
    // dựng đường cấp quyền (kèm một mã `state` dùng một lần), mình chỉ mở cửa sổ; Zalo đá về
    // đường callback của chính app và app tự lưu token.
    //
    // Mở cửa sổ PHỤ chứ không chuyển hướng cả trang: người dùng đang khai dở form, chuyển đi là
    // mất hết những gì vừa gõ mà chưa bấm Lưu.
    /// Mở cửa sổ cấp quyền cho ĐÚNG cách trình duyệt cho phép.
    //
    // ⚠️ Phải gọi window.open NGAY trong cử chỉ bấm, TRƯỚC mọi await. Trình duyệt chỉ cho mở cửa
    // sổ khi lệnh nằm trong ngăn xếp ĐỒNG BỘ của một cử chỉ người dùng; sau await thì cử chỉ đã
    // bị tiêu và lệnh bị chặn. Máy bàn dễ tính nên không lộ, còn Safari/Chrome trên điện thoại
    // chặn thẳng — đúng kiểu lỗi "máy tôi chạy được mà máy khách thì không".
    //
    // Nên: mở cửa sổ RỖNG trước, xin đường dẫn xong mới trỏ nó tới đó. Bị chặn (trả null) thì
    // lùi về điều hướng chính tab hiện tại — thà mất form đang gõ dở còn hơn bấm mà không có gì
    // xảy ra và người dùng không hiểu vì sao.
    function moCuaSoCapQuyen(ten) {
      try { return window.open('', ten, 'width=560,height=720'); } catch { return null; }
    }

    function diToiCapQuyen(cua, url) {
      if (cua && !cua.closed) { cua.location.href = url; return true; }
      window.location.href = url;   // cửa sổ bật lên bị chặn — đi thẳng ở tab này
      return false;
    }

    async function capQuyenZalo(kenh, accId) {
      const cua = moCuaSoCapQuyen('zalo-cap-quyen');
      const r = await authedFetch('/api/v1/chat/channels/' + kenh + '/accounts/' + accId + '/oauth-url',
        { method: 'POST' });
      let j = null; try { j = await r.json(); } catch {}
      if (!r.ok) {
        try { cua?.close(); } catch {}
        pushToast(j?.error || 'Không dựng được đường cấp quyền', 'error'); return;
      }
      if (diToiCapQuyen(cua, j.url))
        pushToast('Cấp quyền xong thì bấm Tải lại để thấy trạng thái mới', 'success');
    }

    // Kết nối mà KHÔNG khai gì trước: ứng dụng Zalo/Facebook là của TourKit, khách chỉ cần đồng ý.
    //
    // Facebook đi thêm một bước Zalo không có: sau khi đồng ý, máy chủ hiện danh sách Trang người
    // đó quản trị để họ chọn. Cả bước đó nằm trong cửa sổ phụ này, mình không phải làm gì thêm.
    // Kênh nào người dùng đã chủ động bung phần khai tay ra (chỉ dùng khi máy chủ chưa khai
    // khoá ứng dụng). Mặc định đóng: đường đúng là báo quản trị, không phải tự đi tìm mã.
    const [khaiTay, setKhaiTay] = React.useState({});

    // Tiến độ lấy hội thoại cũ, khoá theo 'kênh:tài khoản'.
    const [lichSu, setLichSu] = React.useState({});

    async function noiNhanhKenh(kenh) {
      const cua = moCuaSoCapQuyen('chat-cap-quyen');
      const r = await authedFetch('/api/v1/chat/channels/' + kenh + '/connect-url', { method: 'POST' });
      let j = null; try { j = await r.json(); } catch {}
      if (!r.ok) {
        try { cua?.close(); } catch {}
        pushToast(j?.error || 'Không dựng được đường kết nối', 'error'); return;
      }
      // Đi thẳng ở tab này thì trang sắp rời đi, hiện lời nhắc là vô nghĩa.
      if (diToiCapQuyen(cua, j.url))
        pushToast('Nối xong thì bấm Tải lại để thấy tài khoản mới', 'success');
    }

    // Lấy lại đoạn chat cũ. Chạy nền vài phút nên đây chỉ ra lệnh bắt đầu rồi hỏi tiến độ —
    // giữ yêu cầu HTTP mở suốt lượt lấy thì trình duyệt hoặc proxy sẽ cắt, và người dùng thấy
    // "lỗi" trong khi việc vẫn đang chạy tốt.
    async function layLichSu(kenh, accId) {
      const goc = '/api/v1/chat/channels/' + kenh + '/accounts/' + encodeURIComponent(accId) + '/import-history';
      const r = await authedFetch(goc, { method: 'POST' });
      let j = null; try { j = await r.json(); } catch {}
      if (!r.ok) { pushToast(j?.error || 'Không bắt đầu được', 'error'); return; }

      pushToast('Đang lấy hội thoại cũ về…', 'success');
      setLichSu(t => ({ ...t, [kenh + ':' + accId]: { running: true } }));

      // Hỏi lại mỗi 3 giây. Bỏ hẳn sau 10 phút: quá mức đó thì hoặc đã xong mà mình lỡ nhịp,
      // hoặc máy chủ đã khởi động lại — hỏi mãi cũng không ra thêm gì.
      const hetHan = Date.now() + 10 * 60 * 1000;
      const hoi = async () => {
        const rr = await authedFetch(goc);
        if (!rr.ok) return;
        const jj = await rr.json();
        setLichSu(t => ({ ...t, [kenh + ':' + accId]: jj }));
        if (jj.running && Date.now() < hetHan) { setTimeout(hoi, 3000); return; }
        if (jj.error) pushToast(jj.error, 'error');
        else if (!jj.running) pushToast(`Đã lấy ${jj.conversations} hội thoại về hộp thư`, 'success');
      };
      setTimeout(hoi, 3000);
    }

    async function xoa(kenh, accId, ten) {
      if (!window.confirm(`Gỡ kết nối "${ten || accId}"?\n\nLịch sử chat với khách vẫn giữ nguyên, chỉ ngừng nhận và gửi qua tài khoản này.`)) return;
      const r = await authedFetch('/api/v1/chat/channels/' + kenh + '/accounts/' + accId, { method: 'DELETE' });
      if (!r.ok) { pushToast('Gỡ không được', 'error'); return; }
      pushToast('Đã gỡ kết nối', 'success');
      await taiLai();
    }


    let than;
    if (ds === 'cam') than = (
      <div className="ci-trong">Chỉ tài khoản có quyền Cấu hình hệ thống mới khai được kết nối kênh.</div>
    );
    else if (!ds) than = <div className="ci-trong">Đang tải…</div>;
    else than = (
      <>
        {/* Tab thay vì đổ mọi kênh ra một màn hình. Mỗi lần người dùng chỉ khai MỘT kênh, mà
            bày hết thì vừa phải cuộn vừa thêm một lớp viền bao quanh từng kênh.

            Dùng tên NGẮN do máy chủ cấp: từ khi có sáu kênh, tên đầy đủ làm dải tab vỡ thành
            hai dòng cao thấp lệch nhau. Tên đầy đủ vẫn hiện ở tiêu đề mục bên dưới. */}
        <div className="ci-tab">
          {ds.map(k => (
            <button key={k.channel} className={'ci-tab-nut' + (tab === k.channel ? ' on' : '')}
                    onClick={() => setTab(k.channel)}>
              <HuyHieuKenh kenh={k.channel} />
              {k.shortName || k.name}
              {k.accounts.length > 0 && <b>{k.accounts.length}</b>}
            </button>
          ))}
        </div>
        {ds.filter(k => k.channel === tab).map(k => (
          <div key={k.channel} className="ci-tab-noi">
            {/* URL dùng CHUNG (Zalo/Messenger). Telegram để null vì mỗi bot một URL riêng. */}
            {k.webhookUrl && (
              <label className="ci-url">
                Địa chỉ nhận tin (dán vào trang quản trị của kênh)
                <input readOnly value={k.webhookUrl} onFocus={e => e.target.select()} />
              </label>
            )}

            {/* Hàng đầu: đếm tài khoản + nút thêm NHỎ, đặt TRÊN danh sách.

                Trước đây nút thêm là một khối <details> to nằm CUỐI danh sách — khai xong tài khoản
                thứ tư là phải cuộn qua hết bốn khối mới thấy nó. Thêm tài khoản là việc hiếm, nên nó
                phải nhỏ và ở chỗ cố định; danh sách mới là thứ người dùng nhìn. */}
            <div className="ci-tk-dau">
              <span>{k.accounts.length} tài khoản</span>
              {k.noiNhanh
                ? <button className="ci-nut nho chinh" onClick={() => noiNhanhKenh(k.channel)}>{k.nutNoi || 'Kết nối'}</button>
                : k.noiKemKenh != null
                ? (
                  /* Kênh nối KÈM kênh khác (Instagram theo Trang Facebook). Đưa thẳng người
                     dùng sang tab kia thay vì bày ô khai tay — việc cần làm nằm ở đó. */
                  <button className="ci-nut nho chinh" onClick={() => setTab(k.noiKemKenh)}>
                    {k.nutNoi || 'Kết nối'}
                  </button>
                )
                : k.hoTroNoiNhanh
                ? (
                  /* Kênh CÓ đường một nút nhưng máy chủ chưa đủ khoá. Không bày ô khai tay ra
                     ngay: việc cần làm là báo quản trị, không phải tự đi tìm mã. */
                  <button className="ci-nut nho"
                          onClick={() => setKhaiTay(p => ({ ...p, [k.channel]: !p[k.channel] }))}>
                    {khaiTay[k.channel] ? 'Thôi' : 'Khai tay'}
                  </button>
                )
                : (
                  <button className="ci-nut nho"
                          onClick={() => setMo(mo === k.channel + ':moi' ? null : k.channel + ':moi')}>
                    {mo === k.channel + ':moi' ? 'Thôi' : '+ Thêm'}
                  </button>
                )}
            </div>

            {/* Form thêm mở ra NGAY DƯỚI nút, không phải cuối trang — mắt không phải nhảy đi đâu. */}
            {/* Kênh chờ khoá máy chủ: form khai tay chỉ mở khi người dùng CHỦ ĐỘNG bấm "Khai
                tay" — đường lui cho công ty tự tạo ứng dụng riêng, không phải đường mặc định. */}
            {(k.hoTroNoiNhanh && !k.noiNhanh ? khaiTay[k.channel] : mo === k.channel + ':moi')
              && !k.noiNhanh && (
              <div className="ci-tk-form">
                {k.fields.map(f => (
                  <ONhap key={f.key} truong={f} daKhai={false}
                         giaTri={giaTriO(k.channel, null, f, null)}
                         onDoi={v => dat(k.channel, null, f.key, v)} />
                ))}
                <div className="ci-tk-nut">
                  <button className="ci-nut chinh" disabled={dangLuu === k.channel + ':moi'}
                          onClick={() => luu(k.channel, null)}>
                    {dangLuu === k.channel + ':moi' ? 'Đang thêm…' : 'Thêm tài khoản'}
                  </button>
                </div>
              </div>
            )}

            {k.accounts.length === 0 && mo !== k.channel + ':moi' && (
              <div className="ci-trong">
                {/* Chữ phải theo KÊNH đang mở. Trước đây câu này viết cứng cho Zalo nên tab
                    Facebook cũng bảo người dùng đi bấm "Kết nối Zalo OA" — chỉ sang một nút
                    không hề có trên màn hình họ đang nhìn. Dùng lại k.nutNoi do máy chủ trả về,
                    thêm kênh nối-một-chạm mới thì không phải sửa chỗ này nữa. */}
                {k.noiNhanh
                  ? `Chưa nối tài khoản nào. Bấm "${k.nutNoi || 'Kết nối'}" rồi làm theo hướng dẫn trong cửa sổ hiện ra.`
                  : k.noiKemKenh != null
                  ? 'Kênh này không nối riêng — nó đi theo Trang Facebook. Nối Trang xong là tài khoản ở đây hiện ra.'
                  : k.hoTroNoiNhanh
                  ? `${k.shortName || k.name} nối bằng một nút, nhưng máy chủ chưa được khai khoá ứng dụng nên nút chưa bật. Báo quản trị hệ thống giúp bạn.`
                  : 'Chưa nối tài khoản nào cho kênh này.'}
              </div>
            )}

            {/* Danh sách: MỘT dòng mỗi tài khoản, bấm để mở cấu hình. Mỗi lúc chỉ mở MỘT —
                <details> cũ cho mở hết cùng lúc, khai ba OA là hộp thoại dài bằng ba màn hình. */}
            {k.accounts.map(t => {
              const khoa = k.channel + ':' + t.accountId;
              const dangMo = mo === khoa;
              return (
                <div key={t.accountId} className={'ci-tk' + (dangMo ? ' mo' : '')}>
                  <button className="ci-tk-dong" onClick={() => setMo(dangMo ? null : khoa)}
                          aria-expanded={dangMo}>
                    <window.Icon name={dangMo ? 'chevronDown' : 'chevronRight'} size={14} />
                    <b>{t.label || t.oaName || 'Chưa đặt tên'}</b>
                    {/* Tên OA thật do Zalo trả về — khác "Tên gợi nhớ" người dùng tự đặt. Khai
                        nhiều OA mà không có nó thì không biết dòng nào là OA nào. */}
                    {t.oaName && t.label && <span className="ci-tk-oa">{t.oaName}</span>}
                    {t.configured
                      ? <span className="ci-xong">đã khai</span>
                      : <span className="ci-chua">thiếu khoá</span>}
                  </button>

                  {dangMo && (
                    <div className="ci-tk-form">

                      {/* Lọc trường theo VIỆC ĐANG LÀM, không phải theo kênh:
                          · steps — chỉ dùng lúc THÊM MỚI. Người đã nối bot ba tuần trước không
                            cần đọc lại cách vào BotFather; ba bước đó đẩy ô "Tên gợi nhớ" —
                            thứ họ thật sự mở form này để sửa — xuống tận dưới.
                          · note — lúc này là tham khảo, chỉ giữ khi tài khoản ĐANG HỎNG, vì đó
                            đúng là lúc nó thành hướng dẫn chữa.
                          · kênh nối nhanh / nối kèm — khoá nằm ở máy chủ, bày ô rỗng chỉ làm
                            người ta tưởng mình còn thiếu bước nào đó. */}
                      {k.fields.filter(f => {
                        if (f.type === 'steps') return false;
                        if (f.type === 'note') return !t.configured;
                        if (k.noiNhanh || k.noiKemKenh != null) return f.key === 'label';
                        return true;
                      }).map(f => (
                        <ONhap key={f.key} truong={f} daKhai
                               giaTri={giaTriO(k.channel, t.accountId, f, t.values)}
                               onDoi={v => dat(k.channel, t.accountId, f.key, v)} />
                      ))}
                      {/* Thông tin TRA CỨU — mã bot/Trang/OA và địa chỉ nhận tin. Cả hai đều
                          không sửa được và chỉ dùng khi cần đối chiếu.

                          Đặt SAU ô sửa được, và nén thành hai dòng nhỏ. Trước 28/08 địa chỉ nhận
                          tin là một ô nhập chiếm cả chiều ngang, có nhãn dài, nằm TRÊN CÙNG —
                          tức thứ không ai đụng tới chiếm chỗ đẹp nhất, còn ô "Tên gợi nhớ" (thứ
                          duy nhất người ta mở form này để sửa) bị đẩy xuống giữa. */}
                      {(t.oaId || !k.webhookUrl) && (
                        <dl className="ci-tracuu">
                          {t.oaId && (<>
                            <dt>Mã trên nền tảng</dt>
                            <dd><code>{t.oaId}</code></dd>
                          </>)}
                          {!k.webhookUrl && (<>
                            <dt>Địa chỉ nhận tin</dt>
                            {/* Hệ thống TỰ đăng ký địa chỉ này lúc lưu bot token — để đây chỉ để
                                đối chiếu khi nghi ngờ. Trước 27/08 đúng ô này bắt người dùng chép
                                rồi tự gõ lệnh setWebhook bên ngoài trình duyệt. */}
                            <dd><code title={t.webhookUrl}>{t.webhookUrl}</code></dd>
                          </>)}
                        </dl>
                      )}
                      {/* Nói TRƯỚC khi họ bấm. Đây là việc tốn hạn mức gọi API và có thể mất vài
                          phút — bấm rồi mới biết là quá muộn. */}
                      {k.layLichSuDuoc && (() => {
                        const ls = lichSu[k.channel + ':' + t.accountId];
                        if (ls && !ls.running && ls.ever) return (
                          <div className="ci-hs-goiy">
                            Lượt gần nhất: <b>{ls.conversations}</b> hội thoại.
                            {ls.more && ' Còn nữa — bấm lại để lấy tiếp.'}
                          </div>
                        );
                        return (
                          <div className="ci-hs-goiy">
                            Các đoạn chat có từ <b>trước khi nối</b> không tự về. Bấm <b>Lấy hội
                            thoại cũ</b> để kéo về — mất vài phút, và tin đã có thì bỏ qua.
                          </div>
                        );
                      })()}
                      {/* Bước cấp quyền chỉ Zalo mới có: Messenger/Telegram cấp token thẳng ở
                          giao diện của họ, không đi vòng OAuth. */}
                      {k.channel === 0 && !k.noiNhanh && (
                        <div className="ci-hs-goiy">
                          Chưa có Refresh Token? Khai App ID + App Secret Key, bấm <b>Lưu</b>, rồi
                          bấm <b>Cấp quyền OA</b> — hệ thống tự lấy và tự làm mới về sau.
                        </div>
                      )}
                      <div className="ci-tk-nut">
                        <button className="ci-nut chinh" disabled={dangLuu === khoa}
                                onClick={() => luu(k.channel, t.accountId)}>
                          {dangLuu === khoa ? 'Đang lưu…' : 'Lưu'}
                        </button>
                        {k.channel === 0 && !k.noiNhanh && (
                          <button className="ci-nut" onClick={() => capQuyenZalo(k.channel, t.accountId)}>
                            Cấp quyền OA
                          </button>
                        )}
                        {k.layLichSuDuoc && (() => {
                          const ls = lichSu[k.channel + ':' + t.accountId];
                          return (
                            <button className="ci-nut" disabled={ls?.running}
                                    onClick={() => layLichSu(k.channel, t.accountId)}>
                              {ls?.running
                                ? `Đang lấy… ${ls.conversations || 0} hội thoại`
                                : 'Lấy hội thoại cũ'}
                            </button>
                          );
                        })()}
                        <button className="ci-nut nguyhiem"
                                onClick={() => xoa(k.channel, t.accountId, t.label)}>
                          Gỡ kết nối
                        </button>
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        ))}

      </>
    );

    return (
      <div className="ci-modal-nen" onMouseDown={e => { if (e.target === e.currentTarget) onDong(); }}>
        <div className="ci-modal" role="dialog" aria-modal="true" aria-label="Cài đặt hộp thư">
          <div className="ci-modal-dau">
            <b>Cài đặt hộp thư</b>
            <button className="ci-nut-icon" onClick={onDong} aria-label="Đóng">
              <window.Icon name="close" size={16} />
            </button>
          </div>

          {/* Các mục cài đặt của hộp thư. Trước 28/08 hộp này chỉ có phần kênh, nên trợ lý
              không có chỗ nào chỉnh và mẫu trả lời nhanh chỉ sửa được bằng gọi API tay.

              Gom một cửa thay vì mấy nút rời trên thanh tiêu đề: tất cả đều là "chỉnh hộp thư",
              làm một lần lúc cài đặt rồi hiếm khi mở lại.

              "Phân công" vào đây ngày 08/09/2026, trước đó là một TRANG riêng kèm mục menu bên
              trái. Nó không phải nơi để đi tới mà là cài đặt của chính hộp thư này — xem chú
              thích đầu pages/chat-assign-settings.jsx. */}
          <div className="ci-muc">
            {[["kenh", "Kênh"], ["troly", "Trợ lý"], ["mau", "Mẫu trả lời"], ["nhan", "Nhãn"],
              ["phancong", "Phân công"]].map(([ma, ten]) => (
              <button key={ma} className={"ci-muc-nut" + (muc === ma ? " on" : "")}
                      onClick={() => setMuc(ma)}>{ten}</button>
            ))}
          </div>

          <div className="ci-modal-than">
            {muc === "kenh" && than}
            {muc === "troly" && <CaiDatTroLy pushToast={pushToast} />}
            {muc === "mau" && <QuanLyMau pushToast={pushToast} />}
            {muc === "nhan" && <QuanLyNhan pushToast={pushToast} />}
            {muc === "phancong" && (window.ChatAssignSettingsForm
              ? <window.ChatAssignSettingsForm pushToast={pushToast} onLuuXong={onLuuPhanCong} />
              : <div className="ci-pc-dangtai">Khối phân công chưa nạp được.</div>)}
          </div>
        </div>
      </div>
    );
  }

  // ── Trang ────────────────────────────────────────────────────────────────

  function ChatInboxPage({ pushToast }) {
    const [dsach, setDsach] = useState([]);
    const [dem, setDem] = useState({ moi: 0, dangXuLy: 0, daDong: 0, chuaDoc: 0, tong: 0 });
    const [demKenh, setDemKenh] = useState({});
    const [loc, setLoc] = useState(null);          // trạng thái xử lý
    const [kenhLoc, setKenhLoc] = useState(null);  // kênh
    const [nhom, setNhom] = useState('tat-ca');    // tat-ca | chua-doc | cua-toi | toi-theo-doi
    const [tim, setTim] = useState('');
    const [nhanLoc, setNhanLoc] = useState([]);      // slug[] đang lọc — rỗng = không lọc
    const [danhMucNhan, setDanhMucNhan] = useState([]); // {id, slug, name, usageCount}[]
    const [chon, setChon] = useState(null);        // id hội thoại đang mở
    const [chiTiet, setChiTiet] = useState(null);
    // Cấu hình phân công + đội trực — nạp MỘT lần lúc mở hộp thư (xem effect cạnh chỗ nạp
    // mauTraLoi bên dưới). Chưa cấu hình → mặc định "thủ công, không kẹp quyền" khớp hành vi hôm nay.
    // meId = mã của CHÍNH người đang đăng nhập. Cần để nói đúng "Bạn đang phụ trách" hay "Chị
    // Duyên đang phụ trách" — hai câu dẫn tới hai việc khác nhau. Có thể null (phiên cũ chưa lấp
    // mã, hoặc ERP không tra được); chỗ dùng phải chịu được null chứ đừng coi là lỗi.
    const [phanCong, setPhanCong] = useState({ mode: 1, scopeOwnOnly: false, isAdmin: false,
                                                memberIds: [], staffs: [], meId: null });
    // Cờ RIÊNG 'chatAssign' (không dùng chung 'chat'): hộp thư chat đã ra mắt từ trước, còn phân
    // công thì chưa — dùng chung một cờ là bật cái này thì tắt luôn cả cái kia.
    //
    // Đội trực = giao của memberIds với danh sách nhân viên ERP. Máy chủ trả MÃ, tên thì tra ở
    // đây — không lưu tên trong CSDL chat để khỏi phải đồng bộ khi ai đó đổi tên.
    const doiTruc = (phanCong.staffs || []).filter(nv => (phanCong.memberIds || []).includes(nv.id));

    // Ai được đổ vào ô chọn người phụ trách: QUẢN TRỊ thấy toàn bộ nhân viên, người khác chỉ thấy
    // đội trực — khớp ĐÚNG luật ở máy chủ (đặc tả mục 9: đội trực chỉ ràng buộc người KHÔNG phải
    // quản trị).
    //
    // ⚠️ Bản trước luôn lọc theo đội trực. Đội trực sinh ra cho chế độ xoay vòng, nên ở chế độ
    // THỦ CÔNG — chế độ mặc định — nó thường rỗng, và ô chọn KHÔNG hiện. Kết quả: máy chủ cho phép
    // quản trị giao việc mà giao diện không có chỗ nào để giao. Sửa một nửa ở máy chủ mà quên nửa
    // giao diện thì với người dùng là chưa sửa gì (08/09/2026).
    // Và đội trực CHỈ kẹp ở chế độ xoay vòng — khớp đúng luật máy chủ sau chốt 08/09/2026.
    // Ở chế độ thủ công không có "lượt" nào để giữ, nên ai có quyền thì giao cho người mình
    // muốn; đem vòng quay đi chặn việc giao tay là mượn luật của việc này áp cho việc khác.
    const chonDuoc = (phanCong.isAdmin || phanCong.mode !== 2)
      ? (phanCong.staffs || [])
      : doiTruc;
    const [soan, setSoan] = useState('');
    const [dangGui, setDangGui] = useState(false);
    const [dangTai, setDangTai] = useState(true);
    const [moKhai, setMoKhai] = useState(false);
    // Bảng chọn tin mẫu — chỉ mở từ ô soạn đang khoá, xem chỗ dùng.
    const [moMau, setMoMau] = useState(false);
    // Điện thoại (≤760px): một màn hình một việc — danh sách HOẶC khung chat, hồ sơ là tấm trượt
    // từ đáy. Ba cột co lại trên 390px thì mỗi cột còn 100px, không đọc được gì. Dùng MỘT nguồn
    // sự thật là JS (gắn lớp .di-dong) chứ không để CSS tự đo bằng @container: trang phải BIẾT
    // mình đang ở điện thoại để đổi cả cách điều hướng (nút quay lại, đóng hồ sơ), hai bên đo
    // lệch nhau vài chục px là nút quay lại hiện mà bố cục vẫn ba cột.
    const diDong = window.tourkitHooks.useIsMobile(760);
    // Hồ sơ khách mở sẵn ở máy tính (cột thứ tư); ở điện thoại nó che cả khung chat nên phải đóng.
    const [moHoSo, setMoHoSo] = useState(() => window.innerWidth > 760);
    // Menu "⋯" của hội thoại. Messenger để đúng HAI thứ ngoài thanh tiêu đề (một việc chính +
    // nút hồ sơ) và dồn phần còn lại vào một menu — bảy nút chữ xếp ngang như bản trước vừa tràn
    // dòng vừa bắt người ta đọc hết bảy nhãn mỗi lần chỉ để bấm một cái.
    const [moMenu, setMoMenu] = useState(false);
    // Id của dòng đang mở menu "⋯" trong DANH SÁCH. Một biến chứ không phải cờ mỗi dòng:
    // chỉ được mở đúng một menu tại một thời điểm, và mở cái mới là cái cũ tự đóng.
    const [menuDong, setMenuDong] = useState(null);
    const [dinhKem, setDinhKem] = useState(null);      // tệp đã tải lên, CHỜ bấm gửi
    const [dangTai2, setDangTai2] = useState(false);   // đang tải tệp lên kho
    // Tiến độ tải tệp: {ten, phanTram, dangLuu}. dangLuu = trình duyệt đã gửi xong 100% nhưng
    // MÁY CHỦ còn đang đẩy tiếp lên R2 — hai chặng khác nhau, và chặng sau mới là chặng lâu.
    // Để thanh đứng ở 100% suốt chặng đó là nói dối, nên chuyển hẳn sang trạng thái không xác định.
    const [tienDoTep, setTienDoTep] = useState(null);
    // Đang tải TIN của một hội thoại. Thiếu cờ này thì bấm sang hội thoại khác vẫn thấy tin
    // của hội thoại cũ đứng im vài trăm mili giây — người dùng tưởng bấm hụt và bấm lại.
    const [dangTaiTin, setDangTaiTin] = useState(false);
    const [mauTraLoi, setMauTraLoi] = useState([]);
    const [goiY, setGoiY] = useState(null);            // null = đang không gõ lệnh
    // ⚠️ KHÁC `goiY` ngay trên. Cái kia là ô chọn MẪU TRẢ LỜI khi gõ "/", có sẵn từ trước.
    // Ba cái dưới là nút nhờ TRỢ LÝ soạn nháp — trùng chữ "gợi ý" ngoài màn hình nhưng là hai
    // việc khác hẳn, nên tên biến phải tách bạch.
    const [aiDangSoan, setAiDangSoan] = useState(false);
    const [aiNhac, setAiNhac] = useState(null);        // câu máy chủ giải thích vì sao chưa có nháp
    // Nút đi kèm tin SẮP gửi, lấy từ mẫu trả lời nhanh vừa chọn. Không phải chữ nên không nằm
    // trong ô soạn được — giữ riêng ở đây và hiện thành dải chip ngay trên ô soạn.
    const [nutSoan, setNutSoan] = useState([]);
    // Ô thêm nút đang mở hay không. Đóng mặc định: phần lớn tin không có nút, bày sẵn hai ô
    // nhập là làm ô soạn chật thêm cho việc hiếm dùng.
    const [themNut, setThemNut] = useState(null);   // null = đóng; {chu, url} = đang nhập
    const [conTro, setConTro] = useState(null);      // vị trí đọc tiếp; null = hết hoặc chưa tải
    const [dangTaiThem, setDangTaiThem] = useState(false);
    const cuonRef = useRef(null);
    const tepRef = useRef(null);
    const gridRef = useRef(null);

    const taiDsach = useCallback(async (cursor) => {
      try {
        const q = new URLSearchParams();
        if (loc !== null) q.set('status', loc);
        if (kenhLoc !== null) q.set('channel', kenhLoc);
        if (nhom === 'chua-doc') q.set('unread', 'true');
        if (nhom === 'cua-toi') q.set('mine', 'true');
        if (nhom === 'toi-theo-doi') q.set('followed', 'true');
        if (tim.trim()) q.set('search', tim.trim());
        if (nhanLoc.length > 0) q.set('tag', nhanLoc.join(','));
        if (cursor) q.set('cursor', cursor);
        const r = await authedFetch('/api/v1/chat/conversations?' + q);
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const j = await r.json();
        const moi = j.items || [];
        // Trộn theo id chứ không thay thế: sự kiện đẩy tới giữa lúc đang cuộn là chuyện thường,
        // thay thẳng là cuốn người dùng về đầu danh sách giữa lúc họ đang đọc.
        if (cursor) {
          setConTro(j.nextCursor || null);
          setDsach(cu => { const co = new Set(cu.map(x => x.id));
                           return cu.concat(moi.filter(x => !co.has(x.id))); });
        } else {
          // Làm mới đầu danh sách, GIỮ các trang đã cuộn ở dưới.
          setConTro(c => c === null ? (j.nextCursor || null) : c);
          setDsach(cu => { const co = new Set(moi.map(x => x.id));
                           return moi.concat(cu.filter(x => !co.has(x.id))); });
        }
        setDem(j.counts || {});
        setDemKenh(j.channelCounts || {});
      } catch (e) {
        // Không toast mỗi lần hỏng: trang còn đường lùi tự hỏi lại, mạng chập chờn là spam ngay.
      } finally { setDangTai(false); }
    }, [loc, kenhLoc, nhom, tim, nhanLoc]);

    const taiChiTiet = useCallback(async (id) => {
      if (!id) return;
      setDangTaiTin(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + id);
        if (!r.ok) return;
        setChiTiet(await r.json());
        authedFetch('/api/v1/chat/conversations/' + id + '/read', { method: 'POST' }).catch(() => {});
      } catch {}
      finally { setDangTaiTin(false); }
    }, []);

    // Nghe sự kiện ĐẨY thay cho hỏi lại 4 giây một lần. Mười nhân viên mở hộp thư là 300 lượt
    // hỏi mỗi phút cho thứ hầu hết thời gian không đổi — mà tin mới vẫn trễ tới 4 giây.
    //
    // ⚠️ ĐÓNG luồng khi tab ẩn: HTTP/1.1 chỉ cho 6 kết nối mỗi origin, một luồng SSE giữ mất một
    // suất. Mở nhiều tab TRAV-AI mà không đóng là các request thường bị treo — lỗi rất khó lần.
    //
    // ⚠️ Cờ chatRealtime=false nghĩa là máy chủ CHƯA cắm Redis, nên bus chỉ thấy sự kiện của
    // chính instance mình — chạy nhiều bản sau load-balancer là tin tới bản khác không đẩy sang
    // được. Lúc đó giữ đường lùi hỏi lại CHẠY LIÊN TỤC, không chỉ khi luồng đứt.
    //
    // ⚠️ KHÔNG dùng authedFetch cho SSE: nó tự đăng xuất TOÀN CỤC khi gặp bất kỳ 401 nào, nên một
    // luồng đứt lúc phiên hết hạn sẽ đá nhân viên ra khỏi app giữa lúc đang gõ dở cho khách.
    // EventSource không gửi được header tuỳ ý → phiên đi qua ?sessionId=, backend đã đọc sẵn.
    const dayDuTin = window.tourkitFeatures.useFeature('chatRealtime');

    useEffect(() => {
      let huy = false, es = null, hen = null, luiVe = null;

      const lamMoi = async () => {
        if (huy || document.hidden) return;
        await taiDsach();
        if (chon) await taiChiTiet(chon);
      };
      // Gom sự kiện: khách gửi liền 5 tin là 5 sự kiện, tải lại 5 lần thì tệ hơn cả nhịp cũ.
      const gom = () => { clearTimeout(hen); hen = setTimeout(lamMoi, 300); };

      // Đường lùi: SSE hỏng (proxy chặn, hoặc tin tới instance khác khi chạy nhiều bản) thì hộp
      // thư câm hẳn — tệ hơn hiện trạng. Chỉ chạy KHI luồng chưa mở, nên lúc đẩy chạy tốt thì
      // tab Network sạch, không có request định kỳ nào.
      const batLui = () => { if (!luiVe && !huy) luiVe = setInterval(lamMoi, 20000); };
      // Chỉ tắt đường lùi khi máy chủ nói đẩy là ĐỦ. Chưa có Redis thì luồng vẫn mở bình thường
      // nhưng sự kiện của instance khác không tới — tắt đường lùi lúc đó là câm mà trông như chạy.
      const tatLui = () => { if (!dayDuTin) return; clearInterval(luiVe); luiVe = null; };

      const moKet = () => {
        if (huy || document.hidden || es) return;
        const sid = window.tourkitAuth.getSessionId();
        if (!sid) { batLui(); return; }
        es = new EventSource('/api/v1/chat/events?sessionId=' + encodeURIComponent(sid));
        es.onopen = () => { tatLui(); if (!dayDuTin) batLui(); };
        es.onmessage = (ev) => { try { JSON.parse(ev.data); } catch { return; } gom(); };
        // EventSource TỰ nối lại — không đóng tay ở đây, chỉ bật đường lùi cho tới lúc nối được.
        es.onerror = batLui;
      };
      const dong = () => { if (es) { es.close(); es = null; } clearInterval(luiVe); luiVe = null; };
      const doiTab = () => { if (document.hidden) dong(); else { moKet(); lamMoi(); } };

      lamMoi();
      moKet();
      document.addEventListener('visibilitychange', doiTab);
      return () => {
        huy = true;
        document.removeEventListener('visibilitychange', doiTab);
        clearTimeout(hen); dong();
      };
    }, [taiDsach, taiChiTiet, chon, dayDuTin]);

    // Đổi bộ lọc là reset con trỏ + danh sách — không thì trộn kết quả của hai bộ lọc khác nhau.
    useEffect(() => { setDsach([]); setConTro(null); }, [loc, kenhLoc, nhom, tim, nhanLoc]);

    // Danh mục nhãn cho chip lọc. Nạp LẠI khi đổi hội thoại: thanh nhãn trong khung chat tạo được
    // nhãn mới ngay lúc đang trực, mà chip lọc bên trái không có đường nào khác để biết điều đó.
    useEffect(() => {
      let song = true;
      authedFetch('/api/v1/chat/tags')
        .then(r => (r.ok ? r.json() : { items: [] }))
        .then(j => { if (song) setDanhMucNhan(j.items || []); })
        .catch(() => {});
      return () => { song = false; };
    }, [chon]);

    useEffect(() => { if (chon) taiChiTiet(chon); }, [chon, taiChiTiet]);
    useEffect(() => { setMoMau(false); }, [chon]);
    // Kéo cửa sổ qua ngưỡng điện thoại (hoặc xoay máy tính bảng) mà hồ sơ đang mở ở dạng cột
    // thì nó lập tức thành tấm trượt che kín khung chat — người dùng không hề bấm gì. Đóng lại.
    useEffect(() => { if (diDong) setMoHoSo(false); }, [diDong]);
    // Nút soạn dở thuộc về hội thoại CŨ. Giữ lại là gửi nhầm nút của khách này cho khách khác.
    // Câu nhắc của trợ lý cũng vậy: "trợ lý đang trả lời câu này" nói về hội thoại vừa rời khỏi.
    useEffect(() => { setNutSoan([]); setThemNut(null); setAiNhac(null); }, [chon]);

    // Tải một lần, KHÔNG bám theo sự kiện đẩy: bộ mẫu hiếm khi đổi, kéo lại liên tục là
    // tốn truy vấn cho thứ gần như đứng yên.
    useEffect(() => {
      authedFetch('/api/v1/chat/quick-replies')
        .then(r => r.ok ? r.json() : { items: [] })
        .then(j => setMauTraLoi(j.items || []))
        .catch(() => {});
    }, []);

    // Nạp MỘT lần lúc mở hộp thư — cấu hình đổi rất thưa (chỉ khi quản trị sửa), hỏi lại mỗi lần
    // chọn hội thoại là một lượt gọi thừa cho mỗi cú bấm.
    //
    // Tách thành hàm gọi lại được vì từ 08/09/2026 màn hình cấu hình nằm NGAY TRONG hộp thư: sửa
    // đội trực xong đóng hộp lại thì ô chọn người phải đổi theo ngay, không đợi tải lại trang.
    const taiPhanCong = useCallback(async () => {
      try {
        const r = await authedFetch('/api/v1/chat/assign-settings');
        if (r.ok) setPhanCong(await r.json());
      } catch { /* lỗi thì để nguyên mặc định — hộp thư vẫn chạy, chỉ mất ô chọn người */ }
    }, []);
    useEffect(() => { taiPhanCong(); }, [taiPhanCong]);

    // Người trực tự tắt/bật lượt nhận việc của CHÍNH MÌNH — đi họp, đi ăn, hết ca thì tắt.
    // Không đoán theo "có mở tab không": tab để qua đêm vẫn tính là đang trực, mà người thì đã
    // về từ lâu. Chỉ có chính họ mới biết mình có đang nhận việc được hay không.
    async function doiTamNghi() {
      const nghi = !phanCong.tamNghi;
      try {
        const r = await authedFetch('/api/v1/chat/tam-nghi?nghi=' + nghi, { method: 'POST' });
        const data = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(data.error || ('Không đổi được (HTTP ' + r.status + ')'));
        pushToast(nghi
          ? 'Đã tạm dừng — hội thoại mới sẽ không chia cho bạn nữa.'
          : 'Đã nhận việc trở lại.', 'success');
        taiPhanCong();
      } catch (e) {
        pushToast(e.message, 'error');
      }
    }

    useEffect(() => {
      const el = cuonRef.current;
      if (el) el.scrollTop = el.scrollHeight;
    }, [chiTiet?.messages?.length]);

    // Chiều cao khung ở điện thoại: ĐO bằng JS thay vì calc(100dvh - N).
    //
    // Hai lý do. Một: phía trên khung là thanh trên cùng + lề trang, phía dưới là dải nút cố
    // định của app — cộng tay ra một con số N là thứ lệch ngay khi ai đó đổi chiều cao thanh.
    // Hai — và là lý do chính: bàn phím ảo. Cả iOS lẫn Android (Chrome ≥108) KHÔNG co viewport
    // bố cục khi bàn phím bật lên, chỉ co viewport NHÌN THẤY, nên 100dvh giữ nguyên và ô soạn
    // nằm gọn sau bàn phím — người dùng gõ mà không thấy mình gõ gì. visualViewport là thứ
    // duy nhất nói đúng còn bao nhiêu chỗ nhìn thấy được.
    useEffect(() => {
      const el = gridRef.current;
      if (!diDong || !el) return;
      const vv = window.visualViewport;
      const do_ = () => {
        // Đỉnh khung tính theo TÀI LIỆU (cộng lại phần đã cuộn), không theo viewport: đo theo
        // viewport thì trang cứ cuộn xuống một chút là khung "cao thêm" từng ấy, rồi lần đo sau
        // lại cao thêm nữa — không bao giờ dừng.
        const dinh = el.getBoundingClientRect().top + window.scrollY;
        const cao = vv ? vv.height : window.innerHeight;
        const banPhim = !!vv && window.innerHeight - vv.height > 120;
        // Bàn phím đang bật thì dải nút dưới đáy đã bị nó che, đừng chừa chỗ cho thứ không thấy.
        //
        // SÀN 400px khi không có bàn phím: cửa sổ thấp (DevTools mở dọc, máy tính bảng xoay
        // ngang có thanh địa chỉ) thì thà cả trang cuộn còn hơn một cái khung 250px lòi ra đúng
        // một hội thoại rưỡi. Có bàn phím thì hạ sàn — lúc đó thứ phải thấy là ô soạn.
        el.style.height = banPhim
          ? Math.max(220, cao - dinh - 8) + 'px'
          : 'max(400px, calc(' + (cao - dinh) + 'px - 72px - env(safe-area-inset-bottom)))';
        // Bàn phím vừa bật là khung ngắn lại — kéo tin mới nhất về đúng chỗ, không thì tin cuối
        // trốn sau ô soạn.
        const c = cuonRef.current;
        if (c) c.scrollTop = c.scrollHeight;
      };
      do_();
      window.addEventListener('resize', do_);
      vv?.addEventListener('resize', do_);
      vv?.addEventListener('scroll', do_);
      return () => {
        window.removeEventListener('resize', do_);
        vv?.removeEventListener('resize', do_);
        vv?.removeEventListener('scroll', do_);
        el.style.height = '';
      };
    }, [diDong, chon]);

    const cuaSo = chiTiet?.sendWindow;
    const khoaSoan = !cuaSo?.open;

    // Kênh nào hiện trên dải: ba kênh đã nối, cộng kênh nào đang có dữ liệu thật.
    const kenhHien = useMemo(() => {
      const co = new Set(KENH_SONG);
      Object.keys(demKenh).forEach(k => { if (demKenh[k] > 0) co.add(Number(k)); });
      return [...co].sort((a, b) => a - b);
    }, [demKenh]);

    // Tải tệp lên kho TRƯỚC, gửi sau — hai bước tách nhau để nhân viên xem trước ảnh rồi mới bấm
    // gửi thật, giống mọi app chat khác. Gửi thẳng lúc chọn tệp thì lỡ tay là khách nhận ngay.
    async function chonTep(tep) {
      if (!tep || !chon) return;
      if (tep.size > 15 * 1024 * 1024) { pushToast('Tệp quá 15MB', 'error'); return; }
      setDangTai2(true);
      setTienDoTep({ ten: tep.name, phanTram: 0, dangLuu: false });
      try {
        // XHR chứ không fetch: fetch KHÔNG báo được tiến độ tải LÊN. Đây là điểm mấu chốt của cả
        // việc này — tệp đi lên R2 mất hàng chục giây, không có con số thì người dùng chỉ thấy
        // màn hình đứng im và không biết nên chờ hay bấm lại.
        const j = await new Promise((ok, loi) => {
          const xhr = new XMLHttpRequest();
          xhr.open('POST', '/api/v1/chat/conversations/' + chon + '/upload');
          const sid = window.tourkitAuth?.getSessionId?.();
          if (sid) xhr.setRequestHeader('X-Session-Id', sid);
          // KHÔNG tự đặt Content-Type: trình duyệt phải tự thêm boundary cho FormData.

          xhr.upload.onprogress = e => {
            if (!e.lengthComputable) return;
            const pt = Math.round(e.loaded / e.total * 100);
            // Chạm 100% nghĩa là trình duyệt gửi xong, CHƯA phải lưu xong: máy chủ còn đẩy tiếp
            // lên R2. Đổi sang trạng thái không xác định thay vì để thanh đứng im ở 100%.
            setTienDoTep({ ten: tep.name, phanTram: pt, dangLuu: pt >= 100 });
          };
          xhr.onload = () => {
            let than = {};
            try { than = JSON.parse(xhr.responseText); } catch {}
            if (xhr.status >= 200 && xhr.status < 300) ok(than);
            else loi(new Error(than.error || 'HTTP ' + xhr.status));
          };
          xhr.onerror = () => loi(new Error('mất kết nối'));
          xhr.onabort = () => loi(new Error('đã huỷ'));

          const fd = new FormData();
          fd.append('file', tep);
          xhr.send(fd);
        });
        setDinhKem(j);
      } catch (e) { pushToast('Tải tệp lên không được: ' + e.message, 'error'); }
      finally { setDangTai2(false); setTienDoTep(null); }
    }

    async function gui() {
      const noi = soan.trim();
      if ((!noi && !dinhKem) || dangGui || !chon) return;
      setDangGui(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + chon + '/send', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            text: noi,
            attachmentUrl: dinhKem?.url, attachmentKind: dinhKem?.kind,
            attachmentName: dinhKem?.name, attachmentSize: dinhKem?.size,
            buttons: nutSoan.length > 0 ? nutSoan.map(b => ({ label: b.chu, url: b.url })) : null,
          }),
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không gửi được', 'error'); return; }
        // Máy chủ cắt nút cho vừa kênh và nói lại nếu có nút bị bỏ — phải hiện ngay, chứ đợi
        // tới lúc khách hỏi lại thì đã muộn.
        if (j.buttonWarning) pushToast(j.buttonWarning, 'error');
        setSoan('');
        setDinhKem(null);
        setNutSoan([]);
        await taiChiTiet(chon);
      } catch (e) { pushToast('Không gửi được: ' + e.message, 'error'); }
      finally { setDangGui(false); }
    }

    /**
     * Nhờ trợ lý soạn nháp trả lời.
     *
     * Chữ ĐỔ VÀO Ô SOẠN, không gửi — nhân viên đọc, sửa, rồi tự bấm Gửi. Đây là khác biệt duy
     * nhất so với việc trợ lý tự trả lời khách: cùng một bộ sinh, cùng khung cấm bịa giá, chỉ
     * khác chỗ câu chữ đi tới.
     *
     * Máy chủ trả 200 cho mọi ca kèm câu nhắc, kể cả khi không soạn được — không phải 4xx, vì
     * lớp authedFetch chung coi 4xx là hỏng và 401 ở đó còn kéo theo đăng xuất toàn cục.
     */
    async function xinNhapAi() {
      if (!chon || aiDangSoan) return;
      setAiDangSoan(true);
      setAiNhac(null);
      try {
        // KHÔNG kèm Content-Type và KHÔNG kèm thân: đường này không nhận thân, thêm vào là
        // request bị loại ở tầng định tuyến rồi rơi xuống trang SPA.
        const r = await authedFetch('/api/v1/chat/conversations/' + chon + '/goi-y', { method: 'POST' });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không soạn được', 'error'); return; }

        if (j.chu) {
          // Nối vào phần đang gõ dở chứ không đè lên: nhân viên có thể đã gõ nửa câu rồi mới
          // nghĩ ra là nhờ trợ lý, và xoá mất chữ họ vừa gõ là kiểu mất dữ liệu khó chịu nhất.
          setSoan(cu => (cu.trim() ? cu.replace(/\s*$/, '\n\n') : '') + j.chu);
        } else {
          // Câu nhắc hiện ngay dòng dưới ô soạn, KHÔNG dùng toast: toast biến mất sau vài giây,
          // mà câu "trợ lý đang trả lời, muốn tự trả lời thì tạm dừng trợ lý" là một chỉ dẫn
          // người ta cần đọc rồi làm theo.
          setAiNhac(j.loiNhan || 'Chưa soạn được lúc này.');
        }
      } catch (e) { pushToast('Không soạn được: ' + e.message, 'error'); }
      finally { setAiDangSoan(false); }
    }

    async function doiTrangThai(tt, id = chon) {
      if (!id) return;
      await authedFetch('/api/v1/chat/conversations/' + id + '/status', {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status: tt }),
      });
      await taiDsach(); if (chon) await taiChiTiet(chon);
    }

    // Nhận việc cho mình — ĐƯỜNG RIÊNG /assign/me, không thân, không header.
    //
    // Bản trước POST thẳng /assign không thân, dựa vào việc máy chủ hiểu "thân rỗng = nhận việc".
    // Nó KHÔNG chạy: thiếu header Content-Type thì minimal API loại luôn route khỏi danh sách ứng
    // viên, request rơi xuống trang SPA và trả về 404 kèm HTML — bấm nút không có gì xảy ra.
    // Route riêng không có tham số thân nên không còn phụ thuộc header nào cả.
    async function nhanViec() {
      if (!chon) return;
      const r = await authedFetch('/api/v1/chat/conversations/' + chon + '/assign/me', { method: 'POST' });
      // 400 = không xác định được mã nhân viên. 409 = người khác nhận trước. Cả hai đều phải
      // BÁO — im lặng là bấm hoài tưởng nút hỏng, hoặc hai người cùng tưởng việc của mình rồi
      // cùng trả lời một khách.
      if (!r.ok) {
        let j = null; try { j = await r.json(); } catch {}
        pushToast(j?.error || 'Không nhận được việc', 'error');
      }
      await taiDsach(); if (chon) await taiChiTiet(chon);
    }

    // Giao cho người khác qua ô chọn ở thanh tiêu đề. Gửi MÃ người, không gửi tên: tên là thứ
    // đổi được và gõ được sai, mã thì không. Chọn mục trống = nhả việc — đi ĐƯỜNG RIÊNG (DELETE),
    // không gửi POST {userId: null}: máy chủ đòi AssignReq.UserId không rỗng ở nhánh chuyển việc,
    // gửi null vào đó là 400 chứ không nhả được việc.
    async function giaoCho(maNguoi) {
      if (!chon) return;
      const url = '/api/v1/chat/conversations/' + chon + '/assign';
      const r = maNguoi
        ? await authedFetch(url, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ userId: Number(maNguoi) }),
          })
        : await authedFetch(url, { method: 'DELETE' });
      // 400 = người chọn không có trong đội trực (hoặc đội trực chưa cấu hình). 409 = người khác
      // đang giữ, tranh chấp ngay lúc đang chọn. Bản trước chỉ bắt 409 nên bấm 400 không thấy
      // gì xảy ra.
      if (!r.ok) {
        let j = null; try { j = await r.json(); } catch {}
        pushToast(j?.error || 'Giao việc không xong', 'error');
        return;
      }
      await taiDsach(); if (chon) await taiChiTiet(chon);
    }

    async function batTatBot(id = chon, dangCamHienTai = null) {
      if (!id) return;
      const dangCam = dangCamHienTai ?? chiTiet?.conversation?.botPaused;
      await authedFetch('/api/v1/chat/conversations/' + id + '/bot', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ paused: !dangCam, minutes: 30 }),
      });
      await taiChiTiet(chon);
    }

    // id mặc định là hội thoại ĐANG MỞ; menu trên từng dòng truyền id của chính dòng đó.
    async function danhDauChuaDoc(id = chon) {
      if (!id) return;
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + id + '/unread', { method: 'POST' });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không đánh dấu chưa đọc được', 'error'); return; }
        if (!j.ok) {
          pushToast('Hội thoại chưa có tin nào của khách nên không đánh dấu được', 'error');
          return;
        }
        await taiDsach();
      } catch (e) {
        pushToast('Không đánh dấu chưa đọc được: ' + e.message, 'error');
      }
    }

    async function thuHoiTin(tin) {
      try {
        const r = await authedFetch(
          '/api/v1/chat/conversations/' + chon + '/messages/' + tin.id + '/recall',
          { method: 'POST' });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) {
          // Nói THẬT khi muộn. Báo thành công ở đây là để nhân viên tưởng đã rút lại được câu lỡ
          // tay và không đi xin lỗi khách — hậu quả thật, không phải chuyện chữ nghĩa.
          pushToast(j.error || 'Tin đã gửi đi mất rồi — không thu hồi được nữa', 'error');
          await taiChiTiet(chon);
          return;
        }
        pushToast(j.recalledOnChannel
          ? 'Đã thu hồi — khách không còn thấy tin này'
          : 'Đã thu hồi trước khi gửi — tin chưa từng đến tay khách');
        await taiChiTiet(chon);
      } catch (e) {
        pushToast('Không thu hồi được: ' + e.message, 'error');
      }
    }

    async function xoaTin(tin) {
      // ⚠️ Câu hỏi PHẢI nói khách vẫn thấy. Không nói thì nhân viên tưởng đã thu hồi được câu lỡ
      // tay và không đi xin lỗi khách — hậu quả thật, không phải chuyện chữ nghĩa.
      if (!confirm('Gỡ tin này khỏi hộp thư?\n\n'
        + 'Chỉ xoá ở phía bạn — KHÁCH VẪN THẤY tin này. '
        + 'Các nền tảng không cho phép doanh nghiệp thu hồi tin đã gửi.')) return;
      try {
        const r = await authedFetch(
          '/api/v1/chat/conversations/' + chon + '/messages/' + tin.id, { method: 'DELETE' });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không xoá được tin', 'error'); return; }
        await taiChiTiet(chon);
      } catch (e) {
        pushToast('Không xoá được tin: ' + e.message, 'error');
      }
    }

    async function suaTin(tin) {
      const moi = prompt('Sửa nội dung tin (tin chưa gửi đi):', tin.body || '');
      if (moi === null || !moi.trim()) return;
      try {
        const r = await authedFetch(
          '/api/v1/chat/conversations/' + chon + '/messages/' + tin.id, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ body: moi.trim() }),
          });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không sửa được tin', 'error'); return; }
        await taiChiTiet(chon);
      } catch (e) {
        pushToast('Không sửa được tin: ' + e.message, 'error');
      }
    }

    async function doiChan(id = chon, dangChan = v?.blocked) {
      if (!id) return;
      // ⚠️ Câu hỏi PHẢI nói rõ phạm vi. Không nền tảng nào cho phía doanh nghiệp chặn một người
      // qua API, nên đây chỉ là chặn trong hộp thư của mình — khách vẫn nhắn tới được. Gọi tắt
      // thành "chặn" mà không giải thích là người dùng tưởng đã chặn ở Facebook.
      if (!dangChan && !confirm(
        'Chặn khách này trong hộp thư?\n\n'
        + 'Hộp thư sẽ ẩn họ và trợ lý ngừng trả lời. Việc này KHÔNG báo cho Facebook/Zalo, '
        + 'khách vẫn nhắn tới được và vẫn thấy các tin cũ.')) return;
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + id + '/block', {
          method: dangChan ? 'DELETE' : 'POST',
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không cập nhật chặn được', 'error'); return; }
        setDsach([]);
        setConTro(null);
        await taiDsach(); if (chon) await taiChiTiet(chon);
      } catch (e) {
        pushToast('Không cập nhật chặn được: ' + e.message, 'error');
      }
    }

    async function doiTheoDoi(id = chon, dangTheoDoi = v?.followed) {
      if (!id) return;
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + id + '/follow', {
          method: dangTheoDoi ? 'DELETE' : 'POST',
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không cập nhật theo dõi được', 'error'); return; }
        // Lượt tải đầu thường giữ các trang đã cuộn để SSE không làm mất vị trí. Vừa bỏ theo dõi
        // trong bộ lọc "Tôi theo dõi" thì chính dòng cũ có thể không còn thuộc kết quả nữa, nên
        // dùng đúng reset khi đổi bộ lọc trước khi lấy lại từ đầu.
        setDsach([]);
        setConTro(null);
        await taiDsach(); if (chon) await taiChiTiet(chon);
      } catch (e) {
        pushToast('Không cập nhật theo dõi được: ' + e.message, 'error');
      }
    }

    const v = chiTiet?.conversation;
    const lh = chiTiet?.contact;
    // Tên hiển thị của khách, dùng lại ở nhiều chỗ (đầu khung chat, ảnh trên từng tin, hồ sơ).
    // Chưa lấy được tên thật thì hiện mã người dùng — xấu nhưng không bịa ra một cái tên.
    const tenKhach = v ? (v.displayName || v.contactExternalId) : '';
    const tinNhan = chiTiet?.messages || [];
    const coLoc = kenhLoc !== null || nhom !== 'tat-ca' || loc !== null || !!tim.trim()
                || nhanLoc.length > 0;

    return (
      // .xem-chat bám theo `chon` (id vừa chạm) chứ không theo `v` (chi tiết đã tải xong): chạm
      // một dòng là khung chat phải hiện NGAY với khung xương, chờ tải xong mới lật màn thì có
      // vài trăm mili giây đứng im và người dùng chạm lại lần nữa.
      <main className={'page ci-wrap' + (diDong ? ' di-dong' : '') + (diDong && chon ? ' xem-chat' : '')}>
        {moKhai && <KhaiKenh pushToast={pushToast} onDong={() => setMoKhai(false)}
                             mucDau={moKhai} onLuuPhanCong={taiPhanCong} />}

        <div ref={gridRef} className={'ci-grid' + (v && moHoSo ? ' co-hoso' : '')}>
          {/* Hàng tiêu đề nằm TRONG thẻ, trải hết các cột.

              Trước đây dùng PageHero chung của app, đặt bên ngoài lưới. Đưa vào trong vì trang
              này là một CÔNG CỤ dùng liên tục chứ không phải trang đọc: gom tiêu đề, bộ đếm và
              nút kết nối vào cùng một khung có viền thì mắt biết ngay đâu là vùng làm việc, và
              tiết kiệm được một dải chiều cao cho phần đang thật sự cần — danh sách và tin. */}
          <div className="ci-dau">
            <span className="ci-dau-icon"><window.Icon name="send" size={16} /></span>
            <span className="ci-dau-ten">
              <h1>Hộp thư chat</h1>
              <span className="ci-dau-nhan">Đa kênh</span>
            </span>
            <p className="ci-dau-phu">
              Zalo · Facebook Messenger · Telegram — bot trả lời trước, bạn tiếp quản khi cần.
            </p>
            <span className="ci-dau-dem">
              <i className={'ci-cham-song' + (dem.chuaDoc > 0 ? '' : ' im')} aria-hidden="true" />
              <b>{dem.chuaDoc > 0 ? dem.chuaDoc + ' chưa đọc' : 'Đã đọc hết'}</b>
              {dem.tong > 0 && <><span className="tach">·</span>
                <span className="so">{dem.tong} hội thoại</span></>}
            </span>
            {/* "Phân công" đứng CẠNH "Kết nối kênh" — cả hai đều là cài đặt của hộp thư này, và
                cùng mở một hộp, chỉ khác mục vào thẳng. Trước đây phân công là một TRANG riêng
                với mục menu bên trái: người dùng phải rời hộp thư, mất chỗ đang đọc, rồi tự tìm
                đường quay lại. Ẩn khi cờ tắt để không bày một nút dẫn tới hộp trống. */}
            {/* Công tắc của CHÍNH NGƯỜI ĐANG TRỰC. Chỉ hiện khi họ nằm trong đội trực — ngoài
                đội thì vốn không có lượt nào, bày ra chỉ làm người ta tưởng đang có.

                Nhãn nói TRẠNG THÁI ĐANG LÀ, title nói VIỆC SẼ XẢY RA khi bấm. Nút bật/tắt mà
                nhãn nói hành động thì luôn có người đọc ngược — "Tạm dừng" là đang dừng, hay
                bấm vào thì dừng? */}
            {phanCong.trongDoiTruc && (
              <button className={'ci-dau-nut' + (phanCong.tamNghi ? ' dang-nghi' : '')}
                      onClick={doiTamNghi}
                      title={phanCong.tamNghi
                        ? 'Bấm để nhận việc trở lại'
                        : 'Bấm để tạm dừng nhận hội thoại mới'}
                      aria-pressed={!!phanCong.tamNghi}>
                <window.Icon name={phanCong.tamNghi ? 'stop' : 'check'} size={13} />
                <span>{phanCong.tamNghi ? 'Đã tạm dừng' : 'Đang nhận việc'}</span>
              </button>
            )}
            <button className="ci-dau-nut" onClick={() => setMoKhai(m => m === 'phancong' ? false : 'phancong')}
                    title="Phân công chat" aria-label="Phân công chat">
              <window.Icon name="users" size={13} /><span>Phân công</span>
            </button>
            {/* Chữ bọc trong <span> để điện thoại giấu đi, chỉ còn dấu cộng — hàng tiêu đề
                48px không đủ chỗ cho cả bộ đếm lẫn nhãn nút. title/aria-label giữ nghĩa. */}
            <button className="ci-dau-nut" onClick={() => setMoKhai(m => m === 'kenh' ? false : 'kenh')}
                    title="Kết nối kênh" aria-label="Kết nối kênh">
              <window.Icon name="plus" size={12} /><span>Kết nối kênh</span>
            </button>
          </div>
          {/* Vùng 1 — dải kênh */}
          <nav className="ci-dai" aria-label="Lọc theo kênh">
            <button className={'ci-dai-nut' + (kenhLoc === null ? ' on' : '')}
                    onClick={() => setKenhLoc(null)} title="Tất cả kênh">
              <span className="ci-hh">Tất</span>
            </button>
            {kenhHien.map(k => (
              <button key={k} className={'ci-dai-nut' + (kenhLoc === k ? ' on' : '')}
                      onClick={() => setKenhLoc(kenhLoc === k ? null : k)}
                      title={KENH[k].ten + (demKenh[k] ? ' · ' + demKenh[k] + ' hội thoại' : ' · chưa có hội thoại nào')}>
                <HuyHieuKenh kenh={k} />
                {demKenh[k] > 0 && <i className="ci-cham" aria-hidden="true" />}
              </button>
            ))}
          </nav>

          {/* Vùng 2 — danh sách hội thoại */}
          <section className="ci-cot">
            <div className="ci-cot-dau">
              <div className="ci-o-tim">
                <window.Icon name="search" size={14} />
                <input placeholder="Tìm tên, nội dung, mã khách…"
                       value={tim} onChange={e => setTim(e.target.value)} />
              </div>
              <div className="ci-nhom">
                {[['tat-ca', 'Tất cả'], ['chua-doc', 'Chưa đọc'], ['cua-toi', 'Của tôi'],
                  ['toi-theo-doi', 'Tôi theo dõi']].map(([id, nhan]) => (
                  <button key={id} className={nhom === id ? 'on' : ''} onClick={() => setNhom(id)}>
                    {nhan}
                    {id === 'chua-doc' && dem.chuaDoc > 0 && <b>{dem.chuaDoc}</b>}
                  </button>
                ))}
              </div>
              <div className="ci-chip">
                {TRANG_THAI.map(t => (
                  <button key={String(t.v)} className={loc === t.v ? 'on' : ''} onClick={() => setLoc(t.v)}>
                    {t.nhan}
                    {t.v === 0 && dem.moi > 0 && <b>{dem.moi}</b>}
                    {t.v === 1 && dem.dangXuLy > 0 && <b>{dem.dangXuLy}</b>}
                  </button>
                ))}
              </div>
              {/* Chip NHÃN — chỉ mọc khi công ty đã có nhãn; chưa có nhãn nào thì một hàng trống
                  chỉ tổ chiếm chỗ. Chọn nhiều nhãn là HOẶC (khách mang bất kỳ nhãn nào), bấm lại
                  để bỏ. Chip đếm ở hàng trên đi theo bộ lọc này — máy chủ lọc cả hai câu. */}
              {danhMucNhan.length > 0 && (
                <div className="ci-chip ci-chip-nhan" aria-label="Lọc theo nhãn">
                  {danhMucNhan.map(n => (
                    <button key={n.slug} className={nhanLoc.includes(n.slug) ? 'on' : ''}
                            title={n.usageCount > 0 ? n.usageCount + ' khách đang mang nhãn này' : 'chưa khách nào mang'}
                            onClick={() => setNhanLoc(ds => ds.includes(n.slug)
                              ? ds.filter(s => s !== n.slug) : [...ds, n.slug])}>
                      {n.name}
                    </button>
                  ))}
                  {nhanLoc.length > 0 && (
                    <button className="ci-chip-xoa" onClick={() => setNhanLoc([])}
                            title="Bỏ lọc nhãn" aria-label="Bỏ lọc nhãn">×</button>
                  )}
                </div>
              )}
              <div className="ci-tomtat">
                {dangTai ? 'Đang tải…' : dsach.length + ' hội thoại đang hiện'}
                {!dangTai && dem.tong > dsach.length && <span> trên tổng {dem.tong}</span>}
              </div>
            </div>

            <div className="ci-ds">
              {/* KHUNG XƯƠNG đúng hình dạng mục thật (ảnh tròn + ba dòng), không phải vòng
                  xoay chung chung: mắt đã biết trước bố cục sắp hiện nên lúc dữ liệu về
                  không bị giật, và người dùng thấy ngay đây là danh sách chứ không phải lỗi. */}
              {dangTai && dsach.length === 0 && (
                <div aria-hidden="true">
                  {[0, 1, 2, 3, 4, 5].map(i => (
                    <div key={i} className="ci-muc ci-xuong">
                      <span className="ci-muc-avt"><span className="xg tron" /></span>
                      <span className="ci-muc-than">
                        <span className="xg d1" />
                        <span className="xg d2" />
                        <span className="xg d3" />
                      </span>
                    </div>
                  ))}
                </div>
              )}
              {!dangTai && dsach.length === 0 && (
                <div className="ci-trong">
                  {coLoc
                    ? <>Không có hội thoại nào khớp bộ lọc.<br />
                        <span className="muted">Bỏ bớt bộ lọc để xem lại toàn bộ.</span></>
                    : <>Chưa có hội thoại nào.<br />
                        <span className="muted">Bấm “Kết nối kênh” ở trên để lấy địa chỉ nhận tin, rồi dán vào trang quản trị của Zalo OA, Facebook hoặc Telegram.</span></>}
                </div>
              )}
              {dsach.map(c => (
                // Thẻ div chứ không phải button: bên trong có nút "⋯" riêng, mà nút lồng trong
                // nút là HTML sai và React sẽ cảnh báo. Vẫn giữ nguyên hành vi bàn phím bằng
                // role + tabIndex + Enter/Space.
                <div key={c.id} role="button" tabIndex={0}
                     className={'ci-muc' + (chon === c.id ? ' on' : '') + (c.unread ? ' chuadoc' : '')}
                     onClick={() => setChon(c.id)}
                     onKeyDown={e => {
                       if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setChon(c.id); }
                     }}>
                  <span className="ci-muc-avt">
                    {/* url= là BẮT BUỘC, không thì AnhDaiDien lặng lẽ vẽ chữ cái đầu — danh sách
                        hiện "TT" trong khi đầu khung chat ngay cạnh hiện ảnh thật của cùng khách. */}
                    <AnhDaiDien ten={c.displayName || c.contactExternalId}
                                url={c.avatarUrl} co={36} />
                    <HuyHieuKenh kenh={c.channel} />
                  </span>
                  {/* HAI hàng, không phải ba — học cách Messenger xếp danh sách: tên + giờ, rồi
                      dòng xem trước, hết. Bản trước có thêm một hàng huy hiệu CHỮ ("Đang xử lý" ·
                      "admin" · "bot tạm dừng") làm mỗi mục cao gấp rưỡi, tức mỗi màn hình thấy
                      được ít hội thoại hơn hẳn — mà ba chữ đó thì lần nào cũng đọc lại y nguyên.
                      Nay chúng thành mấy dấu hiệu nhỏ nằm ngay cạnh dòng xem trước, có tooltip.

                      Trạng thái bỏ hẳn khỏi dòng: đã có dải chip lọc ngay trên đầu danh sách, và
                      đầu khung chat cũng nói rõ — nhắc lần thứ ba ở đây chỉ tốn chỗ. */}
                  <span className="ci-muc-than">
                    <span className="ci-muc-dau">
                      {/* Chưa đọc đã có sẵn cơ chế riêng: .ci-muc.chuadoc làm đậm tên và
                          chấm một dấu cạnh giờ. Đừng thêm dấu thứ hai cho cùng một chuyện. */}
                      <span className="ci-ten">{c.displayName || c.contactExternalId}</span>
                      <span className="ci-luc">{gioNgan(c.lastActivityAt)}</span>
                    </span>
                    {/* Tên Trang/OA đi TRƯỚC dòng xem trước, cùng một hàng — giữ nguyên luật hai
                        hàng của mục. Máy chủ chỉ gửi khi công ty nối từ hai tài khoản cùng kênh
                        trở lên; một Trang thì đây là null và dòng không đổi gì. */}
                    <span className="ci-xemtruoc">
                      {c.accountLabel && <i className="ci-trang">{c.accountLabel}</i>}
                      {c.lastPreview || 'chưa có tin nào'}
                    </span>
                    {/* NHÃN CHỮ, không phải biểu tượng bé xíu. Bản trước rút xuống mấy ký hiệu
                        10px (★ ⏸ ⊘ và một chữ cái) cho gọn — nhưng gọn tới mức không ai đoán
                        được nghĩa, phải rê chuột từng cái mới biết. Chữ đọc được thắng chỗ trống
                        tiết kiệm: đây là danh sách người ta LIẾC chứ không phải đọc kỹ. */}
                    <span className="ci-muc-cuoi">
                      <span className={'ci-tt' + (c.status === 0 ? ' moi' : '')}>
                        <i />{TEN_TRANG_THAI[c.status]}
                      </span>
                      {/* Dùng biến c (dòng danh sách), không phải v (chi tiết đang mở) — hai biến
                          khác nhau, dễ chép nhầm. Kiểm cả assignedUserId lẫn assignedUsername vì
                          dữ liệu mới chỉ có mã, dòng gán từ trước 07/09/2026 chỉ có tên đăng nhập. */}
                      {(c.assignedUserId || c.assignedUsername) && (
                        <span className="ci-giao">
                          {(phanCong.staffs || []).find(nv => nv.id === c.assignedUserId)?.name
                            || c.assignedUsername || 'chưa ai nhận'}
                        </span>
                      )}
                      {c.botPaused && <span className="ci-botcam">trợ lý dừng</span>}
                      {c.followed && <span className="ci-theodoi">★ theo dõi</span>}
                      {c.blocked && <span className="ci-dachan">đã chặn</span>}
                    </span>
                  </span>

                  {/* "⋯" hiện khi rê chuột — đúng lối Messenger. Nút nằm TUYỆT ĐỐI nên không
                      chiếm chỗ lúc ẩn, và dòng không nhảy khi nó hiện ra. */}
                  <button className="ci-muc-cham" title="Thao tác" aria-label="Thao tác"
                          onClick={e => {
                            e.stopPropagation();
                            // ⚠️ Đo NGAY trong lượt xử lý sự kiện, đừng đo bên trong hàm cập nhật
                            // state. Hàm đó chạy SAU, lúc ấy React đã thu hồi e.currentTarget nên
                            // nó là null — và getBoundingClientRect trên null ném lỗi làm TRẮNG
                            // cả trang. Đã dính thật khi chạy thử bằng trình duyệt.
                            const vt = viTriMenu(e.currentTarget, 200);
                            setMenuDong(x => (x?.id === c.id ? null : { id: c.id, ...vt }));
                          }}>
                    <window.Icon name="more" size={15} />
                  </button>
                  {menuDong?.id === c.id && (
                    <>
                      <div className="ci-menu-nen"
                           onClick={e => { e.stopPropagation(); setMenuDong(null); }} />
                      <div className={'ci-menu ci-menu-dong '
                                      + (menuDong.lat ? 'nhon-duoi' : 'nhon-tren')}
                           role="menu"
                           style={{ left: menuDong.x, top: menuDong.y,
                                    '--nhon': menuDong.nhon + 'px',
                                    transform: menuDong.lat ? 'translate(-100%, -100%)'
                                                            : 'translateX(-100%)' }}
                           onClick={e => e.stopPropagation()}>
                        <button role="menuitem"
                                onClick={() => { setMenuDong(null); danhDauChuaDoc(c.id); }}>
                          Đánh dấu chưa đọc
                        </button>
                        <button role="menuitem"
                                onClick={() => { setMenuDong(null); doiTheoDoi(c.id, c.followed); }}>
                          {c.followed ? 'Bỏ theo dõi' : 'Theo dõi hội thoại'}
                        </button>
                        <button role="menuitem"
                                onClick={() => { setMenuDong(null); batTatBot(c.id, c.botPaused); }}>
                          {c.botPaused ? 'Cho trợ lý nói lại' : 'Tạm dừng trợ lý'}
                        </button>
                        <button role="menuitem"
                                onClick={() => { setMenuDong(null);
                                                 doiTrangThai(c.status !== 2 ? 2 : 1, c.id); }}>
                          {c.status !== 2 ? 'Đóng hội thoại' : 'Mở lại hội thoại'}
                        </button>
                        <span className="ci-menu-vach" aria-hidden="true" />
                        <button role="menuitem" className="nguy-hiem"
                                onClick={() => { setMenuDong(null); doiChan(c.id, c.blocked); }}>
                          {c.blocked ? 'Bỏ chặn khách' : 'Chặn trong hộp thư'}
                        </button>
                      </div>
                    </>
                  )}
                </div>
              ))}
              {conTro && (
                <button className="ci-taithem" disabled={dangTaiThem}
                        onClick={async () => { setDangTaiThem(true);
                                               try { await taiDsach(conTro); }
                                               finally { setDangTaiThem(false); } }}>
                  {dangTaiThem ? "Đang tải…" : "Tải thêm hội thoại"}
                </button>
              )}
            </div>
          </section>

          {/* Vùng 3 — khung chat */}
          <section className="ci-chat">
            {!v && (
              <div className="ci-trong">
                {dangTaiTin ? 'Đang mở hội thoại…'
                  : chon ? 'Không mở được hội thoại này. Thử chạm lại.'
                  : 'Chọn một hội thoại bên trái để xem nội dung.'}
              </div>
            )}
            {v && (
              <>
                <div className="ci-chat-dau">
                  {/* Quay lại danh sách — chỉ ở điện thoại, vì ở đó danh sách đã bị khung chat
                      che kín. Xoá luôn chi tiết đang giữ: không thì lần chạm sau, khung chat
                      hiện tên khách CŨ một nhịp rồi mới đổi. */}
                  {diDong && (
                    <button className="ci-nut-icon ci-lui" onClick={() => { setChon(null); setChiTiet(null); }}
                            title="Quay lại danh sách" aria-label="Quay lại danh sách">
                      <window.Icon name="arrowLeft" size={17} />
                    </button>
                  )}
                  <AnhDaiDien ten={v.displayName || v.contactExternalId} url={lh?.avatarUrl} co={36} />
                  <div className="ci-chat-ten">
                    <b>{tenKhach}</b>
                    {/* Gộp mọi thứ "hội thoại này đang ra sao" vào MỘT dòng, cắt bớt khi hẹp.
                        Tách thành nhiều thẻ thì hàng tiêu đề cao gấp đôi mà không thêm thông tin. */}
                    <span>
                      <i aria-hidden="true" />
                      {/* Tên Trang đứng ngay sau tên kênh: "Messenger · Trang Hà Nội · …".
                          filter(Boolean) vì accountLabel null khi công ty chỉ nối một tài khoản —
                          không lọc thì dòng thành "Messenger ·  · Mới". */}
                      <em>{[KENH[v.channel]?.ten, v.accountLabel, TEN_TRANG_THAI[v.status],
                           (phanCong.staffs || []).find(nv => nv.id === v.assignedUserId)?.name
                             || v.assignedUsername || 'chưa ai nhận',
                           v.botPaused ? 'bot tạm dừng' : 'bot đang trả lời'].filter(Boolean).join(' · ')}</em>
                    </span>
                  </div>
                  {/* Học cách Messenger xếp thanh tiêu đề: chỉ để lộ MỘT việc chính cộng nút hồ
                      sơ, còn lại dồn vào "⋯". Bản trước bày bảy nút chữ cạnh nhau — tràn dòng
                      trên màn hình hẹp, và bắt người trực đọc hết bảy nhãn mỗi lần chỉ để bấm một. */}
                  <div className="ci-nut-nhom">
                    {/* THANH TIÊU ĐỀ CHỈ CÒN MỘT VIỆC: nhận về mình khi chưa ai nhận.
                        Ô chọn người 108 dòng đã chuyển xuống khối "Phụ trách" trong hồ sơ khách —
                        ở đó có chiều dọc để bày ô tìm kiếm, còn ở đây nó bị ép còn 170px.
                        Nút cũng THÔI hiện khi đã có người: bản trước vẫn bày nút xanh ghi "Đã nhận
                        chăm sóc" kể cả lúc người giữ việc là đồng nghiệp — câu đó đọc thành "mình
                        đã nhận", và bấm vào chỉ nhận lỗi 409. Ai đang giữ thì đọc ở dòng ngay dưới
                        tên khách, đổi thì mở hồ sơ. */}
                    {(phanCong.isAdmin || !phanCong.scopeOwnOnly)
                      && !(v.assignedUserId || v.assignedUsername) && (
                      <button className="ci-nut nhan" onClick={nhanViec}>Nhận chăm sóc</button>
                    )}

                    <div className="ci-menu-boc">
                      <button className={'ci-nut-icon' + (moMenu ? ' on' : '')}
                              onClick={() => setMoMenu(x => !x)}
                              title="Thao tác khác" aria-label="Thao tác khác"
                              aria-expanded={moMenu}>
                        <window.Icon name="more" size={15} />
                      </button>
                      {moMenu && (
                        <>
                          {/* Lớp phủ trong suốt: bấm ra ngoài là đóng. Không có nó thì menu dính
                              lại cho tới khi bấm đúng nút — kiểu bực mình nhỏ mà gặp mỗi lần. */}
                          <div className="ci-menu-nen" onClick={() => setMoMenu(false)} />
                          <div className="ci-menu" role="menu">
                            {/* Đường tới chỗ giao việc khi hồ sơ đang đóng. Ô chọn người đã dời
                                xuống panel hồ sơ, mà panel đó tắt được (và luôn tắt ở điện thoại)
                                — thiếu mục này thì có lúc không còn lối nào để giao việc cả. */}
                            {(phanCong.isAdmin || !phanCong.scopeOwnOnly) && (
                              <button role="menuitem"
                                      onClick={() => { setMoMenu(false); setMoHoSo(true); }}>
                                {(v.assignedUserId || v.assignedUsername) ? 'Đổi người phụ trách…' : 'Gán người chăm sóc…'}
                              </button>
                            )}
                            <button role="menuitem"
                                    onClick={() => { setMoMenu(false); danhDauChuaDoc(); }}>
                              Đánh dấu chưa đọc
                            </button>
                            <button role="menuitem"
                                    onClick={() => { setMoMenu(false); doiTheoDoi(); }}>
                              {v.followed ? 'Bỏ theo dõi' : 'Theo dõi hội thoại'}
                            </button>
                            <button role="menuitem"
                                    onClick={() => { setMoMenu(false); batTatBot(); }}>
                              {v.botPaused ? 'Cho trợ lý nói lại' : 'Tạm dừng trợ lý'}
                            </button>
                            <button role="menuitem"
                                    onClick={() => { setMoMenu(false); doiTrangThai(v.status !== 2 ? 2 : 1); }}>
                              {v.status !== 2 ? 'Đóng hội thoại' : 'Mở lại hội thoại'}
                            </button>
                            {/* Việc gây hậu quả nặng nhất nằm CUỐI và tách hẳn ra — để không ai
                                bấm nhầm nó khi định bấm mục ngay trên. */}
                            <span className="ci-menu-vach" aria-hidden="true" />
                            <button role="menuitem" className="nguy-hiem"
                                    onClick={() => { setMoMenu(false); doiChan(); }}>
                              {v.blocked ? 'Bỏ chặn khách' : 'Chặn trong hộp thư'}
                            </button>
                          </div>
                        </>
                      )}
                    </div>

                    <button className={'ci-nut-icon' + (moHoSo ? ' on' : '')}
                            onClick={() => setMoHoSo(x => !x)}
                            title={moHoSo ? 'Ẩn hồ sơ khách' : 'Xem hồ sơ khách'}
                            aria-label={moHoSo ? 'Ẩn hồ sơ khách' : 'Xem hồ sơ khách'}>
                      <window.Icon name="info" size={15} />
                    </button>
                  </div>
                </div>

                <ThanhCuaSo cuaSo={cuaSo} kenh={v.channel} />

                <div className="ci-cuon" ref={cuonRef}>
                  {/* Khung xương bong bóng, so le hai bên đúng như hội thoại thật. Trước đây bấm
                      sang hội thoại khác thì tin CŨ đứng im vài trăm mili giây rồi mới đổi —
                      người dùng tưởng bấm hụt và bấm lại lần nữa. */}
                  {dangTaiTin && tinNhan.length === 0 && (
                    <div aria-hidden="true" className="ci-xuong-tin">
                      {[62, 40, 75, 34, 55].map((w, i) => (
                        <div key={i} className={'ci-dong ' + (i % 2 ? 'ci-phai' : 'ci-trai')}>
                          <span className="xg bong" style={{ width: w + '%' }} />
                        </div>
                      ))}
                    </div>
                  )}
                  {!dangTaiTin && tinNhan.length === 0 && (
                    <div className="ci-trong">Chưa có tin nhắn nào.</div>
                  )}
                  {tinNhan.map((m, i) => {
                    const truoc = i > 0 ? tinNhan[i - 1] : null;
                    const sau = i < tinNhan.length - 1 ? tinNhan[i + 1] : null;

                    const doiNgay = !truoc || ngayCua(m.createdUtc) !== ngayCua(truoc.createdUtc);
                    const mocGio = !doiNgay && canMocGio(truoc, m);

                    // Bất cứ thứ gì chen vào giữa (dải ngày, mốc giờ) đều CẮT cụm: cụm là một
                    // khối liền mạch, có vạch ngăn ở giữa mà vẫn coi là liền thì dấu giờ ở cuối
                    // nằm sau vạch và nói về đoạn nằm trước vạch.
                    const dauCum = doiNgay || mocGio || !cungCum(truoc, m);
                    const cuoiCum = !sau
                      || ngayCua(sau.createdUtc) !== ngayCua(m.createdUtc)
                      || canMocGio(m, sau)
                      || !cungCum(m, sau);

                    return (
                      <React.Fragment key={m.id}>
                        {/* Dải ngày mang LUÔN cả giờ. Bong bóng không in giờ nữa, nên nếu dải
                            này chỉ ghi "Hôm nay" thì cả đoạn đầu ngày không còn mốc thời gian
                            nào — đọc lại không biết khách nhắn lúc sáng hay lúc khuya. */}
                        {doiNgay && (
                          <div className="ci-ngay">
                            <span>{nhanNgay(m.createdUtc)} · {gioPhut(m.createdUtc)}</span>
                          </div>
                        )}
                        {mocGio && (
                          <div className="ci-mocgio"><span>{gioPhut(m.createdUtc)}</span></div>
                        )}
                        <BongBong tin={m} kenh={v.channel} ten0={tenKhach}
                                  dauCum={dauCum} cuoiCum={cuoiCum}
                                  onXoa={xoaTin} onSua={suaTin} onThuHoi={thuHoiTin} />
                      </React.Fragment>
                    );
                  })}
                </div>

                {moMau && (
                  <BangTinMau hoiThoai={v.id} onDong={() => setMoMau(false)}
                              pushToast={pushToast} onGuiXong={async () => { setMoMau(false); await taiChiTiet(chon); }} />
                )}

                <div className="ci-soan">
                  {/* Nhãn nằm ngay TRÊN ô soạn, không phải dưới thanh tiêu đề.
                      Gắn nhãn là việc làm SAU khi đọc xong đoạn hội thoại, cùng nhịp với lúc gõ
                      trả lời — nên nó thuộc về vùng thao tác ở đáy, không phải vùng tiêu đề mà
                      mắt chỉ lướt qua một lần lúc mở. Đặt trên đầu còn tốn một dải chiều cao
                      ngay chỗ khung tin cần nhất.

                      Đứng NGOÀI nhánh khoaSoan để hết cửa sổ trả lời vẫn gắn nhãn được: lúc đó
                      mới đúng là lúc cần đánh dấu "chờ gọi lại", "quá hạn trả lời". */}
                  <ThanhNhan chiTiet={chiTiet} pushToast={pushToast} />

                  {khoaSoan ? (
                    // Nói rõ VÌ SAO và chỉ đường đi tiếp, không chỉ chặn.
                    //
                    // Và đây CHÍNH LÀ chỗ tin mẫu có việc: hết cửa sổ tự do thì mẫu đã duyệt là
                    // đường duy nhất còn lại. Đặt nút ở đâu khác thì đúng lúc cần nhất người
                    // dùng lại không thấy nó.
                    <div className="ci-khoa">
                      <span>{cuaSo?.reason || 'Hiện chưa gửi được cho khách này.'}</span>
                      <button type="button" className="ci-lienket" onClick={() => setMoMau(true)}>
                        Gửi tin mẫu
                      </button>
                    </div>
                  ) : (
                    <>
                      {/* ĐANG tải tệp lên. Trước đây chặng này không có phản hồi nào ngoài việc
                          đổi biểu tượng cái ghim — mà tệp lên R2 mất hàng chục giây, nên người
                          dùng chỉ thấy màn hình đứng im và không biết nên chờ hay bấm lại.

                          Thanh tiến độ THẬT (đọc từ XHR), rồi khi trình duyệt gửi xong 100% thì
                          chuyển sang "đang lưu vào kho" — vì lúc đó máy chủ mới bắt đầu đẩy lên
                          R2, và để thanh đứng ở 100% suốt chặng đó là nói dối về tiến độ. */}
                      {tienDoTep && (
                        <div className="ci-tep-tai" role="status" aria-live="polite">
                          <div className="ci-tep-tai-dau">
                            <span className="ten">{tienDoTep.ten}</span>
                            <span className="pt">
                              {tienDoTep.dangLuu ? 'đang lưu vào kho…' : tienDoTep.phanTram + '%'}
                            </span>
                          </div>
                          <div className={'ci-tep-thanh' + (tienDoTep.dangLuu ? ' khongro' : '')}>
                            <i style={tienDoTep.dangLuu ? undefined
                                                       : { width: tienDoTep.phanTram + '%' }} />
                          </div>
                        </div>
                      )}

                      {/* Tệp đã tải lên, CHỜ bấm gửi. Xem trước rồi mới gửi — lỡ chọn nhầm còn gỡ kịp. */}
                      {dinhKem && (
                        <div className="ci-tep-cho">
                          {dinhKem.kind === 'anh'
                            ? <img src={dinhKem.url} alt="" />
                            : <span className="ci-tep-cho-icon">
                                <window.Icon name="paperclip" size={15} />
                              </span>}
                          <span className="ci-tep-cho-ten">{dinhKem.name}</span>
                          <em>{coCho(dinhKem.size)}</em>
                          <button type="button" onClick={() => setDinhKem(null)}
                                  title="Bỏ tệp này" aria-label="Bỏ tệp này">
                            <window.Icon name="close" size={13} />
                          </button>
                        </div>
                      )}
                      {/* Gõ "/" ra danh sách mẫu. Nổi TRÊN ô soạn, không đẩy ô soạn xuống. */}
                      {goiY !== null && mauTraLoi.filter(m => m.trigger.startsWith(goiY)).length > 0 && (
                        <div className="ci-mau">
                          <div className="ci-mau-dau">Mẫu trả lời</div>
                          {mauTraLoi.filter(m => m.trigger.startsWith(goiY)).slice(0, 6).map(m => (
                            <button key={m.id} className="ci-mau-muc"
                                    onClick={() => { setSoan(m.body); setNutSoan(m.buttons || []); setGoiY(null); }}>
                              <b>/{m.trigger}</b>
                              <span>{m.body}</span>
                            </button>
                          ))}
                        </div>
                      )}
                      {/* Nút CHỜ gửi. Hiện ra để nhân viên thấy tin sắp đi kèm gì — mẫu trả lời
                          nhanh chỉ chèn phần CHỮ vào ô soạn, nút thì không nhìn thấy ở đâu cả
                          nếu không có dải này. Bỏ được từng nút trước khi gửi. */}
                      {(nutSoan.length > 0 || themNut) && (
                        <div className="ci-nut-soan">
                          <span>Kèm nút:</span>
                          {nutSoan.map((b, i) => (
                            <button key={i} type="button" title="Bỏ nút này"
                                    onClick={() => setNutSoan(p => p.filter((_, j) => j !== i))}>
                              {b.chu}
                              <window.Icon name="close" size={11} />
                            </button>
                          ))}

                          {themNut && (
                            <form className="ci-nut-them" onSubmit={e => {
                              e.preventDefault();
                              const chu = (themNut.chu || '').trim();
                              if (!chu) return;
                              const url = (themNut.url || '').trim();
                              setNutSoan(p => [...p, { chu, url: url || undefined }]);
                              // Giữ ô mở để thêm nút tiếp — người ta hiếm khi chỉ thêm MỘT nút.
                              setThemNut({ chu: '', url: '' });
                            }}>
                              <input autoFocus value={themNut.chu || ''} placeholder="Chữ trên nút"
                                     onChange={e => setThemNut(p => ({ ...p, chu: e.target.value }))} />
                              {/* Để trống = nút TRẢ LỜI NHANH: khách bấm là coi như họ nói đúng câu
                                  trên nút, rồi trợ lý xử tiếp như mọi câu khác. */}
                              <input value={themNut.url || ''} placeholder="Đường dẫn (bỏ trống = trả lời nhanh)"
                                     onChange={e => setThemNut(p => ({ ...p, url: e.target.value }))} />
                              <button type="submit" className="ci-lienket">Thêm</button>
                              <button type="button" className="ci-lienket"
                                      onClick={() => setThemNut(null)}>Xong</button>
                            </form>
                          )}
                        </div>
                      )}
                      <div className="ci-soan-o">
                        <input type="file" ref={tepRef} hidden
                               onChange={e => { chonTep(e.target.files?.[0]); e.target.value = ''; }} />
                        <textarea value={soan}
                                  onChange={e => {
                                    const val = e.target.value;
                                    setSoan(val);
                                    // Chỉ gợi ý khi "/" đứng ĐẦU ô soạn — giữa câu thì "/" là dấu
                                    // gạch bình thường (vd "sáng/chiều"), bật popup là phiền.
                                    const m = /^\/([a-z0-9-]*)$/i.exec(val);
                                    setGoiY(m ? m[1].toLowerCase() : null);
                                  }}
                                  placeholder={dinhKem ? 'Thêm chú thích (không bắt buộc)…' : 'Nhập trả lời cho khách… (gõ / để chèn mẫu)'}
                                  onKeyDown={e => {
                                    if (e.key === 'Escape') { setGoiY(null); return; }
                                    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); gui(); }
                                  }} />
                        {/* Hàng công cụ nằm DƯỚI ô gõ, không kẹp hai bên: chữ được trọn chiều
                            ngang, và nút gửi luôn ở một chỗ cố định dù ô gõ cao lên bao nhiêu. */}
                        <div className="ci-soan-nut">
                          <button className={'icon' + (dangTai2 ? ' dang-lam' : '')}
                                  disabled={dangTai2}
                                  onClick={() => tepRef.current?.click()}
                                  title="Gửi ảnh hoặc tệp" aria-label="Gửi ảnh hoặc tệp">
                            <window.Icon name={dangTai2 ? 'refresh' : 'paperclip'} size={15} />
                          </button>
                          {/* Thêm nút cho tin sắp gửi. Đứng cạnh nút mẫu trả lời vì cùng loại
                              việc: chuẩn bị nội dung trước khi bấm gửi. */}
                          <button className="mau" title="Thêm nút bấm dưới tin"
                                  onClick={() => setThemNut(themNut ? null : { chu: '', url: '' })}>
                            + Nút
                          </button>
                          <button className="mau" onClick={() => setGoiY(goiY === null ? '' : null)}
                                  title="Chèn mẫu trả lời">
                            <b>/</b>Mẫu trả lời
                          </button>
                          {/* Nhờ AI soạn nháp. Chữ đổ vào ô soạn, KHÔNG gửi — nhân viên đọc, sửa,
                              rồi tự bấm Gửi. Nút luôn hiện: máy chủ mới là chỗ biết lúc nào trợ
                              lý đang lo câu này, và nó trả về câu nhắc để hiện thẳng ra đây. */}
                          <button className="mau" onClick={xinNhapAi} disabled={aiDangSoan}
                                  title="Nhờ trợ lý soạn nháp trả lời — chữ đổ vào ô soạn, chưa gửi">
                            <window.Icon name={aiDangSoan ? 'refresh' : 'sparkle'} size={13} />
                            {aiDangSoan ? ' Đang soạn…' : ' Gợi ý'}
                          </button>
                          <span className="ci-soan-nhac">
                            {aiNhac || 'Enter để gửi · Shift + Enter xuống dòng'}
                          </span>
                          <button className="ci-gui" onClick={gui}
                                  disabled={dangGui || (!soan.trim() && !dinhKem)}
                                  title="Gửi" aria-label="Gửi">
                            <window.Icon name="send" size={15} />
                          </button>
                        </div>
                      </div>
                      {v.botPaused && (
                        <div className="ci-cho-gui">Bot đang tạm dừng nên sẽ không trả lời chen vào.</div>
                      )}
                    </>
                  )}
                </div>
              </>
            )}
          </section>

          {/* Vùng 4 — hồ sơ khách. Ở điện thoại nó là tấm trượt từ đáy đè lên khung chat, nên
              cần thêm lớp phủ mờ phía sau để chạm ra ngoài là đóng. */}
          {v && moHoSo && diDong && (
            <div className="ci-menu-nen ci-hs-nen" onClick={() => setMoHoSo(false)} aria-hidden="true" />
          )}
          {v && moHoSo && <HoSo chiTiet={chiTiet} phanCong={phanCong} chonDuoc={chonDuoc}
                                pushToast={pushToast} onDong={() => setMoHoSo(false)}
                                onNhan={nhanViec} onGiao={giaoCho} onNha={() => giaoCho('')} />}
        </div>
      </main>
    );
  }

  window.ChatInboxPage = ChatInboxPage;
})();
