// pages/visa-history.jsx — Lịch sử thẩm định visa (list + drawer chi tiết)
// Data: GET /api/v1/visa/assessments (per-tenant qua X-Session-Id).
// Pattern: Linear-style list rows desktop / card mobile / right-slide drawer.

const { useState: _vhS, useEffect: _vhE, useMemo: _vhM } = React;
const _vhIcon = window.Icon;

// ─── helpers ──────────────────────────────────────────────────────────────────
function _vhRankFromLevel(lvl) {
  return lvl === 'cao' ? 'A' : lvl === 'trung_binh' ? 'B' : lvl === 'thap' ? 'C' : '—';
}
function _vhToneFromLevel(lvl) {
  return lvl === 'cao' ? 'good' : lvl === 'trung_binh' ? 'fair' : lvl === 'thap' ? 'poor' : 'muted';
}
// "time ago" → dùng chung window.tourkitUtil.fmtAgo (giữ tên _vhRelative làm alias).
const _vhRelative = (iso) => window.tourkitUtil.fmtAgo(iso);
function _vhAbs(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString('vi-VN', { dateStyle: 'medium', timeStyle: 'short' });
}
function _vhInitials(name) {
  if (!name) return '?';
  const w = name.trim().split(/\s+/);
  return (w.slice(-2).map(x => x[0] || '').join('') || name[0] || '?').toUpperCase();
}
function _vhUseIsMobile(bp = 720) {
  const [m, setM] = _vhS(() => typeof window !== 'undefined' && window.matchMedia
    ? window.matchMedia(`(max-width:${bp}px)`).matches : false);
  _vhE(() => {
    const mq = window.matchMedia(`(max-width:${bp}px)`);
    const fn = () => setM(mq.matches);
    mq.addEventListener?.('change', fn);
    return () => mq.removeEventListener?.('change', fn);
  }, [bp]);
  return m;
}

// ─── Filter chips ─────────────────────────────────────────────────────────────
const _vhLevelOptions = [
  { key: 'all',        label: 'Tất cả' },
  { key: 'cao',        label: 'Hạng A · cao' },
  { key: 'trung_binh', label: 'Hạng B · trung bình' },
  { key: 'thap',       label: 'Hạng C · thấp' },
];
const _vhRangeOptions = [
  { key: 'all', label: 'Mọi lúc' },
  { key: '7',   label: '7 ngày' },
  { key: '30',  label: '30 ngày' },
];

// ─── Page ─────────────────────────────────────────────────────────────────────
function VisaHistoryPage({ pushToast }) {
  const [list, setList] = _vhS([]);
  const [loading, setLoading] = _vhS(true);
  const [err, setErr] = _vhS(null);
  const [search, setSearch] = _vhS('');
  const [country, setCountry] = _vhS('all');
  const [level, setLevel] = _vhS('all');
  const [range, setRange] = _vhS('all');
  const [openId, setOpenId] = _vhS(null);
  const [detail, setDetail] = _vhS(null);
  const [detailLoading, setDetailLoading] = _vhS(false);
  const isMobile = _vhUseIsMobile();

  async function loadList() {
    setLoading(true); setErr(null);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/visa/assessments');
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const data = await r.json();
      setList(Array.isArray(data) ? data : []);
    } catch (e) {
      setErr(e.message || 'Lỗi tải danh sách');
      setList([]);
    } finally { setLoading(false); }
  }
  _vhE(() => { loadList(); }, []);

  async function openDetail(id) {
    setOpenId(id); setDetail(null); setDetailLoading(true);
    try {
      const r = await window.tourkitAuth.authedFetch(`/api/v1/visa/assessments/${encodeURIComponent(id)}`);
      if (!r.ok) throw new Error('HTTP ' + r.status);
      setDetail(await r.json());
    } catch (e) {
      pushToast?.('Lỗi tải chi tiết: ' + e.message, 'error');
      setOpenId(null);
    } finally { setDetailLoading(false); }
  }
  function closeDetail() { setOpenId(null); setDetail(null); }

  async function removeOne(id) {
    if (!window.confirm('Xoá hồ sơ đã thẩm định này? Hành động không thể hoàn tác.')) return;
    try {
      const r = await window.tourkitAuth.authedFetch(`/api/v1/visa/assessments/${encodeURIComponent(id)}`,
        { method: 'DELETE' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      pushToast?.('Đã xoá hồ sơ.', 'success');
      setList(prev => prev.filter(x => x.id !== id));
      if (openId === id) closeDetail();
    } catch (e) { pushToast?.('Lỗi xoá: ' + e.message, 'error'); }
  }

  // unique countries cho dropdown
  const countries = _vhM(() => {
    const set = new Set();
    for (const x of list) if (x.country) set.add(x.country);
    return Array.from(set).sort();
  }, [list]);

  const filtered = _vhM(() => {
    const q = search.trim().toLowerCase();
    const cutoff = range === 'all' ? 0 : Date.now() - (parseInt(range, 10) * 86400000);
    return list.filter(x => {
      if (country !== 'all' && x.country !== country) return false;
      if (level !== 'all' && x.result?.level !== level) return false;
      if (cutoff && x.createdAt && new Date(x.createdAt).getTime() < cutoff) return false;
      if (q) {
        const hay = [x.applicantName, x.country, x.result?.summary, x.id].join(' ').toLowerCase();
        if (!hay.includes(q)) return false;
      }
      return true;
    }).sort((a, b) => (b.createdAt || '').localeCompare(a.createdAt || ''));
  }, [list, search, country, level, range]);

  return (
    <main className="page vh-page">
      <header className="vh-head">
        <div className="vh-head-left">
          <div className="vh-head-icon"><_vhIcon name="shield" size={18} stroke={2} /></div>
          <div>
            <h1 className="vh-h1">Thẩm định Visa</h1>
            <div className="vh-sub">
              {loading ? 'Đang tải…' : `${filtered.length} hồ sơ${filtered.length !== list.length ? ` / ${list.length} tổng` : ''}`}
            </div>
          </div>
        </div>
        <div className="vh-head-actions" style={{ display: 'flex', gap: 10, alignItems: 'center' }}>
          <button className="btn btn-primary btn-sm" onClick={() => window.tourkitRouter.navigate('/visa')}>
            <_vhIcon name="sparkle" size={14} stroke={2.2} /> Thẩm định Visa AI
          </button>
          <button className="vh-refresh" onClick={loadList} disabled={loading}>
            <_vhIcon name="refresh" size={13} stroke={2.2} />
            <span>Tải lại</span>
          </button>
        </div>
      </header>

      <div className="vh-filters">
        <div className="vh-search">
          <_vhIcon name="search" size={14} stroke={2} />
          <input
            type="text"
            placeholder="Tìm theo tên, quốc gia, mã hồ sơ…"
            value={search}
            onChange={e => setSearch(e.target.value)}
          />
          {search && (
            <button className="vh-search-clear" onClick={() => setSearch('')} aria-label="Xoá tìm kiếm">×</button>
          )}
        </div>
        <select className="vh-select" value={country} onChange={e => setCountry(e.target.value)}>
          <option value="all">Tất cả quốc gia</option>
          {countries.map(c => <option key={c} value={c}>{c}</option>)}
        </select>
        <div className="vh-chips">
          {_vhLevelOptions.map(o => (
            <button
              key={o.key}
              className={'vh-chip' + (level === o.key ? ' vh-chip-on' : '')}
              onClick={() => setLevel(o.key)}
            >{o.label}</button>
          ))}
        </div>
        <div className="vh-chips vh-chips-right">
          {_vhRangeOptions.map(o => (
            <button
              key={o.key}
              className={'vh-chip vh-chip-mini' + (range === o.key ? ' vh-chip-on' : '')}
              onClick={() => setRange(o.key)}
            >{o.label}</button>
          ))}
        </div>
      </div>

      {err && <div className="vh-err">{err} · <button onClick={loadList}>Thử lại</button></div>}

      {!loading && list.length === 0 && !err && (
        <div className="vh-empty">
          <div className="vh-empty-icon"><_vhIcon name="shield" size={32} stroke={1.5} /></div>
          <h3>Chưa có hồ sơ nào</h3>
          <p>Mở <a href="#/visa">Thẩm định Visa</a> để chấm hồ sơ đầu tiên — kết quả sẽ tự lưu về đây.</p>
        </div>
      )}

      {!loading && list.length > 0 && filtered.length === 0 && (
        <div className="vh-empty vh-empty-soft">
          <p>Không có hồ sơ khớp bộ lọc hiện tại.</p>
          <button className="vh-btn-soft" onClick={() => { setSearch(''); setCountry('all'); setLevel('all'); setRange('all'); }}>
            Xoá bộ lọc
          </button>
        </div>
      )}

      {loading && (
        <div className="vh-list">
          {[1,2,3,4].map(i => <div key={i} className="vh-skel" />)}
        </div>
      )}

      {!loading && filtered.length > 0 && (
        isMobile
          ? <div className="vh-cards">
              {filtered.map(x => <VisaHistoryCard key={x.id} item={x} onOpen={() => openDetail(x.id)} onDelete={() => removeOne(x.id)} />)}
            </div>
          : <div className="vh-list">
              {filtered.map(x => <VisaHistoryRow key={x.id} item={x} onOpen={() => openDetail(x.id)} onDelete={() => removeOne(x.id)} />)}
            </div>
      )}

      {openId && (
        <VisaHistoryDrawer
          id={openId}
          detail={detail}
          loading={detailLoading}
          onClose={closeDetail}
          onDelete={() => removeOne(openId)}
        />
      )}
    </main>
  );
}

// ─── Row (desktop) ────────────────────────────────────────────────────────────
function VisaHistoryRow({ item, onOpen, onDelete }) {
  const rank = _vhRankFromLevel(item.result?.level);
  const tone = _vhToneFromLevel(item.result?.level);
  const pct  = item.result?.passRate ?? null;
  return (
    <div className="vh-row" onClick={onOpen} role="button" tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpen(); } }}>
      <div className="vh-avatar">{_vhInitials(item.applicantName)}</div>
      <div className="vh-col-main">
        <div className="vh-name">{item.applicantName || 'Không tên'}</div>
        <div className="vh-meta">
          {item.country && <span className="vh-meta-pill">{item.country}</span>}
          <span className="vh-meta-dot">·</span>
          <span title={_vhAbs(item.createdAt)}>{_vhRelative(item.createdAt)}</span>
          <span className="vh-meta-dot">·</span>
          <span>{item.fileCount > 0 ? `${item.fileCount} file` : 'Không file'}</span>
        </div>
      </div>
      <div className="vh-col-summary">{item.result?.summary || <span className="vh-mute">Chưa có tóm tắt</span>}</div>
      <div className={'vh-rank vh-rank-' + tone}>
        {pct !== null && <span className="vh-rank-pct">{pct}<i>%</i></span>}
        <span className="vh-rank-letter">Hạng {rank}</span>
      </div>
      <button className="vh-icon-btn" title="Xoá" onClick={e => { e.stopPropagation(); onDelete(); }}>
        <_vhIcon name="trash" size={14} stroke={2} />
      </button>
    </div>
  );
}

// ─── Card (mobile) ────────────────────────────────────────────────────────────
function VisaHistoryCard({ item, onOpen, onDelete }) {
  const rank = _vhRankFromLevel(item.result?.level);
  const tone = _vhToneFromLevel(item.result?.level);
  const pct  = item.result?.passRate ?? null;
  return (
    <div className="vh-card" onClick={onOpen}>
      <div className="vh-card-head">
        <div className="vh-avatar vh-avatar-sm">{_vhInitials(item.applicantName)}</div>
        <div className="vh-card-id">
          <div className="vh-name">{item.applicantName || 'Không tên'}</div>
          <div className="vh-meta">
            {item.country && <span className="vh-meta-pill">{item.country}</span>}
            <span className="vh-meta-dot">·</span>
            <span>{_vhRelative(item.createdAt)}</span>
          </div>
        </div>
        <div className={'vh-rank vh-rank-' + tone + ' vh-rank-card'}>
          {pct !== null && <span className="vh-rank-pct">{pct}<i>%</i></span>}
          <span className="vh-rank-letter">{rank}</span>
        </div>
      </div>
      {item.result?.summary && <p className="vh-card-summary">{item.result.summary}</p>}
      <div className="vh-card-foot">
        <span>{item.fileCount > 0 ? `${item.fileCount} file kèm` : 'Không file'}</span>
        <button className="vh-icon-btn" onClick={e => { e.stopPropagation(); onDelete(); }} aria-label="Xoá">
          <_vhIcon name="trash" size={13} stroke={2} />
        </button>
      </div>
    </div>
  );
}

// ─── Drawer (right slide) ─────────────────────────────────────────────────────
function VisaHistoryDrawer({ id, detail, loading, onClose, onDelete }) {
  _vhE(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    document.body.style.overflow = 'hidden';
    return () => { window.removeEventListener('keydown', onKey); document.body.style.overflow = ''; };
  }, [onClose]);

  const rank = _vhRankFromLevel(detail?.result?.level);
  const tone = _vhToneFromLevel(detail?.result?.level);

  return (
    <>
      <div className="vh-drawer-back" onClick={onClose} />
      <aside className="vh-drawer" role="dialog" aria-label="Chi tiết hồ sơ visa">
        <header className="vh-drawer-head">
          <button className="vh-drawer-close" onClick={onClose} aria-label="Đóng">×</button>
          <div className="vh-drawer-meta">
            <code className="vh-drawer-id">{id}</code>
            {detail?.createdAt && <span className="vh-drawer-date">{_vhAbs(detail.createdAt)}</span>}
          </div>
        </header>

        {loading && <div className="vh-drawer-loading">Đang tải chi tiết…</div>}

        {detail && (
          <div className="vh-drawer-body">
            <div className="vh-drawer-applicant">
              <div className="vh-avatar vh-avatar-lg">{_vhInitials(detail.applicantName)}</div>
              <div>
                <h2 className="vh-drawer-name">{detail.applicantName || 'Không tên'}</h2>
                <div className="vh-drawer-sub">
                  {detail.country && <span className="vh-meta-pill">{detail.country}</span>}
                  <span>{detail.fileCount > 0 ? `${detail.fileCount} file kèm` : 'Không có file'}</span>
                </div>
              </div>
            </div>

            {detail.result && (
              <div className={'vh-drawer-score vh-rank-' + tone}>
                <div className="vh-drawer-pct">{detail.result.passRate}<span>%</span></div>
                <div className="vh-drawer-level">Tỉ lệ đậu dự kiến · Hạng {rank}</div>
                {detail.result.summary && <p className="vh-drawer-summary">{detail.result.summary}</p>}
              </div>
            )}

            {detail.result?.strengths?.length > 0 && (
              <DrawerSection title="Điểm mạnh" tone="good" items={detail.result.strengths} />
            )}
            {detail.result?.weaknesses?.length > 0 && (
              <DrawerSection title="Điểm yếu" tone="warn" items={detail.result.weaknesses} />
            )}
            {detail.result?.missingDocs?.length > 0 && (
              <DrawerSection title="Hồ sơ còn thiếu" tone="warn" items={detail.result.missingDocs} />
            )}
            {detail.result?.suggestions?.length > 0 && (
              <DrawerSection title="Đề xuất nâng tỉ lệ đậu" tone="suggest" items={detail.result.suggestions} />
            )}

            {detail.extraction?.files?.length > 0 && (
              <div className="vh-drawer-section">
                <h3 className="vh-drawer-h">File AI đã đọc</h3>
                <ul className="vh-drawer-files">
                  {detail.extraction.files.map((f, i) => (
                    <li key={i}>
                      <span className="vh-file-label">{f.docTypeLabel || f.docType}</span>
                      <span className="vh-file-name">{f.fileName}</span>
                      {!f.readable && <span className="vh-file-warn">không đọc được</span>}
                    </li>
                  ))}
                </ul>
              </div>
            )}

            <div className="vh-drawer-foot">
              <button className="vh-btn-danger" onClick={onDelete}>
                <_vhIcon name="trash" size={13} stroke={2} /> Xoá hồ sơ
              </button>
            </div>
          </div>
        )}
      </aside>
    </>
  );
}

function DrawerSection({ title, tone, items }) {
  return (
    <div className={'vh-drawer-section vh-section-' + tone}>
      <h3 className="vh-drawer-h">{title}</h3>
      <ul>{items.map((s, i) => <li key={i}>{s}</li>)}</ul>
    </div>
  );
}

window.VisaHistoryPage = VisaHistoryPage;
