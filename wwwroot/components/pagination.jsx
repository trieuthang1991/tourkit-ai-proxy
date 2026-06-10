// components/pagination.jsx — Pagination control dùng chung (customers + deals + future lists).
//
// Usage:
//   <window.TKPagination page={page} totalPages={totalPages} pageSize={pageSize}
//     total={total} shown={items.length}
//     onPage={setPage} onPageSize={setPageSize}
//     suffix="khách hàng" />              // or "cơ hội", "đơn", etc.
//
// Render: "M–N / total (shown dòng)" | ← Trước | 1 … current±2 … last | Sau → | size selector
// Chỉ tự render khi total > pageSize (caller có thể bọc bằng if tương ứng).

(function () {
  'use strict';

  function TKPagination({ page, totalPages, pageSize, total, shown, onPage, onPageSize, sizes }) {
    const from = (page - 1) * pageSize + 1;
    const to   = Math.min(page * pageSize, total);
    // Window of page numbers (current ± 2, có "…" khi xa rìa)
    const winRadius = 2;
    const pages = [];
    const push = (p) => { if (p !== pages[pages.length - 1]) pages.push(p); };
    for (let p = 1; p <= totalPages; p++) {
      if (p === 1 || p === totalPages || Math.abs(p - page) <= winRadius) push(p);
      else if (Math.abs(p - page) === winRadius + 1) push('…');
    }
    const btn = (active, disabled) => ({
      minWidth: 32, padding: '6px 10px', borderRadius: 6, fontSize: 12, fontWeight: 600,
      border: '1px solid ' + (active ? 'var(--accent)' : 'var(--border)'),
      background: active ? 'var(--accent)' : 'white',
      color: active ? 'white' : (disabled ? 'var(--text-3)' : 'var(--text)'),
      cursor: disabled ? 'not-allowed' : 'pointer',
      opacity: disabled ? 0.5 : 1,
      transition: 'background 120ms ease, border-color 120ms ease',
    });
    const sizeList = sizes || [20, 50, 100, 200];
    return (
      <div style={{display: 'flex', alignItems: 'center', gap: 8, marginTop: 16, flexWrap: 'wrap'}}>
        <span style={{fontSize: 12, color: 'var(--text-3)'}}>
          {from}–{to} / {total}{shown != null ? ` (${shown} dòng hiển thị)` : ''}
        </span>
        <div style={{flex: 1}} />
        <button style={btn(false, page <= 1)}
          onClick={() => onPage(Math.max(1, page - 1))}
          disabled={page <= 1}>← Trước</button>
        {pages.map((p, i) => p === '…' ? (
          <span key={'gap' + i} style={{padding: '6px 4px', color: 'var(--text-3)', fontSize: 12}}>…</span>
        ) : (
          <button key={p} style={btn(p === page, false)} onClick={() => onPage(p)}>{p}</button>
        ))}
        <button style={btn(false, page >= totalPages)}
          onClick={() => onPage(Math.min(totalPages, page + 1))}
          disabled={page >= totalPages}>Sau →</button>
        <select value={pageSize} onChange={e => onPageSize(parseInt(e.target.value, 10))}
          style={{padding: '6px 8px', borderRadius: 6, border: '1px solid var(--border)', fontSize: 12, marginLeft: 4}}>
          {sizeList.map(s => <option key={s} value={s}>{s}/trang</option>)}
        </select>
      </div>
    );
  }

  window.TKPagination = TKPagination;
})();
