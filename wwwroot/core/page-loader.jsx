// core/page-loader.jsx — Top progress bar slim (style YouTube/NProgress).
// Tự bật/tắt theo số lượng fetch đang chạy. Patch window.tourkitAuth.authedFetch + fetch
// để count automatically — page nào dùng cũng được, không cần wire thủ công.

(function () {
  'use strict';

  // ─── State + DOM bar ─────────────────────────────────────────────────────
  let active = 0;
  let bar = null;
  let progressTimer = null;
  let progress = 0;

  function ensureBar() {
    if (bar) return bar;
    bar = document.createElement('div');
    bar.id = 'app-page-loader';
    Object.assign(bar.style, {
      position: 'fixed', top: '0', left: '0', height: '3px', width: '0%',
      background: 'linear-gradient(90deg, #F97316, #EA580C)',
      boxShadow: '0 0 12px rgba(249,115,22,0.6)',
      zIndex: '99999',
      transition: 'width .25s ease, opacity .25s ease',
      opacity: '0', pointerEvents: 'none',
    });
    document.body.appendChild(bar);
    return bar;
  }

  function start() {
    ensureBar();
    bar.style.opacity = '1';
    if (progressTimer) return;
    progress = 0;
    bar.style.width = '0%';
    progressTimer = setInterval(() => {
      // Tăng nhanh đầu, chậm dần — không bao giờ 100% cho tới khi xong
      progress += (90 - progress) * 0.08;
      bar.style.width = progress.toFixed(1) + '%';
    }, 200);
  }

  function done() {
    if (!bar) return;
    if (progressTimer) { clearInterval(progressTimer); progressTimer = null; }
    bar.style.width = '100%';
    setTimeout(() => {
      bar.style.opacity = '0';
      setTimeout(() => { bar.style.width = '0%'; }, 250);
    }, 200);
  }

  function track(promise) {
    active++;
    if (active === 1) start();
    return promise.finally(() => {
      active--;
      if (active === 0) done();
    });
  }

  // ─── Patch fetch + authedFetch ────────────────────────────────────────────
  const _origFetch = window.fetch.bind(window);
  window.fetch = (...args) => track(_origFetch(...args));

  // Patch authedFetch sau khi tourkitAuth load (load có thứ tự script-tag).
  function patchAuthedFetch() {
    if (!window.tourkitAuth || window.tourkitAuth._loaderPatched) return;
    const orig = window.tourkitAuth.authedFetch;
    window.tourkitAuth.authedFetch = (url, opts) => track(orig(url, opts));
    window.tourkitAuth._loaderPatched = true;
  }
  // Thử patch ngay (script này load sau auth.jsx theo index.html order)
  if (window.tourkitAuth) patchAuthedFetch();
  else { /* polling nhẹ cho an toàn nếu thứ tự đổi */
    let n = 0;
    const tick = setInterval(() => {
      if (window.tourkitAuth) { patchAuthedFetch(); clearInterval(tick); }
      else if (++n > 50) clearInterval(tick);
    }, 100);
  }
})();
