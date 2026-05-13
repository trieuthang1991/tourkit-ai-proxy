// components/customer-review-card.jsx — Drawer hiển thị full review của 1 KH.
// Theo spec màn hình 3: header avatar+name+badges, alert box, portrait, 4-stat grid,
// action-now green box, 30-day suggestions, strengths/concerns 2-col, preferences,
// footer feedback + refresh + timestamp.

function CustomerReviewDrawer({ customerId, onClose, onRefreshed, pushToast }) {
  const [data, setData]       = React.useState(null);       // {customer, review}
  const [loading, setLoading] = React.useState(true);
  const [err, setErr]         = React.useState(null);
  const [refreshing, setRef]  = React.useState(false);
  const [fbNote, setFbNote]   = React.useState('');
  const [fbOpen, setFbOpen]   = React.useState(false);

  const load = async () => {
    setLoading(true); setErr(null);
    try {
      const resp = await fetch(`/api/v1/customers/${encodeURIComponent(customerId)}`);
      if (!resp.ok) throw new Error('HTTP ' + resp.status);
      setData(await resp.json());
    } catch (e) {
      setErr(e.message);
    } finally {
      setLoading(false);
    }
  };

  React.useEffect(() => { load(); }, [customerId]);

  // ESC to close
  React.useEffect(() => {
    const h = e => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [onClose]);

  const refreshReview = async () => {
    setRef(true);
    try {
      const resp = await fetch(`/api/v1/reviews/customer/${encodeURIComponent(customerId)}/refresh`, { method: 'POST' });
      const json = await resp.json();
      if (!resp.ok) throw new Error(json.error || 'Refresh fail');
      setData(d => ({ ...d, review: json.review }));
      pushToast('✓ Đã cập nhật review');
      onRefreshed?.();
    } catch (e) {
      pushToast('Refresh lỗi: ' + e.message, 'warn');
    } finally {
      setRef(false);
    }
  };

  const sendFeedback = async (rating) => {
    try {
      const resp = await fetch(`/api/v1/reviews/${encodeURIComponent(customerId)}/feedback`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ rating, note: fbNote || null })
      });
      if (!resp.ok) throw new Error('HTTP ' + resp.status);
      pushToast(rating === 'helpful' ? '👍 Cảm ơn phản hồi' : '👎 Đã ghi nhận');
      setFbOpen(false);
      setFbNote('');
      load();
    } catch (e) {
      pushToast('Gửi feedback lỗi: ' + e.message, 'warn');
    }
  };

  const r = data?.review;
  const c = data?.customer;

  return (
    <div style={drawerOverlay} onClick={onClose}>
      <div style={drawerPanel} onClick={e => e.stopPropagation()}>
        {/* Header */}
        <div style={drawerHead}>
          <div style={{display: 'flex', alignItems: 'center', gap: 12, flex: 1, minWidth: 0}}>
            <div style={avatar}>{(c?.name || '?').slice(0, 1).toUpperCase()}</div>
            <div style={{flex: 1, minWidth: 0}}>
              <div style={{fontSize: 16, fontWeight: 700}}>{c?.name || 'Đang tải...'}</div>
              <div style={{fontSize: 12, color: 'var(--text-3)'}}>
                {c?.id} · {c?.location || '—'} · KH từ {c?.createdAt?.slice(0, 10) || '—'}
              </div>
            </div>
            {c && (
              <div style={{display: 'flex', gap: 6, alignItems: 'center'}}>
                <SegBadge segment={c.segment} />
                {r && <RankBadge rank={r.rank} />}
              </div>
            )}
          </div>
          <button onClick={onClose} style={closeBtn} aria-label="Đóng">×</button>
        </div>

        <div style={drawerBody}>
          {loading && <div style={{padding: 40, textAlign: 'center', color: 'var(--text-3)'}}>Đang tải...</div>}
          {err && <div style={{padding: 12, background: '#fef2f2', color: '#991b1b', borderRadius: 8}}>{err}</div>}

          {!loading && c && !r && (
            <div style={{padding: 40, textAlign: 'center'}}>
              <div style={{color: 'var(--text-3)', marginBottom: 14}}>Khách hàng này chưa được review.</div>
              <button className="btn btn-primary" onClick={refreshReview} disabled={refreshing}>
                <Icon name="sparkle" size={14} /> {refreshing ? 'Đang review...' : 'Review ngay'}
              </button>
            </div>
          )}

          {r && (
            <>
              {/* Alert */}
              {r.alert?.level && r.alert.level !== 'none' && r.alert.message && (
                <div style={{
                  padding: 12, borderRadius: 8, marginBottom: 16,
                  background: r.alert.level === 'high' ? '#fef2f2' : '#fef3c7',
                  borderLeft: `3px solid ${r.alert.level === 'high' ? '#dc2626' : '#f59e0b'}`,
                  display: 'flex', gap: 10, alignItems: 'flex-start'
                }}>
                  <span style={{fontSize: 16}}>{r.alert.level === 'high' ? '⚠️' : 'ℹ️'}</span>
                  <div>
                    <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.08em', color: r.alert.level === 'high' ? '#991b1b' : '#92400e', textTransform: 'uppercase', marginBottom: 2}}>
                      {r.alert.level === 'high' ? 'CẢNH BÁO' : 'CHÚ Ý'}
                    </div>
                    <div style={{fontSize: 13}}>{r.alert.message}</div>
                  </div>
                </div>
              )}

              {/* Portrait + rank reason */}
              <Section label="Chân dung">
                <div style={{fontSize: 13, lineHeight: 1.6, color: 'var(--text-1)'}}>{r.portrait}</div>
                {r.rankReason && (
                  <div style={{fontSize: 12, fontStyle: 'italic', color: 'var(--text-3)', marginTop: 6}}>
                    Lý do xếp hạng {r.rank}: {r.rankReason}
                  </div>
                )}
              </Section>

              {/* 4-stat grid */}
              <div style={{display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8, marginBottom: 18}}>
                <StatCell label="Tổng chi" value={fmtVND(c.metrics?.totalSpent || 0)} />
                <StatCell label="Số tour" value={c.metrics?.totalTours || 0} />
                <StatCell label="AOV" value={fmtVND(c.metrics?.aov || 0)} />
                <StatCell label="Đơn cuối" value={c.metrics?.lastPurchaseDaysAgo != null ? `${c.metrics.lastPurchaseDaysAgo}d` : '—'} />
              </div>

              {/* Action now */}
              {r.actionNow && (
                <div style={{
                  padding: 14, borderRadius: 10, marginBottom: 18,
                  background: 'linear-gradient(135deg, #ecfdf5 0%, #d1fae5 100%)',
                  borderLeft: '3px solid #10b981'
                }}>
                  <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.08em', color: '#065f46', textTransform: 'uppercase', marginBottom: 6}}>
                    🎯 VIỆC CẦN LÀM NGAY
                  </div>
                  <div style={{fontSize: 14, fontWeight: 600, color: '#064e3b', marginBottom: 4}}>{r.actionNow.task}</div>
                  <div style={{fontSize: 12, color: '#065f46', fontStyle: 'italic'}}>{r.actionNow.reason}</div>
                </div>
              )}

              {/* 30-day suggestions */}
              {r.action30Days?.length > 0 && (
                <Section label="Gợi ý 30 ngày tới">
                  <ul style={{margin: 0, paddingLeft: 18, fontSize: 13, lineHeight: 1.7}}>
                    {r.action30Days.map((a, i) => <li key={i}>{a}</li>)}
                  </ul>
                </Section>
              )}

              {/* Strengths / Concerns 2-col */}
              <div style={{display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 18}}>
                {r.strengths?.length > 0 && (
                  <div style={{padding: 12, background: '#f0fdf4', borderRadius: 8, border: '1px solid #bbf7d0'}}>
                    <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.08em', color: '#166534', textTransform: 'uppercase', marginBottom: 8}}>✓ Điểm sáng</div>
                    <ul style={{margin: 0, paddingLeft: 16, fontSize: 12, lineHeight: 1.6}}>
                      {r.strengths.map((s, i) => <li key={i}>{s}</li>)}
                    </ul>
                  </div>
                )}
                {r.concerns?.length > 0 && (
                  <div style={{padding: 12, background: '#fefce8', borderRadius: 8, border: '1px solid #fde68a'}}>
                    <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.08em', color: '#854d0e', textTransform: 'uppercase', marginBottom: 8}}>⚠ Cần lưu ý</div>
                    <ul style={{margin: 0, paddingLeft: 16, fontSize: 12, lineHeight: 1.6}}>
                      {r.concerns.map((s, i) => <li key={i}>{s}</li>)}
                    </ul>
                  </div>
                )}
              </div>

              {/* Preferences */}
              {r.preferences && (
                <Section label="Sở thích & thói quen">
                  <div style={{fontSize: 13, lineHeight: 1.6, color: 'var(--text-2)'}}>{r.preferences}</div>
                </Section>
              )}

              {/* Product suggestions */}
              {r.productSuggestions?.length > 0 && (
                <Section label="Gợi ý sản phẩm">
                  <div style={{display: 'flex', flexWrap: 'wrap', gap: 6}}>
                    {r.productSuggestions.map((p, i) => (
                      <span key={i} style={{
                        fontSize: 12, padding: '6px 10px', borderRadius: 14,
                        background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text-2)'
                      }}>{p}</span>
                    ))}
                  </div>
                </Section>
              )}

              {/* Footer */}
              <div style={{
                marginTop: 20, paddingTop: 14, borderTop: '1px solid var(--border)',
                display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap'
              }}>
                <button className="btn btn-ghost btn-sm" disabled={!!r.feedback} onClick={() => sendFeedback('helpful')}>
                  👍 Hữu ích
                </button>
                <button className="btn btn-ghost btn-sm" disabled={!!r.feedback} onClick={() => setFbOpen(o => !o)}>
                  👎 Chưa chính xác
                </button>
                <div style={{flex: 1}} />
                <button className="btn btn-primary btn-sm" onClick={refreshReview} disabled={refreshing}>
                  <Icon name="refresh" size={13} /> {refreshing ? 'Đang...' : 'Cập nhật review'}
                </button>
              </div>

              {/* Feedback note input */}
              {fbOpen && (
                <div style={{marginTop: 10, padding: 10, background: 'var(--bg)', borderRadius: 8}}>
                  <textarea className="textarea" placeholder="Lý do chưa chính xác (tuỳ chọn)..."
                    rows={2}
                    value={fbNote} onChange={e => setFbNote(e.target.value)}
                    style={{width: '100%', fontSize: 13, marginBottom: 8, resize: 'vertical'}} />
                  <div style={{display: 'flex', gap: 8, justifyContent: 'flex-end'}}>
                    <button className="btn btn-ghost btn-sm" onClick={() => { setFbOpen(false); setFbNote(''); }}>Hủy</button>
                    <button className="btn btn-primary btn-sm" onClick={() => sendFeedback('not_helpful')}>Gửi feedback</button>
                  </div>
                </div>
              )}

              {/* Metadata footer */}
              <div style={{marginTop: 14, fontSize: 11, color: 'var(--text-3)', textAlign: 'right'}}>
                {r.aiProvider}:{r.aiModel} · {r.tokensOut} tokens · {fmtRel(r.generatedAt)}
                {r.feedback && <> · <span style={{color: r.feedback.rating === 'helpful' ? '#16a34a' : '#dc2626'}}>
                  {r.feedback.rating === 'helpful' ? '👍 helpful' : '👎 not helpful'}
                </span></>}
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

// ─── Styles + helpers ─────────────────────────────────────────────────────────
const drawerOverlay = {
  position: 'fixed', inset: 0, zIndex: 1000, background: 'rgba(15, 23, 42, 0.55)',
  backdropFilter: 'blur(2px)', display: 'flex', justifyContent: 'flex-end', animation: 'fadeIn 0.15s ease'
};
const drawerPanel = {
  width: 'min(640px, 92vw)', height: '100vh', background: 'white',
  display: 'flex', flexDirection: 'column', boxShadow: '-12px 0 32px rgba(0,0,0,0.18)',
  animation: 'slideIn 0.2s ease'
};
const drawerHead = {
  display: 'flex', alignItems: 'center', gap: 12, padding: '16px 20px',
  borderBottom: '1px solid var(--border)', flexShrink: 0
};
const drawerBody = { padding: 20, overflowY: 'auto', flex: 1 };
const avatar = {
  width: 40, height: 40, borderRadius: '50%',
  background: 'linear-gradient(135deg, var(--primary), var(--primary-dark))',
  color: 'white', display: 'flex', alignItems: 'center', justifyContent: 'center',
  fontWeight: 700, fontSize: 16, flexShrink: 0
};
const closeBtn = {
  background: 'transparent', border: 'none', fontSize: 24, cursor: 'pointer',
  width: 32, height: 32, borderRadius: 8, color: 'var(--text-3)'
};

// Duplicate from customers.jsx (Babel-standalone scope là per-file, không share function decl).
// Nếu sau này nhiều page dùng, tách ra components/badges.jsx.
function SegBadge({ segment }) {
  const styles = {
    'VIP':     { bg: 'linear-gradient(135deg, #f59e0b, #ea580c)', fg: 'white' },
    'Thường':  { bg: '#e0e7ff', fg: '#3730a3' },
    'Mới':     { bg: '#dcfce7', fg: '#166534' }
  };
  const s = styles[segment] || { bg: 'var(--bg)', fg: 'var(--text-2)' };
  return <span style={{fontSize: 11, fontWeight: 700, padding: '3px 8px', borderRadius: 4, background: s.bg, color: s.fg}}>{segment}</span>;
}
function RankBadge({ rank }) {
  const colors = { A: '#16a34a', B: '#2563eb', C: '#f59e0b', D: '#dc2626' };
  return (
    <span style={{
      display: 'inline-block', width: 26, height: 26, lineHeight: '26px', textAlign: 'center',
      borderRadius: '50%', background: colors[rank] || '#9ca3af', color: 'white', fontWeight: 700, fontSize: 12
    }}>{rank}</span>
  );
}

function Section({ label, children }) {
  return (
    <div style={{marginBottom: 16}}>
      <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.08em', color: 'var(--text-3)', textTransform: 'uppercase', marginBottom: 6}}>
        {label}
      </div>
      {children}
    </div>
  );
}

function StatCell({ label, value }) {
  return (
    <div style={{padding: 10, background: 'var(--bg)', borderRadius: 8, textAlign: 'center'}}>
      <div style={{fontSize: 10, color: 'var(--text-3)', letterSpacing: '0.05em', textTransform: 'uppercase'}}>{label}</div>
      <div style={{fontSize: 14, fontWeight: 700, marginTop: 4}}>{value}</div>
    </div>
  );
}

function fmtRel(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d)) return iso;
  const min = Math.round((Date.now() - d.getTime()) / 60000);
  if (min < 1) return 'vừa xong';
  if (min < 60) return `${min} phút trước`;
  if (min < 1440) return `${Math.floor(min / 60)}h trước`;
  return `${Math.floor(min / 1440)} ngày trước`;
}

// SegBadge / RankBadge cũng dùng trong customers.jsx — re-define gọn để component standalone.
// Nếu sau này cần share, tách ra core/badges.jsx.
if (!window._sharedBadgesLoaded) {
  // no-op — customers.jsx đã định nghĩa cả 2, dùng chung scope global Babel
}

window.CustomerReviewDrawer = CustomerReviewDrawer;
