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
// table-layout:fixed → cột giữ đúng width của <colgroup>; td truncate 1 dòng (ellipsis) thay vì co/wrap.
const _nccTd = () => ({ padding: '12px', verticalAlign: 'middle', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' });
// Ô GỘP nhiều dòng (Nhà cung cấp + Thành phố, SĐT + Email): cho phép 2 dòng xếp dọc, mỗi dòng tự truncate qua _nccLine.
const _nccTdStack = () => ({ padding: '12px', verticalAlign: 'middle', overflow: 'hidden' });
const _nccLine = { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' };
const _nccSub = { ...{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }, color: 'var(--text-3)', fontSize: 12, marginTop: 2 };
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
  const [pageSize, setPageSize] = _uNcc(20);
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
  }, [page, pageSize, q, reloadKey]);

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
        <window.TKTableScroll>
          {/* 4 cột gọn: Mã · Nhà cung cấp (Thành phố xuống dưới tên) · Liên hệ (SĐT + Email gộp) · Trạng thái.
              table-layout:fixed + colgroup → cột ổn định; ô gộp truncate từng dòng (ellipsis). */}
          <table style={{ width: '100%', minWidth: 820, tableLayout: 'fixed', borderCollapse: 'collapse', fontSize: 13 }}>
            <colgroup>
              <col style={{ width: 130 }} />{/* Mã */}
              <col />{/* Nhà cung cấp (+ thành phố) — phần còn lại */}
              <col style={{ width: 240 }} />{/* Liên hệ */}
              <col style={{ width: 170 }} />{/* Trạng thái */}
            </colgroup>
            <thead style={{ background: 'var(--bg)' }}>
              <tr style={{ textAlign: 'left' }}>
                <th style={_nccTh()}>Mã</th>
                <th style={_nccTh()}>Nhà cung cấp</th>
                <th style={_nccTh()}>Liên hệ</th>
                <th style={{ ..._nccTh(), paddingRight: 20 }}>Trạng thái</th>
              </tr>
            </thead>
            <tbody>
              {items.map(p => (
                <tr key={p.id} style={{ borderTop: '1px solid var(--border)' }}>
                  <td style={_nccTd()} title={p.code || ''}>
                    {p.code ? <span style={_nccMono}>{p.code}</span> : <span style={_nccMuted}>-</span>}
                  </td>
                  <td style={_nccTdStack()}>
                    <div style={{ ..._nccLine, fontWeight: 600 }} title={p.name || ''}>{p.name || '-'}</div>
                    {p.city && <div style={_nccSub} title={p.city}>{p.city}</div>}
                  </td>
                  <td style={_nccTdStack()}>
                    {p.phone && (
                      <div style={_nccLine} title={p.phone}>
                        <a href={`tel:${p.phone}`} style={{ color: 'var(--text)', textDecoration: 'none' }}>{p.phone}</a>
                      </div>
                    )}
                    {p.email && <div style={_nccSub} title={p.email}>{p.email}</div>}
                    {!p.phone && !p.email && <span style={_nccMuted}>-</span>}
                  </td>
                  <td style={{ ..._nccTd(), paddingRight: 20 }}><NccStatus item={p} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </window.TKTableScroll>
      )}

      {/* Pagination dùng chung (components/pagination.jsx) — giữ hiển thị + làm mờ khi đang tải trang mới. */}
      {!err && total > pageSize && (
        <window.TKPagination
          page={page} totalPages={totalPages} pageSize={pageSize}
          total={total} shown={items.length} loading={loading}
          onPage={setPage}
          onPageSize={(s) => { setPage(1); setPageSize(s); }} />
      )}
    </main>
  );
}
window.NccListPage = NccListPage;
