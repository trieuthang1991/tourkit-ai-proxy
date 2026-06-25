// Admin shell — /admin-trav-ai/*
// Single-file React app: shell + login + sub-router + tất cả page components.
// KHÔNG share window.tourkit* với user-facing app — admin tự isolation.
(function () {
  const { useState, useEffect } = React;

  function AdminApp() {
    return (
      <div className="admin-loading">
        Admin shell loading… <br/>
        <small>{location.pathname}</small>
      </div>
    );
  }

  const root = ReactDOM.createRoot(document.getElementById("admin-root"));
  root.render(<AdminApp />);
})();
