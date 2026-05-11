// Reusable dialogs (replace native prompt/confirm/alert)
// All use the existing .modal-backdrop / .modal styles for consistency
const { useState: uD, useEffect: uED, useRef: uRD } = React;

function Dialog({ open, onClose, title, eyebrow, icon = 'sparkle', children, footer, maxWidth = 480 }) {
  uED(() => {
    if (!open) return;
    const h = e => { if (e.key === 'Escape') onClose && onClose(); };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [open, onClose]);

  if (!open) return null;
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="dialog" onClick={e => e.stopPropagation()} style={{maxWidth}}>
        <div className="dialog-head">
          <div className="dialog-head-icon"><Icon name={icon} size={16} /></div>
          <div style={{flex: 1, minWidth: 0}}>
            {eyebrow && <div className="dialog-eyebrow">{eyebrow}</div>}
            <h3 className="dialog-title">{title}</h3>
          </div>
          <button className="icon-btn" onClick={onClose} aria-label="Đóng"><Icon name="close" size={18} /></button>
        </div>
        <div className="dialog-body">{children}</div>
        {footer && <div className="dialog-foot">{footer}</div>}
      </div>
    </div>
  );
}

// Prompt dialog with AI suggestion support
function PromptDialog({ open, title, eyebrow, placeholder, initialValue = '', onClose, onSubmit,
                       aiSuggest, suggestContext }) {
  const [val, setVal] = uD(initialValue);
  const [suggestions, setSuggestions] = uD([]);
  const [loadingSug, setLoadingSug] = uD(false);
  const inputRef = uRD(null);

  uED(() => {
    if (open) {
      setVal(initialValue);
      setSuggestions([]);
      setTimeout(() => inputRef.current?.focus(), 60);
    }
  }, [open]);

  const fetchSuggestions = async () => {
    if (!aiSuggest) return;
    setLoadingSug(true);
    try {
      const raw = await window.claude.complete(aiSuggest(suggestContext));
      const m = raw.match(/\[[\s\S]*\]/);
      if (m) setSuggestions(JSON.parse(m[0]).slice(0, 6));
    } catch (e) {
      setSuggestions(['Spa & Massage', 'Phố đi bộ', 'Lễ hội đặc sản', 'Cà phê view biển']);
    } finally {
      setLoadingSug(false);
    }
  };

  const submit = () => {
    const v = val.trim();
    if (!v) return;
    onSubmit(v);
    onClose();
  };

  return (
    <Dialog open={open} onClose={onClose} title={title} eyebrow={eyebrow} icon="sparkle"
      footer={
        <>
          <button className="btn btn-outline" onClick={onClose}>Hủy</button>
          <button className="btn btn-primary" disabled={!val.trim()} onClick={submit}>
            <Icon name="check" size={14} stroke={2.5} /> Thêm vào danh sách
          </button>
        </>
      }>
      <input ref={inputRef} className="input" placeholder={placeholder}
        value={val} onChange={e => setVal(e.target.value)}
        onKeyDown={e => { if (e.key === 'Enter') submit(); }} />

      {aiSuggest && (
        <div style={{marginTop: 16}}>
          <button className="btn btn-ghost btn-sm" onClick={fetchSuggestions} disabled={loadingSug}>
            <Icon name="sparkle" size={13} />
            {loadingSug ? 'Đang nghĩ...' : 'AI gợi ý 5 yêu cầu phù hợp'}
          </button>
          {suggestions.length > 0 && (
            <div className="chips" style={{marginTop: 12}}>
              {suggestions.map(s => (
                <button key={s} className="chip" onClick={() => { onSubmit(s); }}>
                  <Icon name="plus" size={11} /> {s}
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </Dialog>
  );
}

// Confirm dialog
function ConfirmDialog({ open, title, eyebrow = 'XÁC NHẬN', message, confirmLabel = 'Đồng ý',
                        cancelLabel = 'Hủy', danger, onClose, onConfirm }) {
  return (
    <Dialog open={open} onClose={onClose} title={title} eyebrow={eyebrow}
      icon={danger ? 'trash' : 'sparkle'}
      footer={
        <>
          <button className="btn btn-outline" onClick={onClose}>{cancelLabel}</button>
          <button className={danger ? 'btn btn-danger' : 'btn btn-primary'}
            onClick={() => { onConfirm(); onClose(); }}>{confirmLabel}</button>
        </>
      }>
      <p style={{margin: 0, fontSize: 14, lineHeight: 1.55, color: 'var(--text-2)'}}>{message}</p>
    </Dialog>
  );
}

// Share dialog with QR + copy link + channel buttons
function ShareDialog({ open, onClose, link, title, summary, onSent }) {
  const [copied, setCopied] = uD(false);
  // Default message tĩnh — không call AI cho đến khi user bấm "Viết bằng AI".
  const defaultMsg = `📋 Kính gửi Anh/Chị,\n\nTourkit xin gửi đề xuất chương trình ${title}.\n${summary}\n\nXem chi tiết báo giá: ${link}\n\nMọi thông tin chi tiết xin liên hệ trực tiếp với tư vấn viên. Trân trọng.`;
  const [aiMsg, setAiMsg] = uD(defaultMsg);
  const [loading, setLoading] = uD(false);
  const [aiPristine, setAiPristine] = uD(true);

  uED(() => {
    if (!open) return;
    setCopied(false);
    setAiMsg(`📋 Kính gửi Anh/Chị,\n\nTourkit xin gửi đề xuất chương trình ${title}.\n${summary}\n\nXem chi tiết báo giá: ${link}\n\nMọi thông tin chi tiết xin liên hệ trực tiếp với tư vấn viên. Trân trọng.`);
    setAiPristine(true);
  }, [open, title, summary, link]);

  const runAiMsg = async () => {
    setLoading(true);
    try {
      const raw = await window.claude.complete(`Viết tin nhắn Zalo chuyên nghiệp gửi báo giá tour cho khách doanh nghiệp. Ngắn gọn 4-5 dòng, có emoji nhẹ. Không dùng "tuyệt vời/đáng nhớ".

Tour: ${title}
${summary}
Link xem báo giá: ${link}

Output: text thuần, KHÔNG markdown, KHÔNG dấu ngoặc.`);
      setAiMsg(raw.trim().slice(0, 600));
      setAiPristine(false);
    } catch (e) {
      // giữ default message
    } finally {
      setLoading(false);
    }
  };

  const copyLink = async () => {
    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
      setTimeout(() => setCopied(false), 2500);
    } catch (e) {}
  };

  const copyMsg = async () => {
    try {
      await navigator.clipboard.writeText(aiMsg);
      window.Tourkit_toast && window.Tourkit_toast('Đã copy nội dung tin nhắn');
    } catch (e) {}
  };

  const sendVia = (channel) => {
    let url;
    if (channel === 'zalo') url = `https://zalo.me/share?u=${encodeURIComponent(link)}`;
    else if (channel === 'mail') url = `mailto:?subject=${encodeURIComponent('Báo giá ' + title)}&body=${encodeURIComponent(aiMsg)}`;
    else if (channel === 'sms') url = `sms:?body=${encodeURIComponent(aiMsg)}`;
    window.open(url, '_blank', 'noopener');
    onSent && onSent(channel);
  };

  const qrSrc = `https://api.qrserver.com/v1/create-qr-code/?size=180x180&margin=4&data=${encodeURIComponent(link)}`;

  return (
    <Dialog open={open} onClose={onClose} title="Gửi báo giá cho khách"
      eyebrow="CHIA SẺ · QUOTATION" icon="share" maxWidth={560}>
      <div style={{display: 'grid', gridTemplateColumns: '180px 1fr', gap: 20, alignItems: 'start'}}>
        <div style={{padding: 8, border: '1px solid var(--border)', borderRadius: 10, background: 'white'}}>
          <img src={qrSrc} alt="QR báo giá" width="164" height="164" style={{display: 'block', borderRadius: 6}} />
          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textAlign: 'center', letterSpacing: '0.08em', marginTop: 6}}>QUÉT QR XEM BÁO GIÁ</div>
        </div>

        <div>
          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginBottom: 6}}>LINK BÁO GIÁ</div>
          <div className="link-row">
            <span style={{flex: 1, fontSize: 12, fontFamily: 'monospace', color: 'var(--text-2)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap'}}>{link}</span>
            <button className="btn btn-outline btn-sm" onClick={copyLink}>
              <Icon name={copied ? 'check' : 'copy'} size={12} />
              {copied ? 'Đã copy' : 'Copy'}
            </button>
          </div>

          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginTop: 16, marginBottom: 6, display: 'flex', alignItems: 'center', gap: 8}}>
            <Icon name="sparkle" size={11} /> NỘI DUNG AI GỢI Ý
          </div>
          {loading ? (
            <div className="dialog-msg-box">
              <div className="sk" style={{height: 10, marginBottom: 6, width: '90%'}} />
              <div className="sk" style={{height: 10, marginBottom: 6, width: '80%'}} />
              <div className="sk" style={{height: 10, width: '60%'}} />
            </div>
          ) : (
            <textarea className="dialog-msg-box" rows={5}
              value={aiMsg} onChange={e => { setAiMsg(e.target.value); setAiPristine(false); }} />
          )}
          <div style={{display: 'flex', gap: 8, marginTop: 6}}>
            <button className="btn btn-ghost btn-sm" onClick={copyMsg}>
              <Icon name="copy" size={12} /> Copy nội dung
            </button>
            <button className="btn btn-outline btn-sm" onClick={runAiMsg} disabled={loading}>
              <Icon name="sparkle" size={12} /> {aiPristine ? 'Viết bằng AI' : 'Viết lại bằng AI'}
            </button>
          </div>
        </div>
      </div>

      <div style={{marginTop: 22, paddingTop: 18, borderTop: '1px solid var(--border)'}}>
        <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginBottom: 10}}>GỬI QUA KÊNH</div>
        <div style={{display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 10}}>
          <button className="channel-btn zalo" onClick={() => sendVia('zalo')}>
            <Icon name="share" size={16} /> Zalo
          </button>
          <button className="channel-btn mail" onClick={() => sendVia('mail')}>
            <Icon name="mail" size={16} /> Email
          </button>
          <button className="channel-btn sms" onClick={() => sendVia('sms')}>
            <Icon name="phone" size={16} /> SMS
          </button>
        </div>
      </div>
    </Dialog>
  );
}

window.Dialog = Dialog;
window.PromptDialog = PromptDialog;
window.ConfirmDialog = ConfirmDialog;
window.ShareDialog = ShareDialog;
