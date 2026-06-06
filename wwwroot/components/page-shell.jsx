// components/page-shell.jsx — Khung trang DÙNG CHUNG, đồng nhất format Trợ lý số liệu v2.5.
// Mục đích: mọi page tính năng (Customers, Mail, Visa, Deals, TourBuilder, AI Usage...)
// đều có cùng hero card cam-đen + eyebrow + pane → 1 ngôn ngữ thiết kế.
//
// API:
//   <PageHero
//      icon="users"             // tên Icon (lib/icons.jsx)
//      title="KHÁCH HÀNG"        // sẽ tự uppercase
//      badge="AI review"          // pill cam nhỏ cạnh title (tùy chọn)
//      sub="Mô tả ngắn…"
//      status={{ label:'DỮ LIỆU LIVE', detail:'staging.tourkit.vn', tone:'live' }}
//      actions={ <> ... </> }    // nút bên phải (refresh, primary CTA…)
//   />
//
//   <Eyebrow sub="phụ">TIÊU ĐỀ SECTION</Eyebrow>
//   <Pane>{children}</Pane>           // card bo lớn padding 18px

(function () {
  'use strict';

  function PageHero({ icon = 'sparkle', title, badge, sub, status, actions }) {
    return (
      <header className="ph-hero">
        <div className="ph-hero-mark"><Icon name={icon} size={22} stroke={2.4} /></div>
        <div className="ph-hero-text">
          <h1 className="ph-hero-title">
            {String(title || '').toUpperCase()}
            {badge && <span className="ph-hero-badge">{badge}</span>}
          </h1>
          {sub && <p className="ph-hero-sub">{sub}</p>}
        </div>
        {status && (
          <div className="ph-hero-status">
            <span className={'ph-status-pulse' + (status.tone === 'idle' ? ' idle' : '')} />
            <div className="ph-status-text">
              <b>{status.label || '—'}</b>
              {status.detail && <em>{status.detail}</em>}
            </div>
          </div>
        )}
        {actions && <div className="ph-hero-actions">{actions}</div>}
      </header>
    );
  }

  function Eyebrow({ children, sub }) {
    return (
      <div className="ph-eyebrow">
        <span>{children}</span>
        {sub && <em> · {sub}</em>}
      </div>
    );
  }

  function Pane({ children, className = '' }) {
    return <section className={'ph-pane ' + className}>{children}</section>;
  }

  window.PageShell = { PageHero, Eyebrow, Pane };
})();
