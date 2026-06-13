// components/consult-popup.jsx
// Popup đăng ký tư vấn (cho landing page). Hiện khi khách CHƯA đăng nhập click
// vào 1 tính năng → form Họ tên / SĐT / Email / Công ty / Ghi chú →
// POST /api/v1/consult-leads → lưu data/consult-leads.jsonl ở backend.
//
// Dùng: <ConsultPopup open={...} feature="Trợ lý số liệu" onClose={...} />
//
// State sau khi gửi thành công: hiện trạng "✓ Đã gửi, sẽ liên hệ trong 24h" + nút đóng.

(function () {
  'use strict';
  const { useState, useEffect, useRef } = React;

  function ConsultPopup({ open, feature, onClose }) {
    const [name, setName] = useState('');
    const [phone, setPhone] = useState('');
    const [email, setEmail] = useState('');
    const [company, setCompany] = useState('');
    const [note, setNote] = useState('');
    const [sending, setSending] = useState(false);
    const [done, setDone] = useState(false);
    const [err, setErr] = useState(null);
    const firstRef = useRef(null);

    // Mở popup → reset state + focus vào ô họ tên
    useEffect(() => {
      if (!open) return;
      setDone(false); setErr(null); setSending(false);
      setName(''); setPhone(''); setEmail(''); setCompany(''); setNote('');
      setTimeout(() => firstRef.current?.focus(), 80);
    }, [open]);

    // Esc để đóng
    useEffect(() => {
      if (!open) return;
      const onKey = e => { if (e.key === 'Escape') onClose?.(); };
      window.addEventListener('keydown', onKey);
      return () => window.removeEventListener('keydown', onKey);
    }, [open, onClose]);

    if (!open) return null;

    const submit = async (e) => {
      e.preventDefault();
      setErr(null);
      if (!name.trim() || !phone.trim()) {
        setErr('Vui lòng nhập Họ tên và Số điện thoại.');
        return;
      }
      setSending(true);
      try {
        const r = await fetch('/api/v1/consult-leads', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            fullName: name.trim(),
            phone: phone.trim(),
            email: email.trim() || null,
            company: company.trim() || null,
            feature: feature || null,
            note: note.trim() || null
          })
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(j.error || 'Không gửi được, thử lại sau.');
        setDone(true);
      } catch (ex) {
        setErr(ex.message || 'Lỗi gửi.');
      } finally {
        setSending(false);
      }
    };

    return (
      <div className="lp-overlay" onClick={onClose} role="dialog" aria-modal="true">
        <div className="lp-popup" onClick={e => e.stopPropagation()}>
          <button className="lp-popup-close" onClick={onClose} aria-label="Đóng">
            <Icon name="close" size={18} stroke={2.2} />
          </button>

          {done ? (
            <div className="lp-popup-success">
              <div className="lp-popup-checkmark">
                <Icon name="check" size={28} stroke={3} color="#fff" />
              </div>
              <h3>Đã nhận thông tin của Anh/Chị.</h3>
              <p>Đội tư vấn TRAV-AI sẽ liên hệ qua số <strong>{phone}</strong> trong vòng 24 giờ làm việc.</p>
              <button className="lp-popup-btn-primary" onClick={onClose}>Đóng</button>
            </div>
          ) : (
            <form onSubmit={submit} className="lp-popup-form">
              <div className="lp-popup-head">
                <div className="lp-popup-eyebrow">ĐĂNG KÝ TƯ VẤN</div>
                <h3>{feature ? `Trải nghiệm "${feature}"` : 'Để TRAV-AI gánh việc giúp bạn'}</h3>
                <p>Để lại số liên hệ, đội ngũ sẽ gọi giới thiệu và set-up tài khoản demo trong 24 giờ.</p>
              </div>

              <div className="lp-popup-grid">
                <label className="lp-field">
                  <span>Họ tên <em>*</em></span>
                  <input ref={firstRef} value={name} onChange={e => setName(e.target.value)}
                    placeholder="Nguyễn Văn A" autoComplete="name" required />
                </label>
                <label className="lp-field">
                  <span>Số điện thoại <em>*</em></span>
                  <input value={phone} onChange={e => setPhone(e.target.value)}
                    type="tel" placeholder="0901 234 567" autoComplete="tel" required />
                </label>
                <label className="lp-field">
                  <span>Email</span>
                  <input value={email} onChange={e => setEmail(e.target.value)}
                    type="email" placeholder="ban@congty.vn" autoComplete="email" />
                </label>
                <label className="lp-field">
                  <span>Công ty</span>
                  <input value={company} onChange={e => setCompany(e.target.value)}
                    placeholder="Tên công ty du lịch" autoComplete="organization" />
                </label>
                <label className="lp-field lp-field-full">
                  <span>Anh/Chị muốn AI giúp việc gì? (tuỳ chọn)</span>
                  <textarea value={note} onChange={e => setNote(e.target.value)}
                    rows={3} placeholder="Ví dụ: tự động báo giá tour Hàn Quốc, phân loại mail khách…" />
                </label>
              </div>

              {err && <div className="lp-popup-err">{err}</div>}

              <div className="lp-popup-actions">
                <button type="button" className="lp-popup-btn-ghost" onClick={onClose} disabled={sending}>
                  Để sau
                </button>
                <button type="submit" className="lp-popup-btn-primary" disabled={sending}>
                  {sending ? 'Đang gửi…' : 'Gửi đăng ký'}
                </button>
              </div>

              <p className="lp-popup-foot">
                Thông tin chỉ dùng cho mục đích tư vấn nội bộ. Không spam, không chia sẻ cho bên thứ ba.
              </p>
            </form>
          )}
        </div>
      </div>
    );
  }

  window.ConsultPopup = ConsultPopup;
})();
