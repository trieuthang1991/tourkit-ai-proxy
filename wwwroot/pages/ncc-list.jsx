// pages/ncc-list.jsx — Nhà cung cấp (NCC): danh sách lấy TRỰC TIẾP từ TourKit AI surface.
// Luồng: authedFetch GET /api/v1/ncc/list?filter=&pageIndex=&pageSize=
//        → proxy → toutkit-app GET /api/ai/providers (surface AI, envelope đồng nhất)
//        → { section, title, count, total, items:[{id,code,name,phone,email,city,statusText,...}] } (camelCase).
// Style: mirror trang data-list của site (PageShell.PageHero + SearchControls.SearchInput + .btn + th/td theo token),
//        responsive (bảng >640px, card <=640px). authedFetch tự gắn X-Session-Id; page chỉ render sau đăng nhập.

const { useState: _uNcc, useEffect: _uENcc } = React;
const _fmtVNDNcc = (n) => (Number(n || 0)).toLocaleString('vi-VN');

// isMobile hook (≤640px) → dùng chung window.tourkitHooks.useIsMobile (lib/hooks.jsx)

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

// R1 (Sheet BugTRAV-AI): chip cảnh báo trong banner "Gợi ý nâng cao chất lượng dữ liệu".
function NccWarnChip({ n, label }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5, padding: '3px 10px', borderRadius: 999,
                   background: 'var(--surface)', border: '1px solid rgba(245,158,11,0.40)', fontSize: 12, fontWeight: 600, color: '#b45309' }}>
      <span style={{ fontSize: 12 }}>⚠</span>{(n || 0).toLocaleString('vi-VN')} {label}
    </span>
  );
}

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

// ── NCC Preview Modal ──────────────────────────────────────────────────────
// Click 1 dòng NCC → mở modal hiển thị: (1) info NCC từ row + (2) bảng giá dịch vụ
// đã lưu (fetch /api/v1/ncc/providers/{id}/services). Đọc chỉ (không sửa) — user
// muốn sửa thì vào CRM. Format bảng: Tên DV / Số lượng / Giá NET / Giá công bố / Note.
function NccPreviewModal({ item, onClose }) {
  const [rows, setRows] = _uNcc(null);
  const [loading, setLoading] = _uNcc(true);
  const [err, setErr] = _uNcc(null);

  _uENcc(() => {
    if (!item?.id) return;
    let alive = true;
    setLoading(true); setErr(null);
    window.tourkitAuth.authedFetch(`/api/v1/ncc/providers/${item.id}/services`)
      .then(r => r.json().then(d => ({ ok: r.ok, d })))
      .then(({ ok, d }) => {
        if (!alive) return;
        if (!ok) { setErr((d && d.error) || 'Không tải được bảng giá NCC.'); setRows([]); return; }
        // Response envelope: {items} hoặc mảng thẳng — normalize.
        const list = Array.isArray(d) ? d : (d.items || d.data?.items || d.data || []);
        setRows(list);
      })
      .catch(e => { if (alive) { setErr(String((e && e.message) || e)); setRows([]); } })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, [item?.id]);

  if (!item) return null;
  const fieldRow = (label, val) => (
    <div style={{ display: 'grid', gap: 2 }}>
      <span style={{ fontSize: 11, color: 'var(--text-3)', fontWeight: 600, textTransform: 'uppercase', letterSpacing: '0.05em' }}>{label}</span>
      <span style={{ fontSize: 13, color: 'var(--text)' }}>{val || <em style={{ color: 'var(--text-3)' }}>—</em>}</span>
    </div>
  );

  return (
    <div style={{ position: 'fixed', inset: 0, zIndex: 1000, background: 'rgba(15,23,42,0.55)', display: 'flex', justifyContent: 'flex-end' }}
         onClick={onClose}>
      <aside style={{ width: 'min(720px, 96vw)', height: '100vh', background: 'var(--surface)', overflowY: 'auto',
                      boxShadow: '-12px 0 32px rgba(0,0,0,0.18)' }}
             onClick={e => e.stopPropagation()}>
        {/* Header */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '16px 20px', borderBottom: '1px solid var(--border)' }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: 16, fontWeight: 700, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {item.name || '—'}
            </div>
            <div style={{ fontSize: 12, color: 'var(--text-3)' }}>{item.code || 'chưa có mã'} · {item.statusText || ''}</div>
          </div>
          <button onClick={onClose} aria-label="Đóng"
                  style={{ background: 'transparent', border: 'none', fontSize: 22, cursor: 'pointer', color: 'var(--text-3)' }}>×</button>
        </div>

        {/* Info NCC */}
        <div style={{ padding: 20 }}>
          <div style={{ background: 'var(--bg)', border: '1px solid var(--border)', borderRadius: 10, padding: 16, marginBottom: 16 }}>
            <p style={{ margin: '0 0 12px', fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--text-3)' }}>
              Thông tin nhà cung cấp
            </p>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 12 }}>
              {fieldRow('Tên NCC', item.name)}
              {fieldRow('Mã NCC', item.code)}
              {fieldRow('SĐT', item.phone)}
              {fieldRow('Email', item.email)}
              {fieldRow('Thành phố', item.city)}
              {fieldRow('Mã số thuế', item.taxCode)}
            </div>
          </div>

          {/* Bảng giá dịch vụ đã lưu */}
          <p style={{ margin: '0 0 8px', fontSize: 13, fontWeight: 700 }}>
            Bảng giá dịch vụ {rows && `(${rows.length})`}
          </p>
          {loading ? (
            <div style={{ padding: 24, textAlign: 'center', color: 'var(--text-3)' }}>Đang tải bảng giá…</div>
          ) : err ? (
            <div style={{ padding: 12, borderRadius: 8, background: 'rgba(239,68,68,0.08)',
                          border: '1px solid rgba(239,68,68,0.30)', color: 'var(--danger)' }}>{err}</div>
          ) : !rows || rows.length === 0 ? (
            <div style={{ padding: 24, textAlign: 'center', color: 'var(--text-3)', background: 'var(--bg)',
                          border: '1px solid var(--border)', borderRadius: 10 }}>
              NCC này chưa có bảng giá dịch vụ nào.
            </div>
          ) : (
            <div style={{ background: 'var(--bg)', border: '1px solid var(--border)', borderRadius: 10, overflowX: 'auto' }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
                <thead style={{ background: 'var(--surface)' }}>
                  <tr>
                    <th style={{ padding: 10, textAlign: 'left', fontWeight: 700, color: 'var(--text-3)', fontSize: 11, textTransform: 'uppercase' }}>Tên dịch vụ</th>
                    <th style={{ padding: 10, textAlign: 'right', fontWeight: 700, color: 'var(--text-3)', fontSize: 11, textTransform: 'uppercase' }}>SL</th>
                    <th style={{ padding: 10, textAlign: 'right', fontWeight: 700, color: 'var(--text-3)', fontSize: 11, textTransform: 'uppercase' }}>Giá NET</th>
                    <th style={{ padding: 10, textAlign: 'right', fontWeight: 700, color: 'var(--text-3)', fontSize: 11, textTransform: 'uppercase' }}>Giá bán</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((p, i) => {
                    // Envelope AI upstream có thể dùng camelCase or PascalCase — normalize.
                    const name = p.priceName || p.PriceName || p.name || p.Name || '—';
                    const qty = p.quantity ?? p.Quantity ?? 1;
                    const contract = p.contractPrice ?? p.ContractPrice ?? p.contract_price ?? 0;
                    const publicP = p.publicPrice ?? p.PublicPrice ?? p.public_price ?? 0;
                    return (
                      <tr key={i} style={{ borderTop: '1px solid var(--border)' }}>
                        <td style={{ padding: 10 }}>{name}</td>
                        <td style={{ padding: 10, textAlign: 'right' }}>{qty}</td>
                        <td style={{ padding: 10, textAlign: 'right' }}>{_fmtVNDNcc(contract)}</td>
                        <td style={{ padding: 10, textAlign: 'right' }}>{_fmtVNDNcc(publicP)}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </aside>
    </div>
  );
}

function NccListPage({ pushToast }) {
  const [items, setItems] = _uNcc([]);
  const [total, setTotal] = _uNcc(0);
  const [page, setPage]   = _uNcc(1);
  const [pageSize, setPageSize] = _uNcc(20);
  const [q, setQ]         = _uNcc('');
  const [serviceIdFilter, setServiceIdFilter] = _uNcc(0);   // 0 = "Tất cả loại"
  const [services, setServices] = _uNcc([]);                 // loại DV NCC (cho dropdown filter)
  const [loading, setLoad] = _uNcc(true);
  const [err, setErr]      = _uNcc(null);
  const [reloadKey, setReloadKey] = _uNcc(0);
  const [previewItem, setPreviewItem] = _uNcc(null);
  // R1: thống kê chất lượng dữ liệu (thiếu email/SĐT/bảng giá) cho banner — fetch 1 lần, backend cache 10p.
  const [stats, setStats] = _uNcc(null);
  const [statsDismissed, setStatsDismissed] = _uNcc(false);
  const isMobile = window.tourkitHooks.useIsMobile();

  _uENcc(() => {
    window.tourkitAuth.authedFetch('/api/v1/ncc/stats')
      .then(r => r.ok ? r.json() : null)
      .then(j => { if (j && !j.error) setStats(j); })
      .catch(() => { /* banner optional — lỗi thì ẩn */ });
  }, []);

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  // Tải danh sách loại DV NCC 1 lần cho dropdown filter (cùng nguồn với ncc-import: /api/v1/ncc-import/services).
  _uENcc(() => {
    window.tourkitAuth.authedFetch('/api/v1/ncc-import/services')
      .then(r => r.json())
      .then(j => setServices((j.items || []).map(x => ({ id: x.id ?? x.Id, name: x.name ?? x.Name }))))
      .catch(() => { /* fallback trống — dropdown chỉ có "Tất cả" */ });
  }, []);

  _uENcc(() => {
    let alive = true;
    setLoad(true); setErr(null);
    const qs = new URLSearchParams({ pageIndex: String(page), pageSize: String(pageSize) });
    if (q.trim()) qs.set('filter', q.trim());
    if (serviceIdFilter > 0) qs.set('serviceId', String(serviceIdFilter));
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
  }, [page, pageSize, q, serviceIdFilter, reloadKey]);

  const onSearch = (val) => { setPage(1); setQ(val || ''); };
  const onServiceFilterChange = (v) => { setPage(1); setServiceIdFilter(+v || 0); };

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

      {/* R1: Banner "Gợi ý nâng cao chất lượng dữ liệu" — đếm NCC thiếu email/SĐT/bảng giá trên TOÀN BỘ NCC. */}
      {stats && !statsDismissed && (stats.missingEmail > 0 || stats.missingPhone > 0 || stats.missingPrice > 0) && (
        <div style={{ marginTop: 16, display: 'flex', alignItems: 'flex-start', gap: 12, padding: '12px 16px',
                      background: 'rgba(245,158,11,0.08)', border: '1px solid rgba(245,158,11,0.35)', borderRadius: 'var(--radius)' }}>
          <span style={{ fontSize: 18, lineHeight: '20px' }}>💡</span>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontWeight: 700, fontSize: 13, color: '#b45309', marginBottom: 8 }}>Gợi ý nâng cao chất lượng dữ liệu</div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
              {stats.missingEmail > 0 && <NccWarnChip n={stats.missingEmail} label="NCC thiếu email" />}
              {stats.missingPhone > 0 && <NccWarnChip n={stats.missingPhone} label="thiếu SĐT" />}
              {stats.missingPrice > 0 && <NccWarnChip n={stats.missingPrice} label="chưa có bảng giá" />}
            </div>
          </div>
          <button onClick={() => setStatsDismissed(true)} aria-label="Đóng"
                  style={{ background: 'transparent', border: 'none', fontSize: 18, cursor: 'pointer', color: 'var(--text-3)', lineHeight: 1 }}>×</button>
        </div>
      )}

      <div style={{ margin: '16px 0', display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'center' }}>
        <div style={{ flex: '1 1 320px', maxWidth: 520 }}>
          <window.SearchControls.SearchInput
            value={q} onChange={onSearch} submitOnly
            placeholder="Tìm tên / mã / SĐT / email / mã số thuế… (Enter để tìm)" />
        </div>
        {/* Bộ lọc "Loại nhà cung cấp" — nguồn cùng với dropdown Loại DV bên NCC Import. */}
        <select value={serviceIdFilter} onChange={e => onServiceFilterChange(e.target.value)}
                title="Lọc theo loại nhà cung cấp"
                style={{ padding: '8px 12px', border: '1px solid var(--border)', borderRadius: 8, fontSize: 13,
                         background: 'var(--surface)', color: 'var(--text)', minWidth: 200 }}>
          <option value={0}>— Tất cả loại NCC —</option>
          {services.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
        </select>
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
            <div key={p.id} onClick={() => setPreviewItem(p)}
                 style={{ background: 'var(--surface)', border: '1px solid var(--border)',
                          borderRadius: 'var(--radius-md)', padding: 14, cursor: 'pointer' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, alignItems: 'flex-start' }}>
                <div style={{ fontWeight: 700 }}>{p.name || '-'}</div>
                <NccStatus item={p} />
              </div>
              {p.code && <div style={{ ..._nccMono, marginTop: 2 }}>{p.code}</div>}
              <div style={{ marginTop: 8, fontSize: 13, color: 'var(--text-2)', display: 'grid', gap: 3 }}>
                {p.serviceNames && (
                  <div style={{ display: 'inline-flex', flexWrap: 'wrap', gap: 4 }}>
                    {p.serviceNames.split(',').map((s, i) => (
                      <span key={i} style={{ padding: '1px 8px', borderRadius: 999, fontSize: 11, background: 'var(--bg)',
                                             border: '1px solid var(--border)', color: 'var(--text-2)' }}>{s.trim()}</span>
                    ))}
                  </div>
                )}
                {p.phone && <div>{p.phone}</div>}
                {p.email && <div style={{ wordBreak: 'break-all' }}>{p.email}</div>}
                {p.city && <div style={_nccMuted}>{p.city}</div>}
              </div>
            </div>
          ))}
        </div>
      ) : (
        <window.TKTableScroll>
          {/* 5 cột: Mã · Nhà cung cấp (Thành phố xuống dưới tên) · Loại NCC · Liên hệ (SĐT + Email gộp) · Trạng thái.
              table-layout:fixed + colgroup → cột ổn định; ô gộp truncate từng dòng (ellipsis). */}
          <table style={{ width: '100%', minWidth: 960, tableLayout: 'fixed', borderCollapse: 'collapse', fontSize: 13 }}>
            <colgroup>
              <col style={{ width: 130 }} />{/* Mã */}
              <col />{/* Nhà cung cấp (+ thành phố) — phần còn lại */}
              <col style={{ width: 180 }} />{/* Loại NCC */}
              <col style={{ width: 240 }} />{/* Liên hệ */}
              <col style={{ width: 170 }} />{/* Trạng thái */}
            </colgroup>
            <thead style={{ background: 'var(--bg)' }}>
              <tr style={{ textAlign: 'left' }}>
                <th style={_nccTh()}>Mã</th>
                <th style={_nccTh()}>Nhà cung cấp</th>
                <th style={_nccTh()}>Loại NCC</th>
                <th style={_nccTh()}>Liên hệ</th>
                <th style={{ ..._nccTh(), paddingRight: 20 }}>Trạng thái</th>
              </tr>
            </thead>
            <tbody>
              {items.map(p => (
                <tr key={p.id} onClick={() => setPreviewItem(p)}
                    style={{ borderTop: '1px solid var(--border)', cursor: 'pointer' }}
                    onMouseEnter={e => e.currentTarget.style.background = 'var(--bg)'}
                    onMouseLeave={e => e.currentTarget.style.background = ''}>
                  <td style={_nccTd()} title={p.code || ''}>
                    {p.code ? <span style={_nccMono}>{p.code}</span> : <span style={_nccMuted}>-</span>}
                  </td>
                  <td style={_nccTdStack()}>
                    <div style={{ ..._nccLine, fontWeight: 600 }} title={p.name || ''}>{p.name || '-'}</div>
                    {p.city && <div style={_nccSub} title={p.city}>{p.city}</div>}
                  </td>
                  <td style={_nccTd()} title={p.serviceNames || ''}>
                    {p.serviceNames
                      ? <span style={{ fontSize: 12, color: 'var(--text-2)' }}>{p.serviceNames}</span>
                      : <span style={_nccMuted}>-</span>}
                  </td>
                  <td style={_nccTdStack()}>
                    {/* R1: thiếu SĐT/email → hiện "Chưa có" (thay vì để trống) để lộ NCC cần bổ sung dữ liệu. */}
                    <div style={_nccLine} title={p.phone || ''}>
                      {p.phone
                        ? <a href={`tel:${p.phone}`} style={{ color: 'var(--text)', textDecoration: 'none' }}>{p.phone}</a>
                        : <em style={_nccMuted}>Chưa có</em>}
                    </div>
                    <div style={_nccSub} title={p.email || ''}>
                      {p.email || <em style={{ fontStyle: 'italic' }}>Chưa có</em>}
                    </div>
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

      {/* Preview NCC (click row/card) — modal side-drawer, lazy fetch bảng giá. */}
      {previewItem && <NccPreviewModal item={previewItem} onClose={() => setPreviewItem(null)} />}
    </main>
  );
}
window.NccListPage = NccListPage;
