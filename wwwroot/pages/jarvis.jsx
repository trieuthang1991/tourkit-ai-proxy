// pages/jarvis.jsx — "Trợ lý JARVIS" (HUD hội thoại 3D).
// PHÔ DIỄN CÔNG NGHỆ: giao diện JARVIS-style (orb Three.js phản ứng trạng thái) nói chuyện với AI.
// Backend DÙNG NGUYÊN cái chat hiện có: POST /api/v1/chat/stream (planner → /api/ai/* → phân tích).
// Không đụng /assistant. Self-contained: Three.js lazy-load qua CDN + CSS inject inline.
//
// Map trạng thái: SSE stage planning/fetching → orb "thinking"; delta/analyzing → "responding";
// done → "idle" (+ đọc reply qua TTS trình duyệt nếu bật loa).

const { useState: _jS, useEffect: _jE, useRef: _jR } = React;

// ── Lazy-load Three.js (UMD r128 → window.THREE). Chỉ tải khi trang JARVIS mở. ──
function ensureThree() {
  if (window.THREE) return Promise.resolve(window.THREE);
  if (window.__jvThreePromise) return window.__jvThreePromise;
  window.__jvThreePromise = new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = 'https://cdnjs.cloudflare.com/ajax/libs/three.js/r128/three.min.js';
    s.async = true;
    s.onload = () => resolve(window.THREE);
    s.onerror = () => reject(new Error('Không tải được Three.js (CDN chặn?)'));
    document.head.appendChild(s);
  });
  return window.__jvThreePromise;
}

// Màu theo trạng thái: idle = cyan tĩnh, listening = cyan sáng nhịp nhanh,
// thinking = hổ phách gấp gáp, responding = cam TRAV-AI.
const ORB_COLORS = { idle: 0x38bdf8, listening: 0x22d3ee, thinking: 0xf59e0b, responding: 0xff7a1a };

// ── Orb 3D: icosahedron wireframe + lõi + hào quang additive + hạt quỹ đạo. ──
// Nhận prop `state` ('idle'|'thinking'|'responding') → đổi tốc độ xoay / nhịp đập / màu (lerp mượt).
function JarvisOrb({ state }) {
  const mountRef = _jR(null);
  const stateRef = _jR(state);
  const [err, setErr] = _jS(null);
  _jE(() => { stateRef.current = state; }, [state]);

  _jE(() => {
    let alive = true;
    let cleanup = () => {};
    ensureThree().then(THREE => {
      if (!alive || !mountRef.current) return;
      const el = mountRef.current;
      const W = () => el.clientWidth || 1, H = () => el.clientHeight || 1;

      const scene = new THREE.Scene();
      const camera = new THREE.PerspectiveCamera(50, W() / H(), 0.1, 100);
      camera.position.z = 4.4;
      const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
      renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
      renderer.setSize(W(), H());
      el.appendChild(renderer.domElement);

      // Khung wireframe chính
      const shell = new THREE.LineSegments(
        new THREE.WireframeGeometry(new THREE.IcosahedronGeometry(1.35, 2)),
        new THREE.LineBasicMaterial({ color: ORB_COLORS.idle, transparent: true, opacity: 0.55 }));
      scene.add(shell);
      // Lõi trong quay ngược
      const core = new THREE.Mesh(
        new THREE.IcosahedronGeometry(0.82, 1),
        new THREE.MeshBasicMaterial({ color: ORB_COLORS.idle, wireframe: true, transparent: true, opacity: 0.25 }));
      scene.add(core);
      // Hào quang additive
      const glow = new THREE.Mesh(
        new THREE.SphereGeometry(1.08, 40, 40),
        new THREE.MeshBasicMaterial({ color: ORB_COLORS.idle, transparent: true, opacity: 0.06, blending: THREE.AdditiveBlending }));
      scene.add(glow);
      // Hạt quỹ đạo (sphere shell)
      const N = 850;
      const pos = new Float32Array(N * 3);
      for (let i = 0; i < N; i++) {
        const r = 1.9 + Math.random() * 1.0, a = Math.random() * Math.PI * 2, b = Math.acos(2 * Math.random() - 1);
        pos[i * 3] = r * Math.sin(b) * Math.cos(a);
        pos[i * 3 + 1] = r * Math.sin(b) * Math.sin(a);
        pos[i * 3 + 2] = r * Math.cos(b);
      }
      const pgeo = new THREE.BufferGeometry();
      pgeo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
      const pts = new THREE.Points(pgeo,
        new THREE.PointsMaterial({ color: 0x7dd3fc, size: 0.02, transparent: true, opacity: 0.7 }));
      scene.add(pts);

      const cur = new THREE.Color(ORB_COLORS.idle);
      const tgt = new THREE.Color(ORB_COLORS.idle);
      const clock = new THREE.Clock();
      let t = 0, spin = 0, raf = 0;

      const frame = () => {
        raf = requestAnimationFrame(frame);
        const dt = Math.min(clock.getDelta(), 0.05); t += dt;
        const st = stateRef.current;
        tgt.setHex(ORB_COLORS[st] ?? ORB_COLORS.idle);
        cur.lerp(tgt, Math.min(1, dt * 3));
        shell.material.color.copy(cur); core.material.color.copy(cur);
        glow.material.color.copy(cur); pts.material.color.copy(cur);

        const speed = st === 'thinking' ? 1.7 : st === 'responding' ? 0.95 : st === 'listening' ? 0.5 : 0.28;
        spin += dt * speed;
        shell.rotation.set(spin * 0.4, spin, 0);
        core.rotation.set(0, -spin * 1.4, spin * 0.6);
        pts.rotation.y = -spin * 0.5;

        const amp = st === 'thinking' ? 0.10 : st === 'responding' ? 0.14 : st === 'listening' ? 0.085 : 0.045;
        const psp = st === 'thinking' ? 7 : st === 'responding' ? 4 : st === 'listening' ? 3 : 1.6;
        const s = 1 + Math.sin(t * psp) * amp;
        shell.scale.setScalar(s);
        glow.scale.setScalar(s * 1.02);
        glow.material.opacity = 0.05 + (Math.sin(t * psp) * 0.5 + 0.5) * (st === 'idle' ? 0.03 : 0.11);
        renderer.render(scene, camera);
      };
      frame();

      const onResize = () => {
        if (!mountRef.current) return;
        camera.aspect = W() / H(); camera.updateProjectionMatrix();
        renderer.setSize(W(), H());
      };
      window.addEventListener('resize', onResize);

      cleanup = () => {
        window.removeEventListener('resize', onResize);
        cancelAnimationFrame(raf);
        renderer.dispose();
        if (renderer.domElement.parentNode) renderer.domElement.parentNode.removeChild(renderer.domElement);
      };
    }).catch(e => { if (alive) setErr(e.message); });

    return () => { alive = false; cleanup(); };
  }, []);

  return (
    <div className="jv-orb" ref={mountRef}>
      {err && <div className="jv-orb-err"><Icon name="warning" size={22} /><span>{err}</span></div>}
    </div>
  );
}

// Chọn giọng tiếng Việt TỐT NHẤT có sẵn (miễn phí). null nếu máy không có giọng vi nào.
// Ưu tiên giọng neural "Natural/Online" (Edge: HoaiMy/NamMinh) → tự nhiên gần bằng TTS trả phí.
// KHÔNG fallback giọng tiếng Anh (đọc chữ Việt bằng giọng Anh = ngọng líu lô).
function getViVoice() {
  const vs = window.speechSynthesis?.getVoices() || [];
  const vi = vs.filter(v => /^vi([-_]|$)/i.test(v.lang) || /vietnam/i.test(v.name));
  if (!vi.length) return null;
  return vi.find(v => /natural|online/i.test(v.name))     // Edge neural (hay nhất)
      || vi.find(v => /hoaimy|namminh/i.test(v.name))     // tên giọng vi neural
      || vi[0];                                           // giọng vi bất kỳ (SAPI cũ)
}

// Đọc reply qua Web Speech API (MIỄN PHÍ). Chỉ đọc khi có giọng vi thật (tránh ngọng).
// cb.onstart/onend để caller tạm ngưng "luôn nghe" khi loa đang đọc (chống mic nghe lại chính loa).
function speak(text, voice, cb) {
  const synth = window.speechSynthesis;
  if (!synth || !text || !voice) return;
  const clean = String(text)
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/\[(.*?)\]\(.*?\)/g, '$1')
    .replace(/[*_`#>|~]/g, '')
    .replace(/\s+/g, ' ').trim().slice(0, 700);
  if (!clean) return;
  const u = new SpeechSynthesisUtterance(clean);
  u.voice = voice; u.lang = voice.lang || 'vi-VN';
  u.rate = 1; u.pitch = 1;
  if (cb) { u.onstart = cb.onstart || null; u.onend = cb.onend || null; u.onerror = cb.onend || null; }
  synth.cancel();
  synth.speak(u);
}

// Nhận diện giọng nói liên tục (SpeechRecognition) — Chrome/Edge. null nếu không hỗ trợ.
const SpeechRec = window.SpeechRecognition || window.webkitSpeechRecognition;

function JarvisPage({ pushToast }) {
  const sessionId = window.tourkitAuth.getSessionId();
  const sessionInfo = window.tourkitAuth.getUser();

  const [messages, setMessages] = _jS([]);          // {role, content}
  const [input, setInput] = _jS('');
  const [orbState, setOrbState] = _jS('idle');       // 'idle'|'thinking'|'responding'
  const [loading, setLoading] = _jS(false);
  const [voiceOn, setVoiceOn] = _jS(false);          // đọc reply bằng loa
  const [rec, setRec] = _jS('idle');                 // 'idle'|'recording'|'uploading' (mic bấm-tay)
  const [lastTool, setLastTool] = _jS(null);
  const mediaRef = _jR(null);
  const logRef = _jR(null);
  const audioRef = _jR(null);   // <audio> đang phát TTS OpenAI (để dừng khi hỏi câu mới)
  const ttsDisabledRef = _jR(false);   // OpenAI TTS lỗi cấu hình (thiếu key) → ngừng thử, khỏi spam

  // ── Chế độ "luôn nghe" (hands-free) qua SpeechRecognition ──────────────────
  const [listening, setListening] = _jS(false);      // bật/tắt nghe liên tục
  const [interim, setInterim] = _jS('');             // chữ đang nhận diện (chưa chốt)
  const [speaking, setSpeaking] = _jS(false);        // loa đang đọc (TTS)
  const listeningRef = _jR(false);
  const loadingRef = _jR(false);
  const speakingRef = _jR(false);
  const recogRef = _jR(null);
  const sendRef = _jR(null);
  _jE(() => { listeningRef.current = listening; }, [listening]);
  _jE(() => { loadingRef.current = loading; }, [loading]);
  _jE(() => { speakingRef.current = speaking; }, [speaking]);

  const cfg = (window.tourkit && window.tourkit.ai && window.tourkit.ai.getConfig)
    ? window.tourkit.ai.getConfig() : {};

  _jE(() => {
    if (logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight;
  }, [messages]);

  // Web Speech voices nạp bất đồng bộ — kích + lắng nghe voiceschanged để danh sách sẵn khi speak.
  _jE(() => {
    const synth = window.speechSynthesis;
    if (!synth) return;
    try { synth.getVoices(); } catch {}
    const onVoices = () => { try { synth.getVoices(); } catch {} };
    synth.addEventListener?.('voiceschanged', onVoices);
    return () => synth.removeEventListener?.('voiceschanged', onVoices);
  }, []);
  // Dừng đọc khi rời trang
  _jE(() => () => { try { window.speechSynthesis?.cancel(); audioRef.current?.pause(); } catch {} }, []);

  async function send(textArg) {
    const text = (typeof textArg === 'string' ? textArg : input).trim();
    if (!text || loading || !sessionId) return;
    const next = [...messages, { role: 'user', content: text }];
    const asstIdx = next.length;
    setMessages([...next, { role: 'assistant', content: '', streaming: true }]);
    setInput('');
    setLoading(true);
    setOrbState('thinking');
    try { window.speechSynthesis?.cancel(); } catch {}
    try { audioRef.current?.pause(); } catch {}   // ngắt TTS đang đọc khi hỏi câu mới

    const patch = (fn) => setMessages(m => { const c = [...m]; if (c[asstIdx]) c[asstIdx] = fn(c[asstIdx]); return c; });

    try {
      const resp = await fetch('/api/v1/chat/stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream', 'X-Session-Id': sessionId },
        body: JSON.stringify({ messages: next, provider: cfg.provider, model: cfg.model })
      });
      if (resp.status === 401) { pushToast('Phiên hết hạn — đăng nhập lại', 'error'); window.tourkitAuth.logout(); return; }
      if (!resp.ok || !resp.body) {
        const t = await resp.text().catch(() => '');
        throw new Error(t.slice(0, 200) || ('HTTP ' + resp.status));
      }

      let full = '';
      await window.tourkitUtil.readSSE(resp, o => {
        if (o.error) { patch(a => ({ ...a, content: '⚠️ ' + o.error, error: true, streaming: false })); setOrbState('idle'); return; }
        if (o.stage) {
          setOrbState(o.stage === 'analyzing' ? 'responding' : 'thinking');
          if (o.tool) setLastTool(o.tool);
          return;
        }
        if (o.delta) { setOrbState('responding'); full += o.delta; patch(a => ({ ...a, content: a.content + o.delta })); return; }
        if (o.done) {
          if (o.reply) { full = o.reply; patch(a => ({ ...a, content: o.reply })); }
          if (o.toolName) setLastTool(o.toolName);
        }
      });
      if (voiceOn) speakReply(full);
    } catch (e) {
      patch(a => ({ ...a, content: '⚠️ ' + e.message, error: true }));
    } finally {
      patch(a => ({ ...a, streaming: false }));
      setLoading(false);
      setOrbState(listeningRef.current ? 'listening' : 'idle');
    }
  }

  // Đọc reply (hybrid, tiết kiệm): ưu tiên giọng vi MIỄN PHÍ của trình duyệt (Edge Natural);
  // máy KHÔNG có giọng vi → OpenAI TTS (có phí, server cache câu lặp). Báo speaking để chống vọng âm.
  function speakReply(text) {
    if (!voiceOn) return;
    const v = window.speechSynthesis ? getViVoice() : null;
    if (v) {
      speak(text, v, { onstart: () => setSpeaking(true), onend: () => setSpeaking(false) });
    } else {
      speakViaOpenAI(text);   // fallback trả phí (chỉ khi không có giọng vi free)
    }
  }

  // OpenAI TTS: POST /speech/tts → mp3 → phát. Server cache theo nội dung (câu lặp = free).
  // Thiếu key / lỗi cấu hình → báo 1 lần rồi NGỪNG thử (khỏi spam mỗi câu + khỏi phí vô ích).
  async function speakViaOpenAI(text) {
    if (ttsDisabledRef.current) return;
    const clean = String(text).replace(/```[\s\S]*?```/g, ' ').replace(/\[(.*?)\]\(.*?\)/g, '$1')
      .replace(/[*_`#>|~]/g, '').replace(/\s+/g, ' ').trim();
    if (!clean) return;
    try {
      try { audioRef.current?.pause(); } catch {}
      setSpeaking(true);
      const r = await window.tourkitAuth.authedFetch('/api/v1/speech/tts', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: clean })
      });
      if (!r.ok) {
        setSpeaking(false);
        const j = await r.json().catch(() => ({}));
        ttsDisabledRef.current = true;   // ngừng thử tiếp
        pushToast((j.error || 'TTS lỗi') + ' — dùng Microsoft Edge để có giọng đọc miễn phí, hoặc thêm key OpenAI.', 'warn');
        return;
      }
      const url = URL.createObjectURL(await r.blob());
      const audio = new Audio(url);
      audioRef.current = audio;
      const done = () => { setSpeaking(false); URL.revokeObjectURL(url); };
      audio.onended = done; audio.onerror = done;
      await audio.play();
    } catch (e) { setSpeaking(false); pushToast('TTS lỗi: ' + e.message, 'error'); }
  }
  _jE(() => { sendRef.current = send; });   // luôn trỏ tới send mới nhất (tránh closure cũ trong recognition)

  // ── Ghi âm → /api/v1/speech/transcribe → tự gửi (reuse endpoint Whisper sẵn có) ──
  async function toggleMic() {
    if (rec === 'recording') { stopMic(); return; }
    if (rec === 'uploading' || loading) return;
    if (!navigator.mediaDevices?.getUserMedia) { pushToast('Trình duyệt không hỗ trợ ghi âm', 'error'); return; }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const mime = MediaRecorder.isTypeSupported('audio/webm;codecs=opus') ? 'audio/webm;codecs=opus' : 'audio/webm';
      const recorder = new MediaRecorder(stream, { mimeType: mime });
      const chunks = [];
      recorder.ondataavailable = e => { if (e.data?.size > 0) chunks.push(e.data); };
      recorder.onstop = async () => {
        stream.getTracks().forEach(t => t.stop());
        setRec('uploading');
        try {
          const fd = new FormData();
          fd.append('file', new Blob(chunks, { type: mime }), 'recording.webm');
          const r = await window.tourkitAuth.authedFetch('/api/v1/speech/transcribe', { method: 'POST', body: fd });
          const j = await r.json();
          if (!r.ok || j.error) throw new Error(j.error || 'HTTP ' + r.status);
          const tr = (j.text || '').trim();
          if (tr) send(tr); else pushToast('Không nhận diện được tiếng', 'warn');
        } catch (e) { pushToast('Lỗi nhận diện: ' + e.message, 'error'); }
        finally { setRec('idle'); }
      };
      mediaRef.current = { recorder, stream };
      recorder.start(250);
      setRec('recording');
    } catch (e) {
      pushToast('Không truy cập được mic: ' + (e.message || e.name), 'error');
      setRec('idle');
    }
  }
  function stopMic() {
    const m = mediaRef.current;
    if (m?.recorder?.state === 'recording') { try { m.recorder.stop(); } catch {} }
    mediaRef.current = null;
  }

  // Tạo SpeechRecognition 1 lần. onresult: chốt câu → auto gửi; interim → hiện realtime.
  // onend: tự khởi động lại nếu vẫn bật & không bận (browser hay tự dừng sau im lặng/60s).
  _jE(() => {
    if (!SpeechRec) return;
    const r = new SpeechRec();
    r.lang = 'vi-VN'; r.continuous = true; r.interimResults = true;
    r.onresult = (e) => {
      let itm = '', fin = '';
      for (let i = e.resultIndex; i < e.results.length; i++) {
        const res = e.results[i];
        if (res.isFinal) fin += res[0].transcript; else itm += res[0].transcript;
      }
      setInterim(itm);
      const f = fin.trim();
      if (f) { setInterim(''); sendRef.current && sendRef.current(f); }   // nói xong 1 câu → gửi luôn
    };
    r.onend = () => {
      // Tự bật lại khi vẫn đang ở chế độ nghe & AI rảnh & loa không đọc.
      if (listeningRef.current && !loadingRef.current && !speakingRef.current) {
        try { r.start(); } catch {}
      }
    };
    r.onerror = (ev) => {
      if (ev.error === 'not-allowed' || ev.error === 'service-not-allowed') {
        pushToast('Mic bị chặn — cho phép quyền rồi bật lại "Luôn nghe"', 'error');
        setListening(false);
      }
      // 'no-speech'/'aborted' → onend sẽ tự restart, bỏ qua
    };
    recogRef.current = r;
    return () => { try { r.onend = null; r.abort(); } catch {} recogRef.current = null; };
  }, []);

  // Điều phối start/stop theo (listening, loading, speaking): chỉ nghe khi bật + AI rảnh + loa im.
  _jE(() => {
    const r = recogRef.current;
    if (!r) return;
    if (listening && !loading && !speaking) {
      try { r.start(); } catch {}   // start() ném nếu đã chạy → nuốt
      setOrbState(s => (s === 'idle' ? 'listening' : s));
    } else {
      try { r.stop(); } catch {}
      if (!listening) { setInterim(''); setOrbState(s => (s === 'listening' ? 'idle' : s)); }
    }
  }, [listening, loading, speaking]);

  function toggleListening() {
    if (!SpeechRec) { pushToast('Trình duyệt không hỗ trợ nhận diện liên tục (dùng Chrome/Edge)', 'error'); return; }
    setListening(v => {
      const nv = !v;
      if (nv) { window.speechSynthesis?.cancel(); setOrbState(s => (s === 'idle' ? 'listening' : s)); }
      else { setOrbState(s => (s === 'listening' ? 'idle' : s)); }
      return nv;
    });
  }
  // Dừng nghe khi rời trang
  _jE(() => () => { try { recogRef.current?.abort(); } catch {} }, []);

  const STATUS = {
    idle: { label: 'SẴN SÀNG', cls: 'idle' },
    listening: { label: 'ĐANG NGHE…', cls: 'listening' },
    thinking: { label: 'ĐANG SUY NGHĨ', cls: 'thinking' },
    responding: { label: 'ĐANG TRẢ LỜI', cls: 'responding' },
  }[orbState] || { label: 'SẴN SÀNG', cls: 'idle' };

  const suggestions = ['Doanh thu tháng này', 'Top khách hàng', 'Tour sắp khởi hành', 'Cơ hội bán hàng đang chờ'];

  return (
    <main className="page jv-wrap">
      <JarvisStyle />
      <div className="jv-hud">
        {/* Thanh trên: danh tính + điều khiển */}
        <div className="jv-topbar">
          <div className="jv-brand">
            <span className="jv-brand-mark">◈</span>
            <div>
              <div className="jv-brand-name">TRAV-AI · JARVIS</div>
              <div className="jv-brand-sub">GIAO DIỆN HỘI THOẠI · NEURAL HUD</div>
            </div>
          </div>
          <div className="jv-controls">
            <span className="jv-readout">TENANT <b>{sessionInfo?.tenantId || '—'}</b></span>
            <span className="jv-readout">MODEL <b>{cfg.model || 'auto'}</b></span>
            {lastTool && lastTool !== 'none' && <span className="jv-readout">TOOL <b>{lastTool}</b></span>}
            <button className={'jv-toggle' + (listening ? ' listen-on' : '')}
              onClick={toggleListening}
              title={listening ? 'Đang nghe liên tục — bấm để tắt' : 'Bật chế độ luôn lắng nghe (rảnh tay)'}>
              <Icon name="phone" size={14} /> {listening ? 'LUÔN NGHE: BẬT' : 'LUÔN NGHE: TẮT'}
            </button>
            <button className={'jv-toggle' + (voiceOn ? ' on' : '')}
              onClick={() => {
                const v = !voiceOn; setVoiceOn(v);
                if (!v) { window.speechSynthesis?.cancel(); try { audioRef.current?.pause(); } catch {} }
              }}
              title={voiceOn ? 'Đang đọc phản hồi (tắt loa)' : 'Bật đọc phản hồi bằng giọng nói (miễn phí, cần giọng vi — Edge tốt nhất)'}>
              <Icon name="bell" size={14} /> {voiceOn ? 'LOA: BẬT' : 'LOA: TẮT'}
            </button>
            <button className="jv-toggle" onClick={() => { setMessages([]); setLastTool(null); setOrbState('idle'); window.speechSynthesis?.cancel(); }}
              title="Xóa hội thoại">
              <Icon name="refresh" size={14} /> MỚI
            </button>
          </div>
        </div>

        {/* Sân khấu orb */}
        <div className="jv-stage">
          <JarvisOrb state={orbState} />
          <div className={'jv-status ' + STATUS.cls}>
            <span className="jv-status-dot" />
            <span className="jv-status-label">{STATUS.label}</span>
          </div>
        </div>

        {/* Nhật ký hội thoại */}
        <div className="jv-log" ref={logRef}>
          {messages.length === 0 && (
            <div className="jv-empty">
              <p>Xin chào. Tôi là <b>JARVIS</b> — trợ lý số liệu TourKit của bạn.</p>
              <p className="jv-empty-hint">Nhấn 🎤 để nói, hoặc chọn gợi ý bên dưới.</p>
            </div>
          )}
          {messages.map((m, i) => (
            <div key={i} className={'jv-line ' + m.role + (m.error ? ' error' : '')}>
              <span className="jv-who">{m.role === 'user' ? 'BẠN' : 'JARVIS'}</span>
              <span className="jv-text">
                {m.content || (m.streaming ? <span className="jv-cursor">▊</span> : '')}
              </span>
            </div>
          ))}
        </div>

        {/* Gợi ý nhanh */}
        {messages.length === 0 && (
          <div className="jv-suggest">
            {suggestions.map(q => (
              <button key={q} className="jv-chip" onClick={() => send(q)} disabled={loading}>{q}</button>
            ))}
          </div>
        )}

        {/* Chữ đang nhận diện realtime (chế độ luôn nghe) */}
        {listening && (
          <div className="jv-listen-bar">
            <span className="jv-listen-wave"><i /><i /><i /><i /></span>
            <span className="jv-listen-text">{interim || (speaking ? 'Tạm dừng nghe khi đang đọc…' : (loading ? 'Đang xử lý…' : 'Mời nói…'))}</span>
          </div>
        )}

        {/* Nhập liệu */}
        <div className="jv-input-row">
          <button className={'jv-mic' + (rec === 'recording' ? ' rec' : '')} onClick={toggleMic}
            disabled={loading || rec === 'uploading'}
            title={rec === 'recording' ? 'Dừng + nhận diện' : 'Nói với JARVIS'}>
            {rec === 'uploading' ? <span className="jv-spin" /> : <Icon name="phone" size={18} />}
          </button>
          <input className="jv-input"
            placeholder={rec === 'recording' ? 'Đang nghe… nói rõ vào mic' : (rec === 'uploading' ? 'Đang nhận diện giọng nói…' : 'Hỏi JARVIS… (Enter để gửi)')}
            value={input}
            onChange={e => setInput(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') send(); }}
            disabled={loading || rec !== 'idle'} />
          <button className="jv-send" onClick={() => send()} disabled={loading || !input.trim()}>
            <Icon name="arrowRight" size={18} stroke={2.4} />
          </button>
        </div>
      </div>
    </main>
  );
}

// CSS inject 1 lần (self-contained — không đụng styles.css). Namespace .jv-*.
function JarvisStyle() {
  _jE(() => {
    if (document.getElementById('jarvis-css')) return;
    const el = document.createElement('style');
    el.id = 'jarvis-css';
    el.textContent = JV_CSS;
    document.head.appendChild(el);
  }, []);
  return null;
}

const JV_CSS = `
.jv-wrap{padding:0!important;background:#05070d;}
.jv-hud{position:relative;display:flex;flex-direction:column;min-height:calc(100vh - 64px);
  color:#cfe8ff;font-family:'JetBrains Mono','Be Vietnam Pro',ui-monospace,monospace;
  background:radial-gradient(120% 80% at 50% 0%,#0a1424 0%,#05070d 60%);overflow:hidden;}
.jv-hud::before{content:'';position:absolute;inset:0;pointer-events:none;
  background:repeating-linear-gradient(0deg,rgba(56,189,248,.03) 0 1px,transparent 1px 3px);opacity:.5;}
.jv-topbar{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;
  padding:14px 20px;border-bottom:1px solid rgba(56,189,248,.15);position:relative;z-index:2;}
.jv-brand{display:flex;align-items:center;gap:12px;}
.jv-brand-mark{font-size:26px;color:#38bdf8;text-shadow:0 0 16px #38bdf8;animation:jvPulse 3s ease-in-out infinite;}
.jv-brand-name{font-weight:700;letter-spacing:2px;color:#eaf6ff;font-size:15px;}
.jv-brand-sub{font-size:10px;letter-spacing:2px;color:#4d7ea8;margin-top:2px;}
.jv-controls{display:flex;align-items:center;gap:10px;flex-wrap:wrap;}
.jv-readout{font-size:10px;letter-spacing:1px;color:#5a86ad;}
.jv-readout b{color:#9fd4ff;font-weight:600;margin-left:4px;}
.jv-toggle{display:inline-flex;align-items:center;gap:5px;font-family:inherit;font-size:10px;letter-spacing:1px;
  padding:6px 10px;border-radius:6px;border:1px solid rgba(56,189,248,.25);background:rgba(56,189,248,.05);
  color:#8fc4ec;cursor:pointer;transition:.15s;}
.jv-toggle:hover{border-color:rgba(56,189,248,.6);color:#eaf6ff;}
.jv-toggle.on{background:rgba(255,122,26,.12);border-color:rgba(255,122,26,.5);color:#ffb27a;}
.jv-toggle.listen-on{background:rgba(34,211,238,.14);border-color:rgba(34,211,238,.6);color:#67e8f9;
  box-shadow:0 0 14px rgba(34,211,238,.25);animation:jvPulse 1.8s ease-in-out infinite;}
.jv-stage{position:relative;flex:1 1 auto;min-height:240px;display:flex;align-items:center;justify-content:center;z-index:1;}
.jv-orb{position:absolute;inset:0;}
.jv-orb-err{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;
  gap:8px;color:#f59e0b;font-size:12px;}
.jv-status{position:absolute;bottom:14px;left:50%;transform:translateX(-50%);display:flex;align-items:center;gap:8px;
  font-size:11px;letter-spacing:3px;font-weight:600;z-index:2;}
.jv-status-dot{width:8px;height:8px;border-radius:50%;background:#38bdf8;box-shadow:0 0 10px #38bdf8;}
.jv-status.idle{color:#5aa9d8;} .jv-status.idle .jv-status-dot{background:#38bdf8;box-shadow:0 0 10px #38bdf8;}
.jv-status.thinking{color:#f5b342;} .jv-status.thinking .jv-status-dot{background:#f59e0b;box-shadow:0 0 12px #f59e0b;animation:jvBlink .6s steps(2) infinite;}
.jv-status.responding{color:#ff9a52;} .jv-status.responding .jv-status-dot{background:#ff7a1a;box-shadow:0 0 14px #ff7a1a;animation:jvBlink .9s steps(2) infinite;}
.jv-status.listening{color:#67e8f9;} .jv-status.listening .jv-status-dot{background:#22d3ee;box-shadow:0 0 14px #22d3ee;animation:jvBlink .5s steps(2) infinite;}
.jv-listen-bar{position:relative;z-index:2;display:flex;align-items:center;gap:12px;margin:0 20px 8px;padding:9px 14px;
  border-radius:10px;background:rgba(34,211,238,.07);border:1px solid rgba(34,211,238,.25);}
.jv-listen-text{font-size:13px;color:#a5f0fb;font-style:italic;flex:1;min-height:18px;}
.jv-listen-wave{display:inline-flex;align-items:center;gap:3px;height:18px;}
.jv-listen-wave i{width:3px;height:6px;border-radius:2px;background:#22d3ee;animation:jvWave 1s ease-in-out infinite;}
.jv-listen-wave i:nth-child(2){animation-delay:.15s;} .jv-listen-wave i:nth-child(3){animation-delay:.3s;} .jv-listen-wave i:nth-child(4){animation-delay:.45s;}
@keyframes jvWave{0%,100%{height:5px;opacity:.5;}50%{height:16px;opacity:1;}}
.jv-log{position:relative;z-index:2;max-height:34vh;overflow-y:auto;padding:12px 20px;display:flex;flex-direction:column;gap:9px;
  border-top:1px solid rgba(56,189,248,.12);}
.jv-empty{color:#6b93b5;font-size:13px;text-align:center;padding:14px;line-height:1.7;}
.jv-empty b{color:#9fd4ff;} .jv-empty-hint{font-size:11px;color:#4d7ea8;margin-top:4px;}
.jv-line{display:flex;gap:10px;align-items:flex-start;font-size:13px;line-height:1.6;animation:jvIn .25s ease;}
.jv-line .jv-who{flex:0 0 52px;font-size:9px;letter-spacing:1px;font-weight:700;padding-top:3px;}
.jv-line.user .jv-who{color:#ff9a52;} .jv-line.assistant .jv-who{color:#38bdf8;}
.jv-line .jv-text{flex:1;color:#d6ebff;white-space:pre-wrap;word-break:break-word;}
.jv-line.user .jv-text{color:#f4e3d5;} .jv-line.error .jv-text{color:#fca5a5;}
.jv-cursor{color:#38bdf8;animation:jvBlink .8s steps(2) infinite;}
.jv-suggest{position:relative;z-index:2;display:flex;flex-wrap:wrap;gap:8px;justify-content:center;padding:0 20px 8px;}
.jv-chip{font-family:inherit;font-size:11px;padding:7px 12px;border-radius:20px;cursor:pointer;transition:.15s;
  border:1px solid rgba(56,189,248,.28);background:rgba(56,189,248,.06);color:#8fc4ec;}
.jv-chip:hover:not(:disabled){border-color:#38bdf8;color:#eaf6ff;box-shadow:0 0 14px rgba(56,189,248,.25);}
.jv-chip:disabled{opacity:.4;cursor:default;}
.jv-input-row{position:relative;z-index:2;display:flex;gap:10px;align-items:center;padding:14px 20px 18px;
  border-top:1px solid rgba(56,189,248,.15);}
.jv-mic,.jv-send{width:44px;height:44px;flex:0 0 44px;border-radius:50%;display:flex;align-items:center;justify-content:center;
  cursor:pointer;transition:.15s;border:1px solid rgba(56,189,248,.35);background:rgba(56,189,248,.08);color:#9fd4ff;}
.jv-mic:hover:not(:disabled),.jv-send:hover:not(:disabled){border-color:#38bdf8;color:#eaf6ff;box-shadow:0 0 16px rgba(56,189,248,.3);}
.jv-mic.rec{border-color:#ff4e42;background:rgba(255,78,66,.15);color:#ff8a80;animation:jvRec 1s ease-in-out infinite;}
.jv-send{border-color:rgba(255,122,26,.4);background:rgba(255,122,26,.1);color:#ffb27a;}
.jv-send:hover:not(:disabled){border-color:#ff7a1a;box-shadow:0 0 16px rgba(255,122,26,.35);}
.jv-mic:disabled,.jv-send:disabled{opacity:.4;cursor:default;}
.jv-input{flex:1;height:44px;padding:0 16px;border-radius:22px;font-family:inherit;font-size:13px;
  background:rgba(10,20,36,.8);border:1px solid rgba(56,189,248,.25);color:#eaf6ff;outline:none;transition:.15s;}
.jv-input:focus{border-color:#38bdf8;box-shadow:0 0 0 3px rgba(56,189,248,.12);}
.jv-input::placeholder{color:#4d7ea8;}
.jv-spin{width:16px;height:16px;border-radius:50%;border:2px solid rgba(159,212,255,.3);border-top-color:#9fd4ff;animation:jvSpin .7s linear infinite;}
@keyframes jvPulse{0%,100%{opacity:1;}50%{opacity:.55;}}
@keyframes jvBlink{50%{opacity:.25;}}
@keyframes jvRec{0%,100%{box-shadow:0 0 0 0 rgba(255,78,66,.4);}50%{box-shadow:0 0 0 8px rgba(255,78,66,0);}}
@keyframes jvSpin{to{transform:rotate(360deg);}}
@keyframes jvIn{from{opacity:0;transform:translateY(4px);}to{opacity:1;transform:none;}}
@media(max-width:900px){.jv-hud{min-height:calc(100vh - 120px);}.jv-log{max-height:28vh;}.jv-readout{display:none;}}
`;

window.JarvisPage = JarvisPage;
