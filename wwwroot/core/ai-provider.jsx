// core/ai-provider.jsx — THIN client. Tất cả call qua backend /api/v1/completions.
// Frontend KHÔNG hold API key của bất kỳ provider nào — chỉ giữ preferences (provider id + model id).
// Thêm provider mới = thêm IAiProvider ở backend (Services/Providers/) → /api/v1/providers tự lộ ra.
//
// Public API:
//   window.tourkit.ai.getConfig() / setConfig(cfg)
//   window.tourkit.ai.complete(prompt, options?)              → string text
//   window.tourkit.ai.completeStream(prompt, onChunk, options?) → string text + onChunk(delta, full)
//   window.claude.complete(prompt)  — shim ép qua tourkit.ai
//
// options: { provider?, model?, maxTokens?, temperature?, system?, tag? }

(function () {
  'use strict';

  const STORAGE_KEY    = 'tourkit_ai_config';
  const CONFIG_VERSION = 8;   // bump khi đổi shape — buộc client migrate
  const API_BASE       = '/api/v1';

  // window.claude tồn tại trong Claude.ai/Artifacts; còn lại đi qua backend proxy.
  const HAS_BUILTIN_CLAUDE = typeof window !== 'undefined' && !!window.claude?.complete;

  const DEFAULT_CONFIG = {
    provider: HAS_BUILTIN_CLAUDE ? 'claude-builtin' : 'opencode-go',
    model: 'deepseek-v4-flash',
    _v: CONFIG_VERSION
  };

  function loadConfig() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return { ...DEFAULT_CONFIG };
      const parsed = JSON.parse(raw);
      // Migration: drop legacy fields (apiKey, proxyUrl, nineRoutes*) — không còn dùng client-side.
      if (parsed._v !== CONFIG_VERSION) {
        const migrated = {
          provider: parsed.provider || DEFAULT_CONFIG.provider,
          model:    parsed.model    || DEFAULT_CONFIG.model,
          _v: CONFIG_VERSION
        };
        localStorage.setItem(STORAGE_KEY, JSON.stringify(migrated));
        return migrated;
      }
      return { ...DEFAULT_CONFIG, ...parsed, _v: CONFIG_VERSION };
    } catch { return { ...DEFAULT_CONFIG }; }
  }

  function saveConfig(cfg) {
    // Config chỉ giữ provider/model. API key lưu RIÊNG (KEYS_STORAGE) theo yêu cầu: client-side.
    const clean = { provider: cfg.provider, model: cfg.model, _v: CONFIG_VERSION };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(clean));
  }

  // ─── API key client-side (localStorage máy người dùng) ───────────────────────
  // Theo yêu cầu: key của OpenAI/Anthropic lưu TRÊN MÁY người dùng, gửi kèm mỗi request,
  // KHÔNG lưu trên server. (Đánh đổi: key nằm trong localStorage → JS trang đọc được.)
  const KEYS_STORAGE = 'tourkit_ai_keys';
  function loadKeys() {
    try { return JSON.parse(localStorage.getItem(KEYS_STORAGE) || '{}') || {}; } catch { return {}; }
  }
  function getKey(providerId) { return loadKeys()[providerId] || ''; }
  function setKey(providerId, val) {
    const k = loadKeys();
    if (val && val.trim()) k[providerId] = val.trim(); else delete k[providerId];
    localStorage.setItem(KEYS_STORAGE, JSON.stringify(k));
  }

  // ─── Call backend ──────────────────────────────────────────────────────────
  async function callBackend(prompt, cfg, options) {
    const provider = options.provider || cfg.provider;
    const model    = options.model    || cfg.model;
    const body = {
      prompt,
      provider, model,
      maxTokens: options.maxTokens,
      temperature: options.temperature,
      system: options.system,
      apiKey: options.apiKey || getKey(provider) || undefined
    };
    const headers = { 'Content-Type': 'application/json' };
    // X-Workflow tag để backend gắn workflow name vào trace (vd 'WizardTour', 'WizardMarketing').
    // Trống → trace để tên rỗng (chỉ thấy step ai_complete).
    if (options.workflow) headers['X-Workflow'] = options.workflow;
    const resp = await fetch(`${API_BASE}/completions`, {
      method: 'POST',
      headers,
      body: JSON.stringify(body)
    });
    if (!resp.ok) {
      const errText = await resp.text().catch(() => '');
      throw new Error(`Backend ${resp.status}: ${errText.slice(0, 200)}`);
    }
    const json = await resp.json();
    if (json.error) throw new Error(json.error + (json.body ? ` (${String(json.body).slice(0, 120)})` : ''));
    return json;
  }

  async function callBackendStream(prompt, cfg, options, onChunk) {
    const provider = options.provider || cfg.provider;
    const model    = options.model    || cfg.model;
    const body = {
      prompt, provider, model,
      maxTokens: options.maxTokens,
      temperature: options.temperature,
      system: options.system,
      apiKey: options.apiKey || getKey(provider) || undefined
    };
    const headers = { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' };
    if (options.workflow) headers['X-Workflow'] = options.workflow;
    const resp = await fetch(`${API_BASE}/completions/stream`, {
      method: 'POST',
      headers,
      body: JSON.stringify(body)
    });
    if (!resp.ok) {
      const errText = await resp.text().catch(() => '');
      throw new Error(`Backend ${resp.status}: ${errText.slice(0, 200)}`);
    }
    if (!resp.body) throw new Error('Stream không khả dụng (no ReadableStream)');

    const reader = resp.body.getReader();
    const decoder = new TextDecoder('utf-8');
    let buf = '';
    let full = '';
    let finalMeta = null;
    let errMsg = null;
    let errDetail = null;
    let errStatus = null;

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += decoder.decode(value, { stream: true });
      let idx;
      while ((idx = buf.indexOf('\n\n')) >= 0) {
        const evt = buf.slice(0, idx);
        buf = buf.slice(idx + 2);
        const line = evt.split('\n').find(l => l.startsWith('data:'));
        if (!line) continue;
        const payload = line.slice(5).trimStart();
        if (!payload) continue;
        let obj;
        try { obj = JSON.parse(payload); } catch { continue; }
        if (obj.error) {
          errMsg    = obj.error;
          errDetail = obj.detail || obj.body || null;
          errStatus = obj.status || null;
          console.warn('[ai] stream error event:', obj);
          continue;
        }
        if (obj.done) { finalMeta = obj; continue; }
        if (obj.delta) {
          full += obj.delta;
          if (typeof onChunk === 'function') onChunk(obj.delta, full);
        }
      }
    }
    if (errMsg && !full) {
      const parts = [errMsg];
      if (errStatus) parts.push(`status=${errStatus}`);
      if (errDetail) parts.push(String(errDetail).slice(0, 300));
      throw new Error(parts.join(' · '));
    }
    if (finalMeta && finalMeta.text && finalMeta.text.length > full.length) {
      full = finalMeta.text;
    }
    return { text: full, meta: finalMeta };
  }

  // ─── Public API ────────────────────────────────────────────────────────────
  window.tourkit = window.tourkit || {};
  window.tourkit.ai = {
    getConfig: loadConfig,
    setConfig: saveConfig,

    async complete(prompt, options = {}) {
      const cfg = loadConfig();
      const tag = options.tag || 'gen';
      const t0 = Date.now();

      // Built-in Claude (chỉ trong Claude.ai/Artifacts) — passthrough, không qua backend.
      if ((options.provider || cfg.provider) === 'claude-builtin') {
        if (!window.tourkit.ai._originalClaude) {
          throw new Error('Provider claude-builtin chỉ chạy được trong Claude.ai/Artifacts (window.claude không có)');
        }
        const text = await window.tourkit.ai._originalClaude(prompt);
        console.log(`[ai] ✓ ${tag} · claude-builtin · ${Date.now() - t0}ms · ${text.length}ch`);
        return text;
      }

      const result = await callBackend(prompt, cfg, options);
      console.log(`[ai] ✓ ${tag} · ${result.provider}:${result.model} · ${result.latencyMs}ms · ${result.text.length}ch · in=${result.inputTokens} out=${result.outputTokens} finish=${result.finishReason}`);
      return result.text;
    },

    async completeStream(prompt, onChunk, options = {}) {
      const cfg = loadConfig();
      const tag = options.tag || 'gen';
      const t0 = Date.now();

      // Built-in fallback: no streaming, gọi complete rồi simulate single chunk.
      if ((options.provider || cfg.provider) === 'claude-builtin') {
        const text = await this.complete(prompt, options);
        if (typeof onChunk === 'function') onChunk(text, text);
        return text;
      }

      const { text, meta } = await callBackendStream(prompt, cfg, options, onChunk);
      const ms = Date.now() - t0;
      const m = meta || {};
      console.log(`[ai] ✓ ${tag} (stream) · ${m.provider || '?'}:${m.model || '?'} · ${ms}ms · ${text.length}ch · in=${m.inputTokens || '?'} out=${m.outputTokens || '?'} finish=${m.finishReason || '?'}`);
      return text;
    },

    async fetchProviders() {
      const resp = await fetch(`${API_BASE}/providers`);
      if (!resp.ok) throw new Error(`providers ${resp.status}`);
      return resp.json();
    },

    async fetchLiveModels(providerId) {
      const resp = await fetch(`${API_BASE}/providers/${encodeURIComponent(providerId)}/models`);
      const json = await resp.json().catch(() => ({ error: `parse ${resp.status}` }));
      if (!resp.ok) throw new Error(json.error || `models ${resp.status}` + (json.body ? ` (${String(json.body).slice(0, 120)})` : ''));
      return json;
    },

    // Lưu/đọc API key client-side (localStorage máy người dùng). KHÔNG gửi lên server để lưu.
    getKey,
    setKey,
    hasKey(providerId) { return !!getKey(providerId); }
  };

  // Shim: code cũ gọi window.claude.complete(...) đi qua tourkit.ai.
  const originalClaudeComplete = window.claude?.complete;
  if (originalClaudeComplete) {
    window.tourkit.ai._originalClaude = originalClaudeComplete;
    window.claude.complete = (prompt) => window.tourkit.ai.complete(prompt);
  } else {
    window.claude = window.claude || {};
    window.claude.complete = (prompt) => window.tourkit.ai.complete(prompt);
  }
})();

// ─── AISettingsDialog: provider + model selector ────────────────────────────
// Pull list từ backend /api/v1/providers — không hardcode bảng nào ở client.
function AISettingsDialog({ open, onClose, onSaved }) {
  const [cfg, setCfg] = React.useState(() => window.tourkit.ai.getConfig());
  const [providers, setProviders] = React.useState(null);   // null = đang load
  const [loadErr, setLoadErr] = React.useState(null);
  const [testing, setTesting] = React.useState(false);
  const [testResult, setTestResult] = React.useState(null);
  // Live models cache per provider (id → {models, loading, error}).
  // Khi user bấm "Tải model đang chạy", fetch /api/v1/providers/{id}/models qua backend.
  const [liveModels, setLiveModels] = React.useState({});
  const [modelFilter, setModelFilter] = React.useState('');
  const [keyInput, setKeyInput] = React.useState('');
  const [keyMsg, setKeyMsg] = React.useState(null);   // {ok, text}
  const [keyTick, setKeyTick] = React.useState(0);    // bump để re-render khi đổi key local

  React.useEffect(() => {
    if (!open) return;
    setCfg(window.tourkit.ai.getConfig());
    setTestResult(null);
    setLoadErr(null);
    setProviders(null);
    setLiveModels({});
    setModelFilter('');
    setKeyInput(''); setKeyMsg(null);
    window.tourkit.ai.fetchProviders()
      .then(list => {
        setProviders(list);
        // AUTO-HEAL: model đã save có thể bị retire (vd "claude-3-5-haiku-latest"
        // sau khi tôi update Models list). Nếu model không nằm trong list của
        // provider hiện tại → tự đổi sang Recommended (giữ provider nguyên).
        const saved = window.tourkit.ai.getConfig();
        const p = list.find(x => x.id === saved.provider);
        if (p && p.models?.length) {
          const has = p.models.some(m => m.id === saved.model);
          if (!has) {
            const reco = p.models.find(m => m.recommended) || p.models[0];
            const healed = { provider: saved.provider, model: reco.id };
            window.tourkit.ai.setConfig(healed);
            setCfg(healed);
            setLoadErr(`Model "${saved.model}" đã ngưng phục vụ, đã tự đổi sang "${reco.id}".`);
          }
        }
      })
      .catch(e => setLoadErr(e.message));
  }, [open]);

  const loadLiveModels = async (providerId) => {
    setLiveModels(s => ({ ...s, [providerId]: { loading: true, error: null, models: null } }));
    try {
      const models = await window.tourkit.ai.fetchLiveModels(providerId);
      setLiveModels(s => ({ ...s, [providerId]: { loading: false, error: null, models } }));
    } catch (e) {
      setLiveModels(s => ({ ...s, [providerId]: { loading: false, error: e.message, models: null } }));
    }
  };

  if (!open) return null;

  const handleSave = () => {
    window.tourkit.ai.setConfig(cfg);
    onSaved?.(cfg);
    onClose();
  };

  const handleTest = async () => {
    setTesting(true); setTestResult(null);
    const prev = window.tourkit.ai.getConfig();
    window.tourkit.ai.setConfig(cfg);
    try {
      const t0 = Date.now();
      const text = await window.tourkit.ai.complete('Trả lời ngắn "OK" bằng tiếng Việt nếu nhận được tin nhắn này.', { tag: 'test' });
      setTestResult({ ok: true, ms: Date.now() - t0, text: text.slice(0, 100) });
    } catch (e) {
      setTestResult({ ok: false, error: e.message });
    } finally {
      window.tourkit.ai.setConfig(prev);
      setTesting(false);
    }
  };

  const saveKey = (providerId, valArg) => {
    const val = (valArg !== undefined ? valArg : keyInput).trim();
    window.tourkit.ai.setKey(providerId, val);   // lưu localStorage máy người dùng
    setKeyInput('');
    setKeyTick(t => t + 1);
    setKeyMsg({ ok: true, text: val ? 'Đã lưu key trên máy bạn' : 'Đã xóa key' });
  };

  const currentProvider = providers?.find(p => p.id === cfg.provider);
  const builtInCard = (window.tourkit.ai._originalClaude) && {
    id: 'claude-builtin',
    label: 'Claude (built-in)',
    models: []
  };
  const allProviders = providers
    ? (builtInCard ? [builtInCard, ...providers] : providers)
    : [];

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-card" onClick={e => e.stopPropagation()} style={{maxWidth: 560, width: '92vw'}}>
        <div className="modal-head">
          <div className="modal-head-icon" style={{background: 'linear-gradient(135deg, #6366f1, #8b5cf6)'}}>
            <Icon name="sparkle" size={18} stroke={2.5} color="white" />
          </div>
          <div style={{flex: 1, minWidth: 0}}>
            <div className="modal-title">Cấu hình AI Provider</div>
            <div className="modal-sub">API keys do backend quản lý · client chỉ pick provider/model</div>
          </div>
          <button className="modal-close" onClick={onClose} aria-label="Đóng">×</button>
        </div>

        <div style={{padding: '20px 24px', flex: 1, minHeight: 0, overflowY: 'auto'}}>
          {loadErr && (
            <div style={{padding: 12, background: '#fef2f2', color: '#991b1b', borderRadius: 8, marginBottom: 12, fontSize: 13}}>
              Không tải được danh sách provider: {loadErr}
            </div>
          )}
          {!providers && !loadErr && (
            <div style={{padding: 12, color: 'var(--text-3)', fontSize: 13}}>Đang tải provider...</div>
          )}

          {/* Provider cards */}
          {providers && (
            <div className="field">
              <label className="label">Nhà cung cấp</label>
              <div style={{display: 'grid', gap: 10}}>
                {allProviders.map(p => {
                  const hk = p.needsKey && (!!window.tourkit.ai.getKey(p.id) || p.hasKey);
                  return (
                  <label key={p.id} className={`provider-card ${cfg.provider === p.id ? 'active' : ''}`}>
                    <input type="radio" checked={cfg.provider === p.id}
                      onChange={() => { setKeyInput(''); setKeyMsg(null); setCfg(c => ({...c, provider: p.id, model: p.models?.[0]?.id || c.model})); }} />
                    <div className="provider-info">
                      <div className="provider-name">
                        {p.label}
                        {p.needsKey && (
                          <span style={{marginLeft: 8, fontSize: 11, fontWeight: 600,
                            color: hk ? 'var(--success)' : 'var(--warning)'}}>
                            {hk ? '● có key' : '○ cần key'}
                          </span>
                        )}
                      </div>
                      <div className="provider-desc">
                        {p.id === 'claude-builtin' ? 'Trong Claude.ai/Artifacts · không cần backend'
                          : p.needsKey ? (hk ? 'Dùng API key của bạn (lưu trên máy)' : 'Nhập API key của bạn để dùng')
                          : `${p.models.length} model${p.models.length > 1 ? 's' : ''} · server-side key`}
                      </div>
                    </div>
                  </label>
                  );
                })}
              </div>
            </div>
          )}

          {/* API key cho provider BYO-key (OpenAI/Anthropic) — nhập ở UI, lưu server-side */}
          {currentProvider && currentProvider.needsKey && (() => {
            const _t = keyTick;   // ref để re-render khi đổi key
            const curKey = !!window.tourkit.ai.getKey(currentProvider.id) || currentProvider.hasKey;
            return (
              <div className="field">
                <label className="label">API key cho {currentProvider.label}</label>
                <div style={{display: 'flex', gap: 8}}>
                  <input className="input" type="password" autoComplete="off"
                    placeholder={curKey ? '•••• đã lưu — nhập để đổi' : (currentProvider.id === 'openai' ? 'sk-...' : 'sk-ant-...')}
                    value={keyInput} onChange={e => setKeyInput(e.target.value)} style={{flex: 1}} />
                  <button className="btn btn-primary btn-sm" disabled={!keyInput.trim()} onClick={() => saveKey(currentProvider.id)}>
                    Lưu key
                  </button>
                </div>
                {keyMsg ? (
                  <div style={{marginTop: 6, fontSize: 12, color: keyMsg.ok ? 'var(--success)' : 'var(--danger)'}}>
                    {keyMsg.ok ? '✓ ' : '✗ '}{keyMsg.text}
                  </div>
                ) : (
                  <div style={{marginTop: 6, fontSize: 12, color: curKey ? 'var(--success)' : 'var(--text-3)'}}>
                    {curKey
                      ? '✓ Đã có key — lưu trên máy bạn (localStorage), gửi kèm mỗi request.'
                      : 'Key sẽ lưu trên máy bạn (localStorage), không lưu trên server.'}
                  </div>
                )}
                {curKey && (
                  <button className="btn btn-ghost btn-sm" style={{marginTop: 6}}
                    onClick={() => saveKey(currentProvider.id, '')}>
                    Xóa key
                  </button>
                )}
              </div>
            );
          })()}

          {/* Model selector — show static list, kèm nút "Tải model đang chạy" để
              gọi backend GET /providers/{id}/models (cho provider có model động vd 9routes) */}
          {currentProvider && (() => {
            const live = liveModels[currentProvider.id];
            const models = (live && live.models) || currentProvider.models;
            const filtered = modelFilter
              ? models.filter(m => (m.id + ' ' + m.label).toLowerCase().includes(modelFilter.toLowerCase()))
              : models;
            return (
              <div className="field">
                <div style={{display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 8}}>
                  <label className="label" style={{margin: 0}}>
                    Model {live && live.models ? `(${models.length} live)` : models.length > 0 ? `(${models.length})` : ''}
                  </label>
                  <button className="btn btn-ghost btn-sm" onClick={() => loadLiveModels(currentProvider.id)} disabled={live?.loading}>
                    {live?.loading ? 'Đang tải...' : (live?.models ? '↻ Refresh' : 'Tải model đang chạy')}
                  </button>
                </div>

                {live?.error && (
                  <div style={{padding: 8, marginBottom: 8, background: '#fef2f2', color: '#991b1b', borderRadius: 6, fontSize: 12}}>
                    ✗ {live.error}
                  </div>
                )}

                {models.length > 10 && (
                  <input className="input" placeholder="Lọc model..."
                    value={modelFilter}
                    onChange={e => setModelFilter(e.target.value)}
                    style={{marginBottom: 8, fontSize: 13}} />
                )}

                {models.length === 0 ? (
                  <div style={{padding: 12, color: 'var(--text-3)', fontSize: 12, textAlign: 'center', background: '#fafafa', borderRadius: 8}}>
                    Provider này chưa khai báo model static. Bấm "Tải model đang chạy" để fetch list từ upstream.
                  </div>
                ) : (
                  <div style={{
                    display: 'grid', gap: 6,
                    gridTemplateColumns: filtered.length > 4 ? '1fr 1fr' : '1fr',
                    maxHeight: filtered.length > 8 ? 280 : 'none',
                    overflowY: filtered.length > 8 ? 'auto' : 'visible'
                  }}>
                    {filtered.map(m => (
                      <label key={m.id} className={`model-chip ${cfg.model === m.id ? 'active' : ''}`}>
                        <input type="radio" checked={cfg.model === m.id}
                          onChange={() => setCfg(c => ({...c, model: m.id}))}
                          style={{display: 'none'}} />
                        <div className="model-chip-name">{m.label}{m.recommended && ' ⭐'}</div>
                        <div className="model-chip-desc">{m.id}</div>
                      </label>
                    ))}
                    {filtered.length === 0 && (
                      <div style={{padding: 10, color: 'var(--text-3)', fontSize: 12, gridColumn: '1 / -1'}}>
                        Không có model nào khớp "{modelFilter}"
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })()}

          {/* Test connection */}
          <div className="field">
            <button className="btn btn-ghost" onClick={handleTest} disabled={testing}>
              {testing ? 'Đang test...' : 'Test kết nối'}
            </button>
            {testResult && (
              <div style={{marginTop: 8, padding: 10, borderRadius: 8,
                background: testResult.ok ? '#ecfdf5' : '#fef2f2',
                color: testResult.ok ? '#065f46' : '#991b1b', fontSize: 13}}>
                {testResult.ok
                  ? <>✓ OK ({testResult.ms}ms): {testResult.text}</>
                  : <>✗ Lỗi: {testResult.error}</>}
              </div>
            )}
          </div>

          <div style={{padding: 12, background: '#f8fafc', borderRadius: 8, fontSize: 12, color: 'var(--text-3)'}}>
            <strong>Lưu key:</strong> API key ChatGPT/Claude bạn nhập ở đây được lưu <strong>trên máy bạn</strong> (localStorage),
            gửi kèm từng request và <strong>không lưu trên server</strong>. Lưu ý: key nằm trong localStorage nên JS của trang có thể đọc — chỉ dùng trên máy tin cậy.
          </div>
        </div>

        <div className="modal-footer">
          <button className="btn btn-ghost" onClick={onClose}>Hủy</button>
          <button className="btn btn-primary" onClick={handleSave}>Lưu</button>
        </div>
      </div>
    </div>
  );
}

window.AISettingsDialog = AISettingsDialog;
