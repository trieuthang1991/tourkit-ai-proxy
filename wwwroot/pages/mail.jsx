// pages/mail.jsx — SmartMail AI ("Hộp thư AI"). Warm Operations Console.
// 3 cột: TRÁI filter rail, GIỮA list email, PHẢI reading pane + composer ghim.
// Hoàn thiện: đồng bộ incremental (BE), đọc/chưa đọc, soạn thư MỚI, chữ ký.

const { useState: _mS, useEffect: _mE, useRef: _mR } = React;

const _CAT_VI = {
  hoi_dat_tour: 'Hỏi đặt tour', xin_bao_gia: 'Xin báo giá', khieu_nai: 'Khiếu nại',
  xac_nhan: 'Xác nhận', spam: 'Spam', khac: 'Khác',
};
const _STATUS_VI = { moi: 'Mới', dang_xu_ly: 'Đang xử lý', da_phan_hoi: 'Đã phản hồi', da_dong: 'Đã đóng' };
const _STATUS_ORDER = ['moi', 'dang_xu_ly', 'da_phan_hoi', 'da_dong'];
const _CAT_ORDER = ['hoi_dat_tour', 'xin_bao_gia', 'khieu_nai', 'xac_nhan', 'spam', 'khac'];
const _TONES = [
  { key: 'lich_su', label: 'Lịch sự, trang trọng' },
  { key: 'than_thien', label: 'Thân thiện, cởi mở' },
  { key: 'dam_phan', label: 'Đàm phán thương lượng' },
  { key: 'xin_loi', label: 'Lời xin lỗi chuyên biệt' },
];

function _fmtWhen(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d)) return '';
  const now = new Date();
  const sameDay = d.toDateString() === now.toDateString();
  return sameDay
    ? d.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleDateString('vi-VN', { day: '2-digit', month: '2-digit' });
}
function _initials(name) {
  if (!name) return '✉';
  const w = String(name).trim().split(/\s+/);
  return (w.slice(-2).map(x => x[0] || '').join('') || name[0] || '?').toUpperCase();
}
// Avatar gradient hash theo tên → mỗi người một sắc, đáng nhớ.
function _hue(s) { let h = 0; for (let i = 0; i < (s || '').length; i++) h = (h * 31 + s.charCodeAt(i)) % 360; return h; }
function _avatarStyle(name) {
  const h = _hue(name || '?');
  return { background: `linear-gradient(140deg, hsl(${h} 58% 56%), hsl(${(h + 38) % 360} 64% 44%))` };
}

// AI config (provider/model) gửi kèm request soạn.
// v9: server đọc key từ appsettings — FE không gửi apiKey.
function _aiBody() {
  const c = (window.tourkit && window.tourkit.ai && window.tourkit.ai.getConfig) ? window.tourkit.ai.getConfig() : {};
  return { provider: c.provider, model: c.model };
}

// Đọc SSE soạn nháp; gọi onText với TOÀN BỘ text tích lũy. Trả text cuối.
// Dùng authedFetch để tự gắn X-Session-Id (per-tenant scope).
async function _streamDraft(url, body, onText) {
  const r = await window.tourkitAuth.authedFetch(url, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
    body: JSON.stringify(body),
  });
  if (!r.ok || !r.body) { const t = await r.text().catch(() => ''); throw new Error(t.slice(0, 200) || ('HTTP ' + r.status)); }
  const reader = r.body.getReader();
  const dec = new TextDecoder('utf-8');
  let buf = '', full = '';
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let i;
    while ((i = buf.indexOf('\n\n')) >= 0) {
      const evt = buf.slice(0, i); buf = buf.slice(i + 2);
      const line = evt.split('\n').find(l => l.startsWith('data:'));
      if (!line) continue;
      let o; try { o = JSON.parse(line.slice(5).trim()); } catch { continue; }
      if (o.error) throw new Error(o.error);
      if (o.delta) { full += o.delta; onText(full); }
      if (o.done && o.text) { full = o.text; onText(full); }
    }
  }
  return full;
}

// ─── HTML editor (TinyMCE) cho soạn mail ─────────────────────────────────────
function _looksHtml(s) { return /<[a-z][\s\S]*>/i.test(s || ''); }

function RichEditor({ value, onChange, minHeight }) {
  const ref = React.useRef(null);
  const edRef = React.useRef(null);
  React.useEffect(() => {
    if (!ref.current) return;
    let cancelled = false;
    const initial = _looksHtml(value) ? value : (value || '').replace(/\n/g, '<br>');
    // Lazy-load TinyMCE lần đầu mở RichEditor — index.html KHÔNG eager load nữa (tiết kiệm ~5MB).
    window.loadTinyMCE().then(() => {
      if (cancelled || !ref.current) return;
      window.tinymce.init({
        target: ref.current,
        license_key: 'gpl',
        menubar: false, statusbar: false, branding: false, promotion: false,
        plugins: 'lists link autolink',
        toolbar: 'bold italic underline | bullist numlist | link | removeformat',
        height: minHeight || 240,
        content_style: "body{font-family:'Be Vietnam Pro',system-ui,sans-serif;font-size:14px;line-height:1.6;color:#221c15;}",
        setup: (editor) => {
          edRef.current = editor;
          editor.on('init', () => editor.setContent(initial || ''));
          const sync = () => onChange && onChange(editor.getContent());
          editor.on('Change KeyUp ExecCommand Undo Redo blur', sync);
        },
      });
    }).catch((e) => console.error('[RichEditor] Load TinyMCE lỗi:', e));
    return () => {
      cancelled = true;
      try { if (window.tinymce && edRef.current) window.tinymce.remove(edRef.current); } catch {}
      edRef.current = null;
    };
  }, []);
  return <textarea ref={ref} className="mail-rich-target" />;
}

// Lúc AI stream → preview chữ chạy (tránh nhấp nháy editor); xong → editor HTML sửa/định dạng được.
function MailDraftEditor({ value, onChange, drafting, minHeight }) {
  if (drafting) return <div className="mail-draft-stream">{value || 'Đang soạn…'}</div>;
  return <RichEditor value={value} onChange={onChange} minHeight={minHeight} />;
}

// ─── Avatar ───────────────────────────────────────────────────────────────────
function Avatar({ name, lg }) {
  return <div className={'mail-avatar' + (lg ? ' lg' : '')} style={_avatarStyle(name)}>{_initials(name)}</div>;
}

// ─── Cấu hình hộp thư Gmail ──────────────────────────────────────────────────
function MailAccountForm({ account, onSaved, onDisconnected, pushToast }) {
  const [address, setAddress] = _mS(account?.address || '');
  const [appPassword, setAppPassword] = _mS('');
  const [signature, setSignature] = _mS(account?.signature || '');
  const [saving, setSaving] = _mS(false);
  const [disconnecting, setDisconnecting] = _mS(false);

  async function save() {
    if (!address.trim() || !appPassword.trim()) { pushToast('Nhập địa chỉ Gmail + App Password', 'error'); return; }
    setSaving(true);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/mail/account', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ address: address.trim(), appPassword: appPassword.trim(), signature: signature.trim() }),
      });
      const data = await r.json();
      if (!r.ok) { pushToast(data.error || 'Lưu cấu hình lỗi', 'error'); return; }
      setAppPassword('');
      pushToast('Đã lưu cấu hình hộp thư');
      onSaved(data);
    } catch (e) { pushToast('Lưu cấu hình lỗi: ' + e.message, 'error'); }
    finally { setSaving(false); }
  }

  async function disconnect(wipeMails) {
    const msg = wipeMails
      ? `NGẮT KẾT NỐI ${account?.address || 'hộp thư'} và XOÁ TOÀN BỘ email đã đồng bộ?\n\nThao tác KHÔNG hoàn tác được. Nhớ vào Google · App passwords để revoke App Password cũ cho an toàn.`
      : `Ngắt kết nối ${account?.address || 'hộp thư'}?\n\nApp Password sẽ bị xoá; lịch sử mail vẫn giữ. Đăng nhập lại sẽ thấy lại.`;
    const ok = await window.appConfirm(msg, {
      title: 'Ngắt kết nối Gmail',
      confirmLabel: wipeMails ? 'Ngắt + xoá lịch sử' : 'Ngắt kết nối',
      cancelLabel: 'Huỷ',
      danger: true,
    });
    if (!ok) return;

    setDisconnecting(true);
    try {
      const url = '/api/v1/mail/account' + (wipeMails ? '?wipeMails=true' : '');
      const r = await window.tourkitAuth.authedFetch(url, { method: 'DELETE' });
      const data = await r.json().catch(() => ({}));
      if (!r.ok) { pushToast(data.error || 'Ngắt kết nối lỗi', 'error'); return; }
      pushToast(wipeMails ? 'Đã ngắt kết nối + xoá lịch sử mail' : 'Đã ngắt kết nối hộp thư');
      setAddress(''); setAppPassword(''); setSignature('');
      onDisconnected?.();
    } catch (e) { pushToast('Ngắt kết nối lỗi: ' + e.message, 'error'); }
    finally { setDisconnecting(false); }
  }

  return (
    <div className="mail-config">
      <div className="mail-config-card">
        <div className="mail-config-badge"><Icon name="sparkle" size={24} /></div>
        <h2>Kết nối hộp thư Gmail</h2>
        <p className="mail-config-sub">
          Nhập địa chỉ Gmail công ty + <b>App Password</b> (16 ký tự). Cần bật <b>Xác minh 2 bước</b> &amp;
          bật <b>IMAP</b> trong Gmail. App Password lưu mã hóa trên máy chủ, không hiển thị lại.
        </p>
        <label className="mail-field-label">Địa chỉ Gmail</label>
        <input className="mail-field" placeholder="booking@congty.com"
          value={address} onChange={e => setAddress(e.target.value)} />
        <label className="mail-field-label">App Password</label>
        <input className="mail-field" type="password" placeholder="Nhập thông tin app pass email của bạn vào."
          value={appPassword} onChange={e => setAppPassword(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') save(); }} />
        <label className="mail-field-label">Chữ ký cuối email <span className="mail-opt">(tên công ty bạn)</span></label>
        <input className="mail-field" placeholder="VD: Công ty Du lịch ABC · Hotline 1900 xxxx"
          value={signature} onChange={e => setSignature(e.target.value)} />
        <p className="mail-field-hint">AI sẽ ký đúng tên này ở cuối mỗi email gửi khách. Để trống thì email chỉ ký "Trân trọng," — không tự thêm tên công ty.</p>
        <button className="mail-btn primary block" disabled={saving} onClick={save}>
          {saving ? 'Đang lưu…' : 'Lưu & kết nối'}
        </button>
        <a className="mail-config-link" href="https://myaccount.google.com/apppasswords" target="_blank" rel="noopener noreferrer">
          Tạo App Password ↗
        </a>
        {account?.configured && (
          <div className="mail-config-danger">
            <div className="mail-config-danger-head">Ngắt kết nối hộp thư</div>
            <p className="mail-config-danger-sub">
              Xoá App Password đang lưu cho <b>{account.address}</b>. Sau đó cần nhập lại để dùng tiếp.
              Nhớ vào <a href="https://myaccount.google.com/apppasswords" target="_blank" rel="noopener noreferrer">Google · App passwords</a> để revoke luôn cho an toàn.
            </p>
            <button className="mail-btn danger block" disabled={disconnecting} onClick={() => disconnect(false)}>
              {disconnecting ? 'Đang ngắt…' : 'Ngắt kết nối (giữ lịch sử)'}
            </button>
            <button className="mail-btn ghost block" disabled={disconnecting} onClick={() => disconnect(true)}
              style={{ marginTop: 8, color: '#a4321a', borderColor: '#f3c2b6' }}>
              Ngắt kết nối + xoá toàn bộ lịch sử mail
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Soạn thư MỚI cùng AI (modal) ────────────────────────────────────────────
function ComposeNewModal({ onClose, pushToast }) {
  const [to, setTo] = _mS('');
  const [subject, setSubject] = _mS('');
  const [brief, setBrief] = _mS('');
  const [tone, setTone] = _mS('lich_su');
  const [body, setBody] = _mS('');
  const [drafting, setDrafting] = _mS(false);
  const [sending, setSending] = _mS(false);

  async function draft() {
    if (!brief.trim()) { pushToast('Nhập ý chính cần viết', 'error'); return; }
    setDrafting(true); setBody('');
    try {
      await _streamDraft('/api/v1/mail/compose/draft',
        { to: to.trim(), subject: subject.trim(), brief: brief.trim(), tone, ..._aiBody() },
        t => setBody(t));
    } catch (e) { pushToast('Soạn lỗi: ' + e.message, 'error'); }
    finally { setDrafting(false); }
  }

  async function send() {
    if (!to.trim()) { pushToast('Nhập người nhận', 'error'); return; }
    if (!body.trim()) { pushToast('Chưa có nội dung — soạn trước đã', 'error'); return; }
    if (!(await window.appConfirm(`Gửi email tới ${to.trim()}?`, { title: 'Gửi email', confirmLabel: 'Gửi' }))) return;
    setSending(true);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/mail/compose/send', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ to: to.trim(), subject: subject.trim() || '(không tiêu đề)', text: body }),
      });
      const data = await r.json();
      if (!r.ok) { pushToast(data.error || 'Gửi lỗi', 'error'); return; }
      pushToast('Đã gửi email ✓');
      onClose();
    } catch (e) { pushToast('Gửi lỗi: ' + e.message, 'error'); }
    finally { setSending(false); }
  }

  return (
    <div className="mail-modal-backdrop" onClick={onClose}>
      <div className="mail-modal" onClick={e => e.stopPropagation()}>
        <div className="mail-modal-head">
          <h3><Icon name="sparkle" size={16} /> Soạn thư mới cùng AI</h3>
          <button className="mail-icon-btn" onClick={onClose}>✕</button>
        </div>
        <div className="mail-modal-body">
          <div className="mail-compose-row">
            <input className="mail-field" placeholder="Người nhận: khach@email.com" value={to} onChange={e => setTo(e.target.value)} />
          </div>
          <div className="mail-compose-row">
            <input className="mail-field" placeholder="Tiêu đề (tuỳ chọn)" value={subject} onChange={e => setSubject(e.target.value)} />
          </div>
          <textarea className="mail-field" rows={3} placeholder="Ý chính cần viết: VD báo giá tour Phú Quốc 4N3Đ, ưu đãi đặt sớm…"
            value={brief} onChange={e => setBrief(e.target.value)} />
          <div className="mail-tones">
            {_TONES.map(t => (
              <button key={t.key} className={'mail-tone' + (tone === t.key ? ' on' : '')} onClick={() => setTone(t.key)}>{t.label}</button>
            ))}
          </div>
          <button className="mail-btn primary block" onClick={draft} disabled={drafting}>
            <Icon name="sparkle" size={14} /> {drafting ? 'Đang soạn…' : 'Soạn nội dung cùng AI'}
          </button>
          {(body || drafting) && (
            <div className="mail-draft">
              <MailDraftEditor value={body} onChange={setBody} drafting={drafting} minHeight={220} />
            </div>
          )}
        </div>
        <div className="mail-modal-foot">
          <button className="mail-btn ghost" onClick={onClose}>Huỷ</button>
          <button className="mail-btn primary" onClick={send} disabled={sending || !body.trim()}>
            <Icon name="paper" size={13} /> {sending ? 'Đang gửi…' : 'Gửi đi'}
          </button>
        </div>
      </div>
    </div>
  );
}

function EmptyState({ icon, title, hint }) {
  return (
    <div className="mail-empty">
      <div className="mail-empty-icon"><Icon name={icon} size={24} /></div>
      <p className="mail-empty-title">{title}</p>
      {hint && <p className="mail-empty-hint">{hint}</p>}
    </div>
  );
}
function ListSkeleton() {
  return (
    <div className="mail-skel-wrap">
      {[0, 1, 2, 3, 4].map(i => (
        <div key={i} className="mail-skel-row">
          <div className="mail-skel-av" />
          <div className="mail-skel-lines"><div className="mail-skel-l w70" /><div className="mail-skel-l w90" /><div className="mail-skel-l w40" /></div>
        </div>
      ))}
    </div>
  );
}

function MailPage({ pushToast }) {
  const [account, setAccount] = _mS(null);
  const [showConfig, setShowConfig] = _mS(false);
  const [showCompose, setShowCompose] = _mS(false);

  const [items, setItems] = _mS([]);
  const [counts, setCounts] = _mS({ total: 0, unread: 0, byStatus: {}, byCategory: {} });
  const [selId, setSelId] = _mS(null);
  const [fStatus, setFStatus] = _mS(null);
  const [fCategory, setFCategory] = _mS(null);
  const [search, setSearch] = _mS('');
  const [syncing, setSyncing] = _mS(false);
  const [syncProgress, setSyncProgress] = _mS(null); // {current, total, subject} | null
  const [loading, setLoading] = _mS(true);
  const [readerBig, setReaderBig] = _mS(false); // mở rộng vùng đọc: ẩn composer cho text dài

  const [tone, setTone] = _mS('lich_su');
  const [instruction, setInstruction] = _mS('');
  const [draft, setDraft] = _mS('');
  const [drafting, setDrafting] = _mS(false);
  const [sending, setSending] = _mS(false);

  const sel = items.find(m => m.id === selId) || null;

  _mE(() => {
    window.tourkitAuth.authedFetch('/api/v1/mail/account').then(r => r.json()).then(a => {
      setAccount(a);
      if (!a.configured) setShowConfig(true);
    }).catch(() => setAccount({ configured: false }));
  }, []);

  function applyData(data) {
    setItems(data.items || []);
    if (data.counts) setCounts(data.counts);
  }

  async function load() {
    setLoading(true);
    try {
      const qs = new URLSearchParams();
      if (fStatus) qs.set('status', fStatus);
      if (fCategory) qs.set('category', fCategory);
      if (search.trim()) qs.set('search', search.trim());
      const r = await window.tourkitAuth.authedFetch('/api/v1/mail?' + qs.toString());
      const data = await r.json();
      if (r.ok) applyData(data); else pushToast(data.error || 'Lỗi tải hộp thư', 'error');
    } catch (e) { pushToast('Lỗi tải hộp thư: ' + e.message, 'error'); }
    finally { setLoading(false); }
  }

  _mE(() => { load(); }, [fStatus, fCategory]);

  // Sync qua SSE — hiện progress thay vì spinner mơ hồ.
  // Backend stream: {stage:"fetching"} → {stage:"fetched", toClassify} →
  // {stage:"classifying", current, total, subject} (lặp) → {stage:"done", items, counts, classified}
  async function sync() {
    setSyncing(true);
    setSyncProgress({ stage: 'fetching', message: 'Đang kết nối Gmail...' });
    try {
      // Lần đầu sync (chưa có item) → kéo 200; subsequent → default 100
      const max = items.length === 0 ? 200 : 100;
      const r = await window.tourkitAuth.authedFetch(`/api/v1/mail/sync/stream?max=${max}`, { method: 'POST' });
      if (!r.ok) {
        const data = await r.json().catch(() => ({}));
        pushToast(data.error || `Đồng bộ lỗi (${r.status})`, 'error');
        if (r.status === 400) setShowConfig(true);
        return;
      }
      const reader = r.body.getReader();
      const decoder = new TextDecoder();
      let buf = '';
      let finalData = null;
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        let idx;
        while ((idx = buf.indexOf('\n\n')) >= 0) {
          const ev = buf.slice(0, idx).trim();
          buf = buf.slice(idx + 2);
          if (!ev.startsWith('data:')) continue;
          try {
            const obj = JSON.parse(ev.slice(5).trim());
            setSyncProgress(obj);
            if (obj.stage === 'done') finalData = obj;
            if (obj.stage === 'error') {
              pushToast(obj.message || 'Đồng bộ lỗi', 'error');
              return;
            }
          } catch {}
        }
      }
      if (finalData) {
        applyData(finalData);
        pushToast(
          finalData.classified > 0
            ? `Đã đồng bộ · ${finalData.classified} email mới (kéo ${finalData.fetched})`
            : `Đã đồng bộ · không có email mới (kéo ${finalData.fetched})`
        );
      }
    } catch (e) { pushToast('Đồng bộ lỗi: ' + e.message, 'error'); }
    finally { setSyncing(false); setSyncProgress(null); }
  }

  function selectMail(id) {
    setSelId(id);
    const m = items.find(x => x.id === id);
    setDraft(m && m.draft ? m.draft.text : '');
    setTone(m && m.draft ? m.draft.tone : 'lich_su');
    setInstruction(m && m.draft ? (m.draft.instruction || '') : '');
    // Đánh dấu đã đọc (local + server) nếu chưa đọc.
    if (m && !m.isRead) {
      setItems(prev => prev.map(x => x.id === id ? { ...x, isRead: true } : x));
      setCounts(c => ({ ...c, unread: Math.max(0, (c.unread || 0) - 1) }));
      window.tourkitAuth.authedFetch(`/api/v1/mail/${encodeURIComponent(id)}/read`, { method: 'POST' }).catch(() => {});
    }
  }

  async function setStatus(id, status) {
    try {
      const r = await window.tourkitAuth.authedFetch(`/api/v1/mail/${encodeURIComponent(id)}/status`, {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ status }),
      });
      if (r.ok) { setItems(prev => prev.map(m => m.id === id ? { ...m, status } : m)); load(); }
      else pushToast('Đổi trạng thái lỗi', 'error');
    } catch (e) { pushToast(e.message, 'error'); }
  }

  async function composeDraft() {
    if (!sel || drafting) return;
    setDrafting(true); setDraft('');
    try {
      await _streamDraft(`/api/v1/mail/${encodeURIComponent(sel.id)}/reply/draft`,
        { tone, instruction, ..._aiBody() }, t => setDraft(t));
      setItems(prev => prev.map(m => m.id === sel.id ? { ...m, status: 'dang_xu_ly' } : m));
    } catch (e) { pushToast('Soạn nháp lỗi: ' + e.message, 'error'); }
    finally { setDrafting(false); }
  }

  async function sendReply() {
    if (!sel || !draft.trim() || sending) return;
    if (!(await window.appConfirm(`Gửi email trả lời tới ${sel.from?.email || 'khách'}?`, { title: 'Gửi trả lời', confirmLabel: 'Gửi' }))) return;
    setSending(true);
    try {
      const r = await window.tourkitAuth.authedFetch(`/api/v1/mail/${encodeURIComponent(sel.id)}/reply/send`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: draft, tone, instruction }),
      });
      const data = await r.json();
      if (!r.ok) { pushToast(data.error || 'Gửi email lỗi', 'error'); return; }
      setItems(prev => prev.map(m => m.id === sel.id ? { ...m, status: 'da_phan_hoi' } : m));
      pushToast('Đã gửi email cho khách ✓');
    } catch (e) { pushToast('Gửi email lỗi: ' + e.message, 'error'); }
    finally { setSending(false); }
  }

  const cStatus = (k) => counts.byStatus?.[k] || 0;
  const cCat = (k) => counts.byCategory?.[k] || 0;

  if (showConfig) {
    return (
      <main className="page mail">
        <window.PageShell.PageHero
          icon="mail"
          title="Hộp thư SmartMail AI"
          badge="Gmail"
          sub="Kết nối hộp thư Gmail để bắt đầu phân loại + soạn phản hồi bằng AI."
          actions={account?.configured && <button className="mail-btn ghost" onClick={() => setShowConfig(false)}>← Quay lại hộp thư</button>}
        />
        <MailAccountForm account={account} pushToast={pushToast}
          onSaved={(a) => { setAccount(a); setShowConfig(false); load(); }}
          onDisconnected={() => {
            // Reset toàn bộ state UI về "chưa cấu hình" — đứng nguyên màn config.
            setAccount({ configured: false, address: '', signature: '' });
            setItems([]); setSelId(null);
            setCounts({ total: 0, unread: 0, byStatus: {}, byCategory: {} });
          }} />
      </main>
    );
  }

  return (
    <main className="page mail">
      {showCompose && <ComposeNewModal pushToast={pushToast} onClose={() => setShowCompose(false)} />}

      <window.PageShell.PageHero
        icon="mail"
        title="Hộp thư SmartMail AI"
        badge={counts.unread > 0 ? `${counts.unread} chưa đọc` : 'đã đồng bộ'}
        sub="Đồng bộ Gmail · AI phân loại 6 nhóm · soạn & gửi phản hồi 4 tông."
        status={{ label: account?.configured ? 'GMAIL ĐÃ KẾT NỐI' : 'CHƯA CẤU HÌNH',
          detail: account?.address || 'Chọn cấu hình →',
          tone: account?.configured ? 'live' : 'idle' }}
        actions={<>
          <button className="mail-btn ghost" onClick={() => setShowConfig(true)} title="Cấu hình hộp thư">
            <span className="mail-acct-dot" /> Cấu hình
          </button>
          <button className="mail-btn outline" onClick={() => setShowCompose(true)}>
            <Icon name="sparkle" size={14} /> Soạn thư mới
          </button>
          <button className="mail-btn primary" onClick={sync} disabled={syncing}>
            <Icon name="paper" size={14} /> {syncing ? 'Đang đồng bộ…' : 'Đồng bộ'}
          </button>
        </>}
      />

      {/* Progress bar khi đang sync — hiện stage hiện tại + % */}
      {syncing && syncProgress && (
        <div className="mail-sync-progress">
          {(() => {
            const s = syncProgress;
            if (s.stage === 'fetching') return <div className="msp-text">⟳ Đang kéo email từ Gmail...</div>;
            if (s.stage === 'fetched') return <div className="msp-text">✓ Kéo về {s.fetched} email · sẽ phân loại {s.toClassify} email mới</div>;
            if (s.stage === 'classifying') {
              const pct = s.total > 0 ? Math.round((s.current / s.total) * 100) : 0;
              return (<>
                <div className="msp-bar"><div className="msp-bar-fill" style={{width: pct + '%'}} /></div>
                <div className="msp-text">Đang phân loại {s.current}/{s.total} · {s.subject || ''}</div>
              </>);
            }
            if (s.stage === 'done') return <div className="msp-text">✓ Hoàn tất</div>;
            return null;
          })()}
        </div>
      )}

      <div className="mail-grid">
        {/* TRÁI: filter rail */}
        <aside className="mail-rail">
          <div className="mail-rail-group">
            <div className="mail-rail-label">Trạng thái</div>
            <button className={'mail-rail-item' + (!fStatus ? ' on' : '')} onClick={() => setFStatus(null)}>
              <span>Tất cả</span><b>{counts.total}</b>
            </button>
            {_STATUS_ORDER.map(k => (
              <button key={k} className={'mail-rail-item' + (fStatus === k ? ' on' : '')} onClick={() => setFStatus(k)}>
                <span><i className={'mail-stdot st-' + k} /> {_STATUS_VI[k]}</span><b>{cStatus(k)}</b>
              </button>
            ))}
          </div>
          <div className="mail-rail-group">
            <div className="mail-rail-label">Phân loại AI</div>
            <button className={'mail-rail-item' + (!fCategory ? ' on' : '')} onClick={() => setFCategory(null)}>
              <span>Tất cả loại</span><b>{counts.total}</b>
            </button>
            {_CAT_ORDER.map(k => (
              <button key={k} className={'mail-rail-item' + (fCategory === k ? ' on' : '')} onClick={() => setFCategory(k)}>
                <span><i className={'mail-catdot cat-' + k} /> {_CAT_VI[k]}</span><b>{cCat(k)}</b>
              </button>
            ))}
          </div>
        </aside>

        {/* GIỮA: list */}
        <section className="mail-list">
          <div className="mail-search">
            <input className="mail-search-input" placeholder="Tìm theo tên, email, tiêu đề…" value={search}
              onChange={e => setSearch(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') load(); }} />
          </div>
          <div className="mail-list-scroll">
            {loading ? <ListSkeleton /> : items.length === 0 ? (
              <EmptyState icon="paper" title="Hòm thư trống" hint="Bấm “Đồng bộ” để kéo email từ Gmail." />
            ) : items.map((m, idx) => (
              <button key={m.id} style={{ animationDelay: (idx * 28) + 'ms' }}
                className={'mail-row' + (selId === m.id ? ' on' : '') + (m.isRead ? '' : ' unread')}
                onClick={() => selectMail(m.id)}>
                <Avatar name={m.from?.name || m.from?.email} />
                <div className="mail-row-body">
                  <div className="mail-row-top">
                    <span className="mail-from">{!m.isRead && <i className="mail-unread-dot" />}{m.from?.name || m.from?.email}</span>
                    <span className="mail-when">{_fmtWhen(m.receivedAt)}</span>
                  </div>
                  <div className="mail-subject">{m.subject}</div>
                  <div className="mail-row-meta">
                    {m.category && <span className="mail-cat-chip"><i className={'mail-catdot cat-' + m.category} /> {_CAT_VI[m.category]}</span>}
                    <span className={'mail-st-pill st-' + m.status}>{_STATUS_VI[m.status]}</span>
                  </div>
                </div>
              </button>
            ))}
          </div>
        </section>

        {/* PHẢI: reading pane + composer ghim */}
        <section className={'mail-pane' + (readerBig ? ' is-reader-big' : '')}>
          {!sel ? (
            <EmptyState icon="sparkle" title="Chọn một email để đọc & trả lời"
              hint="AI đã phân loại sẵn — chọn ngữ điệu rồi để AI soạn giúp." />
          ) : (
            <>
              <div className="mail-pane-scroll">
                <div className="mail-pane-head">
                  <Avatar name={sel.from?.name || sel.from?.email} lg />
                  <div className="mail-pane-sender">
                    <div className="mail-from lg">{sel.from?.name}</div>
                    <div className="mail-email">{sel.from?.email}</div>
                  </div>
                  <div className="mail-pane-meta">
                    <span className="mail-when">{_fmtWhen(sel.receivedAt)}</span>
                    <button type="button" className="mail-reader-toggle" onClick={() => setReaderBig(v => !v)}
                      title={readerBig ? 'Thu gọn — hiện lại khung soạn trả lời' : 'Mở rộng vùng đọc — ẩn khung soạn để đọc text dài'}>
                      {readerBig ? '⤡ Thu gọn' : '⤢ Mở rộng đọc'}
                    </button>
                    <select className="mail-status-sel" value={sel.status} onChange={e => setStatus(sel.id, e.target.value)}>
                      {_STATUS_ORDER.map(k => <option key={k} value={k}>{_STATUS_VI[k]}</option>)}
                    </select>
                  </div>
                </div>
                <h2 className="mail-pane-subject">{sel.subject}</h2>
                {sel.category && <span className="mail-cat-chip lg"><i className={'mail-catdot cat-' + sel.category} /> {_CAT_VI[sel.category]}</span>}
                {sel.aiSummary && <div className="mail-summary"><span className="mail-summary-tag">Tóm tắt AI</span> {sel.aiSummary}</div>}
                {sel.bodyHtml ? (
                  <iframe className="mail-html-frame" title="Nội dung email" sandbox="allow-same-origin"
                    srcDoc={sel.bodyHtml}
                    onLoad={e => { try { const d = e.target.contentDocument; if (d && d.body) e.target.style.height = (d.body.scrollHeight + 24) + 'px'; } catch (_) {} }} />
                ) : (
                  <div className="mail-pane-body">{sel.body || '(không có nội dung)'}</div>
                )}
              </div>

              <div className="mail-composer">
                <div className="mail-composer-title"><Icon name="sparkle" size={14} /> Soạn trả lời bằng AI</div>
                <div className="mail-guide">Chọn ngữ điệu → bấm <b>Soạn</b> → sửa &amp; định dạng → <b>Gửi cho khách</b>.</div>
                <div className="mail-tones">
                  {_TONES.map(t => (
                    <button key={t.key} className={'mail-tone' + (tone === t.key ? ' on' : '')} onClick={() => setTone(t.key)}>{t.label}</button>
                  ))}
                </div>
                <div className="mail-compose-row">
                  <input className="mail-field" value={instruction} onChange={e => setInstruction(e.target.value)}
                    onKeyDown={e => { if (e.key === 'Enter') composeDraft(); }}
                    placeholder="Chỉ thị thêm: giảm 5%, tặng tour đảo, hẹn chiều mai…" />
                  <button className="mail-btn primary" onClick={composeDraft} disabled={drafting}>
                    <Icon name="sparkle" size={14} /> {drafting ? 'Đang soạn…' : 'Soạn'}
                  </button>
                </div>
                {(draft || drafting) && (
                  <div className="mail-draft">
                    <MailDraftEditor value={draft} onChange={setDraft} drafting={drafting} minHeight={180} />
                    <div className="mail-draft-actions">
                      <button className="mail-btn primary sm" onClick={sendReply} disabled={sending || !draft.trim()}>
                        <Icon name="paper" size={13} /> {sending ? 'Đang gửi…' : 'Gửi cho khách'}
                      </button>
                      <button className="mail-btn ghost sm" onClick={() => { navigator.clipboard?.writeText(draft); pushToast('Đã copy nháp'); }}>Copy</button>
                    </div>
                  </div>
                )}
              </div>
            </>
          )}
        </section>
      </div>
    </main>
  );
}

window.MailPage = MailPage;
