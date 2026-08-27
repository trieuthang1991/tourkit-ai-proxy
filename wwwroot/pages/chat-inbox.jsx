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
          if (tin.kind === 1 || tin.kind === 4) return (
            <a key={i} href={f.url} target="_blank" rel="noopener noreferrer" className="ci-anh">
              <img src={f.url} alt={f.ten || 'Ảnh khách gửi'} loading="lazy" />
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

  function BongBong({ tin, kenh, ten0 }) {
    // 0=khách 1=AI 2=nhân viên 3=hệ thống
    const ben = tin.senderKind;
    const cuaMinh = tin.direction === 1;
    const lop = ben === 0 ? 'ci-khach' : ben === 1 ? 'ci-ai' : ben === 3 ? 'ci-hethong' : 'ci-nv';
    const nhan = ben === 1 ? 'AI trả lời' : ben === 2 ? (tin.senderUsername || 'Nhân viên') : null;
    const coTep = (tin.files || []).length > 0;

    // Ba dạng trình bày KHÁC NHAU, không phải một bong bóng đổi màu:
    //   khách  — bong bóng trắng, có ảnh đại diện, giờ nằm DƯỚI bóng
    //   bot    — KHÔNG bong bóng, chỉ một dải mảnh bên trái. Bot nói nhiều; để nó cũng thành
    //            bong bóng thì khung chat đặc kín và mắt không phân biệt nổi đâu là người thật
    //   mình   — bong bóng đậm, nhãn người gửi nằm TRONG bóng
    const noiDung = (
      <>
        <DinhKem tin={tin} />
        {/* Có đính kèm thì chữ là CHÚ THÍCH, vắng chữ là bình thường — đừng in "(không có chữ)"
            dưới một tấm ảnh, vừa thừa vừa trông như lỗi. */}
        {(tin.body || !coTep) && (
          <div className="ci-noidung">{tin.body || <i>(không có chữ)</i>}</div>
        )}
      </>
    );
    const gio = (
      <div className="ci-gio">
        <span>{gioPhut(tin.createdUtc)}</span>
        {cuaMinh && <DauGui state={tin.state} kenh={kenh} />}
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
      <div className="ci-ai">
        <div className="ci-nhan"><window.Icon name="sparkle" size={11} />Bot đã trả lời</div>
        {noiDung}
        {gio}
        {camXuc}
      </div>
    );

    if (!cuaMinh) return (
      <div className="ci-dong ci-trai">
        <AnhDaiDien ten={tin.senderUsername || ten0} co={26} />
        <div style={{ minWidth: 0 }}>
          <div className="ci-bong ci-khach">{noiDung}</div>
          {gio}
          {camXuc}
        </div>
      </div>
    );

    return (
      <div className="ci-dong ci-phai">
        <div>
          <div className={'ci-bong ' + lop}>
            {nhan && <div className="ci-nhan">{nhan}</div>}
            {noiDung}
            {gio}
          </div>
          {camXuc}
        </div>
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
      <div className={'ci-cuaso ' + (sap ? 'sap' : 'mo')} style={{ '--ci-con': con + '%' }}>
        <window.Icon name="clock" size={12} />
        <span>Cửa sổ trả lời {ten} còn <b>{dienGio(cuaSo.hoursLeft)}</b></span>
        <span className="ci-cuaso-phu">hết hạn thì phải chờ khách nhắn lại mới gửi được</span>
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
    'go-ket-noi': 'gỡ kết nối kênh',
  };

  function MotDongNhatKy({ d }) {
    let ct = null;
    try { ct = d.chiTiet ? JSON.parse(d.chiTiet) : null; } catch {}
    const them =
      d.hanhDong === 'chuyen-viec' && ct?.cho ? ' cho ' + ct.cho
      : d.hanhDong === 'doi-trang-thai' && ct?.trangThai != null ? ' → ' + (TEN_TRANG_THAI[ct.trangThai] || ct.trangThai)
      : d.hanhDong === 'tam-dung-bot' ? (ct?.phut ? ' (tạm dừng ' + ct.phut + ' phút)' : ' (cho chạy lại)')
      : '';
    return (
      <div className="ci-hs-dong nk">
        <span>{fmtAgo(d.createdUtc)}</span>
        <b>{d.username}</b> {TEN_HANH_DONG[d.hanhDong] || d.hanhDong}{them}
      </div>
    );
  }

  // Nối khách chat với khách CRM — NỐI TAY, không đoán tự động. Ghép theo tên sai thường xuyên
  // (trùng tên là chuyện bình thường ở khách du lịch), còn Zalo/Messenger thì không cho biết số
  // điện thoại trừ khi khách tự nhắn. Nối tay đúng 100% và dùng được ngay.
  // Nhãn và ghi chú gắn theo KHÁCH, không theo hội thoại: khách nhắn lại sau ba tháng vẫn còn
  // nhãn cũ. Gắn theo hội thoại thì mỗi lần mở hội thoại mới là mất hết — đúng lúc cần nhất.
  function NhanVaGhiChu({ chiTiet, pushToast }) {
    const id = chiTiet?.conversation?.id;
    const [nhan, setNhan] = useState(null);
    const [ghiChu, setGhiChu] = useState(null);
    const [nhanMoi, setNhanMoi] = useState('');
    const [ghiChuMoi, setGhiChuMoi] = useState('');
    const [dangLam, setDangLam] = useState(false);

    const tai = useCallback(async () => {
      if (!id) return;
      const [a, b] = await Promise.all([
        authedFetch('/api/v1/chat/conversations/' + id + '/tags').then(r => r.ok ? r.json() : { items: [] }).catch(() => ({ items: [] })),
        authedFetch('/api/v1/chat/conversations/' + id + '/notes').then(r => r.ok ? r.json() : { items: [] }).catch(() => ({ items: [] })),
      ]);
      setNhan(a.items || []);
      setGhiChu(b.items || []);
    }, [id]);

    useEffect(() => { setNhan(null); setGhiChu(null); tai(); }, [tai]);

    async function themNhan(e) {
      e.preventDefault();
      const t = nhanMoi.trim();
      if (!t) return;
      setDangLam(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + id + '/tags', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ tag: t }),
        });
        if (!r.ok) { pushToast('Nhãn không hợp lệ', 'error'); return; }
        setNhanMoi(''); await tai();
      } finally { setDangLam(false); }
    }

    async function xoaNhan(t) {
      await authedFetch('/api/v1/chat/conversations/' + id + '/tags/' + encodeURIComponent(t),
        { method: 'DELETE' });
      await tai();
    }

    async function themGhiChu(e) {
      e.preventDefault();
      const t = ghiChuMoi.trim();
      if (!t) return;
      setDangLam(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + id + '/notes', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ noiDung: t }),
        });
        if (!r.ok) { pushToast('Không lưu được ghi chú', 'error'); return; }
        setGhiChuMoi(''); await tai();
      } finally { setDangLam(false); }
    }

    return (
      <>
        <div className="ci-hs-muc">
          <h4>Nhãn</h4>
          <div className="ci-hs-nhan">
            {(nhan || []).map(t => (
              <span key={t} className="ci-nhan">
                {t}
                <button onClick={() => xoaNhan(t)} title={'Bỏ nhãn ' + t} aria-label={'Bỏ nhãn ' + t}>
                  <window.Icon name="close" size={11} />
                </button>
              </span>
            ))}
            {nhan !== null && nhan.length === 0 && <span className="ci-hs-trong">Chưa có nhãn nào.</span>}
          </div>
          <form className="ci-hs-them" onSubmit={themNhan}>
            <input value={nhanMoi} onChange={e => setNhanMoi(e.target.value)}
                   placeholder="Thêm nhãn, vd: khách VIP" />
            <button className="ci-nut nho" type="submit" disabled={dangLam || !nhanMoi.trim()}>Thêm</button>
          </form>
          {/* Dấu sẽ bị bỏ khi lưu — nói trước để người dùng khỏi tưởng hệ thống gõ sai tiếng Việt. */}
          <div className="ci-hs-goiy">Nhãn được bỏ dấu và nối bằng gạch nối khi lưu.</div>
        </div>

        <div className="ci-hs-muc">
          <h4>Ghi chú nội bộ</h4>
          {/* Nói rõ khách không thấy: không có câu này thì không ai dám ghi thật. */}
          <div className="ci-hs-goiy">Chỉ nhân viên đọc được — khách không bao giờ thấy.</div>
          <form className="ci-hs-them doc" onSubmit={themGhiChu}>
            <textarea value={ghiChuMoi} onChange={e => setGhiChuMoi(e.target.value)} rows={2}
                      placeholder="vd: khách khó tính, đừng gọi trước 9h" />
            <button className="ci-nut nho" type="submit" disabled={dangLam || !ghiChuMoi.trim()}>Lưu ghi chú</button>
          </form>
          {ghiChu !== null && ghiChu.length === 0 && <div className="ci-hs-trong">Chưa có ghi chú nào.</div>}
          {(ghiChu || []).map(g => (
            <div key={g.id} className="ci-hs-dong nk">
              <span>{fmtAgo(g.createdUtc)} · {g.username}</span>
              {g.noiDung}
            </div>
          ))}
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

  function HoSo({ chiTiet, onDong, pushToast }) {
    const v = chiTiet?.conversation;
    const lh = chiTiet?.contact;
    const [nhatKy, setNhatKy] = useState(null);

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

        {/* "Hội thoại này đang ra sao" — ba dòng gom trong MỘT thẻ có viền vì chúng là một cụm.
            Để rời thì mắt phải tự gom lại, mà đây là thứ nhân viên liếc đầu tiên khi mở hồ sơ. */}
        <div className="ci-hs-muc">
          <h4>Xử lý</h4>
          <div className="ci-hs-the">
            <div className="ci-hs-dong">
              <span>Trạng thái</span>
              <span className="cham"><i />{TEN_TRANG_THAI[v.status]}</span>
            </div>
            <div className="ci-hs-dong">
              <span>Phụ trách</span>
              <span>{v.assignedUsername || 'chưa ai nhận'}</span>
            </div>
            <div className="ci-hs-dong">
              <span>Trợ lý bot</span>
              <span>{v.botPaused ? 'đang tạm dừng' : 'đang trả lời'}</span>
            </div>
          </div>
        </div>

        <div className="ci-hs-muc">
          <h4>Khách hàng CRM</h4>
          <NoiCrm chiTiet={chiTiet} pushToast={pushToast} />
        </div>

        <div className="ci-hs-muc">
          <h4>Thông tin</h4>
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
          <Dong nhan="Khách nhắn gần nhất">
            {v.contactRepliedAt ? fmtAgo(v.contactRepliedAt) : 'chưa nhắn lần nào'}
          </Dong>
          {/* Khách đến từ đâu. Kênh chỉ nói MỘT LẦN lúc khách mở cuộc trò chuyện nên đây là bản
              ghi duy nhất — không tra lại được ở đâu khác. Chỉ hiện khi có, đừng bày dòng trống. */}
          {v.referral && (
            <>
              <Dong nhan="Đến từ">{NGUON_KHACH[v.referral.source] || v.referral.source}</Dong>
              <Dong nhan="Mã liên kết">{v.referral.gtRef}</Dong>
              <Dong nhan="Mã quảng cáo">{v.referral.adId}</Dong>
            </>
          )}
        </div>


        <NhanVaGhiChu chiTiet={chiTiet} pushToast={pushToast} />

        <div className="ci-hs-muc">
          <h4>Nhật ký thao tác</h4>
          {nhatKy === null
            ? <div className="ci-hs-trong">Đang tải…</div>
            : nhatKy.length === 0
              ? <div className="ci-hs-trong">Chưa có thao tác nào được ghi lại.</div>
              : nhatKy.map(d => <MotDongNhatKy key={d.id} d={d} />)}
        </div>
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
    return (
      <label className="ci-o">
        {truong.label}
        <input type={biMat ? 'password' : 'text'}
               placeholder={biMat && daKhai ? 'để trống = giữ nguyên' : (truong.hint || '')}
               value={giaTri}
               onChange={e => onDoi(e.target.value)} />
      </label>
    );
  }

  function KhaiKenh({ pushToast, onDong }) {
    const [ds, setDs] = useState(null);
    const [dangLuu, setDangLuu] = useState(null);
    const [nhap, setNhap] = useState({});     // { "kenh:accountId" | "kenh:moi" -> {field: value} }
    const [tab, setTab] = useState(0);        // số của kênh đang xem
    // Đang mở cấu hình của tài khoản nào: "kênh:accountId" hoặc "kênh:moi". Một lúc MỘT —
    // mở hết cùng lúc thì khai ba OA là hộp thoại dài bằng ba màn hình.
    const [mo, setMo] = useState(null);

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
    async function capQuyenZalo(kenh, accId) {
      const r = await authedFetch('/api/v1/chat/channels/' + kenh + '/accounts/' + accId + '/oauth-url',
        { method: 'POST' });
      let j = null; try { j = await r.json(); } catch {}
      if (!r.ok) { pushToast(j?.error || 'Không dựng được đường cấp quyền', 'error'); return; }
      window.open(j.url, 'zalo-cap-quyen', 'width=560,height=720');
      pushToast('Cấp quyền xong thì bấm Tải lại để thấy trạng thái mới', 'success');
    }

    // Kết nối mà KHÔNG khai gì trước: ứng dụng Zalo/Facebook là của TourKit, khách chỉ cần đồng ý.
    //
    // Facebook đi thêm một bước Zalo không có: sau khi đồng ý, máy chủ hiện danh sách Trang người
    // đó quản trị để họ chọn. Cả bước đó nằm trong cửa sổ phụ này, mình không phải làm gì thêm.
    async function noiNhanhKenh(kenh) {
      const r = await authedFetch('/api/v1/chat/channels/' + kenh + '/connect-url', { method: 'POST' });
      let j = null; try { j = await r.json(); } catch {}
      if (!r.ok) { pushToast(j?.error || 'Không dựng được đường kết nối', 'error'); return; }
      window.open(j.url, 'chat-cap-quyen', 'width=560,height=720');
      pushToast('Nối xong thì bấm Tải lại để thấy tài khoản mới', 'success');
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
                : (
                  <button className="ci-nut nho"
                          onClick={() => setMo(mo === k.channel + ':moi' ? null : k.channel + ':moi')}>
                    {mo === k.channel + ':moi' ? 'Thôi' : '+ Thêm'}
                  </button>
                )}
            </div>

            {/* Form thêm mở ra NGAY DƯỚI nút, không phải cuối trang — mắt không phải nhảy đi đâu. */}
            {mo === k.channel + ':moi' && !k.noiNhanh && (
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
                      {/* URL RIÊNG từng bot Telegram. Máy chủ TỰ đăng ký địa chỉ này với
                          Telegram lúc lưu bot token — để đây chỉ để quản trị đối chiếu khi
                          nghi ngờ, không phải việc người dùng phải làm. Trước 27/08 đúng ô
                          này bắt họ copy rồi tự gõ lệnh setWebhook bên ngoài. */}
                      {!k.webhookUrl && (
                        <label className="ci-url">
                          Địa chỉ nhận tin (hệ thống đã tự đăng ký)
                          <input readOnly value={t.webhookUrl} onFocus={e => e.target.select()} />
                        </label>
                      )}
                      {/* Kênh nối nhanh: khoá ứng dụng nằm ở máy chủ, khách không có gì để khai
                          ngoài cái tên cho dễ nhớ. Bày ra mấy ô khoá rỗng chỉ làm người ta tưởng
                          mình còn thiếu bước nào đó. */}
                      {k.fields.filter(f => !k.noiNhanh || f.key === 'label').map(f => (
                        <ONhap key={f.key} truong={f} daKhai
                               giaTri={giaTriO(k.channel, t.accountId, f, t.values)}
                               onDoi={v => dat(k.channel, t.accountId, f.key, v)} />
                      ))}
                      {/* Mã thật do nền tảng cấp: OA của Zalo, Trang của Facebook, bot của
                          Telegram. Không gắn với việc kênh đó có nối-một-chạm hay không —
                          gắn nhầm vào noiNhanh thì bot Telegram vừa nối xong không hiện mã. */}
                      {t.oaId && (
                        <div className="ci-hs-goiy">Mã trên nền tảng: {t.oaId}</div>
                      )}
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
        <div className="ci-modal" role="dialog" aria-modal="true" aria-label="Kết nối kênh">
          <div className="ci-modal-dau">
            <b>Kết nối kênh</b>
            <button className="ci-nut-icon" onClick={onDong} aria-label="Đóng">
              <window.Icon name="close" size={16} />
            </button>
          </div>
          <div className="ci-modal-than">{than}</div>
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
    const [nhom, setNhom] = useState('tat-ca');    // tat-ca | chua-doc | cua-toi
    const [tim, setTim] = useState('');
    const [chon, setChon] = useState(null);        // id hội thoại đang mở
    const [chiTiet, setChiTiet] = useState(null);
    const [soan, setSoan] = useState('');
    const [dangGui, setDangGui] = useState(false);
    const [dangTai, setDangTai] = useState(true);
    const [moKhai, setMoKhai] = useState(false);
    const [moHoSo, setMoHoSo] = useState(true);
    const [dinhKem, setDinhKem] = useState(null);      // tệp đã tải lên, CHỜ bấm gửi
    const [dangTai2, setDangTai2] = useState(false);   // đang tải tệp lên kho
    const [mauTraLoi, setMauTraLoi] = useState([]);
    const [goiY, setGoiY] = useState(null);            // null = đang không gõ lệnh
    const [conTro, setConTro] = useState(null);      // vị trí đọc tiếp; null = hết hoặc chưa tải
    const [dangTaiThem, setDangTaiThem] = useState(false);
    const cuonRef = useRef(null);
    const tepRef = useRef(null);

    const taiDsach = useCallback(async (cursor) => {
      try {
        const q = new URLSearchParams();
        if (loc !== null) q.set('status', loc);
        if (kenhLoc !== null) q.set('channel', kenhLoc);
        if (nhom === 'chua-doc') q.set('unread', 'true');
        if (nhom === 'cua-toi') q.set('mine', 'true');
        if (tim.trim()) q.set('search', tim.trim());
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
    }, [loc, kenhLoc, nhom, tim]);

    const taiChiTiet = useCallback(async (id) => {
      if (!id) return;
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + id);
        if (!r.ok) return;
        setChiTiet(await r.json());
        authedFetch('/api/v1/chat/conversations/' + id + '/read', { method: 'POST' }).catch(() => {});
      } catch {}
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
    useEffect(() => { setDsach([]); setConTro(null); }, [loc, kenhLoc, nhom, tim]);

    useEffect(() => { if (chon) taiChiTiet(chon); }, [chon, taiChiTiet]);

    // Tải một lần, KHÔNG bám theo sự kiện đẩy: bộ mẫu hiếm khi đổi, kéo lại liên tục là
    // tốn truy vấn cho thứ gần như đứng yên.
    useEffect(() => {
      authedFetch('/api/v1/chat/quick-replies')
        .then(r => r.ok ? r.json() : { items: [] })
        .then(j => setMauTraLoi(j.items || []))
        .catch(() => {});
    }, []);

    useEffect(() => {
      const el = cuonRef.current;
      if (el) el.scrollTop = el.scrollHeight;
    }, [chiTiet?.messages?.length]);

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
      try {
        const fd = new FormData();
        fd.append('file', tep);
        const r = await authedFetch('/api/v1/chat/conversations/' + chon + '/upload', {
          method: 'POST', body: fd,   // KHÔNG tự đặt Content-Type: trình duyệt phải tự thêm boundary
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Tải tệp lên không được', 'error'); return; }
        setDinhKem(j);
      } catch (e) { pushToast('Tải tệp lên không được: ' + e.message, 'error'); }
      finally { setDangTai2(false); }
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
          }),
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không gửi được', 'error'); return; }
        setSoan('');
        setDinhKem(null);
        await taiChiTiet(chon);
      } catch (e) { pushToast('Không gửi được: ' + e.message, 'error'); }
      finally { setDangGui(false); }
    }

    async function doiTrangThai(tt) {
      if (!chon) return;
      await authedFetch('/api/v1/chat/conversations/' + chon + '/status', {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status: tt }),
      });
      await taiDsach(); await taiChiTiet(chon);
    }

    // Nhận việc: KHÔNG gửi tên, để máy chủ lấy từ phiên. Bản trước gửi
    // một thuộc tính KHÔNG tồn tại trên đối tượng phiên, nên thân yêu cầu luôn là
    // chuỗi rỗng và nút này thật ra đang GỠ giao việc. Nút trông như chạy suốt nhiều tháng.
    async function nhanViec() {
      if (!chon) return;
      const dangGiao = chiTiet?.conversation?.assignedUsername;
      const r = await authedFetch('/api/v1/chat/conversations/' + chon + '/assign', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(dangGiao ? { username: '' } : {}),
      });
      // 409 = người khác nhận trước. Nói TÊN người đang giữ chứ không im lặng đổi nút — im lặng
      // là hai người cùng tưởng việc của mình rồi cùng trả lời một khách.
      if (r.status === 409) {
        let j = null; try { j = await r.json(); } catch {}
        pushToast(j?.error || 'Người khác đã nhận hội thoại này', 'error');
      }
      await taiDsach(); await taiChiTiet(chon);
    }

    async function batTatBot() {
      if (!chon) return;
      const dangCam = chiTiet?.conversation?.botPaused;
      await authedFetch('/api/v1/chat/conversations/' + chon + '/bot', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ paused: !dangCam, minutes: 30 }),
      });
      await taiChiTiet(chon);
    }

    const v = chiTiet?.conversation;
    const lh = chiTiet?.contact;
    // Tên hiển thị của khách, dùng lại ở nhiều chỗ (đầu khung chat, ảnh trên từng tin, hồ sơ).
    // Chưa lấy được tên thật thì hiện mã người dùng — xấu nhưng không bịa ra một cái tên.
    const tenKhach = v ? (v.displayName || v.contactExternalId) : '';
    const tinNhan = chiTiet?.messages || [];
    const coLoc = kenhLoc !== null || nhom !== 'tat-ca' || loc !== null || !!tim.trim();

    return (
      <main className="page ci-wrap">
        {moKhai && <KhaiKenh pushToast={pushToast} onDong={() => setMoKhai(false)} />}

        <div className={'ci-grid' + (v && moHoSo ? ' co-hoso' : '')}>
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
            <button className="ci-dau-nut" onClick={() => setMoKhai(x => !x)}>
              <window.Icon name="plus" size={12} />Kết nối kênh
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
                {[['tat-ca', 'Tất cả'], ['chua-doc', 'Chưa đọc'], ['cua-toi', 'Của tôi']].map(([id, nhan]) => (
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
              <div className="ci-tomtat">
                {dangTai ? 'Đang tải…' : dsach.length + ' hội thoại đang hiện'}
                {!dangTai && dem.tong > dsach.length && <span> trên tổng {dem.tong}</span>}
              </div>
            </div>

            <div className="ci-ds">
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
                <button key={c.id}
                        className={'ci-muc' + (chon === c.id ? ' on' : '') + (c.unread ? ' chuadoc' : '')}
                        onClick={() => setChon(c.id)}>
                  <span className="ci-muc-avt">
                    <AnhDaiDien ten={c.displayName || c.contactExternalId} co={36} />
                    <HuyHieuKenh kenh={c.channel} />
                  </span>
                  <span className="ci-muc-than">
                    <span className="ci-muc-dau">
                      <span className="ci-ten">{c.displayName || c.contactExternalId}</span>
                      <span className="ci-luc">{gioNgan(c.lastActivityAt)}</span>
                    </span>
                    <span className="ci-xemtruoc">{c.lastPreview || 'chưa có tin nào'}</span>
                    {/* Hàng cuối LUÔN có ít nhất viên trạng thái. Trước đây cả hàng biến mất khi
                        chưa ai nhận việc, nên các mục cao thấp so le và danh sách trông lởm chởm. */}
                    <span className="ci-muc-cuoi">
                      <span className={'ci-tt' + (c.status === 0 ? ' moi' : '')}>
                        <i />{TEN_TRANG_THAI[c.status]}
                      </span>
                      {c.assignedUsername && <span className="ci-giao">{c.assignedUsername}</span>}
                      {c.botPaused && <span className="ci-botcam">bot tạm dừng</span>}
                    </span>
                  </span>
                </button>
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
            {!v && <div className="ci-trong">Chọn một hội thoại bên trái để xem nội dung.</div>}
            {v && (
              <>
                <div className="ci-chat-dau">
                  <AnhDaiDien ten={v.displayName || v.contactExternalId} url={lh?.avatarUrl} co={36} />
                  <div className="ci-chat-ten">
                    <b>{tenKhach}</b>
                    {/* Gộp mọi thứ "hội thoại này đang ra sao" vào MỘT dòng, cắt bớt khi hẹp.
                        Tách thành nhiều thẻ thì hàng tiêu đề cao gấp đôi mà không thêm thông tin. */}
                    <span>
                      <i aria-hidden="true" />
                      <em>{[KENH[v.channel]?.ten, TEN_TRANG_THAI[v.status],
                           v.assignedUsername || 'chưa ai nhận',
                           v.botPaused ? 'bot tạm dừng' : 'bot đang trả lời'].join(' · ')}</em>
                    </span>
                  </div>
                  <div className="ci-nut-nhom">
                    <button className={'ci-nut' + (v.assignedUsername ? '' : ' chinh')} onClick={nhanViec}>
                      {v.assignedUsername ? 'Bỏ nhận' : 'Nhận việc'}
                    </button>
                    <button className="ci-nut" onClick={batTatBot}>{v.botPaused ? 'Cho bot nói lại' : 'Tạm dừng bot'}</button>
                    {v.status !== 2
                      ? <button className="ci-nut" onClick={() => doiTrangThai(2)}>Đóng</button>
                      : <button className="ci-nut" onClick={() => doiTrangThai(1)}>Mở lại</button>}
                    <span className="ci-vach" aria-hidden="true" />
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
                  {tinNhan.length === 0 && <div className="ci-trong">Chưa có tin nhắn nào.</div>}
                  {tinNhan.map((m, i) => (
                    <React.Fragment key={m.id}>
                      {(i === 0 || ngayCua(m.createdUtc) !== ngayCua(tinNhan[i - 1].createdUtc)) && (
                        <div className="ci-ngay"><span>{nhanNgay(m.createdUtc)}</span></div>
                      )}
                      <BongBong tin={m} kenh={v.channel} ten0={tenKhach} />
                    </React.Fragment>
                  ))}
                </div>

                <div className="ci-soan">
                  {khoaSoan ? (
                    // Nói rõ VÌ SAO và chỉ đường đi tiếp, không chỉ chặn.
                    <div className="ci-khoa">{cuaSo?.reason || 'Hiện chưa gửi được cho khách này.'}</div>
                  ) : (
                    <>
                      {/* Tệp đã tải lên, CHỜ bấm gửi. Xem trước rồi mới gửi — lỡ chọn nhầm còn gỡ kịp. */}
                      {dinhKem && (
                        <div className="ci-cho-gui">
                          {dinhKem.kind === 'anh'
                            ? <img src={dinhKem.url} alt="" />
                            : <window.Icon name="paperclip" size={16} />}
                          <span>{dinhKem.name}<em>{coCho(dinhKem.size)}</em></span>
                          <button onClick={() => setDinhKem(null)} aria-label="Bỏ tệp này">
                            <window.Icon name="close" size={14} />
                          </button>
                        </div>
                      )}
                      {/* Gõ "/" ra danh sách mẫu. Nổi TRÊN ô soạn, không đẩy ô soạn xuống. */}
                      {goiY !== null && mauTraLoi.filter(m => m.trigger.startsWith(goiY)).length > 0 && (
                        <div className="ci-mau">
                          <div className="ci-mau-dau">Mẫu trả lời</div>
                          {mauTraLoi.filter(m => m.trigger.startsWith(goiY)).slice(0, 6).map(m => (
                            <button key={m.id} className="ci-mau-muc"
                                    onClick={() => { setSoan(m.body); setGoiY(null); }}>
                              <b>/{m.trigger}</b>
                              <span>{m.body}</span>
                            </button>
                          ))}
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
                          <button className="icon" disabled={dangTai2}
                                  onClick={() => tepRef.current?.click()}
                                  title="Gửi ảnh hoặc tệp" aria-label="Gửi ảnh hoặc tệp">
                            <window.Icon name={dangTai2 ? 'refresh' : 'paperclip'} size={15} />
                          </button>
                          <button className="mau" onClick={() => setGoiY(goiY === null ? '' : null)}
                                  title="Chèn mẫu trả lời">
                            <b>/</b>Mẫu trả lời
                          </button>
                          <span className="ci-soan-nhac">
                            Enter để gửi · Shift + Enter xuống dòng
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

          {/* Vùng 4 — hồ sơ khách */}
          {v && moHoSo && <HoSo chiTiet={chiTiet} pushToast={pushToast} onDong={() => setMoHoSo(false)} />}
        </div>
      </main>
    );
  }

  window.ChatInboxPage = ChatInboxPage;
})();
