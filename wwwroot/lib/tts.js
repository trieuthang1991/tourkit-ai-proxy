// lib/tts.js — Phát giọng đọc (TTS) dùng chung: gọi server /speech/tts rồi phát audio.
// 1 NGUỒN cho mọi nút "Nghe" ngoài JARVIS. Vì sao qua server chứ không speechSynthesis: giọng
// trình duyệt tùy máy (Windows/Mac/điện thoại khác nhau, có máy không có giọng Việt) → server cho
// giọng đồng nhất. Dùng 1 phần tử <audio> DUY NHẤT: iOS chỉ cho play() sau khi phần tử được mở
// khoá bằng 1 cú chạm tay; tạo mới mỗi lần → iOS chặn. Nút "Nghe" là cú chạm đó.
// LƯU Ý iOS: play() ở đây chạy SAU fetch nên có thể mất "gesture token" → lần đầu trên iOS Safari
// có thể bị chặn (trả onError "chạm lại để nghe"). Bản đầy đủ (mồi unlock im lặng + WebAudio +
// tap-to-play) nằm ở wwwroot/pages/jarvis.jsx nếu sau này cần cho trang dùng nhiều.
'use strict';
(function () {
  let audioEl = null;
  function el() {
    if (!audioEl) { audioEl = new Audio(); audioEl.preload = 'auto'; }
    return audioEl;
  }
  let curUrl = null;                 // object URL đang phát → revoke khi xong/đổi
  function cleanupUrl() { if (curUrl) { try { URL.revokeObjectURL(curUrl); } catch {} curUrl = null; } }

  let gen = 0;                       // "đời" phát hiện tại — tăng mỗi speak()/stop() để vô hiệu hoá call cũ còn treo
  let curAc = null;                  // AbortController của fetch đang bay → hủy khi stop()/speak() mới

  function stop() {
    gen++;                                        // mọi continuation cũ so gen sẽ tự bỏ
    if (curAc) { try { curAc.abort(); } catch {} curAc = null; }
    const a = el();
    try { a.pause(); a.currentTime = 0; } catch {}
    a.onended = null; a.onerror = null;
    cleanupUrl();
  }

  // Phát 1 đoạn text. Trả { stop } để caller ngắt giữa chừng.
  function speak(text, cbs) {
    cbs = cbs || {};
    const t = String(text || '').trim();
    if (!t) { cbs.onError && cbs.onError('Không có nội dung để đọc'); return { stop() {} }; }

    stop();                                       // ngắt cái đang phát + hủy fetch cũ (gen đã tăng)
    const my = gen;                               // đời của call này
    const ac = new AbortController();
    curAc = ac;

    window.tourkitAuth.authedFetch('/api/v1/speech/tts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: t }),
      signal: ac.signal,
    }).then(async (r) => {
      if (my !== gen) return;                      // đã có speak()/stop() mới hơn → bỏ
      if (!r.ok) {
        let msg = 'HTTP ' + r.status;
        try { const j = await r.json(); msg = j.error || msg; } catch {}
        throw new Error(msg);
      }
      // Quá dài thì server đọc rút gọn. Không báo ra thì người nghe tưởng nội dung hết ở chỗ
      // tiếng nói dừng — đúng kiểu hỏng im lặng, nghe xong không biết là mình đang thiếu thông tin.
      if (r.headers.get('X-Tts-Truncated') === '1') cbs.onTruncated && cbs.onTruncated();
      const buf = await r.arrayBuffer();
      if (my !== gen) return;
      cleanupUrl();
      curUrl = URL.createObjectURL(new Blob([buf], { type: 'audio/mpeg' }));
      const a = el();
      a.src = curUrl;
      a.onended = () => { if (my === gen) { cleanupUrl(); cbs.onEnd && cbs.onEnd(); } };
      a.onerror = () => { if (my === gen) { cleanupUrl(); cbs.onError && cbs.onError('Không phát được audio'); } };
      cbs.onStart && cbs.onStart();
      try {
        await a.play();
      } catch (e) {
        // iOS chặn play() sau fetch (mất gesture) hoặc lỗi phát → dọn URL + báo để người dùng chạm lại.
        if (my === gen) { cleanupUrl(); cbs.onError && cbs.onError('Trình duyệt chặn phát — chạm lại để nghe'); }
      }
    }).catch((e) => {
      if (my !== gen || e.name === 'AbortError') return;
      cbs.onError && cbs.onError(e.message || 'Lỗi đọc');
    });

    return { stop() { if (my === gen) stop(); } };
  }

  window.tourkitTts = { speak, stop };
})();
