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
  const LEGACY_KEYS    = 'tourkit_ai_keys';   // legacy: key client-side — đã bỏ
  const CONFIG_VERSION = 9;   // v9: drop client-side API keys (server-only via appsettings)
  const API_BASE       = '/api/v1';

  // Migration v8→v9: xóa localStorage key cũ (client không còn giữ API key).
  try { localStorage.removeItem(LEGACY_KEYS); } catch {}

  // window.claude tồn tại trong Claude.ai/Artifacts; còn lại đi qua backend proxy.
  const HAS_BUILTIN_CLAUDE = typeof window !== 'undefined' && !!window.claude?.complete;

  // Default: KHÔNG đặt provider/model — backend tự dùng Models:Primary từ appsettings.
  // (UI Cấu hình AI đã ẩn, key đã chuyển backend, FE không cần biết model nào).
  // claude-builtin override khi chạy trong Claude.ai/Artifacts.
  const DEFAULT_CONFIG = {
    provider: HAS_BUILTIN_CLAUDE ? 'claude-builtin' : null,
    model: null,
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

  // ─── Quota refresh ─────────────────────────────────────────────────────────
  // Mọi AI call (complete/completeStream) qua backend đều ăn quota → fire event
  // để chip ".tb-quota" ở topbar refresh ngay, đỡ phải chờ poll 10s.
  // Debounce ~1s: gộp nhiều call song song thành 1 fetch /api/v1/quota.
  let _aiQuotaTimer = null;
  function fireQuotaRefresh() {
    clearTimeout(_aiQuotaTimer);
    _aiQuotaTimer = setTimeout(() => {
      window.dispatchEvent(new CustomEvent('tourkit:quota'));
    }, 1000);
  }

  // ─── Call backend ──────────────────────────────────────────────────────────
  // API key cho mọi provider lấy SERVER-SIDE từ appsettings (Providers:{X}:ApiKey
  // / Models:Primary:ApiKey / Models:Review:ApiKey / env var). Frontend KHÔNG hold key.
  async function callBackend(prompt, cfg, options) {
    const provider = options.provider || cfg.provider;
    const model    = options.model    || cfg.model;
    const body = {
      prompt,
      provider, model,
      maxTokens: options.maxTokens,
      temperature: options.temperature,
      system: options.system
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
      system: options.system
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

    // Plumbing SSE dùng chung; capture error/meta/full ở đây (semantics riêng: chỉ throw nếu KHÔNG có partial text).
    let full = '';
    let finalMeta = null;
    let errMsg = null;
    let errDetail = null;
    let errStatus = null;

    await window.tourkitUtil.readSSE(resp, obj => {
      if (obj.error) {
        errMsg    = obj.error;
        errDetail = obj.detail || obj.body || null;
        errStatus = obj.status || null;
        console.warn('[ai] stream error event:', obj);
        return;
      }
      if (obj.done) { finalMeta = obj; return; }
      if (obj.delta) {
        full += obj.delta;
        if (typeof onChunk === 'function') onChunk(obj.delta, full);
      }
    });
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
      fireQuotaRefresh();
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
      fireQuotaRefresh();
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
    }
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

  React.useEffect(() => {
    if (!open) return;
    setCfg(window.tourkit.ai.getConfig());
    setTestResult(null);
    setLoadErr(null);
    setProviders(null);
    setLiveModels({});
    setModelFilter('');
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
                {allProviders.map(p => (
                  <label key={p.id} className={`provider-card ${cfg.provider === p.id ? 'active' : ''}`}>
                    <input type="radio" checked={cfg.provider === p.id}
                      onChange={() => setCfg(c => ({...c, provider: p.id, model: p.models?.[0]?.id || c.model}))} />
                    <div className="provider-info">
                      <div className="provider-name">
                        {p.label}
                        {p.needsKey && !p.hasKey && (
                          <span style={{marginLeft: 8, fontSize: 11, fontWeight: 600, color: 'var(--warning)'}}>
                            ○ chưa cấu hình key (server)
                          </span>
                        )}
                      </div>
                      <div className="provider-desc">
                        {p.id === 'claude-builtin' ? 'Trong Claude.ai/Artifacts · không cần backend'
                          : `${p.models.length} model${p.models.length > 1 ? 's' : ''} · key do server quản lý (appsettings)`}
                      </div>
                    </div>
                  </label>
                ))}
              </div>
            </div>
          )}

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
            <strong>API key:</strong> tất cả key (OpenCode/9routes/OpenAI/Anthropic) do <strong>server</strong> quản lý
            qua <code>appsettings.json</code> (sections <code>Providers:*</code> / <code>Models:Primary</code> / <code>Models:Review</code>)
            hoặc env var. Frontend không lưu key.
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
