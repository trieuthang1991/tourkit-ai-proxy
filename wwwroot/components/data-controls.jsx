// components/data-controls.jsx — Bộ control HIỂN THỊ DATA dùng chung.
// Mượn pattern mobile (AppKpiCard chip, group tabs scroll ngang, date range icon-calendar).
// Mục tiêu: page nào cũng dùng được → giao diện đồng nhất, đổi 1 chỗ áp tất cả.
//
// API:
//   <KpiStrip items={[{ icon, label, value, highlight?, onClick? }]} />
//   <GroupTabs items={[{ value, label, count?, color? }]} value onChange />
//   <DateRange from to onFrom onTo />
//   <StatRow shown={N} total={M} suffix="khách hàng" />
//
// CSS: .dc-* (cuối styles.css).

(function () {
  'use strict';

  // ─── KPI strip — chip thống kê scroll ngang (mobile KPI card) ──────────────
  function KpiStrip({ items = [] }) {
    return (
      <div className="dc-kpi">
        {items.map((it, i) => (
          <button key={i} type="button"
            className={'dc-kpi-chip' + (it.highlight ? ' hi' : '') + (it.onClick ? ' clickable' : '')}
            onClick={it.onClick} disabled={!it.onClick}>
            {it.icon && <span className="dc-kpi-ic"><Icon name={it.icon} size={13} /></span>}
            <span className="dc-kpi-lbl">{it.label}</span>
            <b className="dc-kpi-val">{it.value}</b>
          </button>
        ))}
      </div>
    );
  }

  // ─── Group tabs — tab scroll ngang có count badge (mobile groupTabs) ───────
  function GroupTabs({ items = [], value, onChange }) {
    return (
      <div className="dc-tabs">
        {items.map(t => (
          <button key={String(t.value)} type="button"
            className={'dc-tab' + (value === t.value ? ' on' : '')}
            style={t.color && value === t.value ? { background: t.color, borderColor: t.color } : undefined}
            onClick={() => onChange(t.value)}>
            <span>{t.label}</span>
            {t.count != null && <span className="dc-tab-cnt">{t.count}</span>}
          </button>
        ))}
      </div>
    );
  }

  // ─── DateRange — 2 ô date icon calendar (mobile mẫu) ───────────────────────
  function DateRange({ from, to, onFrom, onTo }) {
    return (
      <div className="dc-range">
        <div className="dc-range-fld">
          <Icon name="calendar" size={14} />
          <input type="date" value={from || ''} onChange={e => onFrom(e.target.value)} placeholder="Từ ngày" />
        </div>
        <div className="dc-range-fld">
          <Icon name="calendar" size={14} />
          <input type="date" value={to || ''} onChange={e => onTo(e.target.value)} placeholder="Đến ngày" />
        </div>
      </div>
    );
  }

  // ─── StatRow — text "Hiển thị N/M …" ──────────────────────────────────────
  function StatRow({ shown, total, suffix = '' }) {
    if (shown == null || total == null) return null;
    return (
      <div className="dc-stat-row">
        Hiển thị <b>{shown}</b>{total != shown && <>/<span className="dc-stat-tot">{total}</span></>} {suffix}
      </div>
    );
  }

  window.DataControls = { KpiStrip, GroupTabs, DateRange, StatRow };
})();
