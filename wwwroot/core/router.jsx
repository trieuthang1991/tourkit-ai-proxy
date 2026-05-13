// core/router.jsx — hash-based router cho no-build setup.
// URL: /#/wizard, /#/quotes, /#/customers, …
//
// Cách dùng:
//   <Router>
//     <Route path="/" component={WizardPage} />
//     <Route path="/quotes" component={QuotesPage} />
//     <Route path="/customers/:id" component={CustomerDetail} />
//     <NotFound />
//   </Router>
//
//   <Link to="/quotes">Báo giá</Link>
//   window.tourkitRouter.navigate('/quotes')
//
// Thêm page mới: 1 file pages/<name>.jsx + 1 <Route> trong app.jsx. Hết.

(function () {
  'use strict';

  // ─── Hash parsing ──────────────────────────────────────────────────────────
  function currentPath() {
    const h = window.location.hash || '';
    if (!h.startsWith('#')) return '/';
    // Strip query string (hash thường không có nhưng support phòng hờ)
    const p = h.slice(1).split('?')[0];
    return p || '/';
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

  function useHashPath() {
    const [path, setPath] = React.useState(currentPath());
    React.useEffect(() => {
      const onChange = () => setPath(currentPath());
      window.addEventListener('hashchange', onChange);
      return () => window.removeEventListener('hashchange', onChange);
    }, []);
    return path;
  }

  // ─── <Router> ──────────────────────────────────────────────────────────────
  // Walks children, picks first <Route> whose `path` matches. Children không phải <Route>
  // (vd: nav, header, sidebar) render bình thường ngoài context.
  function Router({ children }) {
    const path = useHashPath();
    const kids = React.Children.toArray(children);

    // Tách routes ra (children với prop.path) vs static elements (header, sidebar...)
    const routes = kids.filter(c => c.props && typeof c.props.path === 'string');
    const statics = kids.filter(c => !c.props || typeof c.props.path !== 'string');

    let matched = null;
    let matchedParams = null;
    for (const r of routes) {
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
      <a href={'#' + to} className={cls} style={style} {...rest}
         onClick={e => {
           if (onClick) onClick(e);
           // Cho phép cmd/ctrl-click mở tab mới (default browser hành xử)
         }}>
        {children}
      </a>
    );
  }

  // ─── Programmatic nav ──────────────────────────────────────────────────────
  function navigate(to) {
    if (window.location.hash === '#' + to) return;
    window.location.hash = to;
  }

  // ─── Expose ────────────────────────────────────────────────────────────────
  window.tourkitRouter = { Router, Route, Link, navigate, useRoute, currentPath };
})();
