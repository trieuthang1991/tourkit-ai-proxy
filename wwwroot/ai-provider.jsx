// AI Provider Abstraction — routes window.tourkit.ai.complete() to selected backend
// Supports: OpenCode Go (DeepSeek V4 Pro / MiniMax / Kimi...) via same-origin proxy by default,
// or built-in window.claude.complete khi chạy trong Claude.ai/Artifacts.

(function() {
  const STORAGE_KEY = 'tourkit_ai_config';
  // Bump khi đổi DEFAULT_CONFIG để ép client cũ migrate (bỏ qua localStorage cũ).
  const CONFIG_VERSION = 3;

  // window.claude chỉ tồn tại trong Claude.ai/Artifacts; còn lại đi qua proxy same-origin.
  const HAS_BUILTIN_CLAUDE = typeof window !== 'undefined' && !!window.claude?.complete;

  const DEFAULT_CONFIG = {
    provider: HAS_BUILTIN_CLAUDE ? 'claude-builtin' : 'opencode-go',
    apiKey: '',
    model: 'deepseek-v4-flash',
    proxyUrl: HAS_BUILTIN_CLAUDE ? '' : '/api/ai',
    _v: CONFIG_VERSION
  };

  function loadConfig() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return {...DEFAULT_CONFIG};
      const parsed = JSON.parse(raw);
      // Migration: config cũ trỏ tới claude-builtin nhưng môi trường không có window.claude
      // → sẽ luôn lỗi "window.claude.complete không khả dụng". Reset về default mới.
      if (parsed._v !== CONFIG_VERSION && !HAS_BUILTIN_CLAUDE && parsed.provider === 'claude-builtin') {
        const migrated = {...DEFAULT_CONFIG};
        localStorage.setItem(STORAGE_KEY, JSON.stringify(migrated));
        return migrated;
      }
      // Khi bump CONFIG_VERSION mà chỉ đổi default model: reset model về default mới,
      // giữ nguyên các tuỳ chọn khác (provider, apiKey...).
      if (parsed._v !== CONFIG_VERSION) {
        const migrated = {...DEFAULT_CONFIG, ...parsed, model: DEFAULT_CONFIG.model, _v: CONFIG_VERSION};
        localStorage.setItem(STORAGE_KEY, JSON.stringify(migrated));
        return migrated;
      }
      return {...DEFAULT_CONFIG, ...parsed, _v: CONFIG_VERSION};
    } catch { return {...DEFAULT_CONFIG}; }
  }

  function saveConfig(cfg) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(cfg));
  }

  // OpenCode Go endpoint info
  const OPENCODE_GO_ENDPOINTS = {
    'deepseek-v4-pro':   { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'deepseek-v4-flash': { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'glm-5.1':           { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'glm-5':             { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'kimi-k2.6':         { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'kimi-k2.5':         { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'minimax-m2.7':      { url: 'https://opencode.ai/zen/go/v1/messages',         format: 'anthropic' },
    'minimax-m2.5':      { url: 'https://opencode.ai/zen/go/v1/messages',         format: 'anthropic' },
    'qwen3.6-plus':      { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'qwen3.5-plus':      { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'mimo-v2.5':         { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' },
    'mimo-v2.5-pro':     { url: 'https://opencode.ai/zen/go/v1/chat/completions', format: 'openai' }
  };

  async function callOpenCodeGo(prompt, cfg) {
    const ep = OPENCODE_GO_ENDPOINTS[cfg.model];
    if (!ep) throw new Error(`Model không hỗ trợ: ${cfg.model}`);
    // Key chỉ bắt buộc khi gọi trực tiếp; nếu có proxy thì server-side đã giữ key
    if (!cfg.proxyUrl && !cfg.apiKey) throw new Error('Chưa cấu hình API key OpenCode Go (hoặc nhập Backend Proxy URL)');

    // Proxy URL có thể là:
    //   http://localhost:5080            → ghép /api/ai/complete
    //   http://localhost:5080/api/ai     → ghép /complete
    //   http://localhost:5080/api/ai/complete → giữ nguyên
    let endpoint;
    if (cfg.proxyUrl) {
      const base = cfg.proxyUrl.replace(/\/$/, '');
      if (base.endsWith('/complete')) endpoint = base;
      else if (base.endsWith('/api/ai')) endpoint = base + '/complete';
      else endpoint = base + '/api/ai/complete';
    } else {
      endpoint = ep.url;
    }

    let body;
    if (cfg.proxyUrl) {
      // Proxy nhận shape { prompt, model, maxTokens } — không phải messages[]
      body = {
        prompt,
        model: cfg.model,
        maxTokens: 4096,
        temperature: 0.7
      };
    } else if (ep.format === 'openai') {
      body = {
        model: cfg.model,
        messages: [{ role: 'user', content: prompt }],
        max_tokens: 4096,
        temperature: 0.7
      };
    } else {
      body = {
        model: cfg.model,
        max_tokens: 4096,
        messages: [{ role: 'user', content: prompt }]
      };
    }

    const headers = { 'Content-Type': 'application/json' };
    if (!cfg.proxyUrl) {
      headers['Authorization'] = `Bearer ${cfg.apiKey}`;
      if (ep.format === 'anthropic') headers['anthropic-version'] = '2023-06-01';
    }

    const resp = await fetch(endpoint, {
      method: 'POST',
      headers,
      body: JSON.stringify(body)
    });

    if (!resp.ok) {
      const errText = await resp.text().catch(() => '');
      throw new Error(`HTTP ${resp.status}: ${errText.slice(0, 200)}`);
    }

    const data = await resp.json();
    // Proxy trả thẳng { text, ... }
    if (cfg.proxyUrl) return data.text || '';
    // Direct call
    if (ep.format === 'openai') return data.choices?.[0]?.message?.content || '';
    return data.content?.[0]?.text || data.completion || '';
  }

  async function callClaudeBuiltin(prompt) {
    if (!window.claude || !window.claude.complete) {
      throw new Error('window.claude.complete không khả dụng');
    }
    return window.claude.complete(prompt);
  }

  // Main entry point
  window.tourkit = window.tourkit || {};
  window.tourkit.ai = {
    getConfig: loadConfig,
    setConfig(patch) {
      const cfg = {...loadConfig(), ...patch};
      saveConfig(cfg);
      window.dispatchEvent(new CustomEvent('tourkit-ai-config-changed', { detail: cfg }));
      return cfg;
    },

    async complete(prompt, options = {}) {
      const cfg = loadConfig();
      const t0 = Date.now();
      try {
        let result;
        if (cfg.provider === 'opencode-go') {
          result = await callOpenCodeGo(prompt, cfg);
        } else {
          result = await callClaudeBuiltin(prompt);
        }
        const ms = Date.now() - t0;
        console.log(`[tourkit.ai] ${cfg.provider}/${cfg.model || 'default'} OK ${ms}ms, ${result.length} chars`);
        return result;
      } catch (e) {
        console.error(`[tourkit.ai] ${cfg.provider} FAIL:`, e.message);
        throw e;
      }
    },

    // Streaming variant — cần proxy (POST /api/ai/stream). Fallback sang complete() khi không có proxy.
    // onChunk(deltaText, fullText) gọi cho mỗi delta. Trả về full text khi xong.
    async completeStream(prompt, onChunk, options = {}) {
      const cfg = loadConfig();
      const t0 = Date.now();

      // Không có proxy → fallback sang non-stream complete()
      if (cfg.provider !== 'opencode-go' || !cfg.proxyUrl) {
        const text = await this.complete(prompt, options);
        onChunk && onChunk(text, text);
        return text;
      }

      const resp = await fetch(cfg.proxyUrl + '/stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
        body: JSON.stringify({
          prompt, model: cfg.model,
          maxTokens: options.maxTokens, temperature: options.temperature, system: options.system
        })
      });
      if (!resp.ok || !resp.body) {
        const t = await resp.text().catch(() => '');
        throw new Error(`Stream HTTP ${resp.status}: ${t.slice(0, 200)}`);
      }

      const reader = resp.body.getReader();
      const decoder = new TextDecoder('utf-8');
      let buf = '';
      let full = '';
      let finalStats = null;

      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });

        // SSE: events tách nhau bằng "\n\n"
        let sepIdx;
        while ((sepIdx = buf.indexOf('\n\n')) >= 0) {
          const ev = buf.slice(0, sepIdx);
          buf = buf.slice(sepIdx + 2);
          // mỗi event có thể nhiều dòng "data:", ghép vào
          const dataLines = ev.split(/\r?\n/).filter(l => l.startsWith('data:'));
          if (dataLines.length === 0) continue;
          const payload = dataLines.map(l => l.slice(5).replace(/^ /, '')).join('\n');
          let obj;
          try { obj = JSON.parse(payload); } catch { continue; }

          if (obj.error) throw new Error(obj.error + (obj.body ? ': ' + String(obj.body).slice(0, 200) : ''));
          if (obj.delta) {
            full += obj.delta;
            onChunk && onChunk(obj.delta, full);
          }
          if (obj.done) {
            // server có gửi `text` cuối — ưu tiên dùng (chính xác hơn nếu reasoning_content xuất hiện)
            if (typeof obj.text === 'string' && obj.text.length > full.length) {
              full = obj.text;
            }
            finalStats = obj;
          }
        }
      }

      const ms = Date.now() - t0;
      console.log(`[tourkit.ai] stream ${cfg.model} OK ${ms}ms, ${full.length} chars`,
        finalStats ? `(in=${finalStats.inputTokens} out=${finalStats.outputTokens})` : '');
      return full;
    }
  };

  // Toàn app gọi window.claude.complete(prompt) trực tiếp. Override / shim để chuyển hướng
  // qua tourkit.ai (provider được chọn trong Settings).
  const originalClaudeComplete = window.claude?.complete;
  if (originalClaudeComplete) {
    window.claude.complete = (prompt) => window.tourkit.ai.complete(prompt);
    window.tourkit.ai._originalClaude = originalClaudeComplete;
  } else {
    // Không có window.claude (chạy ngoài Claude.ai/Artifacts) → tạo shim ngay để
    // các đoạn code window.claude.complete(...) trong app hoạt động bình thường.
    window.claude = window.claude || {};
    window.claude.complete = (prompt) => window.tourkit.ai.complete(prompt);
  }
})();

// ============== Settings Dialog Component ==============

const OPENCODE_MODELS = [
  { id: 'deepseek-v4-flash', label: 'DeepSeek V4 Flash',  desc: '⭐ Khuyến nghị · Nhanh, rẻ · 158K req/tháng' },
  { id: 'deepseek-v4-pro',   label: 'DeepSeek V4 Pro',    desc: 'Reasoning mạnh · JSON ổn · chậm hơn' },
  { id: 'minimax-m2.5',      label: 'MiniMax M2.5',       desc: 'Context 4M token · 31K req/tháng' },
  { id: 'minimax-m2.7',      label: 'MiniMax M2.7',       desc: 'Bản mới hơn · 17K req/tháng' },
  { id: 'kimi-k2.6',         label: 'Kimi K2.6',          desc: 'Reasoning tốt · 5.7K req/tháng' },
  { id: 'glm-5.1',           label: 'GLM-5.1',            desc: 'Cao cấp · 4.3K req/tháng' },
  { id: 'qwen3.6-plus',      label: 'Qwen 3.6 Plus',      desc: 'Cân bằng · 16K req/tháng' },
];

function AISettingsDialog({ open, onClose, onSaved }) {
  const [cfg, setCfg] = React.useState(() => window.tourkit.ai.getConfig());
  const [testing, setTesting] = React.useState(false);
  const [testResult, setTestResult] = React.useState(null);

  React.useEffect(() => {
    if (open) {
      setCfg(window.tourkit.ai.getConfig());
      setTestResult(null);
    }
  }, [open]);

  if (!open) return null;

  const handleSave = () => {
    window.tourkit.ai.setConfig(cfg);
    onSaved?.(cfg);
    onClose();
  };

  const handleTest = async () => {
    setTesting(true);
    setTestResult(null);
    // Apply current cfg temporarily
    const prevCfg = window.tourkit.ai.getConfig();
    window.tourkit.ai.setConfig(cfg);
    try {
      const t0 = Date.now();
      const result = await window.tourkit.ai.complete('Trả lời ngắn "OK" bằng tiếng Việt nếu nhận được tin nhắn này.');
      const ms = Date.now() - t0;
      setTestResult({ ok: true, ms, text: result.slice(0, 100) });
    } catch (e) {
      setTestResult({ ok: false, error: e.message });
    } finally {
      // Restore if user hasn't saved
      window.tourkit.ai.setConfig(prevCfg);
      setTesting(false);
    }
  };

  const isOpenCodeGo = cfg.provider === 'opencode-go';

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-card" onClick={e => e.stopPropagation()}
           style={{maxWidth: 640, width: '92vw'}}>
        <div className="modal-head">
          <div className="modal-head-icon" style={{background: 'linear-gradient(135deg, #6366f1, #8b5cf6)'}}>
            <Icon name="sparkle" size={18} stroke={2.5} color="white" />
          </div>
          <div>
            <div className="modal-title">Cấu hình AI Provider</div>
            <div className="modal-sub">Chọn nhà cung cấp AI cho Tourkit · localStorage</div>
          </div>
          <button className="modal-close" onClick={onClose} aria-label="Đóng">×</button>
        </div>

        <div style={{padding: '20px 24px', maxHeight: '70vh', overflowY: 'auto'}}>
          {/* Provider selector */}
          <div className="field">
            <label className="label">Nhà cung cấp</label>
            <div style={{display: 'grid', gap: 10}}>
              <label className={`provider-card ${cfg.provider === 'claude-builtin' ? 'active' : ''}`}>
                <input type="radio" checked={cfg.provider === 'claude-builtin'}
                  onChange={() => setCfg(c => ({...c, provider: 'claude-builtin'}))} />
                <div className="provider-info">
                  <div className="provider-name">Claude Haiku (built-in)</div>
                  <div className="provider-desc">Mặc định · Hoạt động ngay trong preview · Không cần cấu hình</div>
                </div>
                <span className="provider-badge default">Mặc định</span>
              </label>
              <label className={`provider-card ${cfg.provider === 'opencode-go' ? 'active' : ''}`}>
                <input type="radio" checked={cfg.provider === 'opencode-go'}
                  onChange={() => setCfg(c => ({...c, provider: 'opencode-go'}))} />
                <div className="provider-info">
                  <div className="provider-name">OpenCode Go</div>
                  <div className="provider-desc">$10/tháng · DeepSeek, MiniMax, Kimi, GLM, Qwen · Rẻ hơn 5-10×</div>
                </div>
                <span className="provider-badge premium">$10/mo</span>
              </label>
            </div>
          </div>

          {isOpenCodeGo && (
            <>
              {/* Model selector */}
              <div className="field">
                <label className="label">Model</label>
                <div style={{display: 'grid', gap: 6, gridTemplateColumns: '1fr 1fr'}}>
                  {OPENCODE_MODELS.map(m => (
                    <label key={m.id} className={`model-chip ${cfg.model === m.id ? 'active' : ''}`}>
                      <input type="radio" checked={cfg.model === m.id}
                        onChange={() => setCfg(c => ({...c, model: m.id}))}
                        style={{display: 'none'}} />
                      <div className="model-chip-name">{m.label}</div>
                      <div className="model-chip-desc">{m.desc}</div>
                    </label>
                  ))}
                </div>
              </div>

              {/* API Key */}
              <div className="field">
                <label className="label">API Key</label>
                <input className="input" type="password"
                  placeholder="sk-zen-xxxxxxxxxxxx"
                  value={cfg.apiKey}
                  onChange={e => setCfg(c => ({...c, apiKey: e.target.value}))}
                  autoComplete="off" />
                <div className="field-hint">
                  Lấy tại <a href="https://opencode.ai/auth" target="_blank" rel="noopener" style={{color: 'var(--accent)'}}>opencode.ai/auth</a> → Subscribe Go → Console → API Keys
                </div>
              </div>

              {/* Proxy URL */}
              <div className="field">
                <label className="label">Backend Proxy URL <span style={{color: 'var(--text-3)', fontWeight: 400}}>(khuyến nghị)</span></label>
                <input className="input"
                  placeholder="https://api.tourkit.vn/api  (để trống = gọi trực tiếp, sẽ lỗi CORS)"
                  value={cfg.proxyUrl}
                  onChange={e => setCfg(c => ({...c, proxyUrl: e.target.value}))} />
                <div className="field-hint">
                  Gọi trực tiếp từ browser tới OpenCode Go sẽ bị chặn bởi CORS. Production nên có backend ASP.NET proxy giấu API key.
                </div>
              </div>

              <div className="info-box warn">
                <Icon name="warning" size={16} />
                <div>
                  <strong>Lưu ý CORS</strong>: trong preview demo, gọi trực tiếp OpenCode Go từ browser thường sẽ fail.
                  Để test thật, build backend proxy ASP.NET Core (xem hướng dẫn trong chat) và paste URL proxy vào ô trên.
                </div>
              </div>
            </>
          )}

          {/* Test button */}
          <div style={{display: 'flex', gap: 10, marginTop: 16}}>
            <button className="btn btn-outline" onClick={handleTest} disabled={testing}>
              <Icon name="play" size={14} /> {testing ? 'Đang test...' : 'Test kết nối'}
            </button>
            {testResult && (
              <div className={`test-result ${testResult.ok ? 'ok' : 'fail'}`}>
                {testResult.ok
                  ? `✓ OK trong ${testResult.ms}ms · "${testResult.text}"`
                  : `✗ Lỗi: ${testResult.error}`}
              </div>
            )}
          </div>
        </div>

        <div className="modal-footer">
          <div style={{marginLeft: 'auto', display: 'flex', gap: 10}}>
            <button className="btn btn-outline" onClick={onClose}>Hủy bỏ</button>
            <button className="btn btn-primary" onClick={handleSave}>
              <Icon name="save" size={14} stroke={2} /> Lưu cấu hình
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

window.AISettingsDialog = AISettingsDialog;
