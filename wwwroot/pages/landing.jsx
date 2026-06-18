// pages/landing.jsx — Trang giới thiệu TRAV-AI (public landing).
// Visual rhythm: Hero split → quick value chips → 6-card feature bento →
// 3-step "Cách bắt đầu" → CTA orange band → Footer minimal.
// Anim: mascot float (CSS keyframes), scroll-reveal (IntersectionObserver),
// hover lift trên feature card. Tất cả tôn trọng prefers-reduced-motion.
//
// Auth gate: click feature/CTA mà CHƯA có tourkit_tk_session → mở ConsultPopup.
// Đã đăng nhập → navigate tới route tương ứng (/wizard, /assistant, …).

(function () {
  'use strict';
  const { useEffect, useState, useRef, useMemo } = React;
  const navigate = window.tourkitRouter.navigate;

  // ──────────────────────────────────────────────────────────────────────────
  // Catalog tính năng (1 nguồn — UI và popup share). Mỗi feature có mascot
  // riêng để mỗi card có nhân vật khác nhau (12 robots → 6 cho features +
  // 1 hero + 3 steps + 2 decoration).
  // 8 tính năng AI — khớp đầy đủ với route đang có (không gồm "Báo giá đã lưu"
  // vì là CRM list nội bộ, không phải tính năng AI để quảng).
  // Bento: 2 large (Wizard + Trợ lý) + 6 small. 6-col grid, exact 8 cells.
  const FEATURES = [
    {
      key: 'wizard',
      title: 'AI Tính giá Tour',
      pitch: 'Vẽ một lộ trình tour mới trong 5 phút. AI gợi ý khách sạn, xe, giá vốn và công thức báo giá. Bạn chỉ duyệt và gửi.',
      route: '/wizard',
      robot: 'robot1.png',
      tone: 'warm',
    },
    {
      key: 'assistant',
      title: 'Trợ lý số liệu',
      pitch: 'Hỏi như Google: "Doanh thu thị trường Hàn tháng này?". AI rút số từ CRM thật, vẽ biểu đồ, phân tích tiếng Việt cho lãnh đạo.',
      route: '/assistant',
      robot: 'robot2.png',
      tone: 'cool',
    },
    {
      key: 'mail',
      title: 'Hộp thư AI',
      pitch: 'Gmail tự phân 6 nhóm (đặt tour, báo giá, khiếu nại). AI viết nháp trả lời theo 4 tông giọng, bạn duyệt và gửi.',
      route: '/mail',
      robot: 'robot3.png',
      tone: 'white',
    },
    {
      key: 'customers',
      title: 'Chấm điểm khách hàng',
      pitch: 'AI xếp hạng A / B / C / D từng khách, chỉ rõ đáng giữ hay nên buông, gợi 1 hành động cụ thể trong tuần.',
      route: '/customers',
      robot: 'robot5.png',
      tone: 'white',
    },
    {
      key: 'deals',
      title: 'AI phân tích Cơ hội',
      pitch: 'AI quét pipeline, gợi cơ hội nào nên đẩy, cơ hội nào sắp tuột, lý do cụ thể theo lịch sử tương tác và mức chi tiêu khách.',
      route: '/deals',
      robot: 'robot6.png',
      tone: 'white',
    },
    {
      key: 'visa',
      title: 'Visa AI',
      pitch: 'Quét hồ sơ visa, dự đoán khả năng đậu, chấm rủi ro theo từng quốc gia. Cảnh báo điểm yếu cần bổ sung trước nộp.',
      route: '/visa',
      robot: 'robot7.png',
      tone: 'white',
    },
    {
      key: 'tour-builder',
      title: 'Bóc tour bằng AI',
      pitch: 'Dán mô tả tour viết tay của sales, AI tự tách thành lịch trình, lịch xe, danh sách dịch vụ và giá vốn từng buổi.',
      route: '/tour-builder',
      robot: 'robot8.PNG',
      tone: 'white',
    },
    {
      key: 'widget',
      title: 'Widget chat khách',
      pitch: 'Nhúng một dòng code vào website công ty. Khách hỏi tour AI trả lời 24/7, đúng giọng và đúng kho dữ liệu của bạn.',
      route: '/widget-admin',
      robot: 'robot10.PNG',
      tone: 'white',
    },
    {
      key: 'ncc-import',
      title: 'Import NCC bằng AI',
      pitch: 'Kéo file Excel cũ, CSV hay dán text từ Word vào. AI bóc tách thành 13 cột chuẩn, xuất file mẫu để upload trực tiếp lên hệ thống.',
      route: '/ncc-import',
      robot: 'robot11.PNG',
      tone: 'white',
    },
  ];

  // Quick value chips dưới hero (max 4, ngắn gọn — không phải fake numbers).
  const QUICK_VALUES = [
    'Tạo tour trong 5 phút',
    'Trả mail khách tự động',
    'Chấm điểm hồ sơ + cơ hội',
    'Báo cáo nói tiếng Việt',
  ];

  // ──────────────────────────────────────────────────────────────────────────
  // Hook scroll-reveal: gắn .in vào element khi enter viewport (chỉ 1 lần).
  // Tôn trọng prefers-reduced-motion → set in luôn, bỏ animation.
  function useReveal() {
    const ref = useRef(null);
    useEffect(() => {
      const el = ref.current;
      if (!el) return;
      if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
        el.classList.add('in'); return;
      }
      const io = new IntersectionObserver((entries) => {
        entries.forEach(e => {
          if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
        });
      }, { threshold: 0.18 });
      io.observe(el);
      return () => io.disconnect();
    }, []);
    return ref;
  }

  function Reveal({ children, delay = 0, as: Tag = 'div', className = '' }) {
    const ref = useReveal();
    const style = delay ? { transitionDelay: delay + 'ms' } : undefined;
    return <Tag ref={ref} className={'lp-reveal ' + className} style={style}>{children}</Tag>;
  }

  // Initials cho avatar (2 ký tự đầu các từ cuối tên)
  function initials(name) {
    if (!name) return '?';
    const w = name.trim().split(/\s+/);
    return (w.slice(-2).map(x => x[0] || '').join('') || name[0] || '?').toUpperCase();
  }

  // ──────────────────────────────────────────────────────────────────────────
  function LandingPage() {
    const [popup, setPopup] = useState({ open: false, feature: null });
    // Auth state qua tourkitAuth — real-time, listen 'tourkit-auth-changed' + 'storage'
    // (login/logout từ tab khác cũng cập nhật).
    const [authUser, setAuthUser] = useState(() => window.tourkitAuth?.getUser?.() || null);
    const [userMenu, setUserMenu] = useState(false);

    useEffect(() => {
      if (!window.tourkitAuth) return;
      const refresh = () => setAuthUser(window.tourkitAuth.getUser());
      const off = window.tourkitAuth.onChange(refresh);
      // Có session nhưng chưa có user (cold-load sau restart) → pull /api/v1/session
      if (window.tourkitAuth.isAuthed() && !window.tourkitAuth.getUser()) {
        window.tourkitAuth.refresh?.().then(u => { if (u) setAuthUser(u); }).catch(() => {});
      }
      return off;
    }, []);

    // Đóng user menu khi click ngoài
    useEffect(() => {
      if (!userMenu) return;
      const onClick = (e) => { if (!e.target.closest('.lp-user-chip-wrap')) setUserMenu(false); };
      window.addEventListener('click', onClick);
      return () => window.removeEventListener('click', onClick);
    }, [userMenu]);

    const isAuthed = !!authUser;

    // Click 1 feature: đã đăng nhập → vào trực tiếp. Chưa → popup tư vấn.
    const onFeatureClick = (f) => {
      if (isAuthed) { navigate(f.route); return; }
      setPopup({ open: true, feature: f.title });
    };

    const onCtaClick = (label) => {
      if (isAuthed) { navigate('/home'); return; }
      setPopup({ open: true, feature: label || null });
    };

    const onLogout = () => {
      window.tourkitAuth?.logout?.();
      setUserMenu(false);
    };

    const goToFeatures = () => {
      document.querySelector('#lp-features')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };

    // Mobile menu (chỉ hiện ở ≤900px). Khoá scroll body khi mở.
    const [mobileMenu, setMobileMenu] = useState(false);
    useEffect(() => {
      if (mobileMenu) document.body.classList.add('lp-nav-locked');
      else document.body.classList.remove('lp-nav-locked');
      return () => document.body.classList.remove('lp-nav-locked');
    }, [mobileMenu]);
    const closeMobileMenu = () => setMobileMenu(false);
    const goAndClose = (fn) => { fn(); closeMobileMenu(); };

    return (
      <div className="lp">
        {/* ── Topbar tối giản ──────────────────────────────────────────────── */}
        <header className="lp-topbar">
          <a className="lp-brand" href="#/" aria-label="TOURKIT">
            <img className="lp-brand-logo" src="/images/tourkit-logo.png" alt="TOURKIT" loading="lazy" decoding="async"
              onError={(e) => { e.target.style.display = 'none'; const t = e.target.nextElementSibling; if (t) t.style.display = 'flex'; }} />
            <span className="lp-brand-name" style={{ display: 'none' }}>TOURKIT<em>cho công ty du lịch</em></span>
          </a>
          {/* Hamburger — chỉ hiện mobile (CSS gate) */}
          <button
            className={'lp-burger' + (mobileMenu ? ' is-open' : '')}
            onClick={() => setMobileMenu(v => !v)}
            aria-label={mobileMenu ? 'Đóng menu' : 'Mở menu'}
            aria-expanded={mobileMenu}
          >
            <span></span><span></span><span></span>
          </button>

          <nav className="lp-topnav">
            <a href="#features" onClick={(e) => { e.preventDefault(); goToFeatures(); }}>Tính năng</a>
            <a href="#how" onClick={(e) => { e.preventDefault(); document.querySelector('#lp-how')?.scrollIntoView({ behavior: 'smooth' }); }}>Cách bắt đầu</a>
            {isAuthed ? (
              <div className="lp-user-chip-wrap">
                <button
                  className="lp-user-chip"
                  onClick={(e) => { e.stopPropagation(); setUserMenu(v => !v); }}
                  aria-haspopup="menu"
                  aria-expanded={userMenu}
                  title={[authUser.fullName, authUser.companyName].filter(Boolean).join(' · ')}
                >
                  <span className="lp-user-avatar">{initials(authUser.fullName || authUser.companyName)}</span>
                  <span className="lp-user-meta">
                    <span className="lp-user-name">{authUser.fullName || 'Đã đăng nhập'}</span>
                    {authUser.companyName && <span className="lp-user-org">{authUser.companyName}</span>}
                  </span>
                  <Icon name="chevronDown" size={14} stroke={2.2} />
                </button>
                {userMenu && (
                  <div className="lp-user-menu" role="menu">
                    <button className="lp-user-menu-item" onClick={() => { setUserMenu(false); navigate('/home'); }}>
                      <Icon name="sparkle" size={14} stroke={2.2} /> Vào ứng dụng
                    </button>
                    <button className="lp-user-menu-item" onClick={onLogout}>
                      <Icon name="close" size={14} stroke={2.2} /> Đăng xuất
                    </button>
                  </div>
                )}
              </div>
            ) : (
              <>
                <a className="lp-toplogin" href="/home" onClick={(e) => { e.preventDefault(); navigate('/home'); }}>Đăng nhập</a>
                <button className="lp-topcta" onClick={() => onCtaClick('Đăng ký tư vấn miễn phí')}>Đăng ký tư vấn</button>
              </>
            )}
          </nav>
        </header>

        {/* ── MOBILE drawer (≤900px) — click hamburger để mở ─────────────────── */}
        {mobileMenu && (
          <div className="lp-mobile-overlay" onClick={closeMobileMenu} role="dialog" aria-modal="true">
            <div className="lp-mobile-panel" onClick={e => e.stopPropagation()}>
              <div className="lp-mobile-head">
                <span className="lp-mobile-brand">TRAV-AI<em>cho công ty du lịch</em></span>
                <button className="lp-mobile-close" onClick={closeMobileMenu} aria-label="Đóng">
                  <Icon name="close" size={18} stroke={2.2} />
                </button>
              </div>

              {/* User info — chỉ khi đã đăng nhập */}
              {isAuthed && (
                <div className="lp-mobile-user">
                  <span className="lp-user-avatar">{initials(authUser.fullName || authUser.companyName)}</span>
                  <div className="lp-mobile-user-meta">
                    <span>{authUser.fullName || 'Đã đăng nhập'}</span>
                    {authUser.companyName && <em>{authUser.companyName}</em>}
                  </div>
                </div>
              )}

              {/* Nav links chính */}
              <nav className="lp-mobile-nav">
                <button onClick={() => goAndClose(goToFeatures)}>
                  <Icon name="sparkle" size={16} stroke={2.2} /> Tính năng
                </button>
                <button onClick={() => goAndClose(() => document.querySelector('#lp-how')?.scrollIntoView({ behavior: 'smooth' }))}>
                  <Icon name="check" size={16} stroke={2.2} /> Cách bắt đầu
                </button>
                {isAuthed && (
                  <button onClick={() => goAndClose(() => navigate('/home'))}>
                    <Icon name="arrowRight" size={16} stroke={2.2} /> Vào ứng dụng
                  </button>
                )}
              </nav>

              {/* Phím tắt FEATURES (4 tính năng quan trọng) */}
              <div className="lp-mobile-section-label">TÍNH NĂNG NHANH</div>
              <nav className="lp-mobile-features">
                {FEATURES.slice(0, 4).map(f => (
                  <button key={f.key} onClick={() => goAndClose(() => onFeatureClick(f))}>
                    <img src={'/images/robots/' + f.robot} alt="" loading="lazy" decoding="async" />
                    <span>{f.title}</span>
                  </button>
                ))}
              </nav>

              {/* CTA chính */}
              <div className="lp-mobile-cta-row">
                {isAuthed ? (
                  <button className="lp-mobile-cta-secondary" onClick={() => goAndClose(onLogout)}>
                    Đăng xuất
                  </button>
                ) : (
                  <>
                    <button className="lp-mobile-cta-secondary" onClick={() => goAndClose(() => navigate('/home'))}>
                      Đăng nhập
                    </button>
                    <button className="lp-mobile-cta-primary" onClick={() => goAndClose(() => onCtaClick('Đăng ký tư vấn miễn phí'))}>
                      <Icon name="sparkle" size={14} stroke={2.4} /> Đăng ký tư vấn
                    </button>
                  </>
                )}
              </div>
            </div>
          </div>
        )}

        {/* ── HERO: split asymmetric + aurora blobs + staggered entrance + mascot parallax */}
        <section className="lp-hero">
          {/* Atmosphere — 2 aurora blob mờ rất nhẹ ở 2 góc (motion 7) */}
          <div className="lp-hero-aurora lp-aurora-1" aria-hidden="true" />
          <div className="lp-hero-aurora lp-aurora-2" aria-hidden="true" />
          <div className="lp-hero-grid-bg" aria-hidden="true" />

          <div className="lp-hero-text">
            <div className="lp-hero-chip lp-anim-fade" style={{ animationDelay: '60ms' }}>
              <span className="lp-hero-chip-dot" />
              Dành cho công ty du lịch Việt Nam
            </div>
            <h1 className="lp-anim-fade" style={{ animationDelay: '160ms' }}>
              AI gánh việc tour,<br />
              <span className="lp-hl-accent">
                bạn dồn vào chốt deal.
                <svg className="lp-hl-underline" viewBox="0 0 320 14" preserveAspectRatio="none" aria-hidden="true">
                  <path d="M3 10 Q 80 2, 160 7 T 317 6" />
                </svg>
              </span>
            </h1>
            <p className="lp-hero-sub lp-anim-fade" style={{ animationDelay: '300ms' }}>
              TRAV-AI tự tạo tour, trả mail khách, chấm điểm hồ sơ và phân tích số liệu. Bạn chỉ duyệt và quyết.
            </p>
            <div className="lp-hero-cta lp-anim-fade" style={{ animationDelay: '440ms' }}>
              <button className="lp-btn-primary" onClick={() => onCtaClick('Đăng ký tư vấn miễn phí')}>
                <span className="lp-btn-primary-shine" aria-hidden="true" />
                <Icon name="sparkle" size={16} stroke={2.4} />
                Đăng ký tư vấn miễn phí
                <Icon name="arrowRight" size={16} stroke={2.4} />
              </button>
              <button className="lp-btn-ghost" onClick={goToFeatures}>
                Xem tính năng
              </button>
            </div>
            <div className="lp-hero-trust lp-anim-fade" style={{ animationDelay: '580ms' }}>
              <Icon name="check" size={13} stroke={2.6} />
              Demo miễn phí 15 phút <span>·</span> Chưa cần thanh toán ngay
            </div>
          </div>

          <div
            className="lp-hero-visual lp-anim-fade-scale"
            style={{ animationDelay: '220ms' }}
            aria-hidden="true"
            onMouseMove={(e) => {
              const r = e.currentTarget.getBoundingClientRect();
              const x = (e.clientX - r.left) / r.width - 0.5;
              const y = (e.clientY - r.top) / r.height - 0.5;
              e.currentTarget.style.setProperty('--tx', x.toFixed(3));
              e.currentTarget.style.setProperty('--ty', y.toFixed(3));
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.setProperty('--tx', '0');
              e.currentTarget.style.setProperty('--ty', '0');
            }}
          >
            <div className="lp-hero-glow" />
            <div className="lp-hero-rings">
              <div className="lp-hero-ring" /><div className="lp-hero-ring" /><div className="lp-hero-ring" />
            </div>
            <img className="lp-hero-bot" src="/images/robots/robot4.PNG" alt=""
              fetchpriority="high" decoding="async" />
            <div className="lp-hero-pill lp-pill-1">
              <span className="lp-pill-live" />
              <Icon name="sparkle" size={12} stroke={2.2} />
              Đang phân tích doanh thu…
            </div>
            <div className="lp-hero-pill lp-pill-2">
              <Icon name="check" size={12} stroke={2.6} />
              Nháp mail đã sẵn
            </div>
            <div className="lp-hero-pill lp-pill-3">
              <Icon name="users" size={12} stroke={2.2} />
              12 khách hạng A
            </div>
          </div>
        </section>

        {/* ── Quick value strip (không phải fake numbers — chỉ phát biểu giá trị) */}
        <Reveal as="section" className="lp-values">
          {QUICK_VALUES.map((v, i) => (
            <div key={i} className="lp-value-chip">
              <span className="lp-value-dot" />
              {v}
            </div>
          ))}
        </Reveal>

        {/* ── LP7: Khối "Sẵn sàng Bứt Phá cùng TOURKIT AI" (ảnh mẫu) — dưới banner chính */}
        <section className="lp-intro-img bg-intro-1">
          <img src="/images/intro/image2.webp"
            alt="Sẵn sàng bứt phá cùng TOURKIT AI — Công nghệ lõi + Nhận diện thương hiệu = Chuyển đổi số toàn diện"
            loading="lazy" decoding="async"
            style={{ display: 'block', width: '100%', maxWidth: 1180, height: 'auto', margin: '0 auto', borderRadius: 20, boxShadow: '0 14px 44px rgba(15,23,42,0.16)' }} />
        </section>

        {/* ── FEATURES BENTO (eyebrow 1 / 2 cho phép) */}
        <section id="lp-features" className="lp-section">
          <Reveal className="lp-section-head">
            <div className="lp-eyebrow">TÍNH NĂNG NỔI BẬT</div>
            <h2>09 tính năng AI gánh việc cho team tour mỗi ngày.</h2>
          </Reveal>

          <div className="lp-features-grid">
            {FEATURES.map((f, i) => (
              <Reveal
                key={f.key}
                delay={i * 50}
                className={`lp-feature lp-tone-${f.tone}`}
              >
                <button className="lp-feature-card" onClick={() => onFeatureClick(f)}>
                  <div className="lp-feature-bot">
                    <img src={'/images/robots/' + f.robot} alt="" loading="lazy" decoding="async" />
                  </div>
                  <div className="lp-feature-body">
                    <h3>{f.title}</h3>
                    <p>{f.pitch}</p>
                    <span className="lp-feature-cta">
                      Trải nghiệm <Icon name="arrowRight" size={14} stroke={2.4} />
                    </span>
                  </div>
                </button>
              </Reveal>
            ))}
          </div>
        </section>

        {/* ── LP8: Khối "Tương Lai Vận Hành Bằng AI" (ảnh mẫu) — dưới khối tính năng */}
            <section className="lp-intro-img bg-intro-2" >
          <img src="/images/intro/image3.webp"
            alt="Tương lai vận hành bằng AI — Tiên phong, Tin cậy, Đồng hành, Tăng trưởng"
            loading="lazy" decoding="async"
            style={{ display: 'block', width: '100%', maxWidth: 1180, height: 'auto', margin: '0 auto', borderRadius: 20, boxShadow: '0 14px 44px rgba(15,23,42,0.16)' }} />
        </section>


            {/* ── HOW IT WORKS (eyebrow 2 / 2) */}
            <section id="lp-how" className="lp-section lp-section-tint">
                <Reveal className="lp-section-head">
                    <div className="lp-eyebrow">CÁCH BẮT ĐẦU</div>
                    <h2>Ba bước, dưới một tuần.</h2>
                </Reveal>

                <div className="lp-steps">
                    {[
                        { n: '01', t: 'Đăng ký tư vấn', d: 'Để lại số, đội TRAV-AI gọi tìm hiểu quy mô, thị trường, đặc thù phòng tour của bạn.', bot: 'robot8.PNG' },
                        { n: '02', t: 'Kết nối CRM Tourkit', d: 'Cấp quyền đọc TourKit CRM. AI bắt đầu học cách công ty bạn làm tour, viết mail, chấm khách.', bot: 'robot9.PNG' },
                        { n: '03', t: 'AI bắt đầu phụ trợ', d: 'Sau buổi training ngắn, AI tạo tour, viết mail, phân tích số liệu hàng ngày cho team.', bot: 'robot10.PNG' },
                    ].map((s, i) => (
                        <Reveal key={s.n} delay={i * 80} className="lp-step">
                            <div className="lp-step-num">{s.n}</div>
                            <div className="lp-step-bot"><img src={'/images/robots/' + s.bot} alt="" loading="lazy" decoding="async" /></div>
                            <h3>{s.t}</h3>
                            <p>{s.d}</p>
                        </Reveal>
                    ))}
                </div>
            </section>


        {/* ── LP9: Khối "Nền tảng Công nghệ Đột phá" (ảnh mẫu) — dưới khối Tương lai vận hành */}
            <section className="lp-intro-img bg-intro-3" >
          <img src="/images/intro/image1.webp"
            alt="Nền tảng công nghệ đột phá — Mobile First, Cloud-Based, Tích hợp mở, Bảo mật & an toàn"
            loading="lazy" decoding="async"
            style={{ display: 'block', width: '100%', maxWidth: 1180, height: 'auto', margin: '0 auto', borderRadius: 20, boxShadow: '0 14px 44px rgba(15,23,42,0.16)' }} />
        </section>


        {/* Khối "KHÁCH HÀNG NÓI GÌ" (testimonials) đã bỏ theo yêu cầu (LP5) */}

        {/* ── BIG CTA BAND (1 dấu nhấn cuối, không lặp lại intent CTA) */}
        <section className="lp-cta-band">
          <img className="lp-cta-bot-left" src="/images/robots/robot11.PNG" alt="" aria-hidden="true" loading="lazy" decoding="async" />
          <img className="lp-cta-bot-right" src="/images/robots/robot12.PNG" alt="" aria-hidden="true" loading="lazy" decoding="async" />
          <div className="lp-cta-inner">
            <h2>Sẵn sàng để AI gánh việc lặp lại?</h2>
            <p>15 phút demo trực tiếp với team Tourkit, chưa cần thanh toán ngay.</p>
            <button className="lp-btn-primary lp-btn-large" onClick={() => onCtaClick('Đăng ký tư vấn miễn phí')}>
              Đăng ký tư vấn miễn phí
              <Icon name="arrowRight" size={18} stroke={2.4} />
            </button>
          </div>
        </section>

        {/* ── FOOTER (LP10) — chuẩn theo tourkitweb: dark 4-col + lưới mờ + 2 quầng sáng */}
        <footer className="lpf">
          <div className="lpf-grid-bg" aria-hidden="true" />
          <div className="lpf-glow lpf-glow-1" aria-hidden="true" />
          <div className="lpf-glow lpf-glow-2" aria-hidden="true" />
          <div className="lpf-inner">
            <div className="lpf-cols">
              <div className="lpf-brand-col">
                <div className="lpf-logo">
                  <img className="lpf-logo-img" src="/images/tourkit-logo.png" alt="TOURKIT" loading="lazy" decoding="async"
                    onError={(e) => { e.target.style.display = 'none'; const t = e.target.nextElementSibling; if (t) t.style.display = 'inline-flex'; }} />
                  <span className="lpf-logo-text" style={{ display: 'none' }}>TOURKIT<em>Chuyển đổi số doanh nghiệp du lịch</em></span>
                  <p className="lpf-company">Công ty Cổ phần TourKit Việt Nam</p>
                </div>
                <p className="lpf-desc">Đơn vị thiết kế website du lịch và hệ thống OTA hàng đầu. Chúng tôi biến traffic thành booking với thiết kế đỉnh cao và trải nghiệm người dùng tối ưu.</p>
                <div className="lpf-badge"><span className="lpf-badge-dot" /> Hệ thống hoạt động 24/7</div>
              </div>

              <div className="lpf-col">
                <h4>Dịch Vụ</h4>
                <ul>
                  <li><a href="#">Thiết kế Web Du Lịch</a></li>
                  <li><a href="#">Xây dựng OTA Platform</a></li>
                  <li><a href="#">Tối ưu chuyển đổi (CRO)</a></li>
                  <li><a href="#">Thiết kế Landing Page</a></li>
                  <li><a href="#">AI Trip Planner</a></li>
                </ul>
              </div>

              <div className="lpf-col">
                <h4>Công Ty</h4>
                <ul>
                  <li><a href="#">Về chúng tôi</a></li>
                  <li><a href="#portfolio">Dự án tiêu biểu</a></li>
                  <li><a href="#">Bảng giá</a></li>
                  <li><a href="#">Blog chia sẻ</a></li>
                  <li><a href="#consultation">Liên hệ tư vấn</a></li>
                </ul>
              </div>

              <div className="lpf-col">
                <h4>Liên Hệ</h4>
                <ul className="lpf-contact-list">
                  <li className="lpf-contact lpf-contact--top">
                    <span className="lpf-contact-ico"><Icon name="pin" size={14} stroke={2} /></span>
                    <div><span className="lpf-contact-label">Hà Nội</span><span className="lpf-contact-addr">Tầng 4, Tòa nhà 242 Nguyễn Văn Lộc, Hà Đông</span></div>
                  </li>
                  <li className="lpf-contact lpf-contact--top">
                    <span className="lpf-contact-ico"><Icon name="pin" size={14} stroke={2} /></span>
                    <div><span className="lpf-contact-label">Hồ Chí Minh</span><span className="lpf-contact-addr">Số 1, Đặng Văn Sâm, P.9, Q.Phú Nhuận</span></div>
                  </li>
                  <li className="lpf-contact">
                    <span className="lpf-contact-ico"><Icon name="phone" size={14} stroke={2} /></span>
                    <a className="lpf-contact-link is-phone" href="tel:0383202404">0383.202.404</a>
                  </li>
                  <li className="lpf-contact">
                    <span className="lpf-contact-ico"><Icon name="mail" size={14} stroke={2} /></span>
                    <a className="lpf-contact-link" href="mailto:info@tourkit.vn">info@tourkit.vn</a>
                  </li>
                </ul>
              </div>
            </div>

            <div className="lpf-bottom">
              <p className="lpf-copy">© {new Date().getFullYear()} Công ty Cổ phần TourKit Việt Nam. All rights reserved.</p>
              <div className="lpf-bottom-links">
                <a href="#">Chính sách bảo mật</a>
                <a href="#">Điều khoản sử dụng</a>
              </div>
            </div>
          </div>
        </footer>

        {window.ConsultPopup && (
          <window.ConsultPopup
            open={popup.open}
            feature={popup.feature}
            onClose={() => setPopup({ open: false, feature: null })}
          />
        )}
      </div>
    );
  }

  window.LandingPage = LandingPage;
})();
