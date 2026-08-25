# Giao diện: bố cục, bundle, SEO

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Frontend layout

```
wwwroot/
  index.html                                ← controls load order; <script src="..."> imperative
  styles.css
  lib/
    data.js                                 ← demo data + fmtVND
    icons.jsx                               ← Icon component
  core/
    router.jsx                              ← hash router (Router, Route, Link, navigate)
    storage.js                              ← TourCache + RequestHistory + tour stats
    parsers.js                              ← parseLooseJSON + parseTourText
    ai-provider.jsx                         ← thin client → /api/v1/completions; AISettingsDialog
    features.js                             ← window.tourkitFeatures — cờ tính năng chưa ra mắt (đọc /api/v1/features 1 lần); plain .js vì CẢ index.html lẫn admin-trav-ai.html dùng
  components/
    dialogs.jsx                             ← ConfirmDialog, ShareDialog, AIAssistantPanel
    tweaks-panel.jsx                        ← editorial Tweaks UI
    customer-review-card.jsx                ← rendered review card (rank/alert/actions) for the drawer
  steps/
    step1.jsx … step4.jsx                   ← sub-views inside the wizard page
  pages/
    wizard.jsx                              ← 4-step wizard (handleGenerate orchestration here)
    quotes.jsx                              ← list of cached tours — example of a 2nd page
    customers.jsx                           ← Customer Review page: list + batch confirm + SSE progress + review drawer
    assistant.jsx                           ← Chat-Analytics page: token login + chat-left + data-right (stats + table)
    mail.jsx                                ← SmartMail AI page: Gmail config form + 3-col (filters/list/detail) + AI compose (SSE)
    digest.jsx                              ← KHỐI (không phải trang): "Bản tin của tôi" + cấu hình OA Zalo, nhúng vào thẻ tác vụ
    insights.jsx                            ← KHỐI: "Bảng tin" — tab thứ 2 của trang Tự động hoá
  app.jsx                                   ← App shell: header + nav + <Router> + global state
```

**Adding a new page:**
1. `pages/<name>.jsx`: `function MyPage({ pushToast }) {...} window.MyPage = MyPage;`
2. `index.html`: add `<script type="text/babel" src="pages/<name>.jsx"></script>` after existing pages.
3. **`bundle-entry.js`: add `import "./pages/<name>.jsx";`** — BẮT BUỘC, dễ quên. Thiếu bước này thì dev (Babel) chạy được nhưng **prod bundle thiếu trang → trắng trang + `React #130`**. `index.html` (dev) và `bundle-entry.js` (prod esbuild) phải LUÔN khớp danh sách.
4. `app.jsx`: add `<Route path="/<name>" render={() => <window.MyPage pushToast={pushToast} />} />` inside `<Router>`.
5. `app.jsx`: add `<Link to="/<name>">Tên</Link>` in the nav.

No bundler, no npm install. `<script type="text/babel">` is transformed in-browser by `@babel/standalone`.

**Thêm một file `.js` THƯỜNG (không phải `text/babel`)** — vd `core/features.js`, `lib/data.js`: khai
thẻ `<script src>` trong `index.html` VÀ `import` trong `bundle-entry.js`. **Chỉ hai chỗ đó** — danh
sách gỡ thẻ ở [StaticFilesSetup.cs](../Configuration/StaticFilesSetup.cs) nay **đọc thẳng từ
`bundle-entry.js`** lúc khởi động, không khai tay nữa.

⚠️ Trước 20/08 danh sách đó viết tay, và nó **đã lệch 12 file** dù ngay cạnh có sẵn dòng chú thích
dặn phải thêm. Hậu quả không phải "tải đôi cho tốn": bản trong bundle nạp **SAU** nên nó **thắng** —
sửa một file plain `.js` mà chưa dựng lại bundle thì bản sửa **im lặng không có tác dụng ở prod**,
dev không bao giờ lộ ra vì dev không có bundle. Chốt chặn: `BundledPlainJsStripTests` đối chiếu
`index.html` thật với `bundle-entry.js` thật.

**Dùng lại helper, KHÔNG copy-paste:** React hook chung ở [`wwwroot/lib/hooks.jsx`](../wwwroot/lib/hooks.jsx) (`window.tourkitHooks` — vd `useIsMobile`); util thuần ở [`wwwroot/lib/util.js`](../wwwroot/lib/util.js) (`window.tourkitUtil` — `readSSE`, `fmtAgo`, `fmtDate`, `copyText`); tiền VND ở `window.fmtVND` (lib/data.js); auth/fetch ở `window.tourkitAuth.authedFetch`. Cần thêm helper dùng nhiều nơi → thêm vào các file này thay vì định nghĩa lại trong từng page.

---

## SEO cho trang public

Toàn bộ ở **server** ([`Configuration/SeoSetup.cs`](../Configuration/SeoSetup.cs), nối vào
`ServeIndex` của [`StaticFilesSetup`](../Configuration/StaticFilesSetup.cs)) — vì trang vẽ bằng JS nên
HTML gốc **không có một chữ nào** của nội dung; máy tìm kiếm và bộ xem trước link (Zalo/Facebook/
LinkedIn/Bing — phần lớn KHÔNG chạy JS) nhìn vào chỉ thấy trang trắng.

- **Nội dung dựng sẵn** (`SeoSetup.LandingBody()`) nhét vào `#root` **chỉ cho `/` và `/landing`**.
  `ReactDOM.createRoot().render()` xoá sạch container khi khởi động nên người dùng thấy đúng trang
  thật — không phải hydrate, không có chuyện lệch markup.
  ⚠️ **Chữ ở đây phải TRÙNG nguyên văn với `wwwroot/pages/landing.jsx`.** Trùng thì đó chỉ là "gửi
  sớm hơn"; lệch thì Google coi là gian lận nội dung (cloaking) và phạt. Cố ý chỉ lấy phần chữ ỔN
  ĐỊNH (tiêu đề, tên tính năng, tên các bước), KHÔNG lấy đoạn giới thiệu dài của từng tính năng —
  chữ càng dài càng hay sửa, sửa một bên quên bên kia là lệch.
- **`SeoSetup.Routes` là 1 nguồn cho 3 việc**: tiêu đề từng trang · `noindex` cho trang nội bộ ·
  **danh sách đường hợp lệ để trả 404**. Thêm trang mới mà quên khai ở đây thì mở link trực tiếp vào
  trang đó ra **404** (trong app bấm qua vẫn chạy vì router client không hỏi server — hỏng kiểu khó lần).
- **Không hardcode tên miền.** `canonical`/`sitemap` dựng từ request, đọc `X-Forwarded-Host`/`-Proto`
  trước (sau IIS/nginx thì `Request.Host` là host nội bộ → canonical thành `http://localhost/`, tức
  khai với Google rằng bản chính nằm ở localhost).
- `/landing` **canonical về `/`** — hai đường cùng nội dung, không khai thì tự chia điểm.
- `robots.txt` + `sitemap.xml` map **TRƯỚC `MapFallback`**, không thì fallback nuốt và trả
  `index.html` kèm 200 (đúng cái bẫy đã ghi ở mục `Features:Digest`). Sitemap **chỉ có trang chủ**:
  khai trang nội bộ vào sitemap là tự mời Google index đúng những trang vừa gắn `noindex` — hai tín
  hiệu chỏi nhau, Search Console báo lỗi.
- **Escape tối thiểu** (`SeoSetup.EscapeText`, chỉ `& < > "`), KHÔNG dùng `WebUtility.HtmlEncode`:
  nó mã hoá cả chữ có dấu thành `&#7897;` nên "Hộp thư AI" thành một dãy số — meta phình gấp mấy
  lần, mô tả vượt giới hạn ký tự của Google, và xem mã nguồn không đọc được gì.
- **Bộ kiểm chống lệch**: `node scripts/e2e/seo-prerender.check.js` — đối chiếu từng câu dựng sẵn với
  `landing.jsx`, đối chiếu `SeoSetup.Routes` với các `<Route path>` trong `app.jsx`, và chặn việc mở
  `Index: true` cho trang nội bộ.
