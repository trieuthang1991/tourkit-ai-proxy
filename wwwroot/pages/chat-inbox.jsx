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
  const KENH = {
    0: { ten: 'Zalo', chu: 'Z' },
    1: { ten: 'Messenger', chu: 'f' },
    2: { ten: 'Web', chu: 'W' },
    3: { ten: 'Telegram', chu: 'T' },
  };
  const KENH_SONG = [0, 1, 3];   // kênh đã nối thật; Web chỉ hiện khi có dữ liệu

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
    return <span className={'ci-hh' + (day ? ' day' : '')} title={k.ten}>{k.chu}</span>;
  }

  // Dấu tích trạng thái gửi. Ghép từ biểu tượng "check" có sẵn thay vì vẽ tay đường SVG mới.
  function DauGui({ state }) {
    if (state === 0) return <span className="ci-tich cho">đang gửi…</span>;
    if (state === 4) return null;   // lỗi có dòng riêng, màu đỏ, không nhét vào đây
    const nhan = state >= 3 ? 'Khách đã xem' : state === 2 ? 'Đã tới máy khách' : 'Đã gửi';
    return (
      <span className={'ci-tich' + (state >= 3 ? ' xem' : '')} title={nhan} aria-label={nhan}>
        <window.Icon name="check" size={11} stroke={2.6} />
        {state >= 2 && <window.Icon name="check" size={11} stroke={2.6} />}
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

  function BongBong({ tin }) {
    // 0=khách 1=AI 2=nhân viên 3=hệ thống
    const ben = tin.senderKind;
    const cuaMinh = tin.direction === 1;
    const lop = ben === 0 ? 'ci-khach' : ben === 1 ? 'ci-ai' : ben === 3 ? 'ci-hethong' : 'ci-nv';
    const nhan = ben === 1 ? 'AI trả lời' : ben === 2 ? (tin.senderUsername || 'Nhân viên') : null;
    const coTep = (tin.files || []).length > 0;
    return (
      <div className={'ci-dong ' + (cuaMinh ? 'ci-phai' : 'ci-trai')}>
        <div className={'ci-bong ' + lop}>
          {nhan && <div className="ci-nhan">{nhan}</div>}
          <DinhKem tin={tin} />
          {/* Có đính kèm thì chữ là CHÚ THÍCH, vắng chữ là bình thường — đừng in "(không có chữ)"
              dưới một tấm ảnh, vừa thừa vừa trông như lỗi. */}
          {(tin.body || !coTep) && (
            <div className="ci-noidung">{tin.body || <i>(không có chữ)</i>}</div>
          )}
          <div className="ci-gio">
            <span>{gioPhut(tin.createdUtc)}</span>
            {cuaMinh && <DauGui state={tin.state} />}
            {tin.state === 4 && <span className="ci-loi" title={tin.errorMessage}>gửi hỏng</span>}
          </div>
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
    const sap = cuaSo.hoursLeft < 6;
    return (
      <div className={'ci-cuaso ' + (sap ? 'sap' : 'mo')}>
        <window.Icon name="clock" size={14} />
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

  function HoSo({ chiTiet, onDong, pushToast }) {
    const v = chiTiet?.conversation;
    const lh = chiTiet?.contact;
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

        <div className="ci-hs-muc">
          <h4>Khách hàng CRM</h4>
          {lh?.crmCustomerId
            ? <div className="ci-hs-crm">Đã nối với khách <b>#{lh.crmCustomerId}</b></div>
            : (
              <div className="ci-hs-trong">
                Chưa nối với khách hàng trong CRM. Bot đang trả lời bằng kiến thức chung, không đọc
                lịch sử mua hay bảng giá của khách này.
              </div>
            )}
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
        </div>

        <div className="ci-hs-muc">
          <h4>Xử lý</h4>
          <Dong nhan="Trạng thái">{TEN_TRANG_THAI[v.status]}</Dong>
          <Dong nhan="Phụ trách">{v.assignedUsername || 'chưa ai nhận'}</Dong>
          <Dong nhan="Bot">{v.botPaused ? 'đang tạm dừng' : 'đang trả lời'}</Dong>
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
  function KhaiKenh({ pushToast, onDong }) {
    const [ds, setDs] = useState(null);
    const [dangLuu, setDangLuu] = useState(null);
    const [nhap, setNhap] = useState({});     // { "kenh:accountId" | "kenh:moi" -> {field: value} }

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
        if (!r.ok) { pushToast('Lưu không được', 'error'); return; }
        pushToast(accId ? 'Đã cập nhật tài khoản' : 'Đã thêm tài khoản', 'success');
        setNhap(p => ({ ...p, [khoa]: {} }));
        await taiLai();
      } finally { setDangLuu(null); }
    }

    async function xoa(kenh, accId, ten) {
      if (!window.confirm(`Gỡ kết nối "${ten || accId}"?\n\nLịch sử chat với khách vẫn giữ nguyên, chỉ ngừng nhận và gửi qua tài khoản này.`)) return;
      const r = await authedFetch('/api/v1/chat/channels/' + kenh + '/accounts/' + accId, { method: 'DELETE' });
      if (!r.ok) { pushToast('Gỡ không được', 'error'); return; }
      pushToast('Đã gỡ kết nối', 'success');
      await taiLai();
    }

    function ONhap({ kenh, accId, truong, daKhai, sanCo }) {
      if (truong.type === 'note') return <div className="ci-ghichu">{truong.label}</div>;
      const biMat = truong.type === 'secret';
      // Trường thường thì ĐIỀN SẴN giá trị đang lưu — không thấy giá trị cũ thì không kiểm được
      // mình khai đúng Trang/OA nào. Bí mật thì máy chủ không trả về, để trống = giữ nguyên.
      const dangGo = o(kenh, accId)[truong.key];
      const giaTri = dangGo !== undefined ? dangGo : (biMat ? '' : (sanCo?.[truong.key] || ''));
      return (
        <label className="ci-o">
          {truong.label}
          <input type={biMat ? 'password' : 'text'}
                 placeholder={biMat && daKhai ? 'để trống = giữ nguyên' : ''}
                 value={giaTri}
                 onChange={e => dat(kenh, accId, truong.key, e.target.value)} />
        </label>
      );
    }

    let than;
    if (ds === 'cam') than = (
      <div className="ci-trong">Chỉ tài khoản có quyền Cấu hình hệ thống mới khai được kết nối kênh.</div>
    );
    else if (!ds) than = <div className="ci-trong">Đang tải…</div>;
    else than = (
      <div className="ci-khai-luoi">
        {ds.map(k => (
          <div key={k.channel} className="ci-kenh-the">
            <div className="ci-kenh-ten">
              <HuyHieuKenh kenh={k.channel} day />
              {k.name}
              <span className="ci-so-tk">{k.accounts.length} tài khoản</span>
            </div>

            {/* URL dùng CHUNG (Zalo/Messenger). Telegram để null vì mỗi bot một URL riêng. */}
            {k.webhookUrl && (
              <label className="ci-url">
                Địa chỉ nhận tin (dán vào trang quản trị của kênh)
                <input readOnly value={k.webhookUrl} onFocus={e => e.target.select()} />
              </label>
            )}

            {k.accounts.map(t => (
              <details key={t.accountId} className="ci-tk">
                <summary>
                  <b>{t.label || 'Chưa đặt tên'}</b>
                  {t.configured
                    ? <span className="ci-xong">đã khai</span>
                    : <span className="ci-chua">thiếu khoá</span>}
                </summary>
                {/* URL RIÊNG từng bot Telegram — dán vào lệnh setWebhook của đúng bot đó. */}
                {!k.webhookUrl && (
                  <label className="ci-url">
                    Địa chỉ nhận tin của tài khoản này
                    <input readOnly value={t.webhookUrl} onFocus={e => e.target.select()} />
                  </label>
                )}
                {k.fields.map(f => (
                  <ONhap key={f.key} kenh={k.channel} accId={t.accountId} truong={f} daKhai
                         sanCo={t.values} />
                ))}
                <div className="ci-tk-nut">
                  <button className="btn-primary" disabled={dangLuu === k.channel + ':' + t.accountId}
                          onClick={() => luu(k.channel, t.accountId)}>
                    {dangLuu === k.channel + ':' + t.accountId ? 'Đang lưu…' : 'Lưu'}
                  </button>
                  <button className="ci-nut-xoa" onClick={() => xoa(k.channel, t.accountId, t.label)}>
                    Gỡ kết nối
                  </button>
                </div>
              </details>
            ))}

            <details className="ci-tk ci-tk-moi">
              <summary><b>+ Thêm tài khoản</b></summary>
              {k.fields.map(f => (
                <ONhap key={f.key} kenh={k.channel} accId={null} truong={f} daKhai={false} />
              ))}
              <button className="btn-primary" disabled={dangLuu === k.channel + ':moi'}
                      onClick={() => luu(k.channel, null)}>
                {dangLuu === k.channel + ':moi' ? 'Đang thêm…' : 'Thêm'}
              </button>
            </details>
          </div>
        ))}
      </div>
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
    const cuonRef = useRef(null);
    const tepRef = useRef(null);

    const taiDsach = useCallback(async () => {
      try {
        const q = new URLSearchParams();
        if (loc !== null) q.set('status', loc);
        if (kenhLoc !== null) q.set('channel', kenhLoc);
        if (nhom === 'chua-doc') q.set('unread', 'true');
        if (nhom === 'cua-toi') q.set('mine', 'true');
        if (tim.trim()) q.set('search', tim.trim());
        const r = await authedFetch('/api/v1/chat/conversations?' + q);
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const j = await r.json();
        setDsach(j.items || []);
        setDem(j.counts || {});
        setDemKenh(j.channelCounts || {});
      } catch (e) {
        // Không toast mỗi lần hỏng: trang tự hỏi lại 4 giây một lần, mạng chập chờn là spam ngay.
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

    // Hỏi lại định kỳ thay cho đẩy thời gian thực (xem mục Hạ tầng dữ liệu trong kế hoạch).
    // DỪNG khi tab ẩn — không thì mở 10 tab là nhân 10 lần tải, và lỗi kiểu đó chỉ lộ ra lúc
    // đông người dùng, tức là đúng lúc tệ nhất.
    useEffect(() => {
      let huy = false;
      const nhip = async () => {
        if (document.hidden || huy) return;
        await taiDsach();
        if (chon) await taiChiTiet(chon);
      };
      nhip();
      const t = setInterval(nhip, 4000);
      return () => { huy = true; clearInterval(t); };
    }, [taiDsach, taiChiTiet, chon]);

    useEffect(() => { if (chon) taiChiTiet(chon); }, [chon, taiChiTiet]);

    // Tải một lần, KHÔNG theo nhịp hỏi lại 4 giây: bộ mẫu hiếm khi đổi, kéo lại liên tục là
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

    async function nhanViec() {
      if (!chon) return;
      const dangGiao = chiTiet?.conversation?.assignedUsername;
      await authedFetch('/api/v1/chat/conversations/' + chon + '/assign', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: dangGiao ? '' : (window.tourkitAuth?.session?.username || '') }),
      });
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
    const tinNhan = chiTiet?.messages || [];
    const coLoc = kenhLoc !== null || nhom !== 'tat-ca' || loc !== null || !!tim.trim();

    return (
      <main className="page ci-wrap">
        {/* Dùng lại PageHero chung của app thay vì tự dựng header riêng: trang này là trang DUY
            NHẤT còn xài `page-head`, mà lớp đó chưa bao giờ được khai kiểu dáng nên tiêu đề bị
            thanh trên che. */}
        <window.PageShell.PageHero
          icon="send"
          title="Hộp thư chat"
          badge="Đa kênh"
          sub="Tin khách nhắn từ Zalo, Facebook Messenger và Telegram. Bot trả lời trước, bạn tiếp quản khi cần."
          status={{
            label: dem.chuaDoc > 0 ? dem.chuaDoc + ' chưa đọc' : 'Đã đọc hết',
            detail: dem.tong ? dem.tong + ' hội thoại' : null,
            tone: dem.chuaDoc > 0 ? 'live' : 'idle',
          }}
          actions={<button className="btn-ghost" onClick={() => setMoKhai(x => !x)}>Kết nối kênh</button>}
        />

        {moKhai && <KhaiKenh pushToast={pushToast} onDong={() => setMoKhai(false)} />}

        <div className={'ci-grid' + (v && moHoSo ? ' co-hoso' : '')}>
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
                    {(c.assignedUsername || c.botPaused) && (
                      <span className="ci-muc-cuoi">
                        {c.assignedUsername && <span className="ci-giao">{c.assignedUsername}</span>}
                        {c.botPaused && <span className="ci-botcam">bot tạm dừng</span>}
                      </span>
                    )}
                  </span>
                </button>
              ))}
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
                    <b>{v.displayName || v.contactExternalId}</b>
                    <span>{KENH[v.channel]?.ten} · {TEN_TRANG_THAI[v.status]}
                      {v.assignedUsername ? ' · ' + v.assignedUsername : ''}</span>
                  </div>
                  <div className="ci-nut-nhom">
                    <button onClick={nhanViec}>{v.assignedUsername ? 'Bỏ nhận' : 'Nhận việc'}</button>
                    <button onClick={batTatBot}>{v.botPaused ? 'Cho bot nói lại' : 'Tạm dừng bot'}</button>
                    {v.status !== 2
                      ? <button onClick={() => doiTrangThai(2)}>Đóng</button>
                      : <button onClick={() => doiTrangThai(1)}>Mở lại</button>}
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
                      <BongBong tin={m} />
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
                        <button className="ci-kep" disabled={dangTai2}
                                onClick={() => tepRef.current?.click()}
                                title="Gửi ảnh hoặc tệp" aria-label="Gửi ảnh hoặc tệp">
                          <window.Icon name={dangTai2 ? 'refresh' : 'paperclip'} size={16} />
                        </button>
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
                        <button className="ci-gui" onClick={gui}
                                disabled={dangGui || (!soan.trim() && !dinhKem)}
                                title="Gửi" aria-label="Gửi">
                          <window.Icon name="send" size={16} />
                        </button>
                      </div>
                      <div className="ci-soan-nhac">
                        Enter để gửi, Shift + Enter xuống dòng.
                        {v.botPaused && <span> Bot đang tạm dừng nên sẽ không trả lời chen vào.</span>}
                      </div>
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
