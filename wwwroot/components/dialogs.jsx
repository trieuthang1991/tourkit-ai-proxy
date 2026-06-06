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

// Share dialog with QR + copy link + customer details + channel buttons
function ShareDialog({ open, onClose, link, title, summary, onSent,
                      request, marketing, totalSale, perPax }) {
  const [copied, setCopied] = uD(false);
  // Default message tĩnh — không call AI cho đến khi user bấm "Viết bằng AI".
  const defaultMsg = `📋 Kính gửi Anh/Chị,\n\nTourkit xin gửi đề xuất chương trình ${title}.\n${summary}\n\nXem chi tiết báo giá: ${link}\n\nMọi thông tin chi tiết xin liên hệ trực tiếp với tư vấn viên. Trân trọng.`;
  const [aiMsg, setAiMsg] = uD(defaultMsg);
  const [loading, setLoading] = uD(false);
  const [aiPristine, setAiPristine] = uD(true);
  // Tích hợp Hộp thư AI: gửi email qua /api/v1/mail/compose/send thay vì mailto:
  const [toEmail, setToEmail] = uD('');
  const [mailReady, setMailReady] = uD(null); // true/false sau khi check; null = chưa biết
  const [sending, setSending] = uD(false);

  uED(() => {
    if (!open) return;
    setCopied(false);
    setAiMsg(`📋 Kính gửi Anh/Chị,\n\nTourkit xin gửi đề xuất chương trình ${title}.\n${summary}\n\nXem chi tiết báo giá: ${link}\n\nMọi thông tin chi tiết xin liên hệ trực tiếp với tư vấn viên. Trân trọng.`);
    setAiPristine(true);
    setToEmail(request?.customer?.email || request?.contactEmail || '');
    // Check trạng thái hộp thư AI 1 lần khi mở dialog
    (async () => {
      try {
        const r = await (window.tourkitAuth?.authedFetch?.('/api/v1/mail/account') || fetch('/api/v1/mail/account'));
        const d = await r.json();
        setMailReady(!!d.configured);
      } catch { setMailReady(false); }
    })();
  }, [open, title, summary, link, request]);

  const sendViaSmartMail = async () => {
    if (!toEmail.trim()) { window.Tourkit_toast?.('Nhập email khách trước'); return; }
    setSending(true);
    try {
      // Body HTML: dòng đầu giữ format AI message, link clickable.
      const lines = aiMsg.split('\n').map(l => l ? `<p style="margin:0 0 8px">${l.replace(/&/g,'&amp;').replace(/</g,'&lt;')}</p>` : '<p style="margin:0 0 8px">&nbsp;</p>').join('');
      const body = `<div style="font-family:'Be Vietnam Pro',Arial,sans-serif;font-size:14px;line-height:1.6;color:#0F172A">${lines}<p style="margin:16px 0 0"><a href="${link}" style="display:inline-block;background:#F97316;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none;font-weight:700">Xem báo giá chi tiết →</a></p></div>`;
      const subject = `Báo giá: ${title}`;
      const r = await (window.tourkitAuth?.authedFetch?.('/api/v1/mail/compose/send', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ to: toEmail.trim(), subject, text: body })
      }) || fetch('/api/v1/mail/compose/send', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ to: toEmail.trim(), subject, text: body })
      }));
      const d = await r.json().catch(() => ({}));
      if (!r.ok) throw new Error(d.error || ('HTTP ' + r.status));
      window.Tourkit_toast?.(`✓ Đã gửi báo giá tới ${toEmail.trim()}`);
      onSent && onSent('smartmail');
    } catch (e) {
      window.Tourkit_toast?.('Gửi email lỗi: ' + e.message);
    } finally { setSending(false); }
  };

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

  const qrSrc = `https://api.qrserver.com/v1/create-qr-code/?size=200x200&margin=4&data=${encodeURIComponent(link)}`;

  // Format ngày khởi hành dạng dd/mm/yyyy
  const fmtDate = (iso) => {
    if (!iso) return null;
    const d = new Date(iso);
    if (isNaN(d)) return iso;
    return d.toLocaleDateString('vi-VN', { day: '2-digit', month: '2-digit', year: 'numeric' });
  };

  const totalPax = request ? (request.adults || 0) + (request.children || 0) : 0;
  const startDateFmt = fmtDate(request?.startDate);
  const budgetCmpPct = (request?.budgetPerPax && perPax) ? Math.round((perPax / request.budgetPerPax - 1) * 100) : null;
  const budgetBadge = budgetCmpPct === null ? null
    : budgetCmpPct <= 0 ? { text: `Thấp hơn ngân sách ${Math.abs(budgetCmpPct)}%`, color: 'var(--success)' }
    : budgetCmpPct <= 5 ? { text: `Sát ngân sách (+${budgetCmpPct}%)`, color: 'var(--warning)' }
    : { text: `Vượt ngân sách +${budgetCmpPct}%`, color: 'var(--danger)' };

  return (
    <Dialog open={open} onClose={onClose} title="Gửi báo giá cho khách"
      eyebrow="CHIA SẺ · QUOTATION" icon="share" maxWidth={860}>

      {/* Tour header card */}
      {(marketing || request) && (
        <div style={{padding: 16, borderRadius: 12, background: 'linear-gradient(135deg, #f8fafc 0%, #eef2ff 100%)', border: '1px solid var(--border)', marginBottom: 16}}>
          <div style={{display: 'flex', alignItems: 'baseline', gap: 10, flexWrap: 'wrap'}}>
            {request?.code && (
              <span style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.1em', padding: '3px 8px', borderRadius: 4, background: 'var(--text)', color: 'white'}}>
                {request.code}
              </span>
            )}
            <div style={{fontSize: 17, fontWeight: 800, color: 'var(--text)', letterSpacing: '-0.01em', flex: 1, minWidth: 0}}>
              {title || marketing?.tourName || 'Tour'}
            </div>
          </div>
          {marketing?.tagline && (
            <div style={{fontSize: 12, fontStyle: 'italic', color: 'var(--text-2)', marginTop: 4}}>
              "{marketing.tagline}"
            </div>
          )}
        </div>
      )}

      <div style={{display: 'grid', gridTemplateColumns: '200px 1fr', gap: 24, alignItems: 'start'}}>
        {/* Left: QR + channels */}
        <div>
          <div style={{padding: 10, border: '1px solid var(--border)', borderRadius: 10, background: 'white'}}>
            <img src={qrSrc} alt="QR báo giá" width="180" height="180" style={{display: 'block', borderRadius: 6, margin: '0 auto'}} />
            <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textAlign: 'center', letterSpacing: '0.08em', marginTop: 8}}>QUÉT QR XEM BÁO GIÁ</div>
          </div>

          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginTop: 16, marginBottom: 8}}>GỬI QUA KÊNH</div>
          <div style={{display: 'grid', gap: 8}}>
            <button className="channel-btn zalo" onClick={() => sendVia('zalo')}>
              <Icon name="share" size={14} /> Zalo
            </button>
            <input type="email" className="input" placeholder="Email khách (vd lan@gmail.com)"
              value={toEmail} onChange={e => setToEmail(e.target.value)}
              style={{fontSize: 12, padding: '8px 10px', borderRadius: 8}} />
            <button className="channel-btn smartmail" onClick={sendViaSmartMail}
              disabled={!toEmail.trim() || !mailReady || sending}
              title={mailReady === false ? 'Hộp thư AI chưa cấu hình — vào /mail để kết nối Gmail' : undefined}>
              <Icon name="sparkle" size={14} />
              {sending ? 'Đang gửi…' : (mailReady === false ? 'Cấu hình hộp thư AI →' : 'Gửi qua Hộp thư AI')}
            </button>
            <button className="channel-btn mail" onClick={() => sendVia('mail')}
              title="Mở mail client trên máy bạn">
              <Icon name="mail" size={14} /> Mở Mail client
            </button>
            <button className="channel-btn sms" onClick={() => sendVia('sms')}>
              <Icon name="phone" size={14} /> SMS
            </button>
          </div>
        </div>

        {/* Right: info + link + AI */}
        <div>
          {/* Customer / tour info grid */}
          {request && (
            <div style={{display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 16}}>
              <InfoCell label="Đoàn khách" value={`${request.adults || 0} NL + ${request.children || 0} TE`} sub={`Tổng ${totalPax} khách`} icon="users" />
              <InfoCell label="Tuyến" value={request.route || '—'} icon="pin" />
              <InfoCell label="Thời lượng" value={`${request.days} N ${request.nights} Đ`} icon="calendar" />
              <InfoCell label="Khởi hành" value={startDateFmt || 'Chưa định ngày'} icon="calendar" />
              {totalSale !== undefined && (
                <InfoCell label="Tổng báo giá" value={fmtVND(totalSale)} icon="dollar" highlight />
              )}
              {perPax !== undefined && (
                <InfoCell label="Giá / Người lớn" value={fmtVND(perPax)} icon="user" highlight badge={budgetBadge} />
              )}
            </div>
          )}

          {/* Preferences chips */}
          {request?.preferences?.length > 0 && (
            <div style={{marginBottom: 14}}>
              <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginBottom: 6}}>SỞ THÍCH</div>
              <div style={{display: 'flex', flexWrap: 'wrap', gap: 6}}>
                {request.preferences.map(p => (
                  <span key={p} style={{fontSize: 11, padding: '3px 9px', borderRadius: 10, background: 'var(--bg)', color: 'var(--text-2)', border: '1px solid var(--border)'}}>{p}</span>
                ))}
              </div>
            </div>
          )}

          {/* Notes */}
          {request?.notes && (
            <div style={{marginBottom: 14, padding: 10, background: 'var(--bg)', borderRadius: 8, borderLeft: '3px solid var(--accent)'}}>
              <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginBottom: 4}}>GHI CHÚ CỦA KHÁCH</div>
              <div style={{fontSize: 12, color: 'var(--text-2)', lineHeight: 1.55}}>{request.notes}</div>
            </div>
          )}

          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginBottom: 6}}>LINK BÁO GIÁ</div>
          <div className="link-row">
            <span style={{flex: 1, fontSize: 12, fontFamily: 'monospace', color: 'var(--text-2)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap'}}>{link}</span>
            <button className="btn btn-outline btn-sm" onClick={copyLink}>
              <Icon name={copied ? 'check' : 'copy'} size={12} />
              {copied ? 'Đã copy' : 'Copy'}
            </button>
          </div>

          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginTop: 16, marginBottom: 6, display: 'flex', alignItems: 'center', gap: 8}}>
            <Icon name="sparkle" size={11} /> NỘI DUNG TIN NHẮN
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
    </Dialog>
  );
}

// Helper cell cho ShareDialog info grid
function InfoCell({ label, value, sub, icon, highlight, badge }) {
  return (
    <div style={{padding: 10, border: '1px solid var(--border)', borderRadius: 8, background: highlight ? 'linear-gradient(135deg, #fef3c7 0%, #fde68a 30%, #ffffff 100%)' : 'white'}}>
      <div style={{display: 'flex', alignItems: 'center', gap: 5, fontSize: 9, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.1em', textTransform: 'uppercase', marginBottom: 4}}>
        {icon && <Icon name={icon} size={10} />}
        {label}
      </div>
      <div style={{fontSize: 14, fontWeight: 700, color: 'var(--text)', letterSpacing: '-0.01em', lineHeight: 1.2}}>{value}</div>
      {sub && <div style={{fontSize: 11, color: 'var(--text-3)', marginTop: 2}}>{sub}</div>}
      {badge && (
        <div style={{display: 'inline-block', fontSize: 10, fontWeight: 700, padding: '2px 6px', borderRadius: 4, background: badge.color, color: 'white', marginTop: 4}}>
          {badge.text}
        </div>
      )}
    </div>
  );
}

window.Dialog = Dialog;
window.PromptDialog = PromptDialog;
window.ConfirmDialog = ConfirmDialog;
window.ShareDialog = ShareDialog;
