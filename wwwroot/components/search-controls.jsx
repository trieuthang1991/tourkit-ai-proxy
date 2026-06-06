// components/search-controls.jsx — Bộ control TÌM KIẾM dùng chung, mượn pattern từ mobile
// (toutkit-app/MauiApp/Components/UI/AppSearchInput, AppSearchSelect, FilterChip, BottomSheet).
// Lý do tách: cùng pattern dùng ở Khách hàng, Cơ hội (Deals), TourBuilder và sau này nữa —
// một nguồn duy nhất, đổi 1 lần áp tất cả.
//
// API:
//   <SearchInput value, onChange, placeholder, debounceMs?, onClear? />
//   <FilterChip on, onClick, children />   // bo tròn pill, on = nền cam
//   <FilterChipRow>{chips}</FilterChipRow> // overflow-x scroll, mobile-first
//   <FilterButton onClick, count? />       // nút "Bộ lọc" + dot số filter đang bật
//   <BottomSheet open, onClose, title, children />
//   <SearchSelect items, value, onChange, placeholder, getLabel?, getKey? />
//
// CSS đi cùng: .sc-* trong styles.css (cuối file).

(function () {
  'use strict';
  const { useState: s, useEffect: e, useRef: r, useMemo: m } = React;

  // ─── SearchInput: pill có icon search, nút × xóa nhanh, debounce ────────────
  function SearchInput({ value, onChange, placeholder = 'Tìm…', debounceMs = 0, onClear }) {
    const [local, setLocal] = s(value || '');
    e(() => setLocal(value || ''), [value]);
    e(() => {
      if (debounceMs <= 0) return;
      const t = setTimeout(() => { if (local !== value) onChange(local); }, debounceMs);
      return () => clearTimeout(t);
    }, [local]);
    const fire = (v) => { setLocal(v); if (debounceMs <= 0) onChange(v); };
    return (
      <div className="sc-search">
        <Icon name="search" size={14} />
        <input value={local} onChange={ev => fire(ev.target.value)} placeholder={placeholder} />
        {local && (
          <button className="sc-search-x" onClick={() => { fire(''); onClear?.(); }} aria-label="Xóa">
            <Icon name="close" size={13} />
          </button>
        )}
      </div>
    );
  }

  // ─── FilterChip: pill toggle (mẫu mobile button chip) ───────────────────────
  function FilterChip({ on, onClick, children, tone = 'primary' }) {
    return (
      <button className={`sc-chip sc-chip-${tone}` + (on ? ' on' : '')} onClick={onClick}>
        {children}
      </button>
    );
  }

  // Row chip có scroll ngang (mobile-friendly, mẫu hide-scrollbar mobile)
  function FilterChipRow({ children }) {
    return <div className="sc-chip-row">{children}</div>;
  }

  // ─── FilterButton: nút "Bộ lọc" mở bottom sheet, có badge số filter đang bật
  function FilterButton({ onClick, count = 0 }) {
    return (
      <button className="sc-filter-btn" onClick={onClick}>
        <Icon name="sliders" size={15} /> Bộ lọc
        {count > 0 && <span className="sc-filter-dot">{count}</span>}
      </button>
    );
  }

  // ─── BottomSheet: drawer từ dưới lên (mẫu mobile Customer "Bộ lọc nâng cao") ─
  function BottomSheet({ open, onClose, title, children, footer }) {
    e(() => {
      if (!open) return;
      const f = (ev) => { if (ev.key === 'Escape') onClose(); };
      document.addEventListener('keydown', f);
      return () => document.removeEventListener('keydown', f);
    }, [open]);
    if (!open) return null;
    return (
      <>
        <div className="sc-sheet-back" onClick={onClose} />
        <div className="sc-sheet" role="dialog" aria-label={title}>
          <div className="sc-sheet-grip" />
          <div className="sc-sheet-head">
            <div className="sc-sheet-title">{title}</div>
            <button className="sc-sheet-x" onClick={onClose} aria-label="Đóng"><Icon name="close" size={16} /></button>
          </div>
          <div className="sc-sheet-body">{children}</div>
          {footer && <div className="sc-sheet-foot">{footer}</div>}
        </div>
      </>
    );
  }

  // ─── SearchSelect: dropdown có ô tìm (mẫu AppSearchSelect mobile) ──────────
  function SearchSelect({ items, value, onChange, placeholder = '— Chọn —', getLabel, getKey, allowClear = true }) {
    const [open, setOpen] = s(false);
    const [q, setQ] = s('');
    const ref = r(null);
    e(() => {
      const f = (ev) => { if (ref.current && !ref.current.contains(ev.target)) setOpen(false); };
      document.addEventListener('mousedown', f);
      return () => document.removeEventListener('mousedown', f);
    }, []);
    const label = (it) => (getLabel ? getLabel(it) : String(it));
    const keyOf = (it) => (getKey ? getKey(it) : label(it));
    const selected = m(() => items.find(it => keyOf(it) === value) ?? (typeof value === 'string' ? value : null), [items, value]);
    const filtered = m(() => {
      if (!q.trim()) return items;
      const ql = q.toLowerCase();
      return items.filter(it => label(it).toLowerCase().includes(ql));
    }, [items, q]);
    return (
      <div className="sc-ss" ref={ref}>
        <button type="button" className="sc-ss-btn" onClick={() => setOpen(o => !o)}>
          <span className={selected ? '' : 'sc-ph'}>{selected ? (typeof selected === 'string' ? selected : label(selected)) : placeholder}</span>
          <span className="sc-ss-end">
            {allowClear && value && (
              <span className="sc-ss-clear" role="button" onClick={ev => { ev.stopPropagation(); onChange(null); }} title="Xóa"><Icon name="close" size={12} /></span>
            )}
            <Icon name="chevronDown" size={14} />
          </span>
        </button>
        {open && (
          <div className="sc-ss-drop">
            <div className="sc-ss-search">
              <Icon name="search" size={13} />
              <input autoFocus placeholder="Tìm…" value={q} onChange={ev => setQ(ev.target.value)} />
            </div>
            <div className="sc-ss-list">
              {filtered.length === 0 && <div className="sc-ss-empty">Không có kết quả</div>}
              {filtered.map((it, i) => {
                const k = keyOf(it);
                return (
                  <button key={k + i} type="button"
                    className={'sc-ss-item' + (k === value ? ' on' : '')}
                    onClick={() => { onChange(k); setOpen(false); setQ(''); }}>
                    {label(it)}
                  </button>
                );
              })}
            </div>
          </div>
        )}
      </div>
    );
  }

  // ─── AdvancedFilterSheet — wrapper sẵn footer Apply/Clear + draft state ────
  // Tránh page nào cũng tự viết draft + 2 nút footer. Children render schema-free
  // (page tự quyết các section bên trong), sheet lo lifecycle.
  function AdvancedFilterSheet({ open, onClose, title = 'Bộ lọc nâng cao',
                                 value, onApply, defaultValue, children }) {
    const [draft, setDraft] = s(value || {});
    e(() => { if (open) setDraft(value || {}); }, [open]);
    const apply = () => { onApply(draft); onClose(); };
    const clear = () => { setDraft(defaultValue || {}); onApply(defaultValue || {}); onClose(); };
    // children là 1 function nhận { draft, set } → page render section trong sheet
    return (
      <BottomSheet open={open} onClose={onClose} title={title}
        footer={<>
          <button className="sc-sheet-btn ghost" onClick={clear}><Icon name="trash" size={14} /> Xóa lọc</button>
          <button className="sc-sheet-btn primary" onClick={apply}><Icon name="check" size={14} stroke={2.5} /> Áp dụng</button>
        </>}>
        {typeof children === 'function'
          ? children({ draft, set: (k, v) => setDraft(d => ({ ...d, [k]: v })) })
          : children}
      </BottomSheet>
    );
  }

  window.SearchControls = { SearchInput, FilterChip, FilterChipRow, FilterButton, BottomSheet, SearchSelect, AdvancedFilterSheet };
})();
