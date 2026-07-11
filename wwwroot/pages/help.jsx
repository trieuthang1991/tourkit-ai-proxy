// pages/help.jsx — Trang Hướng dẫn sử dụng (kiểu tài liệu Google Doc).
// Route: /help (mục lục) + /help/:slug (đọc 1 guide).
// Nội dung là markdown ở docs/features/<slug>.md (server serve qua /docs/features/…);
// render markdown ngay tại client bằng renderer tối giản (guides chỉ dùng heading/list/
// bold/ảnh/blockquote/đoạn — KHÔNG bảng, KHÔNG code fence) → không cần lib ngoài (no CDN).
(function () {
  'use strict';

  // Danh mục guide — slug PHẢI khớp tên file docs/features/<slug>.md
  const GUIDES = [
    { slug: 'bao-gia-tour',        label: 'Tính giá Tour (AI)' },
    { slug: 'danh-gia-khach-hang', label: 'Chấm điểm khách hàng' },
    { slug: 'tro-ly-so-lieu',      label: 'Trợ lý số liệu' },
    { slug: 'hop-thu-ai',          label: 'Hộp thư AI' },
    { slug: 'uu-tien-deal',        label: 'AI phân tích Cơ hội' },
    { slug: 'tham-dinh-visa',      label: 'Thẩm định Visa' },
    { slug: 'tu-dong-hoa',         label: 'Tự động hóa' },
    { slug: 'jarvis',              label: 'TRAVAI — trợ lý giọng nói' },
  ];
  const SLUGS = new Set(GUIDES.map(g => g.slug));

  // ── Renderer markdown tối giản ───────────────────────────────────────────────
  function esc(s) {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }
  function inline(s) {
    s = esc(s);
    // ảnh ![alt](src) — src đã được rewrite ../images/ → /docs/images/ ở bước render()
    s = s.replace(/!\[([^\]]*)\]\(([^)\s]+)[^)]*\)/g, (m, alt, src) => `<img alt="${alt}" src="${src}" loading="lazy" />`);
    // link [text](url)
    s = s.replace(/\[([^\]]+)\]\(([^)\s]+)[^)]*\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');
    // đậm **x**
    s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    // code `x`
    s = s.replace(/`([^`]+)`/g, '<code>$1</code>');
    // nghiêng *x* (sau đậm để không đụng **)
    s = s.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
    return s;
  }
  function render(md) {
    md = md.replace(/\r\n/g, '\n').replace(/\.\.\/images\//g, '/docs/images/');
    const lines = md.split('\n');
    const out = [];
    let para = [];
    const flush = () => { if (para.length) { out.push('<p>' + inline(para.join('<br>')) + '</p>'); para = []; } };
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (/^\s*$/.test(line)) { flush(); i++; continue; }
      let m;
      if ((m = line.match(/^(#{1,6})\s+(.*)$/))) {
        flush(); const n = m[1].length; out.push(`<h${n}>` + inline(m[2]) + `</h${n}>`); i++; continue;
      }
      if (/^\s*(---|\*\*\*|___)\s*$/.test(line)) { flush(); out.push('<hr/>'); i++; continue; }
      if (/^\s*>\s?/.test(line)) {
        flush(); const buf = [];
        while (i < lines.length && /^\s*>\s?/.test(lines[i])) { buf.push(lines[i].replace(/^\s*>\s?/, '')); i++; }
        out.push('<blockquote>' + inline(buf.join('<br>')) + '</blockquote>'); continue;
      }
      if (/^\s*[-*+]\s+/.test(line)) {
        flush(); const buf = [];
        while (i < lines.length && /^\s*[-*+]\s+/.test(lines[i])) { buf.push('<li>' + inline(lines[i].replace(/^\s*[-*+]\s+/, '')) + '</li>'); i++; }
        out.push('<ul>' + buf.join('') + '</ul>'); continue;
      }
      if (/^\s*\d+\.\s+/.test(line)) {
        flush(); const buf = [];
        while (i < lines.length && /^\s*\d+\.\s+/.test(lines[i])) { buf.push('<li>' + inline(lines[i].replace(/^\s*\d+\.\s+/, '')) + '</li>'); i++; }
        out.push('<ol>' + buf.join('') + '</ol>'); continue;
      }
      para.push(line); i++;
    }
    flush();
    return out.join('\n');
  }

  function HelpPage({ slug }) {
    const nav = window.tourkitRouter.navigate;
    const active = SLUGS.has(slug) ? slug : null;
    const [html, setHtml]   = React.useState('');
    const [state, setState] = React.useState('idle'); // idle | loading | ok | error
    const mainRef = React.useRef(null);
    const docRef  = React.useRef(null);

    React.useEffect(() => {
      if (!active) { setHtml(''); setState('idle'); return; }
      let cancel = false;
      setState('loading');
      fetch('/docs/features/' + active + '.md', { cache: 'no-cache' })
        .then(r => { if (!r.ok) throw new Error(r.status); return r.text(); })
        .then(md => { if (cancel) return; setHtml(render(md)); setState('ok'); })
        .catch(() => { if (!cancel) setState('error'); });
      return () => { cancel = true; };
    }, [active]);

    // Cuộn lên đầu khi đổi guide
    React.useEffect(() => { if (mainRef.current) mainRef.current.scrollTop = 0; }, [active, state]);

    // Ảnh chưa chụp (404) → ẩn cho gọn (note "📸 Cần chụp" ngay dưới vẫn giữ lại).
    React.useEffect(() => {
      const el = docRef.current;
      if (!el) return;
      el.querySelectorAll('img').forEach(img => {
        const hide = () => { img.style.display = 'none'; };
        if (img.complete && img.naturalWidth === 0) hide();
        else img.addEventListener('error', hide, { once: true });
      });
    }, [html]);

    const go = (s) => (e) => { e.preventDefault(); nav('/help/' + s); };

    return (
      <div className="help-wrap">
        <aside className="help-side">
          <div className="help-side-title">Hướng dẫn sử dụng</div>
          <nav className="help-side-list">
            {GUIDES.map(g => (
              <a key={g.slug} href={'/help/' + g.slug}
                 className={'help-side-item' + (g.slug === active ? ' active' : '')}
                 onClick={go(g.slug)}>
                {g.label}
              </a>
            ))}
          </nav>
        </aside>
        <main className="help-main" ref={mainRef}>
          {!active && (
            <div className="help-doc help-intro">
              <h1>Trung tâm hướng dẫn</h1>
              <p>Chọn một tính năng ở cột bên trái để xem hướng dẫn từng bước, lưu ý và câu hỏi thường gặp.</p>
            </div>
          )}
          {active && state === 'loading' && <div className="help-status">Đang tải hướng dẫn…</div>}
          {active && state === 'error'   && <div className="help-status">Không tải được hướng dẫn cho tính năng này.</div>}
          {active && state === 'ok'      && <article className="help-doc" ref={docRef} dangerouslySetInnerHTML={{ __html: html }} />}
        </main>
      </div>
    );
  }

  // Route hiện tại → slug guide (dùng cho nút "Hướng dẫn sử dụng" ở topbar).
  window.HELP_SLUG_BY_ROUTE = {
    '/wizard': 'bao-gia-tour',
    '/customers': 'danh-gia-khach-hang',
    '/assistant': 'tro-ly-so-lieu',
    '/mail': 'hop-thu-ai',
    '/deals': 'uu-tien-deal',
    '/visa': 'tham-dinh-visa',
    '/visa/history': 'tham-dinh-visa',
    '/visa-config': 'tham-dinh-visa',
    '/workflows': 'tu-dong-hoa',
    '/travai': 'jarvis',
    '/jarvis': 'jarvis',
    '/quotes': 'bao-gia-tour',
  };

  window.HelpPage = HelpPage;
})();
