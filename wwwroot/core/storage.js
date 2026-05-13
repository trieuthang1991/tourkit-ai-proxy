// core/storage.js — localStorage abstraction cho 3 thứ:
//   1) TourCache  — toàn bộ output (itinerary + marketing + costing) theo canonical request key
//   2) RequestHistory — list time-ordered các yêu cầu gần đây để user re-load
//   3) TourStats — hit/miss counters của TourCache
//
// Tất cả expose qua `window.tourkitStorage`. Module này không phụ thuộc React —
// có thể require từ bất kỳ step .jsx nào.

(function () {
  'use strict';

  // ─── TourCache (Level A) ────────────────────────────────────────────────────
  // Same request twice → instant restore, 0 API call.
  // Bump TOUR_CACHE_VER khi đổi shape của cached value (vd: thêm field mới).
  const TOUR_CACHE_VER    = 1;
  const TOUR_CACHE_TTL_MS = 7 * 24 * 60 * 60 * 1000;   // 7 ngày
  const TOUR_CACHE_PREFIX = `tourkit_tour_v${TOUR_CACHE_VER}:`;
  const TOUR_STATS_KEY    = 'tourkit_tour_stats';

  function buildTourCacheKey(req) {
    // Canonical: prefs sorted để [biển,núi] và [núi,biển] cùng key.
    // Notes (req.notes) CHƯA dùng trong prompts → không đưa vào key.
    const prefs = (req.preferences || []).slice().sort().join(',');
    return `${TOUR_CACHE_PREFIX}${req.route}|${req.days}n${req.nights}d|${req.adults}+${req.children}|${req.budgetPerPax}|${prefs}`;
  }

  function readTourCache(req) {
    try {
      const key = buildTourCacheKey(req);
      const raw = localStorage.getItem(key);
      if (!raw) return null;
      const cached = JSON.parse(raw);
      if (!cached || !cached.ts) return null;
      if (Date.now() - cached.ts >= TOUR_CACHE_TTL_MS) return null;
      if (!Array.isArray(cached.itinerary) || cached.itinerary.length === 0) return null;
      return { key, value: cached, ageMs: Date.now() - cached.ts };
    } catch { return null; }
  }

  function writeTourCache(req, value) {
    try {
      const key = buildTourCacheKey(req);
      localStorage.setItem(key, JSON.stringify({ ts: Date.now(), ...value }));
      bumpTourStat('saves');
      return key;
    } catch (e) {
      // QuotaExceeded hoặc serialize fail
      console.warn('[Storage] tour cache save failed:', e.message);
      return null;
    }
  }

  function bumpTourStat(field) {
    try {
      const s = JSON.parse(localStorage.getItem(TOUR_STATS_KEY) || '{"hits":0,"misses":0,"saves":0}');
      s[field] = (s[field] || 0) + 1;
      localStorage.setItem(TOUR_STATS_KEY, JSON.stringify(s));
      return s;
    } catch { return null; }
  }

  function tourHitRate(stats) {
    if (!stats) return '?';
    const total = (stats.hits || 0) + (stats.misses || 0);
    if (total === 0) return '0/0 (0%)';
    return `${stats.hits}/${total} (${Math.round(100 * stats.hits / total)}%)`;
  }

  // ─── RequestHistory ─────────────────────────────────────────────────────────
  // Time-ordered list cho user browse. Tách riêng TourCache: history giữ entry
  // ngay cả khi gen fail (user re-load để chỉnh + thử lại); cache chỉ giữ output thành công.
  const REQUEST_HISTORY_KEY = 'tourkit_request_history';
  const REQUEST_HISTORY_MAX = 30;

  function loadRequestHistory() {
    try {
      const raw = localStorage.getItem(REQUEST_HISTORY_KEY);
      if (!raw) return [];
      const arr = JSON.parse(raw);
      return Array.isArray(arr) ? arr : [];
    } catch { return []; }
  }

  function saveRequestToHistory(req) {
    try {
      if (!req || !req.route) return;
      const list = loadRequestHistory();
      const cacheKey = buildTourCacheKey(req);
      // Dedup theo canonical key — entry cũ bị thay bằng entry mới ở đầu list
      const deduped = list.filter(e => e.cacheKey !== cacheKey);
      const totalPax = (req.adults || 0) + (req.children || 0);
      const entry = {
        id: `rq_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`,
        ts: Date.now(),
        cacheKey,
        request: { ...req },
        summary: `${req.route} · ${req.days}N${req.nights}Đ · ${totalPax} khách`
      };
      deduped.unshift(entry);
      const capped = deduped.slice(0, REQUEST_HISTORY_MAX);
      localStorage.setItem(REQUEST_HISTORY_KEY, JSON.stringify(capped));
      return entry;
    } catch (e) {
      console.warn('[Storage] history save failed:', e.message);
    }
  }

  function deleteRequestFromHistory(id) {
    try {
      const list = loadRequestHistory().filter(e => e.id !== id);
      localStorage.setItem(REQUEST_HISTORY_KEY, JSON.stringify(list));
    } catch {}
  }

  function clearRequestHistory() {
    try { localStorage.removeItem(REQUEST_HISTORY_KEY); } catch {}
  }

  // ─── Public API ────────────────────────────────────────────────────────────
  window.tourkitStorage = {
    // Tour cache
    buildTourCacheKey,
    readTourCache,
    writeTourCache,
    bumpTourStat,
    tourHitRate,
    TOUR_CACHE_TTL_MS,
    // Request history
    loadRequestHistory,
    saveRequestToHistory,
    deleteRequestFromHistory,
    clearRequestHistory,
    REQUEST_HISTORY_MAX
  };

  // Backwards-compat alias used by step1.jsx
  window.tourkitHistory = {
    load: loadRequestHistory,
    save: saveRequestToHistory,
    remove: deleteRequestFromHistory,
    clear: clearRequestHistory
  };
})();
