// lib/hooks.jsx — React hooks dùng chung (1 NGUỒN).
// Thay cho các bản copy-paste từng page (_uCIsMobile/_nccIsMobile/_wzIsMobile/useIsMobile…).
// Expose qua window.tourkitHooks để mọi page gọi: window.tourkitHooks.useIsMobile().
(function () {
  const { useState, useEffect } = React;

  // Mobile breakpoint hook (≤bp px). Trước đây bị viết lại y hệt ở
  // customers.jsx / deals.jsx / ncc-list.jsx / wizard.jsx.
  function useIsMobile(bp = 640) {
    const [m, setM] = useState(() => window.innerWidth <= bp);
    useEffect(() => {
      const check = () => setM(window.innerWidth <= bp);
      window.addEventListener('resize', check);
      check();
      return () => window.removeEventListener('resize', check);
    }, [bp]);
    return m;
  }

  window.tourkitHooks = { useIsMobile };
})();
