// pages/home.jsx — App launcher SAU đăng nhập. Route mặc định '/'.
// Mục tiêu: 1 chỗ duy nhất NV chọn AI Agent phù hợp việc cần làm.
//
// Layout (taste skill, dial 5/4/4):
//   PageHero brand "TOURKIT AI-POWERED FUTURE"
//   Greeting strip (thời-điểm: sáng/chiều/tối + tên NV)
//   Section eyebrow "CHỌN TRỢ LÝ AI CHUYÊN BIỆT" + ô tìm Agent (filter live)
//   Hero card centerpiece (mascot gradient + tagline TourKit AI)
//   Grid 7 feature cards 3-3-1 (KHÔNG spam 9 ô có 2 ô rỗng)
//
// KHÔNG có: em-dash, scroll cue, version label, locale strip, fake screenshot.

(function () {
  const { useState: s, useMemo: m } = React;
  const Link = window.tourkitRouter.Link;
  const navigate = window.tourkitRouter.navigate;
  const PageHero = window.PageShell?.PageHero;
  const Icon = window.Icon;

  // 7 AI Agent — mỗi cái = 1 tính năng đã có. Edit ở đây = đổi launcher.
  const AGENTS = [
    {
      id: 'chat', to: '/assistant', icon: 'sparkle',
      title: 'AI Chat',
      desc: 'Đội ngũ kinh doanh ảo đắc lực của bạn',
      tags: ['trợ lý', 'số liệu', 'tư vấn', 'chatbot'],
      featured: true,
    },
    {
      id: 'quote', to: '/wizard', icon: 'sparkle',
      title: 'AI Tính giá Tour',
      desc: 'Tính toán chính xác, tối ưu biên lợi nhuận',
      tags: ['báo giá', 'tour', 'wizard', 'soạn'],
    },
    {
      id: 'customers', to: '/customers', icon: 'users',
      title: 'AI phân tích khách hàng',
      desc: 'Thấu hiểu hành vi, kiến tạo chân dung khách hàng',
      tags: ['khách hàng', 'phân tích', 'phân khúc', 'review'],
    },
    {
      id: 'deals', to: '/deals', icon: 'trend',
      title: 'AI Phân tích Cơ hội bán hàng',
      desc: 'Nhận diện xu thế thị trường du lịch',
      tags: ['deal', 'bán hàng', 'cơ hội', 'ưu tiên'],
    },
    {
      id: 'mail', to: '/mail', icon: 'paper',
      title: 'AI Mail',
      desc: 'Gửi thư điện tử thông minh, tự động hóa tương tác',
      tags: ['email', 'hộp thư', 'gmail', 'soạn thư'],
    },
    {
      id: 'visa', to: '/visa', icon: 'shield',
      title: 'AI Thẩm định Visa',
      desc: 'Đánh giá chính xác tỉ lệ đậu Visa',
      tags: ['visa', 'hồ sơ', 'thẩm định', 'đánh giá'],
    },
    {
      id: 'tour', to: '/tour-builder', icon: 'pin',
      title: 'AI nhập Tour',
      desc: 'Chuyển văn bản thô thành hành trình hoàn mỹ',
      tags: ['tour', 'git', 'nhập liệu', 'lịch trình'],
    },
  ];

  function greetingByHour() {
    const h = new Date().getHours();
    if (h < 11) return 'buổi sáng';
    if (h < 14) return 'buổi trưa';
    if (h < 18) return 'buổi chiều';
    return 'buổi tối';
  }

  function normalize(str) {
    return (str || '').toLowerCase()
      .normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd');
  }

  function HomePage({ pushToast }) {
    const [q, setQ] = s('');
    const user = window.tourkitAuth?.getUser?.() || {};
    const name = (user.fullName || user.userName || '').trim() || 'Bạn';

    const filtered = m(() => {
      const nq = normalize(q.trim());
      if (!nq) return AGENTS;
      return AGENTS.filter(a => {
        const hay = normalize(a.title + ' ' + a.desc + ' ' + (a.tags || []).join(' '));
        return hay.includes(nq);
      });
    }, [q]);

    return (
      <div className="page home-page">
        {PageHero && (
          <PageHero
            eyebrow="TOURKIT AI-POWERED FUTURE"
            icon="sparkle"
            title="Trợ lý AI cho doanh nghiệp du lịch"
            subtitle="Thông minh hơn. Vận hành dễ dàng hơn. Tăng trưởng bền vững hơn." />
        )}

        {/* Greeting strip — thân thiện, KHÔNG to như hero */}
        <div className="home-greet">
          <span className="home-greet-emoji" aria-hidden>👋</span>
          <span>Chào <b>{greetingByHour()}</b>, <b>{name}</b>. Tôi là Trợ lý AI riêng của bạn. Chọn một Agent dưới đây để bắt đầu.</span>
        </div>

        {/* Eyebrow + search filter Agent */}
        <div className="home-section-head">
          <div className="home-section-title">
            <div className="home-section-ico"><Icon name="sparkle" size={16} /></div>
            <div>
              <div className="home-section-eyebrow">CHỌN TRỢ LÝ AI CHUYÊN BIỆT</div>
              <div className="home-section-sub">Đặt lịch trình, phân tích hồ sơ, tối ưu giá trong tích tắc</div>
            </div>
          </div>
          <div className="home-search">
            <Icon name="search" size={15} />
            <input value={q} onChange={e => setQ(e.target.value)}
              placeholder="Tìm kiếm AI Agent…" aria-label="Tìm AI Agent" />
            <span className="home-search-count">{filtered.length}/{AGENTS.length}</span>
          </div>
        </div>

        {/* Hero centerpiece — mascot + tagline TourKit AI */}
        <button className="home-mascot" onClick={() => navigate('/assistant')}
          aria-label="Mở Trợ lý AI">
          <div className="home-mascot-ring">
            <div className="home-mascot-orb">
              <svg viewBox="0 0 64 64" width="56" height="56" aria-hidden>
                <defs>
                  <linearGradient id="m-g" x1="0" y1="0" x2="1" y2="1">
                    <stop offset="0%" stopColor="#FED7AA" />
                    <stop offset="100%" stopColor="#FB923C" />
                  </linearGradient>
                </defs>
                <path fill="url(#m-g)" d="M32 6l4 10 10 4-10 4-4 10-4-10-10-4 10-4z" />
                <circle cx="48" cy="16" r="3" fill="#FCD34D" />
                <circle cx="14" cy="46" r="2.2" fill="#FCD34D" />
              </svg>
            </div>
          </div>
          <div className="home-mascot-label">
            <div className="home-mascot-title">TOURKIT AI</div>
            <div className="home-mascot-sub">Sẵn sàng tư vấn Tour & Telesales</div>
          </div>
        </button>

        {/* Grid 7 Agent cards — 3 cột desktop, 2 cột tablet, 1 cột mobile */}
        <div className="home-grid">
          {filtered.map(a => (
            <Link to={a.to} key={a.id}
              className={'home-card' + (a.featured ? ' featured' : '')}>
              <div className="home-card-ico"><Icon name={a.icon} size={18} /></div>
              <div className="home-card-body">
                <div className="home-card-title">{a.title}</div>
                <div className="home-card-desc">{a.desc}</div>
              </div>
              <div className="home-card-arrow"><Icon name="chevronRight" size={15} /></div>
            </Link>
          ))}
          {filtered.length === 0 && (
            <div className="home-empty">
              <Icon name="search" size={22} />
              <div><b>Không tìm thấy Agent phù hợp</b><br/>Thử từ khoá khác (vd "khách", "visa", "deal")</div>
            </div>
          )}
        </div>
      </div>
    );
  }

  window.HomePage = HomePage;
})();
