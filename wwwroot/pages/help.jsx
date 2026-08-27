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
    { slug: 'nhap-gia-ncc',        label: 'Nhà cung cấp & Import giá' },
    { slug: 'danh-gia-khach-hang', label: 'Chấm điểm khách hàng' },
    { slug: 'tro-ly-so-lieu',      label: 'Trợ lý số liệu' },
    { slug: 'hop-thu-ai',          label: 'Hộp thư AI' },
    { slug: 'hop-thu-chat',        label: 'Hộp thư chat đa kênh' },
    { slug: 'uu-tien-deal',        label: 'AI phân tích Cơ hội' },
    { slug: 'tham-dinh-visa',      label: 'Thẩm định Visa' },
    { slug: 'tu-dong-hoa',         label: 'Tự động hóa' },
    { slug: 'jarvis',              label: 'TRAVAI — trợ lý giọng nói' },
    // Bài con của "Tự động hóa" (một tác vụ có nhiều tuỳ chọn nên tách riêng). Vẫn PHẢI khai ở đây:
    // danh mục này cũng là danh sách đường dẫn hợp lệ, thiếu thì /help/<slug> rơi về trang mục lục
    // và cả link từ bài "Tự động hóa" sang cũng không mở được.
    { slug: 'nhac-cham-khach',     label: 'Nhắc chăm lại khách ngủ quên' },
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
    // link [text](url) — riêng link sang guide khác ("nhac-cham-khach.md") phải đổi thành
    // /help/<slug>. Để nguyên thì trình duyệt hiểu là đường dẫn tương đối "/help/<slug>.md",
    // không khớp danh mục nên rơi về trang mục lục — bấm vào tưởng hỏng. Cũng KHÔNG mở tab mới:
    // đây là điều hướng trong chính trang hướng dẫn.
    s = s.replace(/\[([^\]]+)\]\(([^)\s]+)[^)]*\)/g, (m, chu, url) => {
      const noiBo = /^[a-z0-9-]+\.md$/i.test(url);
      return noiBo
        ? `<a href="/help/${url.slice(0, -3)}" data-help-link="1">${chu}</a>`
        : `<a href="${url}" target="_blank" rel="noopener">${chu}</a>`;
    });
    // đậm **x**
    s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    // code `x`
    s = s.replace(/`([^`]+)`/g, '<code>$1</code>');
    // nghiêng *x* (sau đậm để không đụng **)
    s = s.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
    return s;
  }
  // Khung trích dẫn hay có gạch đầu dòng ("> - ..."). Nhánh blockquote không đi qua bộ dựng
  // danh sách nên phải tự dựng ở đây, không thì mấy dòng đó hiện ra dạng "- chữ" chạy thẳng hàng.
  function quoteHtml(lines) {
    const laGach = s => /^\s*[-*+]\s+/.test(s);
    const out = [];
    let i = 0;
    while (i < lines.length) {
      if (laGach(lines[i])) {
        const li = [];
        while (i < lines.length && laGach(lines[i])) {
          li.push('<li>' + inline(lines[i].replace(/^\s*[-*+]\s+/, '')) + '</li>'); i++;
        }
        out.push('<ul>' + li.join('') + '</ul>');
      } else {
        const p = [];
        while (i < lines.length && !laGach(lines[i])) { p.push(inline(lines[i])); i++; }
        out.push('<p>' + p.join('<br>') + '</p>');
      }
    }
    return out.join('');
  }
  function render(md) {
    md = md.replace(/\r\n/g, '\n').replace(/\.\.\/images\//g, '/docs/images/');
    const lines = md.split('\n');
    const out = [];
    let para = [];
    // Escape TỪNG DÒNG rồi mới nối bằng <br>. Nối trước rồi escape sau thì chính thẻ <br> bị
    // escape thành &lt;br&gt; → người đọc thấy chữ "<br>" chèn giữa câu. Đã hiện thật ở 120
    // đoạn trong cả 9 bài hướng dẫn.
    const flush = () => { if (para.length) { out.push('<p>' + para.map(inline).join('<br>') + '</p>'); para = []; } };
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
        out.push('<blockquote>' + quoteHtml(buf) + '</blockquote>'); continue;
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

    // Ảnh chưa chụp (404) → ẩn ảnh, GIỮ note "📸 Cần chụp" ngay dưới làm chỗ trống.
    // Ảnh đã chụp rồi → ẩn NOTE đi: nó là lời dặn cho người đi chụp, không phải nội dung cho người
    // dùng đọc. Thiếu vế này thì mỗi ảnh chụp xong lại để lại một dòng thừa ngay bên dưới — đã có
    // lúc 42 dòng như vậy hiện trên trang hướng dẫn thật. Làm ở đây thay vì xoá tay trong từng file
    // .md để lần chụp sau tự đúng, khỏi phải nhớ đi dọn.
    React.useEffect(() => {
      const el = docRef.current;
      if (!el) return;
      // Note nằm ngay sau khối ảnh, dạng blockquote bắt đầu bằng "📸".
      const noteSau = (img) => {
        const khoi = img.closest('p') || img;
        const kh = khoi.nextElementSibling;
        return kh && kh.tagName === 'BLOCKQUOTE' && kh.textContent.trim().startsWith('📸') ? kh : null;
      };
      // Ẩn note TRƯỚC, chỉ hiện lại khi ảnh báo hỏng. Làm ngược lại (chờ sự kiện tải xong mới ẩn)
      // thì hỏng vì ảnh khai loading="lazy": sự kiện chỉ nổ khi cuộn tới, nên mọi note phía dưới
      // màn hình hiện chình ình rồi mới lần lượt biến mất khi người ta cuộn qua.
      el.querySelectorAll('img').forEach(img => {
        const note = noteSau(img);
        if (note) note.style.display = 'none';
        const hong = () => {
          img.style.display = 'none';
          if (note) note.style.display = '';   // chưa có ảnh → trả note về làm chỗ trống
        };
        if (img.complete && img.naturalWidth === 0) hong();
        else img.addEventListener('error', hong, { once: true });
      });
    }, [html]);

    const go = (s) => (e) => { e.preventDefault(); nav('/help/' + s); };

    // Link sang guide khác nằm trong nội dung markdown → cho đi qua router thay vì tải lại cả trang.
    const bamTrongBai = (e) => {
      const a = e.target.closest('a[data-help-link]');
      if (!a) return;
      e.preventDefault();
      nav(a.getAttribute('href'));
    };

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
          {active && state === 'ok'      && <article className="help-doc" ref={docRef} onClick={bamTrongBai} dangerouslySetInnerHTML={{ __html: html }} />}
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
    '/chat-inbox': 'hop-thu-chat',
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
