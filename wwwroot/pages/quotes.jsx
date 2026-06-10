// pages/quotes.jsx — Danh sách báo giá tour đã lưu (DB-backed, replace localStorage cũ).
// Load từ GET /api/v1/tour-quotes (paginated, per-tenant). Click row → /tour-builder?id=X mở lại.

const { useState: _qS, useEffect: _qE } = React;

function QuotesPage({ pushToast }) {
  const { navigate } = window.tourkitRouter;
  const [items, setItems]       = _qS([]);
  const [total, setTotal]       = _qS(0);
  const [page, setPage]         = _qS(1);
  const [pageSize, setPageSize] = _qS(20);
  const [search, setSearch]     = _qS('');
  const [loading, setLoading]   = _qS(true);
  const [err, setErr]           = _qS(null);

  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const vnd = (n) => (Number(n) || 0).toLocaleString('vi-VN');

  const load = async () => {
    setLoading(true); setErr(null);
    try {
      const q = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
      if (search.trim()) q.set('search', search.trim());
      const r = await window.tourkitAuth.authedFetch('/api/v1/tour-quotes?' + q.toString());
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const d = await r.json();
      setItems(d.items || []);
      setTotal(d.total ?? 0);
    } catch (e) { setErr(e.message); }
    finally { setLoading(false); }
  };

  _qE(() => { setPage(1); }, [pageSize, search]);
  _qE(() => {
    const t = setTimeout(load, 300);    // debounce search + instant cho page change
    return () => clearTimeout(t);
  }, [page, pageSize, search]);

  const removeOne = async (id, title) => {
    if (!(await window.appConfirm(`Xóa báo giá "${title || id}"?`, {
      title: 'Xóa báo giá', danger: true, confirmLabel: 'Xóa'
    }))) return;
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/tour-quotes/' + encodeURIComponent(id), { method: 'DELETE' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      pushToast('Đã xóa');
      load();
    } catch (e) { pushToast('Xóa lỗi: ' + e.message, 'error'); }
  };

  const fmtDate = (iso) => {
    if (!iso) return '—';
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    return d.toLocaleString('vi-VN', { hour12: false });
  };

  return (
    <main className="page" style={{padding: '24px 32px', width: '100%'}}>
      <window.PageShell.PageHero
        icon="bookmark"
        title="Báo giá tour đã lưu"
        badge="DB persist"
        sub="Danh sách báo giá đã lưu trên hệ thống (per-tenant). Click 1 dòng để mở lại trong Trình soạn."
        status={{ label: total > 0 ? `${total} BÁO GIÁ` : (loading ? 'ĐANG TẢI' : 'CHƯA CÓ DỮ LIỆU'),
          detail: search ? `Lọc: "${search}"` : 'Tất cả' }}
        actions={<>
          <button className="btn btn-ghost btn-sm" onClick={load} disabled={loading}>
            <Icon name="refresh" size={14} /> Refresh
          </button>
          <button className="btn btn-primary btn-sm" onClick={() => navigate('/tour-builder')}>
            <Icon name="plus" size={14} /> Tạo báo giá mới
          </button>
        </>}
      />

      <div style={{display:'flex', gap:10, marginBottom:16, alignItems:'center'}}>
        <window.SearchControls.SearchInput value={search} onChange={setSearch}
          placeholder="Tìm tên tour / khách / SĐT…" />
        <window.DataControls.StatRow shown={items.length} total={total} suffix="báo giá" />
      </div>

      {err && (
        <div style={{padding:12, background:'#fef2f2', color:'#991b1b', borderRadius:8, marginBottom:12, fontSize:13}}>
          Lỗi: {err}
        </div>
      )}

      {loading ? (
        <div style={{padding:60, textAlign:'center', color:'var(--text-3)'}}>Đang tải…</div>
      ) : items.length === 0 ? (
        <div style={{padding:60, textAlign:'center', color:'var(--text-3)', background:'#fafafa', borderRadius:12}}>
          <div style={{fontSize:14, marginBottom:8}}>{search ? 'Không có báo giá khớp bộ lọc.' : 'Chưa có báo giá nào.'}</div>
          <button className="btn btn-primary btn-sm" onClick={() => navigate('/tour-builder')}>Tạo báo giá đầu tiên</button>
        </div>
      ) : (
        <div style={{background:'white', border:'1px solid var(--border)', borderRadius:10, overflow:'hidden'}}>
          <table style={{width:'100%', borderCollapse:'collapse', fontSize:13}}>
            <thead style={{background:'var(--bg)'}}>
              <tr style={{textAlign:'left'}}>
                <th style={th()}>Tour / Khách</th>
                <th style={th(160)}>Thời gian</th>
                <th style={th(80)}>Pax</th>
                <th style={th(140)}>Tổng thu</th>
                <th style={th(120)}>Lợi nhuận</th>
                <th style={th(80)}>Margin</th>
                <th style={th(170)}>Cập nhật</th>
                <th style={th(60)}></th>
              </tr>
            </thead>
            <tbody>
              {items.map(it => {
                const isRedZone = it.marginPercent != null && it.marginPercent < 15;
                return (
                  <tr key={it.id} style={{borderTop:'1px solid var(--border)', cursor:'pointer'}}
                      onClick={() => navigate('/tour-builder?id=' + encodeURIComponent(it.id))}>
                    <td style={td()}>
                      <div style={{fontWeight:600}}>{it.title || <em style={{color:'var(--text-3)'}}>(chưa đặt tên)</em>}</div>
                      <div style={{color:'var(--text-3)', fontSize:11, marginTop:2}}>
                        {it.customerName || '—'}{it.customerPhone ? ' · ' + it.customerPhone : ''}
                      </div>
                    </td>
                    <td style={td()}>
                      <div style={{fontSize:12}}>{it.startDate || '—'}{it.endDate ? ' → ' + it.endDate : ''}</div>
                      {it.marketName && <div style={{color:'var(--text-3)', fontSize:11}}>{it.marketName}</div>}
                    </td>
                    <td style={td()}>
                      <span style={{fontSize:12}}>{it.adultCount}NL{it.childCount > 0 ? ' + ' + it.childCount + 'TE' : ''}</span>
                    </td>
                    <td style={td()}><span style={{fontWeight:600, color:'#10B981'}}>{vnd(it.totalRevenue)}₫</span></td>
                    <td style={td()}>
                      <span style={{fontWeight:600, color: it.profit >= 0 ? 'var(--text-1)' : '#ef4444'}}>
                        {vnd(it.profit)}₫
                      </span>
                    </td>
                    <td style={td()}>
                      {it.marginPercent != null ? (
                        <span style={{fontWeight:700, color: isRedZone ? '#ef4444' : (it.marginPercent >= 25 ? '#10B981' : '#F59E0B')}}>
                          {it.marginPercent.toFixed(1)}%
                        </span>
                      ) : <span style={{color:'var(--text-3)'}}>—</span>}
                    </td>
                    <td style={td()}>
                      <div style={{fontSize:12, color:'var(--text-2)'}}>{fmtDate(it.updatedAt)}</div>
                      {it.createdBy && <div style={{fontSize:10, color:'var(--text-3)'}}>{it.createdBy}</div>}
                    </td>
                    <td style={td()} onClick={e => e.stopPropagation()}>
                      <button className="btn btn-ghost btn-sm" onClick={() => removeOne(it.id, it.title)} title="Xóa">×</button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {!loading && total > pageSize && (
        <window.TKPagination page={page} totalPages={totalPages} pageSize={pageSize}
          total={total} shown={items.length}
          onPage={setPage} onPageSize={setPageSize} />
      )}
    </main>
  );
}

const th = (w) => ({ padding: '10px 12px', fontWeight: 700, fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--text-3)', width: w, whiteSpace: 'nowrap' });
const td = () => ({ padding: '12px', verticalAlign: 'middle' });

window.QuotesPage = QuotesPage;
