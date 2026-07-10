// wwwroot/bundle-entry.js — Single entry cho esbuild bundle.
// Liệt kê theo ĐÚNG ORDER như index.html ở chế độ dev (jsx + Babel).
// Mỗi file dạng IIFE đăng ký window.X, không export → import for side-effects.
//
// Quy tắc: KHÔNG đổi order giữa các block dưới (core trước components,
// components trước steps + pages, app.jsx CUỐI CÙNG — App() đọc window.HomePage v.v.).

// /lib — third-party-style libs (data, icons, hooks)
import "./lib/data.js";
import "./lib/icons.jsx";
import "./lib/util.js";
import "./lib/hooks.jsx";

// /core — cross-cutting concerns: router, storage, parsers, AI client, auth, page-loader
import "./core/router.jsx";
import "./core/storage.js";
import "./core/parsers.js";
import "./core/ai-provider.jsx";
import "./core/auth.jsx";
import "./core/page-loader.jsx";

// /components — reusable UI (dialogs PHẢI trước dialog-api để window.ConfirmDialog tồn tại)
import "./components/tweaks-panel.jsx";
import "./components/dialogs.jsx";
import "./components/customer-review-card.jsx";
import "./components/quota-upgrade-modal.jsx";
import "./components/consult-popup.jsx";
import "./components/search-controls.jsx";
import "./components/page-shell.jsx";
import "./components/data-controls.jsx";
import "./components/trace-view.jsx";
import "./components/tk-checkbox.jsx";
import "./components/pagination.jsx";
import "./components/table-scroll.jsx";
import "./components/ncc-tier-picker.jsx";
import "./components/hotel-picker-modal.jsx";
import "./core/dialog-api.jsx";

// /steps — wizard sub-views
import "./steps/step1.jsx";
import "./steps/step2.jsx";
import "./steps/step3.jsx";
import "./steps/step4.jsx";

// /pages — top-level pages
import "./pages/landing.jsx";
import "./pages/home.jsx";
import "./pages/wizard.jsx";
import "./pages/customers.jsx";
import "./pages/assistant.jsx";
import "./pages/jarvis.jsx";
import "./pages/mail.jsx";
import "./pages/visa.jsx";
import "./pages/visa-history.jsx";
import "./pages/deals.jsx";
import "./pages/tour-builder.jsx";
import "./pages/quotes.jsx";
import "./pages/quote-view.jsx";
import "./pages/ai-usage.jsx";
import "./pages/widget-admin.jsx";
import "./pages/ncc-import.jsx";
import "./pages/ncc-list.jsx";
import "./pages/visa-config.jsx";
import "./pages/workflows.jsx";
import "./pages/help.jsx";

// Root: App shell + router (PHẢI CUỐI — dùng tất cả window.X định nghĩa ở trên)
import "./app.jsx";
