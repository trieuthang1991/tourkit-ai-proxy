// pages/chat-inbox.jsx — Hộp thư chat đa kênh (route /chat-inbox).
//
// Ba cột: bộ lọc | danh sách hội thoại | khung chat + ô soạn. Cùng bố cục với Hộp thư AI để
// người dùng khỏi phải học lại.
//
// Hai điều KHÔNG được bỏ:
//   1. Bong bóng phân biệt BA bên (khách / AI / nhân viên), không phải hai. Người đọc cần biết
//      câu nào do máy trả lời — nhất là khi phải sửa lại lời máy nói với khách.
//   2. Hết cửa sổ gửi thì KHOÁ ô soạn kèm lý do. Để bấm gửi rồi mới báo hỏng là đã muộn: nhân
//      viên gõ xong cả đoạn mới biết không gửi được.
(function () {
  'use strict';

  const { useState, useEffect, useRef, useCallback } = React;
  const authedFetch = (...a) => window.tourkitAuth.authedFetch(...a);
  const fmtAgo = (t) => (window.tourkitUtil?.fmtAgo ? window.tourkitUtil.fmtAgo(t) : t || '');

  const KENH = { 0: 'Zalo', 1: 'Messenger', 2: 'Web', 3: 'Telegram' };
  const TRANG_THAI = [
    { v: null, nhan: 'Tất cả' },
    { v: 0, nhan: 'Mới' },
    { v: 1, nhan: 'Đang xử lý' },
    { v: 2, nhan: 'Đã đóng' },
  ];

  function BongBong({ tin }) {
    // 0=khách 1=AI 2=nhân viên 3=hệ thống
    const ben = tin.senderKind;
    const cuaMinh = tin.direction === 1;
    const lop = ben === 0 ? 'ci-khach' : ben === 1 ? 'ci-ai' : ben === 3 ? 'ci-hethong' : 'ci-nv';
    const nhan = ben === 1 ? 'AI trả lời' : ben === 2 ? (tin.senderUsername || 'Nhân viên') : null;
    return (
      <div className={'ci-dong ' + (cuaMinh ? 'ci-phai' : 'ci-trai')}>
        <div className={'ci-bong ' + lop}>
          {nhan && <div className="ci-nhan">{nhan}</div>}
          <div className="ci-noidung">{tin.body || <i>(không có chữ)</i>}</div>
          <div className="ci-gio">
            {fmtAgo(tin.createdUtc)}
            {tin.state === 4 && <span className="ci-loi" title={tin.errorMessage}> · gửi hỏng</span>}
            {tin.state === 0 && <span className="ci-cho"> · đang gửi…</span>}
          </div>
        </div>
      </div>
    );
  }

  // Khối khai kết nối kênh. Form TỰ VẼ theo danh sách ô mà máy chủ trả về — thêm kênh mới ở
  // backend là giao diện tự có ô nhập, không phải sửa hai nơi rồi lệch nhau.
  function KhaiKenh({ pushToast, onDong }) {
    const [ds, setDs] = useState(null);
    const [dangLuu, setDangLuu] = useState(null);
    const [nhap, setNhap] = useState({});

    useEffect(() => {
      authedFetch('/api/v1/chat/channels')
        .then(r => r.ok ? r.json() : Promise.reject(r.status))
        .then(j => setDs(j.items || []))
        .catch(st => setDs(st === 403 ? 'cam' : []));
    }, []);

    async function luu(kenh) {
      setDangLuu(kenh);
      try {
        const r = await authedFetch('/api/v1/chat/channels/' + kenh, {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(nhap[kenh] || {}),
        });
        if (!r.ok) { pushToast('Lưu không được', 'error'); return; }
        pushToast('Đã lưu kết nối kênh', 'success');
        setNhap(p => ({ ...p, [kenh]: {} }));
        const j = await (await authedFetch('/api/v1/chat/channels')).json();
        setDs(j.items || []);
      } finally { setDangLuu(null); }
    }

    if (ds === 'cam') return (
      <div className="ci-khai"><div className="ci-trong">
        Chỉ tài khoản có quyền Cấu hình hệ thống mới khai được kết nối kênh.
      </div></div>
    );
    if (!ds) return <div className="ci-khai"><div className="ci-trong">Đang tải…</div></div>;

    return (
      <div className="ci-khai">
        <div className="ci-khai-dau">
          <b>Kết nối kênh</b>
          <button onClick={onDong}>Đóng</button>
        </div>
        {ds.map(k => (
          <div key={k.channel} className="ci-kenh-the">
            <div className="ci-kenh-ten">
              {k.name}
              {k.configured === true && <span className="ci-xong">đã khai</span>}
              {k.configured === false && <span className="ci-chua">chưa khai</span>}
            </div>
            <label className="ci-url">
              Địa chỉ nhận tin (dán vào trang quản trị của kênh)
              <input readOnly value={k.webhookUrl} onFocus={e => e.target.select()} />
            </label>
            {k.fields.map(o => o.type === 'note'
              ? <div key={o.key} className="ci-ghichu">{o.label}</div>
              : (
                <label key={o.key} className="ci-o">
                  {o.label}
                  <input type={o.type === 'secret' ? 'password' : 'text'}
                         placeholder={k.configured ? 'để trống = giữ nguyên' : ''}
                         value={(nhap[k.channel] || {})[o.key] || ''}
                         onChange={e => setNhap(p => ({
                           ...p, [k.channel]: { ...(p[k.channel] || {}), [o.key]: e.target.value },
                         }))} />
                </label>
              ))}
            {k.fields.some(o => o.type !== 'note') && (
              <button className="btn-primary" disabled={dangLuu === k.channel}
                      onClick={() => luu(k.channel)}>
                {dangLuu === k.channel ? 'Đang lưu…' : 'Lưu'}
              </button>
            )}
          </div>
        ))}
      </div>
    );
  }

  function ChatInboxPage({ pushToast }) {
    const [dsach, setDsach] = useState([]);
    const [dem, setDem] = useState({ moi: 0, dangXuLy: 0, daDong: 0 });
    const [loc, setLoc] = useState(null);
    const [tim, setTim] = useState('');
    const [chon, setChon] = useState(null);      // id hội thoại đang mở
    const [chiTiet, setChiTiet] = useState(null);
    const [soan, setSoan] = useState('');
    const [dangGui, setDangGui] = useState(false);
    const [dangTai, setDangTai] = useState(true);
    const [moKhai, setMoKhai] = useState(false);
    const cuonRef = useRef(null);

    const taiDsach = useCallback(async () => {
      try {
        const q = new URLSearchParams();
        if (loc !== null) q.set('status', loc);
        if (tim.trim()) q.set('search', tim.trim());
        const r = await authedFetch('/api/v1/chat/conversations?' + q);
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const j = await r.json();
        setDsach(j.items || []);
        setDem(j.counts || {});
      } catch (e) {
        // Không toast mỗi lần hỏng: trang tự hỏi lại 4 giây một lần, mạng chập chờn là spam ngay.
      } finally { setDangTai(false); }
    }, [loc, tim]);

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

    useEffect(() => {
      const el = cuonRef.current;
      if (el) el.scrollTop = el.scrollHeight;
    }, [chiTiet?.messages?.length]);

    const cuaSo = chiTiet?.sendWindow;
    const khoaSoan = !cuaSo?.open;

    async function gui() {
      const noi = soan.trim();
      if (!noi || dangGui || !chon) return;
      setDangGui(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + chon + '/send', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ text: noi }),
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không gửi được', 'error'); return; }
        setSoan('');
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

    return (
      <main className="page ci-wrap">
        <header className="page-head">
          <div>
            <h1>Hộp thư chat</h1>
            <p className="muted">Tin khách nhắn từ Zalo, Facebook Messenger và Telegram — bot trả lời trước, bạn tiếp quản khi cần.</p>
          </div>
          <button className="btn-ghost" onClick={() => setMoKhai(v => !v)}>Kết nối kênh</button>
        </header>

        {moKhai && <KhaiKenh pushToast={pushToast} onDong={() => setMoKhai(false)} />}

        <div className="ci-grid">
          {/* Cột 1 — bộ lọc */}
          <aside className="ci-loc">
            {TRANG_THAI.map(t => (
              <button key={String(t.v)}
                      className={'ci-loc-nut' + (loc === t.v ? ' on' : '')}
                      onClick={() => setLoc(t.v)}>
                <span>{t.nhan}</span>
                {t.v === 0 && dem.moi > 0 && <b className="ci-dem">{dem.moi}</b>}
                {t.v === 1 && dem.dangXuLy > 0 && <b className="ci-dem">{dem.dangXuLy}</b>}
              </button>
            ))}
            <input className="ci-tim" placeholder="Tìm tên / nội dung…"
                   value={tim} onChange={e => setTim(e.target.value)} />
          </aside>

          {/* Cột 2 — danh sách hội thoại */}
          <section className="ci-ds">
            {dangTai && <div className="ci-trong">Đang tải…</div>}
            {!dangTai && dsach.length === 0 && (
              <div className="ci-trong">
                Chưa có hội thoại nào.<br />
                <span className="muted">Bấm “Kết nối kênh” ở trên để lấy địa chỉ nhận tin, rồi dán vào trang quản trị của Zalo OA / Facebook / Telegram.</span>
              </div>
            )}
            {dsach.map(c => (
              <button key={c.id}
                      className={'ci-muc' + (chon === c.id ? ' on' : '') + (c.unread ? ' chuadoc' : '')}
                      onClick={() => setChon(c.id)}>
                <div className="ci-muc-dau">
                  <span className="ci-ten">{c.displayName || c.contactExternalId}</span>
                  <span className="ci-kenh">{KENH[c.channel] || '?'}</span>
                </div>
                <div className="ci-xemtruoc">{c.lastPreview || '—'}</div>
                <div className="ci-muc-cuoi">
                  <span>{fmtAgo(c.lastActivityAt)}</span>
                  {c.assignedUsername && <span className="ci-giao">{c.assignedUsername}</span>}
                  {c.botPaused && <span className="ci-botcam">bot tạm dừng</span>}
                </div>
              </button>
            ))}
          </section>

          {/* Cột 3 — khung chat */}
          <section className="ci-chat">
            {!v && <div className="ci-trong">Chọn một hội thoại để xem.</div>}
            {v && (
              <>
                <div className="ci-chat-dau">
                  <div>
                    <b>{v.displayName || v.contactExternalId}</b>
                    <span className="muted"> · {KENH[v.channel]}</span>
                  </div>
                  <div className="ci-nut-nhom">
                    <button onClick={nhanViec}>
                      {v.assignedUsername ? 'Bỏ nhận' : 'Nhận việc'}
                    </button>
                    <button onClick={batTatBot}>
                      {v.botPaused ? 'Cho bot nói lại' : 'Tạm dừng bot'}
                    </button>
                    {v.status !== 2
                      ? <button onClick={() => doiTrangThai(2)}>Đóng</button>
                      : <button onClick={() => doiTrangThai(1)}>Mở lại</button>}
                  </div>
                </div>

                <div className="ci-cuon" ref={cuonRef}>
                  {(chiTiet.messages || []).map(m => <BongBong key={m.id} tin={m} />)}
                </div>

                <div className="ci-soan">
                  {khoaSoan ? (
                    // Nói rõ VÌ SAO và chỉ đường đi tiếp, không chỉ chặn.
                    <div className="ci-khoa">{cuaSo?.reason || 'Hiện chưa gửi được.'}</div>
                  ) : (
                    <>
                      {cuaSo?.hoursLeft != null && cuaSo.hoursLeft < 6 && (
                        <div className="ci-sapdong">
                          Còn {cuaSo.hoursLeft} giờ nữa là hết hạn trả lời khách này.
                        </div>
                      )}
                      <textarea value={soan} onChange={e => setSoan(e.target.value)}
                                placeholder="Nhập trả lời… (Enter để gửi, Shift+Enter xuống dòng)"
                                onKeyDown={e => {
                                  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); gui(); }
                                }} />
                      <button className="btn-primary" onClick={gui} disabled={dangGui || !soan.trim()}>
                        {dangGui ? 'Đang gửi…' : 'Gửi'}
                      </button>
                    </>
                  )}
                </div>
              </>
            )}
          </section>
        </div>
      </main>
    );
  }

  window.ChatInboxPage = ChatInboxPage;
})();
