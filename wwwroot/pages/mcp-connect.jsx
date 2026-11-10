// pages/mcp-connect.jsx — Kết nối AI (MCP, giai đoạn 1: chỉ đọc).
//
// Mỗi nhân viên tự tạo khoá riêng cho từng thiết bị rồi dán vào Claude Code / Cursor / Claude Desktop
// để hỏi số liệu TourKit ngay trong công cụ AI của mình. Khoá chỉ hiện ĐÚNG MỘT LẦN lúc tạo —
// máy chủ chỉ giữ bản băm. Thu hồi hai bước để khỏi lỡ tay.

(function () {
  const { useState, useEffect, useRef } = React;
  const Icon = window.Icon;
  const PageHero = window.PageShell?.PageHero;
  const api = (url, opt) => window.tourkitAuth.authedFetch(url, opt);
  const luc = iso => iso ? new Date(iso).toLocaleString('vi-VN') : 'Chưa dùng';

  // Địa chỉ lấy theo nơi đang mở trang để staging/local hiện đúng địa chỉ của chính nó.
  const diaChiMcp = () => window.location.origin + '/mcp';

  function mauCauHinh(khoa) {
    const url = diaChiMcp();
    return [
      { id: 'claude-code', ten: 'Claude Code',
        noiDung: `claude mcp add --transport http tourkit ${url} --header "Authorization: Bearer ${khoa}"` },
      { id: 'cursor', ten: 'Cursor (.cursor/mcp.json)',
        noiDung: JSON.stringify({ mcpServers: { tourkit: { url, headers: { Authorization: 'Bearer ' + khoa } } } }, null, 2) },
      { id: 'claude-desktop', ten: 'Claude Desktop (qua mcp-remote)',
        noiDung: `npx mcp-remote ${url} --header "Authorization: Bearer ${khoa}"` },
    ];
  }

  function McpConnectPage({ pushToast }) {
    const [dangTai, setDangTai] = useState(true);
    const [ds, setDs] = useState([]);
    const [ten, setTen] = useState('');
    const [dangTao, setDangTao] = useState(false);
    const [loi, setLoi] = useState(null);
    const [moi, setMoi] = useState(null);          // { id, token, label } — chỉ giữ trong bộ nhớ trang
    const [chuaThuHoi, setChuaThuHoi] = useState(null); // id đang chờ bấm lần hai
    const hen = useRef(null);

    async function tai() {
      try {
        const r = await api('/api/v1/mcp/tokens');
        const j = await r.json().catch(() => []);
        if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status);
        setDs((Array.isArray(j) ? j : (j.items || [])).filter(k => !k.revokedUtc));
      } catch (e) { pushToast?.('Không tải được danh sách khoá: ' + e.message, 'error'); }
      finally { setDangTai(false); }
    }
    useEffect(() => { tai(); return () => clearTimeout(hen.current); }, []);

    async function tao(e) {
      e?.preventDefault();
      setLoi(null);
      const nhan = ten.trim();
      if (!nhan) { setLoi('Hãy đặt tên cho thiết bị'); return; }
      setDangTao(true);
      try {
        const r = await api('/api/v1/mcp/tokens', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ label: nhan }),
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { setLoi(j.error || 'Không tạo được khoá (HTTP ' + r.status + ')'); return; }
        setMoi({ id: j.id, token: j.token, label: nhan });
        setTen('');
        await tai();
      } catch (er) { setLoi('Không tạo được khoá: ' + er.message); }
      finally { setDangTao(false); }
    }

    function chep(noiDung, nhan) {
      window.tourkitUtil.copyText(noiDung);
      pushToast?.('Đã chép ' + nhan, 'success');
    }

    async function thuHoi(k) {
      if (chuaThuHoi !== k.id) {
        setChuaThuHoi(k.id);
        clearTimeout(hen.current);
        hen.current = setTimeout(() => setChuaThuHoi(null), 4000);
        return;
      }
      setChuaThuHoi(null);
      const r = await api('/api/v1/mcp/tokens/' + encodeURIComponent(k.id), { method: 'DELETE' });
      if (!r.ok && r.status !== 404) { pushToast?.('Không thu hồi được khoá', 'error'); return; }
      if (moi?.id === k.id) setMoi(null);
      pushToast?.('Đã thu hồi khoá "' + k.label + '"', 'success');
      await tai();
    }

    return (
      <main className="page mcp-page">
        {PageHero && <PageHero
          icon="sparkle"
          title="Kết nối AI"
          sub="Hỏi số liệu TourKit ngay trong Claude, Cursor… bằng khoá riêng của bạn."
        />}

        <section className="mcp-canhbao" role="note">
          <Icon name="warning" size={16} />
          <span>Dữ liệu bạn hỏi sẽ được gửi tới dịch vụ AI bạn đang dùng (Claude, Cursor…). Chỉ kết nối
            công cụ mà công ty cho phép, và không chia sẻ khoá cho người khác.</span>
        </section>

        <form className="mcp-the" onSubmit={tao}>
          <h3 className="mcp-tieude">Tạo khoá mới</h3>
          <div className="mcp-hang">
            <input className="mcp-nhap" value={ten} maxLength={100} placeholder="Tên thiết bị, vd: Laptop văn phòng"
                   aria-label="Tên thiết bị" onChange={e => { setTen(e.target.value); setLoi(null); }} />
            <button type="submit" className="mcp-nut mcp-chinh" disabled={dangTao || !ten.trim()}>
              <Icon name="check" size={13} /> {dangTao ? 'Đang tạo…' : 'Tạo khoá'}
            </button>
          </div>
          {loi && <div className="mcp-loi" role="alert"><Icon name="warning" size={14} /> {loi}</div>}
        </form>

        {moi && (
          <section className="mcp-the mcp-moi" aria-live="polite">
            <h3 className="mcp-tieude">Khoá "{moi.label}" — chỉ hiện một lần</h3>
            <p className="mcp-ghichu">Hãy chép ngay. Đóng trang này là không xem lại được; lỡ mất thì thu hồi và tạo khoá mới.</p>
            <div className="mcp-hang">
              <code className="mcp-ma" data-testid="mcp-token">{moi.token}</code>
              <button className="mcp-nut" onClick={() => chep(moi.token, 'khoá')}><Icon name="copy" size={13} /> Chép khoá</button>
            </div>
            <h4 className="mcp-con">Dán vào công cụ của bạn</h4>
            {mauCauHinh(moi.token).map(m => (
              <div key={m.id} className="mcp-mau">
                <div className="mcp-hang">
                  <b>{m.ten}</b><span className="mcp-gian" />
                  <button className="mcp-nut" onClick={() => chep(m.noiDung, m.ten)}><Icon name="copy" size={13} /> Chép</button>
                </div>
                <pre className="mcp-pre" data-testid={'mcp-mau-' + m.id}>{m.noiDung}</pre>
              </div>
            ))}
            <div className="mcp-hang">
              <span className="mcp-gian" />
              <button className="mcp-nut" onClick={() => setMoi(null)}>Tôi đã chép xong</button>
            </div>
          </section>
        )}

        <section className="mcp-the">
          <h3 className="mcp-tieude">Khoá của bạn</h3>
          {dangTai ? <div className="mcp-trong">Đang tải…</div>
            : ds.length === 0 ? <div className="mcp-trong">Bạn chưa có khoá nào.</div>
            : ds.map(k => (
              <div key={k.id} className="mcp-khoa" data-testid="mcp-khoa">
                <div className="mcp-khoa-tt">
                  <b>{k.label}</b>
                  <span>Tạo {luc(k.createdUtc)} · Dùng lần cuối: {luc(k.lastUsedUtc)}</span>
                </div>
                <button className={'mcp-nut mcp-nguy' + (chuaThuHoi === k.id ? ' dang-hoi' : '')} onClick={() => thuHoi(k)}>
                  <Icon name="trash" size={13} /> {chuaThuHoi === k.id ? 'Bấm lần nữa để thu hồi' : 'Thu hồi'}
                </button>
              </div>
            ))}
        </section>
      </main>
    );
  }

  window.McpConnectPage = McpConnectPage;
})();
