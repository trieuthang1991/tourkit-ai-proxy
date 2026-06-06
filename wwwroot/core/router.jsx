// core/router.jsx — HTML5 history-based router (KHÔNG còn `#` trong URL).
// URL: /wizard, /quotes, /customers/123, …
//
// Cách dùng:
//   <Router>
//     <Route path="/" component={WizardPage} />
//     <Route path="/quotes" component={QuotesPage} />
//     <Route path="/customers/:id" component={CustomerDetail} />
//     <NotFound />   {/* path="*" */}
//   </Router>
//
//   <Link to="/quotes">Báo giá</Link>
//   window.tourkitRouter.navigate('/quotes')
//
// CẦN server fallback: mọi GET không match endpoint API/file → trả index.html
// (xem app.MapFallbackToFile("index.html") trong Program.cs).
// Trên IIS — đã tự cấu hình qua aspNetCore module (ASP.NET Core fallback chạy qua).

(function () {
  'use strict';

  // ─── Migrate hash URL cũ → path URL mới (1 lần khi user mở link cũ) ────────
  // VD user bookmark /#/mail → tự redirect sang /mail.
  (function migrateHashToPath() {
    const h = window.location.hash || '';
    if (h.startsWith('#/') || h === '#') {
      const newPath = h.slice(1) || '/';
      const target = newPath + window.location.search;
      window.history.replaceState({}, '', target);
    }
  })();

  // ─── Path parsing ──────────────────────────────────────────────────────────
  function currentPath() {
    const p = window.location.pathname || '/';
    return p === '' ? '/' : p;
  }

  function matchRoute(pattern, path) {
    const pSeg = pattern.split('/').filter(Boolean);
    const aSeg = path.split('/').filter(Boolean);
    if (pSeg.length !== aSeg.length) return null;
    const params = {};
    for (let i = 0; i < pSeg.length; i++) {
      if (pSeg[i].startsWith(':')) params[pSeg[i].slice(1)] = decodeURIComponent(aSeg[i]);
      else if (pSeg[i] !== aSeg[i]) return null;
    }
    return params;
  }

  // ─── React context cho route info ──────────────────────────────────────────
  const RouteCtx = React.createContext({ path: '/', params: {} });

  function useRoute() { return React.useContext(RouteCtx); }

  // Theo dõi pushState/replaceState/popstate (history API không tự fire event khi push)
  function usePath() {
    const [path, setPath] = React.useState(currentPath());
    React.useEffect(() => {
      const onChange = () => setPath(currentPath());
      window.addEventListener('popstate', onChange);
      window.addEventListener('tourkit:navigate', onChange);
      return () => {
        window.removeEventListener('popstate', onChange);
        window.removeEventListener('tourkit:navigate', onChange);
      };
    }, []);
    return path;
  }

  // ─── <Router> ──────────────────────────────────────────────────────────────
  function Router({ children }) {
    const path = usePath();
    const kids = React.Children.toArray(children);

    const routes  = kids.filter(c => c.props && typeof c.props.path === 'string');
    const statics = kids.filter(c => !c.props || typeof c.props.path !== 'string');

    let matched = null;
    let matchedParams = null;
    for (const r of routes) {
      if (r.props.path === '*') continue;
      const params = matchRoute(r.props.path, path);
      if (params) { matched = r; matchedParams = params; break; }
    }

    const ctx = { path, params: matchedParams || {} };
    return (
      <RouteCtx.Provider value={ctx}>
        {statics}
        {matched ? React.cloneElement(matched, { _matchedParams: matchedParams }) : null}
        {!matched && routes.find(r => r.props.path === '*')
          ? React.cloneElement(routes.find(r => r.props.path === '*'))
          : null}
      </RouteCtx.Provider>
    );
  }

  // ─── <Route path="..." component={...} /> ──────────────────────────────────
  function Route({ component: Cmp, render, _matchedParams }) {
    if (render) return render(_matchedParams || {});
    if (Cmp) return <Cmp params={_matchedParams || {}} />;
    return null;
  }

  // ─── <Link to="..."> ───────────────────────────────────────────────────────
  function Link({ to, children, className, style, onClick, ...rest }) {
    const { path } = useRoute();
    const active = path === to;
    const cls = [className, active ? 'active' : ''].filter(Boolean).join(' ');
    return (
      <a href={to} className={cls} style={style} {...rest}
         onClick={e => {
           // Cho phép cmd/ctrl/shift-click + middle-click mở tab mới (default browser)
           if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
           e.preventDefault();
           if (onClick) onClick(e);
           navigate(to);
         }}>
        {children}
      </a>
    );
  }

  // ─── Programmatic nav ──────────────────────────────────────────────────────
  function navigate(to, opts = {}) {
    if (currentPath() === to) return;
    if (opts.replace) window.history.replaceState({}, '', to);
    else window.history.pushState({}, '', to);
    window.dispatchEvent(new CustomEvent('tourkit:navigate'));
  }

  // ─── Expose ────────────────────────────────────────────────────────────────
  window.tourkitRouter = { Router, Route, Link, navigate, useRoute, currentPath };
})();
