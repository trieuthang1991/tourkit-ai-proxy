// pages/oauth-consent.jsx — Trang đồng ý kết nối của Claude.ai / ChatGPT / MCP client (/oauth/authorize).
//
// Luồng: ứng dụng AI mở trình duyệt tới /oauth/authorize?client_id=…&redirect_uri=…&code_challenge=… →
//   1. Hỏi máy chủ GET /api/v1/mcp/oauth/authorize-info với NGUYÊN query (KHÔNG cần đăng nhập):
//        kind=error    → hiện lỗi, TUYỆT ĐỐI không chuyển hướng (client/redirect không tin được)
//        kind=redirect → máy chủ đã dựng sẵn URL báo lỗi về client → window.location.replace
//        kind=consent  → hiện thẻ đồng ý (chưa đăng nhập thì hiện LoginGate sẵn có, URL giữ nguyên nên
//                        đăng nhập xong quay lại đúng thẻ này, query còn nguyên)
//   2. "Cho phép"/"Từ chối" → POST /approve | /deny kèm X-Session-Id, thân là NGUYÊN query gốc.
//      Máy chủ KIỂM LẠI toàn bộ rồi trả { redirectUrl } → window.location.replace.
//
// Trang này nằm NGOÀI khung menu (app.jsx bắt sớm theo đường dẫn, trước cổng đăng nhập).
// Mọi chuỗi từ máy chủ (tên ứng dụng, logo) chỉ hiện bằng React text/thuộc tính — không bao giờ là HTML.

(function () {
  const { useState, useEffect } = React;
  const auth = () => window.tourkitAuth;
  const laHttp = u => typeof u === 'string' && /^https?:\/\//i.test(u);

  const CSS = `
  .oc-wrap{min-height:100vh;display:flex;align-items:center;justify-content:center;padding:16px;background:var(--bg,#F8FAFC);box-sizing:border-box}
  .oc-card{width:100%;max-width:440px;background:var(--surface,#fff);border:1px solid var(--border,#E2E8F0);border-radius:16px;
    padding:28px 24px;box-shadow:0 10px 30px rgba(15,23,42,.08);box-sizing:border-box}
  .oc-brand{display:flex;align-items:center;justify-content:center;gap:12px;margin-bottom:18px}
  .oc-logo{width:44px;height:44px;border-radius:12px;object-fit:contain;background:#fff;border:1px solid var(--border,#E2E8F0)}
  .oc-logo-tk{width:44px;height:44px;border-radius:12px;background:var(--primary,#F97316);color:#fff;display:flex;align-items:center;justify-content:center;font-weight:700;font-size:15px}
  .oc-link-ico{color:var(--text-3,#94A3B8);font-size:18px}
  .oc-title{margin:0 0 6px;text-align:center;font-size:19px;line-height:1.35;color:var(--text,#0F172A);font-weight:700;overflow-wrap:anywhere}
  .oc-sub{margin:0 0 18px;text-align:center;font-size:14px;color:var(--text-2,#475569)}
  .oc-box{border:1px solid var(--border,#E2E8F0);border-radius:12px;padding:12px 14px;margin-bottom:12px;font-size:14px;color:var(--text,#0F172A);line-height:1.5}
  .oc-box small{display:block;color:var(--text-2,#475569);font-size:12px;margin-bottom:2px}
  .oc-perm{background:var(--primary-soft,#FEF3E7);border-color:transparent}
  .oc-note{font-size:12px;color:var(--text-2,#475569);margin:4px 0 16px;text-align:center;line-height:1.5}
  .oc-actions{display:flex;gap:10px}
  .oc-actions .btn{flex:1;padding:13px 14px;font-size:14px}
  .oc-actions .btn:disabled{opacity:.6;cursor:not-allowed}
  .oc-ghost{background:var(--surface,#fff);color:var(--text,#0F172A);border:1px solid var(--border-strong,#CBD5E1)}
  .oc-err{background:#FEF2F2;border:1px solid #FECACA;color:#991B1B;border-radius:12px;padding:10px 12px;font-size:13px;margin-bottom:12px;line-height:1.5}
  .oc-foot{margin-top:14px;text-align:center;font-size:13px;color:var(--text-2,#475569)}
  .oc-foot button{background:none;border:0;padding:0;color:var(--primary-dark,#EA580C);font:inherit;text-decoration:underline;cursor:pointer}
  @media (max-width:480px){.oc-card{padding:22px 16px}.oc-actions{flex-direction:column-reverse}}
  `;

  function Khung({ children }) {
    return (
      <div className="oc-wrap">
        <style>{CSS}</style>
        <div className="oc-card" role="main">{children}</div>
      </div>
    );
  }

  function OAuthConsentPage({ authUser, authReady, onAuthed }) {
    // Query gốc, giữ NGUYÊN không sửa: máy chủ cần đúng chuỗi này để kiểm lại khi bấm nút.
    const [query] = useState(() => window.location.search || '');
    const [info, setInfo] = useState(null);       // null = đang kiểm; { kind, ... }
    const [dang, setDang] = useState(null);       // 'approve' | 'deny' | null — đang gửi
    const [loi, setLoi] = useState(null);
    const [xong, setXong] = useState(false);      // đã nhận redirectUrl, đang chuyển đi

    useEffect(() => {
      let sống = true;
      (async () => {
        try {
          const r = await fetch('/api/v1/mcp/oauth/authorize-info' + query, { headers: { Accept: 'application/json' } });
          const j = await r.json().catch(() => null);
          if (!sống) return;
          if (!r.ok || !j || !j.kind) throw new Error('HTTP ' + r.status);
          if (j.kind === 'redirect' && laHttp(j.url)) {
            setInfo({ kind: 'redirect' });
            window.location.replace(j.url);
            return;
          }
          setInfo(j.kind === 'consent' ? j : { kind: 'error', message: j.message });
        } catch {
          if (sống) setInfo({ kind: 'error', message: 'Chưa kiểm tra được yêu cầu kết nối lúc này — thử lại sau ít phút.' });
        }
      })();
      return () => { sống = false; };
    }, []);

    async function quyet(loai) {
      if (dang || xong) return;
      setDang(loai); setLoi(null);
      try {
        const r = await auth().authedFetch('/api/v1/mcp/oauth/' + loai, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ query }),
        });
        const j = await r.json().catch(() => ({}));
        if (r.status === 401) { setLoi('Phiên đăng nhập đã hết — hãy đăng nhập lại rồi thử kết nối lại.'); setDang(null); return; }
        if (!r.ok || !laHttp(j.redirectUrl)) throw new Error(j.error || ('Lỗi máy chủ (HTTP ' + r.status + ')'));
        setXong(true);                       // giữ nút khoá trong lúc trình duyệt chuyển đi
        window.location.replace(j.redirectUrl);
      } catch (e) {
        setLoi(e.message || 'Không gửi được lựa chọn của bạn, thử lại.');
        setDang(null);
      }
    }

    if (!info || (info.kind === 'consent' && !authReady)) {
      return <div className="login-splash"><div className="login-splash-mark" /></div>;
    }
    if (info.kind === 'redirect') {
      return <Khung><p className="oc-sub" style={{ margin: 0 }}>Đang chuyển về ứng dụng…</p></Khung>;
    }
    if (info.kind === 'error') {
      return (
        <Khung>
          <h1 className="oc-title">Yêu cầu kết nối không hợp lệ</h1>
          <div className="oc-err" role="alert">{info.message || 'Yêu cầu kết nối không hợp lệ.'}</div>
          <p className="oc-note">Không có gì được cấp quyền. Hãy đóng tab này và bắt đầu lại từ ứng dụng AI bạn đang dùng.</p>
        </Khung>
      );
    }

    // kind === 'consent' nhưng chưa đăng nhập → màn đăng nhập sẵn có. URL không đổi nên query còn nguyên.
    if (!authUser) return <window.LoginGate onAuthed={onAuthed} />;

    // Tên do client tự khai (đăng ký động không ai kiểm) nên LUÔN kèm địa chỉ quay về để người dùng tự đối chiếu.
    const tenKhai = (info.clientName || '').trim();
    const ten = tenKhai && !laHttp(tenKhai) ? tenKhai : (info.redirectHost || 'Ứng dụng bên ngoài');
    const logo = typeof info.logoUri === 'string' && info.logoUri.startsWith('https://') ? info.logoUri : null;
    const khoa = !!dang || xong;

    return (
      <Khung>
        <div className="oc-brand">
          {logo
            ? <img className="oc-logo" src={logo} alt="" referrerPolicy="no-referrer" onError={e => { e.currentTarget.style.display = 'none'; }} />
            : <div className="oc-logo-tk" aria-hidden="true">{(ten[0] || '?').toUpperCase()}</div>}
          <span className="oc-link-ico" aria-hidden="true">⇄</span>
          <div className="oc-logo-tk" aria-hidden="true">TK</div>
        </div>
        <h1 className="oc-title">{ten} muốn truy cập TourKit của bạn</h1>
        <p className="oc-sub">Bạn đang cho phép ứng dụng này kết nối với tài khoản TourKit.</p>

        <div className="oc-box">
          <small>Đăng nhập với tài khoản</small>
          <b>{authUser.fullName || 'Tài khoản TourKit'}</b>
          {authUser.companyName ? <span> · {authUser.companyName}</span> : null}
        </div>
        <div className="oc-box oc-perm">
          <small>Quyền được cấp</small>
          Đọc số liệu kinh doanh TourKit (doanh thu, khách hàng, cơ hội, tour…) theo đúng quyền của tài khoản bạn.
        </div>
        {info.redirectHost ? (
          <div className="oc-box"><small>Sau khi xong sẽ quay về</small>{info.redirectHost}</div>
        ) : null}

        {loi ? <div className="oc-err" role="alert">{loi}</div> : null}

        <div className="oc-actions">
          <button className="btn oc-ghost" disabled={khoa} onClick={() => quyet('deny')}>
            {dang === 'deny' ? 'Đang gửi…' : 'Từ chối'}
          </button>
          <button className="btn btn-primary" disabled={khoa} onClick={() => quyet('approve')}>
            {dang === 'approve' || xong ? 'Đang kết nối…' : 'Cho phép'}
          </button>
        </div>
        <p className="oc-note">Bạn có thể ngắt kết nối bất cứ lúc nào trong trang Kết nối AI.</p>
        <div className="oc-foot">
          Không phải tài khoản này? <button type="button" disabled={khoa} onClick={() => auth().logout()}>Đăng xuất</button>
        </div>
      </Khung>
    );
  }

  window.OAuthConsentPage = OAuthConsentPage;
})();
