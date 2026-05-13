// core/parsers.js — robust parsers cho LLM output:
//   - parseLooseJSON: gỡ markdown fences, normalize quotes, truncate-repair, balanced-bracket close.
//   - parseTourText: text format "TÊN: ... NGÀY n | ... HH:MM | TYPE | name | cost | NCC | desc".
//
// Tại sao tách parsers riêng: LLM hay trả output không sạch (truncate vì max_tokens, prose
// kèm JSON, sai dấu phẩy cuối). Logic repair phức tạp → giữ thuần helpers, không phụ thuộc React.

(function () {
  'use strict';

  // ─── parseLooseJSON ────────────────────────────────────────────────────────
  // Aggressive JSON repair: gỡ fence, trim đến balanced object, fix trailing comma,
  // tìm last complete element ở truncate point và close brackets cho phần còn lại.
  function parseLooseJSON(raw) {
    const cleaned = raw.replace(/```json\s*/gi, '').replace(/```\s*/g, '').trim();
    const start = cleaned.indexOf('{');
    if (start < 0) throw new Error('Không có JSON: ' + raw.slice(0, 80));
    let text = cleaned.slice(start);
    text = text.replace(/[""]/g, '"').replace(/['']/g, "'");

    // Trim tới first balanced top-level object — handle case model trả về 2 blob
    // (vd: "{a:[...]}\n{another:...}") hoặc JSON + prose. Nếu truncate (không có matching `}`),
    // endIdx = -1 → để nguyên cho repair logic xử lý.
    {
      let d = 0, s = false, e = false, endIdx = -1;
      for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (e) { e = false; continue; }
        if (c === '\\') { e = true; continue; }
        if (c === '"') { s = !s; continue; }
        if (s) continue;
        if (c === '{' || c === '[') d++;
        else if (c === '}' || c === ']') { d--; if (d === 0) { endIdx = i; break; } }
      }
      if (endIdx > 0) text = text.slice(0, endIdx + 1);
    }

    try { return JSON.parse(text); } catch (_) {}
    try { return JSON.parse(text.replace(/,\s*([}\]])/g, '$1')); } catch (_) {}

    // Aggressive: tìm last complete element ở array level rồi close brackets còn open.
    // Track CẢ last complete `}` (array of objects) lẫn last closing `"` (array of strings)
    // ở content depth của array, để cả {"a":[{...},{...}]} và {"titles":["...","..."]} đều repair được.
    let depth = 0, inStr = false, esc = false;
    let lastElemEnd = -1, arrayDepth = -1;
    for (let i = 0; i < text.length; i++) {
      const ch = text[i];
      if (esc) { esc = false; continue; }
      if (ch === '\\') { esc = true; continue; }
      if (ch === '"') {
        if (inStr && arrayDepth >= 0 && depth === arrayDepth + 1) lastElemEnd = i;
        inStr = !inStr;
        continue;
      }
      if (inStr) continue;
      if (ch === '[') { if (arrayDepth < 0) arrayDepth = depth; depth++; }
      else if (ch === '{') depth++;
      else if (ch === ']' || ch === '}') {
        depth--;
        if (ch === '}' && depth === arrayDepth + 1) lastElemEnd = i;
      }
    }

    if (lastElemEnd > 0 && arrayDepth >= 0) {
      let repaired = text.slice(0, lastElemEnd + 1);
      // Close remaining open brackets in stack order
      const stack = [];
      depth = 0; inStr = false; esc = false;
      for (let i = 0; i < repaired.length; i++) {
        const ch = repaired[i];
        if (esc) { esc = false; continue; }
        if (ch === '\\') { esc = true; continue; }
        if (ch === '"') { inStr = !inStr; continue; }
        if (inStr) continue;
        if (ch === '{') stack.push('}');
        else if (ch === '[') stack.push(']');
        else if (ch === '}' || ch === ']') stack.pop();
      }
      while (stack.length) repaired += stack.pop();
      try { return JSON.parse(repaired); }
      catch (e) { throw new Error('Parse fail sau repair: ' + e.message); }
    }
    throw new Error('Không parse được JSON cụt');
  }

  // ─── parseTourText ─────────────────────────────────────────────────────────
  // Text format dùng `|` separator — robust với truncate/malform.
  // Parser skip line malformed thay vì all-or-nothing fail → partial output vẫn dùng được.
  function parseTourText(raw, expectedDays) {
    const lines = raw.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
    const TYPES = new Set(['TRANSPORT', 'SIGHTSEEING', 'MEAL', 'HOTEL', 'ACTIVITY']);
    // Placeholder detection: angle bracket pair `<...>` HOẶC exact template phrase.
    // Không trigger trên "..." hay "<" đơn lẻ vì có thể xuất hiện trong title hợp lệ.
    const ANGLE_PLACEHOLDER = /<[^>]+>/;
    const TEMPLATE_PHRASES = /^(tên ngày|tên tour|tagline|tên activity|day name|tour name|activity name|n\/a)$/i;
    const isPlaceholderText = (t) => {
      const trimmed = (t || '').trim();
      if (trimmed.length < 3) return true;
      if (ANGLE_PLACEHOLDER.test(trimmed)) return true;
      if (TEMPLATE_PHRASES.test(trimmed)) return true;
      return false;
    };

    let name = '', tag = '';
    const rawDays = [];
    let currentDay = null;

    for (const line of lines) {
      if (/^TÊN\s*:/i.test(line)) {
        const v = line.replace(/^TÊN\s*:/i, '').trim();
        if (!isPlaceholderText(v)) name = v;
        continue;
      }
      if (/^TAG\s*:/i.test(line)) {
        const v = line.replace(/^TAG\s*:/i, '').trim();
        if (!isPlaceholderText(v)) tag = v;
        continue;
      }
      const dayMatch = line.match(/^NGÀY\s+(\d+)\s*[|:\-–]\s*(.+)$/i);
      if (dayMatch) {
        if (currentDay) rawDays.push(currentDay);
        const titleText = dayMatch[2].trim();
        currentDay = {
          dayNum: parseInt(dayMatch[1], 10),
          titleText,
          titleIsPlaceholder: isPlaceholderText(titleText),
          activities: []
        };
        continue;
      }
      // Activity line: HH:MM | TYPE | name | cost | supplier | description
      if (currentDay && line.includes('|')) {
        const all = line.split('|').map(p => p.trim());
        if (all.length < 6) continue;
        if (!/^\d{1,2}:\d{2}$/.test(all[0])) continue;
        const type = all[1].toUpperCase();
        if (!TYPES.has(type)) continue;
        if (isPlaceholderText(all[2])) continue;
        const desc = all.slice(5).join(' | ').trim();
        currentDay.activities.push({
          h: all[0],
          y: type,
          n: all[2],
          c: parseInt(all[3].replace(/[^\d]/g, ''), 10) || 0,
          s: all[4] || 'NCC',
          d: desc
        });
      }
    }
    if (currentDay) rawDays.push(currentDay);

    // Chỉ bỏ day với 0 activity. Title placeholder → fallback "Ngày N" generic.
    const withActivities = rawDays.filter(d => d.activities.length > 0);
    const capped = withActivities.slice(0, expectedDays).map((d, i) => ({
      day: i + 1,
      title: d.titleIsPlaceholder ? `Ngày ${i + 1}` : `Ngày ${i + 1}: ${d.titleText}`,
      activities: d.activities
    }));
    if (rawDays.length !== capped.length) {
      console.log(`[parseTourText] ${rawDays.length} raw markers → ${capped.length} valid (cap=${expectedDays})`);
    }
    if (capped.length === 0) {
      console.log('[parseTourText] DEBUG raw days:', rawDays);
      throw new Error(`Không parse được ngày nào có activities (raw markers=${rawDays.length})`);
    }
    return { name, tag, days: capped };
  }

  window.tourkitParsers = { parseLooseJSON, parseTourText };
})();
