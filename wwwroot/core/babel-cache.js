// core/babel-cache.js — Cache JSX đã transform vào localStorage để giảm parse mỗi lần load.
// Babel Standalone parse JSX in-browser ~80-150ms mỗi file × 30+ file = ~3-5s.
// Cache compiled output theo hash source → lần 2+ skip transform, page load nhanh hơn nhiều.
//
// Load TRƯỚC babel-standalone trong index.html. Patch Babel.transform để check cache trước.

(function () {
  'use strict';
  const CACHE_PREFIX = 'tkai_jsx_';
  const VERSION = '2'; // bump khi thay đổi cách compile
  const TTL_DAYS = 7;

  // Simple hash (FNV-1a 32-bit) — đủ cho cache key, nhanh hơn SHA.
  function hash(s) {
    let h = 0x811c9dc5;
    for (let i = 0; i < s.length; i++) {
      h ^= s.charCodeAt(i);
      h = (h + ((h << 1) + (h << 4) + (h << 7) + (h << 8) + (h << 24))) >>> 0;
    }
    return h.toString(36);
  }

  function readCache(key) {
    try {
      const raw = localStorage.getItem(CACHE_PREFIX + key);
      if (!raw) return null;
      const obj = JSON.parse(raw);
      if (obj.v !== VERSION) return null;
      if (Date.now() - obj.t > TTL_DAYS * 86400000) {
        localStorage.removeItem(CACHE_PREFIX + key);
        return null;
      }
      return obj.code;
    } catch { return null; }
  }
  function writeCache(key, code) {
    try {
      localStorage.setItem(CACHE_PREFIX + key, JSON.stringify({ v: VERSION, t: Date.now(), code }));
    } catch (e) {
      // localStorage full → xóa các entry cũ
      try {
        const old = [];
        for (let i = 0; i < localStorage.length; i++) {
          const k = localStorage.key(i);
          if (k?.startsWith(CACHE_PREFIX)) old.push(k);
        }
        old.slice(0, Math.ceil(old.length / 2)).forEach(k => localStorage.removeItem(k));
        // không retry
      } catch {}
    }
  }

  // Đợi Babel load xong rồi patch
  let patched = false;
  function patchBabel() {
    if (patched) return true;
    if (!window.Babel?.transform) return false;
    const orig = window.Babel.transform.bind(window.Babel);
    window.Babel.transform = function (code, options) {
      // Chỉ cache khi là JSX transform (preset-react/typescript) — bỏ qua case parse khác.
      const isJsx = options?.presets?.some(p =>
        (typeof p === 'string' && /react|typescript/.test(p)) ||
        (Array.isArray(p) && /react|typescript/.test(p[0])));
      if (!isJsx) return orig(code, options);
      const key = hash(code + JSON.stringify(options?.presets || ''));
      const cached = readCache(key);
      if (cached) return { code: cached };
      const result = orig(code, options);
      if (result?.code) writeCache(key, result.code);
      return result;
    };
    patched = true;
    return true;
  }

  if (!patchBabel()) {
    const iv = setInterval(() => { if (patchBabel()) clearInterval(iv); }, 50);
    setTimeout(() => clearInterval(iv), 5000);
  }
})();
