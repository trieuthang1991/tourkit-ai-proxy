// pages/ai-key.jsx — Key AI riêng của công ty (BYO, mở lại 03/10/2026).
//
// Hai cách dùng AI song song: nạp lượt và dùng key của hệ thống như trước, HOẶC khai key của chính
// công ty ở đây. Khai key thì mọi tính năng AI chạy bằng tài khoản của công ty và KHÔNG trừ lượt.
// Key hết tiền/sai thì hệ thống tự dùng key chung để không gián đoạn — và TRỪ lượt như thường.
//
// Chỉ ba nhà cung cấp ChatGPT · Claude · Grok (chủ dự án chốt: ba cái này chạy được mọi tính năng).
// Giới hạn này CHỈ ở giao diện, máy chủ không chặn.
//
// Key thô KHÔNG BAO GIỜ quay về trình duyệt: máy chủ chỉ trả bản che "xai-…abcd".

(function () {
  const { useState, useEffect } = React;
  const Icon = window.Icon;
  const PageHero = window.PageShell?.PageHero;
  const api = (url, opt) => window.tourkitAuth.authedFetch(url, opt);

  const NHA_CUNG_CAP = [
    { id: 'openai',    ten: 'ChatGPT', cua: 'OpenAI',    goiY: 'sk-…' },
    { id: 'anthropic', ten: 'Claude',  cua: 'Anthropic', goiY: 'sk-ant-…' },
    { id: 'grok',      ten: 'Grok',    cua: 'xAI',       goiY: 'xai-…' },
  ];
  const tenNcc = id => NHA_CUNG_CAP.find(n => n.id === id)?.ten || id;
  const luc = iso => iso ? new Date(iso).toLocaleString('vi-VN') : '—';

  function AiKeyPage({ pushToast }) {
    const [dangTai, setDangTai] = useState(true);
    const [cauHinh, setCauHinh] = useState(null);     // kết quả GET /api/v1/ai-key
    const [models, setModels] = useState({});         // providerId → [{id,label,recommended}]
    const [moForm, setMoForm] = useState(false);
    const [ncc, setNcc] = useState('grok');
    const [model, setModel] = useState('');
    const [key, setKey] = useState('');
    const [dangLuu, setDangLuu] = useState(false);
    const [loi, setLoi] = useState(null);

    async function tai() {
      setDangTai(true);
      try {
        const r = await api('/api/v1/ai-key');
        const j = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status);
        setCauHinh(j);
        if (j.configured) { setNcc(j.provider); setModel(j.model || ''); }
        setMoForm(!j.configured);
      } catch (e) { pushToast?.('Không tải được cấu hình: ' + e.message, 'error'); }
      finally { setDangTai(false); }
    }

    useEffect(() => {
      tai();
      // Danh sách model của từng nhà cung cấp — lỗi thì vẫn lưu được, model để trống = mặc định.
      api('/api/v1/providers').then(r => r.ok ? r.json() : []).then(ds => {
        const m = {};
        (ds || []).forEach(p => { m[p.id] = p.models || []; });
        setModels(m);
      }).catch(() => {});
    }, []);

    async function luu(e) {
      e?.preventDefault();
      setLoi(null);
      if (!key.trim()) { setLoi('Chưa nhập key'); return; }
      setDangLuu(true);
      try {
        const r = await api('/api/v1/ai-key', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ provider: ncc, model: model || null, apiKey: key.trim(), enabled: true }),
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok || !j.ok) { setLoi(j.error || 'Không lưu được (HTTP ' + r.status + ')'); return; }
        setKey('');
        pushToast?.('Đã kiểm và lưu key ' + tenNcc(ncc) + '. Mọi tính năng AI giờ chạy bằng key của công ty.', 'success');
        await tai();
      } catch (er) { setLoi('Không lưu được: ' + er.message); }
      finally { setDangLuu(false); }
    }

    async function batTat(bat) {
      const r = await api('/api/v1/ai-key/enabled', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: bat }),
      });
      if (!r.ok) { pushToast?.('Không đổi được trạng thái', 'error'); return; }
      pushToast?.(bat ? 'Đã bật dùng key riêng' : 'Đã tắt — quay về dùng lượt của hệ thống', 'success');
      await tai();
    }

    async function xoa() {
      const ok = await window.appConfirm(
        'Xoá key riêng? Mọi tính năng AI sẽ quay về dùng key của hệ thống và trừ lượt như trước.',
        { title: 'Xoá key AI riêng', confirmLabel: 'Xoá key', danger: true });
      if (!ok) return;
      const r = await api('/api/v1/ai-key', { method: 'DELETE' });
      if (!r.ok) { pushToast?.('Không xoá được', 'error'); return; }
      pushToast?.('Đã xoá key riêng', 'success');
      await tai();
    }

    if (dangTai) return <main className="page aik-page"><div className="aik-trong">Đang tải…</div></main>;

    if (cauHinh && cauHinh.featureOn === false) {
      return (
        <main className="page aik-page">
          {PageHero && <PageHero icon="user" title="Key AI riêng" sub="Dùng tài khoản AI của chính công ty bạn." />}
          <section className="aik-the aik-trong">
            Tính năng này chưa được bật trên hệ thống. Liên hệ bộ phận quản trị để mở.
          </section>
        </main>
      );
    }

    const daKhai = cauHinh?.configured;
    const dsModel = models[ncc] || [];

    return (
      <main className="page aik-page">
        {PageHero && <PageHero
          icon="user"
          title="Key AI riêng"
          sub="Khai key của chính công ty để mọi tính năng AI chạy bằng tài khoản của bạn — không trừ lượt."
          status={daKhai
            ? { label: cauHinh.enabled ? (cauHinh.failing ? 'KEY ĐANG LỖI' : 'ĐANG DÙNG KEY RIÊNG') : 'ĐÃ TẮT',
                detail: tenNcc(cauHinh.provider) + ' · ' + cauHinh.masked,
                tone: cauHinh.enabled && !cauHinh.failing ? 'live' : 'idle' }
            : { label: 'ĐANG DÙNG LƯỢT HỆ THỐNG', tone: 'idle' }}
        />}

        {/* Key riêng đang hỏng: lượt của công ty ĐANG BỊ TRỪ. Phải nói ra — lùi im lặng thì khách tưởng
            key vẫn chạy trong khi lượt trôi, tới lúc hết lượt mới biết. */}
        {daKhai && cauHinh.failing && (
          <section className="aik-canhbao" role="alert">
            <Icon name="warning" size={16} />
            <div>
              <b>Key riêng đang lỗi — hệ thống đang dùng key chung và trừ lượt của công ty.</b>
              <span>{cauHinh.lastFailReason || 'Không rõ lý do'} · lần gần nhất {luc(cauHinh.lastFailAtUtc)}
                {cauHinh.failCount > 1 ? ' · đã ' + cauHinh.failCount + ' lần' : ''}</span>
              <span>Nạp thêm tiền vào tài khoản {tenNcc(cauHinh.provider)} hoặc nhập key mới bên dưới.</span>
            </div>
          </section>
        )}

        {daKhai && (
          <section className="aik-the">
            <div className="aik-dong"><span>Nhà cung cấp</span><b>{tenNcc(cauHinh.provider)}</b></div>
            <div className="aik-dong"><span>Model</span><b>{cauHinh.model || 'Mặc định của nhà cung cấp'}</b></div>
            <div className="aik-dong"><span>Key</span><b className="aik-ma">{cauHinh.masked}</b></div>
            <div className="aik-dong"><span>Kiểm lần cuối</span><b>{luc(cauHinh.validatedAtUtc)}</b></div>
            <div className="aik-dong"><span>Cập nhật bởi</span><b>{cauHinh.updatedBy || '—'}</b></div>
            <div className="aik-nut-hang">
              <label className="aik-bat">
                <input type="checkbox" checked={!!cauHinh.enabled} onChange={e => batTat(e.target.checked)} />
                <span>Dùng key riêng cho mọi tính năng AI</span>
              </label>
              <span className="aik-gian" />
              {!moForm && <button className="aik-nut" onClick={() => setMoForm(true)}>Đổi key</button>}
              <button className="aik-nut aik-nguy" onClick={xoa}><Icon name="trash" size={13} /> Xoá key</button>
            </div>
          </section>
        )}

        {moForm && (
          <form className="aik-the" onSubmit={luu}>
            <h3 className="aik-tieude">{daKhai ? 'Đổi key' : 'Khai key AI của công ty'}</h3>

            <div className="aik-ncc" role="radiogroup" aria-label="Nhà cung cấp">
              {NHA_CUNG_CAP.map(n => (
                <button type="button" key={n.id} role="radio" aria-checked={ncc === n.id}
                        className={'aik-ncc-o' + (ncc === n.id ? ' on' : '')}
                        onClick={() => { setNcc(n.id); setModel(''); setLoi(null); }}>
                  <b>{n.ten}</b><span>{n.cua}</span>
                </button>
              ))}
            </div>

            <label className="aik-o">
              <span>Model</span>
              <select value={model} onChange={e => setModel(e.target.value)}>
                <option value="">Mặc định của {tenNcc(ncc)}</option>
                {dsModel.map(m => <option key={m.id} value={m.id}>{m.label || m.id}</option>)}
              </select>
            </label>

            <label className="aik-o">
              <span>API key</span>
              <input type="password" autoComplete="off" spellCheck={false} value={key}
                     placeholder={NHA_CUNG_CAP.find(n => n.id === ncc)?.goiY}
                     onChange={e => { setKey(e.target.value); setLoi(null); }} />
            </label>

            {loi && <div className="aik-loi" role="alert"><Icon name="warning" size={14} /> {loi}</div>}

            <p className="aik-ghichu">
              Bấm lưu là hệ thống gọi thử một lệnh bằng key này — chỉ lưu khi gọi được. Key được mã hoá
              trước khi lưu và không bao giờ hiện lại đầy đủ. Nếu sau này key hết tiền hoặc bị thu hồi,
              hệ thống tự chuyển sang key chung để không gián đoạn và <b>trừ lượt như thường</b>.
            </p>

            <div className="aik-nut-hang">
              <span className="aik-gian" />
              {daKhai && <button type="button" className="aik-nut"
                                 onClick={() => { setMoForm(false); setKey(''); setLoi(null); }}>Huỷ</button>}
              <button type="submit" className="aik-nut aik-chinh" disabled={dangLuu || !key.trim()}>
                <Icon name="check" size={13} /> {dangLuu ? 'Đang kiểm key…' : 'Kiểm tra & lưu'}
              </button>
            </div>
          </form>
        )}
      </main>
    );
  }

  window.AiKeyPage = AiKeyPage;
})();
