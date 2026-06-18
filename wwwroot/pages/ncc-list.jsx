// pages/ncc-list.jsx — Nhà cung cấp (NCC): danh sách lấy TRỰC TIẾP từ TourKit AI surface.
// Luồng: authedFetch GET /api/v1/ncc/list?filter=&pageIndex=&pageSize=
//        → proxy → toutkit-app GET /api/ai/providers (surface AI, envelope đồng nhất)
//        → { section, title, count, total, items:[{id,code,name,phone,email,city,statusText,...}] } (camelCase).
// Style: mirror trang data-list của site (PageShell.PageHero + SearchControls.SearchInput + .btn + th/td theo token),
//        responsive (bảng >640px, card <=640px). authedFetch tự gắn X-Session-Id; page chỉ render sau đăng nhập.

const { useState: _uNcc, useEffect: _uENcc } = React;

// matchMedia isMobile (<=640px) — cùng pattern customers.jsx (_uCIsMobile).
function _nccIsMobile(bp = 640) {
  const [m, setM] = _uNcc(() => window.innerWidth <= bp);
  _uENcc(() => {
    const check = () => setM(window.innerWidth <= bp);
    window.addEventListener('resize', check);
    check();
    return () => window.removeEventListener('resize', check);
  }, []);
  return m;
}

// th/td theo đúng style bảng data-list của site (customers.jsx).
const _nccTh = (w) => ({ padding: '10px 12px', fontWeight: 700, fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--text-3)', width: w, whiteSpace: 'nowrap' });
const _nccTd = () => ({ padding: '12px', verticalAlign: 'middle' });
const _nccMuted = { color: 'var(--text-3)' };
const _nccMono = { fontFamily: 'ui-monospace, "SF Mono", monospace', fontSize: 12, color: 'var(--text-2)' };

// statusText do server (AI surface) format sẵn TV; tô accent khi đang hoạt động (status=2).
function NccStatus({ item }) {
  const active = item.status === 2;
  return (
    <span style={{
      display: 'inline-block', padding: '2px 10px', borderRadius: 999, fontSize: 12, fontWeight: 600,
      color: active ? 'var(--accent)' : 'var(--text-3)',
      background: 'var(--bg)', border: '1px solid var(--border)'
    }}>{item.statusText || '-'}</span>
  );
}

function NccListPage({ pushToast }) {
  const [items, setItems] = _uNcc([]);
  const [total, setTotal] = _uNcc(0);
  const [page, setPage]   = _uNcc(1);
  const pageSize = 20;
  const [q, setQ]         = _uNcc('');
  const [loading, setLoad] = _uNcc(true);
  const [err, setErr]      = _uNcc(null);
  const [reloadKey, setReloadKey] = _uNcc(0);
  const isMobile = _nccIsMobile();

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  _uENcc(() => {
    let alive = true;
    setLoad(true); setErr(null);
    const qs = new URLSearchParams({ pageIndex: String(page), pageSize: String(pageSize) });
    if (q.trim()) qs.set('filter', q.trim());
    window.tourkitAuth.authedFetch('/api/v1/ncc/list?' + qs.toString())
      .then(r => r.json().then(d => ({ ok: r.ok, d })))
      .then(({ ok, d }) => {
        if (!alive) return;
        if (!ok) { setErr((d && d.error) || 'Không tải được danh sách nhà cung cấp.'); setItems([]); setTotal(0); }
        else { setItems(d.items || []); setTotal(d.total || 0); }  // envelope AI: total (không phải totalCount)
      })
      .catch(e => { if (alive) setErr(String((e && e.message) || e)); })
      .finally(() => { if (alive) setLoad(false); });
    return () => { alive = false; };
  }, [page, q, reloadKey]);

  const onSearch = (val) => { setPage(1); setQ(val || ''); };

  return (
    <main className="page" style={{ padding: '18px 28px 60px', width: '100%' }}>
      <window.PageShell.PageHero
        icon="list"
        title="AI Import NCC"
        sub="Danh sách nhà cung cấp lấy trực tiếp từ TourKit AI API."
        status={{
          label: total > 0 ? `${total.toLocaleString('vi-VN')} NHÀ CUNG CẤP` : 'CHƯA CÓ DỮ LIỆU',
          detail: q.trim() ? `Đang lọc: "${q.trim()}"` : 'Toàn bộ NCC'
        }}
        actions={<>
          <button className="btn btn-primary btn-sm" onClick={() => window.tourkitRouter.navigate('/ncc-import')}>
            <Icon name="sparkle" size={14} /> Import bằng AI
          </button>
          <button className="btn btn-ghost btn-sm" onClick={() => setReloadKey(k => k + 1)} disabled={loading}>
            <Icon name="refresh" size={14} /> Tải lại
          </button>
        </>}
      />

      <div style={{ margin: '16px 0', maxWidth: 520 }}>
        <window.SearchControls.SearchInput
          value={q} onChange={onSearch} submitOnly
          placeholder="Tìm tên / mã / SĐT / email / mã số thuế… (Enter để tìm)" />
      </div>

      {loading ? (
        <div style={{ padding: 32, textAlign: 'center', ..._nccMuted }}>Đang tải danh sách…</div>
      ) : err ? (
        <div style={{ padding: 16, borderRadius: 'var(--radius)', background: 'rgba(239,68,68,0.08)',
                      border: '1px solid rgba(239,68,68,0.30)', color: 'var(--danger)' }}>
          {err}
        </div>
      ) : items.length === 0 ? (
        <div style={{ padding: 40, textAlign: 'center', background: 'var(--surface)',
                      border: '1px solid var(--border)', borderRadius: 10, ..._nccMuted }}>
          {q.trim() ? 'Không có nhà cung cấp nào khớp từ khóa.' : 'Chưa có nhà cung cấp nào.'}
        </div>
      ) : isMobile ? (
        /* <=640px: card layout — bảng 6 cột scroll ngang khó dùng trên điện thoại. */
        <div style={{ display: 'grid', gap: 10 }}>
          {items.map(p => (
            <div key={p.id} style={{ background: 'var(--surface)', border: '1px solid var(--border)',
                                     borderRadius: 'var(--radius-md)', padding: 14 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, alignItems: 'flex-start' }}>
                <div style={{ fontWeight: 700 }}>{p.name || '-'}</div>
                <NccStatus item={p} />
              </div>
              {p.code && <div style={{ ..._nccMono, marginTop: 2 }}>{p.code}</div>}
              <div style={{ marginTop: 8, fontSize: 13, color: 'var(--text-2)', display: 'grid', gap: 3 }}>
                {p.phone && <div>{p.phone}</div>}
                {p.email && <div style={{ wordBreak: 'break-all' }}>{p.email}</div>}
                {p.city && <div style={_nccMuted}>{p.city}</div>}
              </div>
            </div>
          ))}
        </div>
      ) : (
        <div style={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 10, overflowX: 'auto' }}>
          <table style={{ width: '100%', minWidth: 760, borderCollapse: 'collapse', fontSize: 13 }}>
            <thead style={{ background: 'var(--bg)' }}>
              <tr style={{ textAlign: 'left' }}>
                <th style={_nccTh(120)}>Mã</th>
                <th style={_nccTh()}>Tên nhà cung cấp</th>
                <th style={_nccTh(140)}>SĐT</th>
                <th style={_nccTh(220)}>Email</th>
                <th style={_nccTh(150)}>Thành phố</th>
                <th style={_nccTh(120)}>Trạng thái</th>
              </tr>
            </thead>
            <tbody>
              {items.map(p => (
                <tr key={p.id} style={{ borderTop: '1px solid var(--border)' }}>
                  <td style={_nccTd()}>
                    {p.code ? <span style={_nccMono}>{p.code}</span> : <span style={_nccMuted}>-</span>}
                  </td>
                  <td style={_nccTd()}><div style={{ fontWeight: 600 }}>{p.name || '-'}</div></td>
                  <td style={_nccTd()}>
                    {p.phone
                      ? <a href={`tel:${p.phone}`} style={{ color: 'var(--text)', textDecoration: 'none' }}>{p.phone}</a>
                      : <span style={_nccMuted}>-</span>}
                  </td>
                  <td style={_nccTd()}>
                    {p.email ? <span style={{ color: 'var(--text-2)' }}>{p.email}</span> : <span style={_nccMuted}>-</span>}
                  </td>
                  <td style={_nccTd()}>{p.city || <span style={_nccMuted}>-</span>}</td>
                  <td style={_nccTd()}><NccStatus item={p} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {!loading && !err && totalPages > 1 && (
        <div style={{ display: 'flex', gap: 10, alignItems: 'center', justifyContent: 'center', marginTop: 18 }}>
          <button className="btn btn-ghost btn-sm" disabled={page <= 1} onClick={() => setPage(p => Math.max(1, p - 1))}>
            ‹ Trước
          </button>
          <span style={{ color: 'var(--text-3)', fontSize: 13 }}>Trang {page} / {totalPages}</span>
          <button className="btn btn-ghost btn-sm" disabled={page >= totalPages} onClick={() => setPage(p => Math.min(totalPages, p + 1))}>
            Sau ›
          </button>
        </div>
      )}
    </main>
  );
}
window.NccListPage = NccListPage;
