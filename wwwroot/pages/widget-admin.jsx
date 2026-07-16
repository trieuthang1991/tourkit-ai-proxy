// pages/widget-admin.jsx — Quản lý token Widget Chat. List + tạo mới + sửa + xoá + copy embed snippet.
// Tenant nhập vào /widget-admin sẽ chỉ thấy token của tenant mình (backend filter theo X-Session-Id).

const { useState: _wUS, useEffect: _wUE } = React;

function WidgetAdminPage({ pushToast }) {
  const [items, setItems] = _wUS([]);
  const [defaults, setDefaults] = _wUS(null);
  const [loading, setLoading] = _wUS(true);
  const [editing, setEditing] = _wUS(null);   // null | {token, ...} hoặc 'new' cho form tạo mới
  const [demoUrl, setDemoUrl] = _wUS('');

  const load = async () => {
    setLoading(true);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/admin/widget/tokens');
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const data = await r.json();
      setItems(data.items || []);
      setDefaults(data.defaults || null);
    } catch (e) { pushToast('Lỗi tải danh sách: ' + e.message, 'error'); }
    finally { setLoading(false); }
  };
  _wUE(() => { load(); }, []);

  const onCreated = (item) => {
    setItems([item, ...items]);
    setEditing(null);
    pushToast('Đã tạo widget mới — copy snippet để embed', 'success');
  };
  const onUpdated = (item) => {
    setItems(items.map(x => x.token === item.token ? { ...x, ...item } : x));
    setEditing(null);
    pushToast('Đã lưu thay đổi', 'success');
  };
  const onDeleted = async (token) => {
    if (!await window.appConfirm('Xoá widget này? Token sẽ ngừng hoạt động ngay.', { danger: true, confirmLabel: 'Xoá' })) return;
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/admin/widget/tokens/' + token, { method: 'DELETE' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      setItems(items.filter(x => x.token !== token));
      pushToast('Đã xoá widget');
    } catch (e) { pushToast('Lỗi xoá: ' + e.message, 'error'); }
  };
  const copySnippet = (snippet) => {
    window.tourkitUtil.copyText(snippet);
    pushToast('Đã copy snippet — paste vào trước </body> của site khách');
  };
  const openDemo = (token) => {
    window.open(`/widget-demo.html?token=${encodeURIComponent(token)}`, '_blank');
  };

  // Stats KPI strip — compute từ items hiện tại
  const stats = {
    total: items.length,
    enabled: items.filter(x => x.enabled).length,
    crmLinked: items.filter(x => x.crmLinked).length,
    totalMessages: items.reduce((s, x) => s + (x.totalMessages || 0), 0),
  };

  return (
    <main className="page wga">
      <div className="wga-head">
        <div>
          <div className="wga-eyebrow">Tích hợp · Chat AI</div>
          <h1>Widget Chat</h1>
          <p className="wga-sub">Token nhúng JS vào website / landing page khách. 1 dòng script là chạy, không phụ thuộc framework.</p>
        </div>
        {editing == null && (
          <button className="wga-btn primary lg" onClick={() => setEditing('new')}>
            <Icon name="plus" size={14} /> Tạo widget mới
          </button>
        )}
      </div>

      {!loading && items.length > 0 && (
        <div className="wga-kpi-strip">
          <div className="wga-kpi">
            <div className="wga-kpi-l">Widget</div>
            <div className="wga-kpi-v">{stats.total}</div>
          </div>
          <div className="wga-kpi">
            <div className="wga-kpi-l">Đang hoạt động</div>
            <div className="wga-kpi-v">{stats.enabled}<span className="wga-kpi-s">/{stats.total}</span></div>
          </div>
          <div className="wga-kpi">
            <div className="wga-kpi-l">Kết nối CRM</div>
            <div className="wga-kpi-v">{stats.crmLinked}<span className="wga-kpi-s">/{stats.total}</span></div>
          </div>
          <div className="wga-kpi">
            <div className="wga-kpi-l">Tổng tin nhắn</div>
            <div className="wga-kpi-v">{stats.totalMessages.toLocaleString('vi-VN')}</div>
          </div>
        </div>
      )}

      {editing === 'new' && defaults && (
        <WidgetForm
          initial={{ botName: defaults.botName, greeting: defaults.greeting,
                     systemPrompt: defaults.systemPrompt, color: defaults.color,
                     allowedOrigins: [], allowedTools: defaults.allowedTools, crmLinked: false }}
          defaults={defaults}
          isNew
          onCancel={() => setEditing(null)}
          onSubmit={async (payload) => {
            const r = await window.tourkitAuth.authedFetch('/api/v1/admin/widget/tokens', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(payload),
            });
            if (!r.ok) {
              const er = await r.json().catch(() => ({ error: 'HTTP ' + r.status }));
              throw new Error(er.error || ('HTTP ' + r.status));
            }
            onCreated(await r.json());
          }}
        />
      )}

      {editing && editing !== 'new' && (
        <WidgetForm
          initial={editing}
          defaults={defaults}
          onCancel={() => setEditing(null)}
          onTestCrm={async () => {
            const r = await window.tourkitAuth.authedFetch('/api/v1/admin/widget/tokens/' + editing.token + '/test-crm', { method: 'POST' });
            return r.json();
          }}
          onSubmit={async (payload) => {
            const r = await window.tourkitAuth.authedFetch('/api/v1/admin/widget/tokens/' + editing.token, {
              method: 'PATCH', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(payload),
            });
            if (!r.ok) {
              const er = await r.json().catch(() => ({ error: 'HTTP ' + r.status }));
              throw new Error(er.error || ('HTTP ' + r.status));
            }
            onUpdated(await r.json());
          }}
        />
      )}

      {loading ? (
        <div className="wga-loading">Đang tải…</div>
      ) : items.length === 0 ? (
        <div className="wga-empty">
          <div className="wga-empty-icon">
            <img src="/lib/trav-ai.png" alt="TRAV-AI"
                 onError={e => { e.target.style.display = 'none'; }} />
          </div>
          <h3>Chưa có widget nào</h3>
          <p>Tạo widget đầu tiên để dán vào website du lịch, landing page chiến dịch, hay portal khách hàng.</p>
        </div>
      ) : (
        <div className="wga-list">
          {items.map(it => (
            <div key={it.token} className="wga-card">
              <div className="wga-card-top">
                <div className="wga-card-avatar" style={{ borderColor: it.color }}>
                  <img src="/lib/trav-ai.png" alt="TRAV-AI"
                       onError={e => { e.target.style.display = 'none'; }} />
                </div>
                <div className="wga-card-meta">
                  <div className="wga-card-name-row">
                    <span className="wga-card-name">{it.botName}</span>
                    <span className={'wga-pill ' + (it.crmLinked ? 'crm' : 'faq')}>
                      {it.crmLinked ? 'CRM realtime' : 'FAQ'}
                    </span>
                    {!it.enabled && <span className="wga-pill off">Vô hiệu</span>}
                  </div>
                  <div className="wga-card-substats">
                    <span className="wga-mono">{it.token}</span>
                    <span className="wga-sep">·</span>
                    <span><b>{(it.totalMessages || 0).toLocaleString('vi-VN')}</b> tin nhắn</span>
                    <span className="wga-sep">·</span>
                    <span><b>{(it.allowedTools || []).length}</b> CRM tool</span>
                  </div>
                </div>
                <div className="wga-card-actions">
                  <button className="wga-icon-btn" onClick={() => copySnippet(it.embedSnippet)} title="Copy snippet">
                    <Icon name="copy" size={14} />
                  </button>
                  <button className="wga-icon-btn" onClick={() => openDemo(it.token)} title="Thử ngay">
                    <Icon name="arrowRight" size={14} />
                  </button>
                  <button className="wga-icon-btn" onClick={() => setEditing(it)} title="Sửa">
                    <Icon name="edit" size={14} />
                  </button>
                  <button className="wga-icon-btn danger" onClick={() => onDeleted(it.token)} title="Xoá">
                    <Icon name="trash" size={14} />
                  </button>
                </div>
              </div>
              <div className="wga-card-greeting">"{it.greeting}"</div>
              <div className="wga-snippet">
                <pre>{it.embedSnippet}</pre>
              </div>
            </div>
          ))}
        </div>
      )}
    </main>
  );
}

// Form tạo / sửa — dùng chung cho cả create + edit.
function WidgetForm({ initial, defaults, isNew, onCancel, onSubmit, onTestCrm }) {
  const [botName, setBotName] = _wUS(initial.botName || '');
  const [greeting, setGreeting] = _wUS(initial.greeting || '');
  const [systemPrompt, setSystemPrompt] = _wUS(initial.systemPrompt || '');
  const [color, setColor] = _wUS(initial.color || '#F97316');
  const [enabled, setEnabled] = _wUS(initial.enabled !== false);
  const [origins, setOrigins] = _wUS(() => {
    try {
      if (Array.isArray(initial.allowedOrigins)) return initial.allowedOrigins.join('\n');
      if (typeof initial.allowedOrigins === 'string' && initial.allowedOrigins.startsWith('['))
        return JSON.parse(initial.allowedOrigins).join('\n');
    } catch {}
    return '';
  });
  // CRM section
  const [tourKitToken, setTourKitToken] = _wUS('');
  const [unlinkCrm, setUnlinkCrm] = _wUS(false);
  const [tools, setTools] = _wUS(() => new Set(Array.isArray(initial.allowedTools)
    ? initial.allowedTools : (defaults?.allowedTools || [])));
  const [testRes, setTestRes] = _wUS(null);   // {ok, message, sampleCount, sampleTitles}
  const [testing, setTesting] = _wUS(false);
  const [saving, setSaving] = _wUS(false);

  const toggleTool = (name) => {
    const s = new Set(tools);
    if (s.has(name)) s.delete(name); else s.add(name);
    setTools(s);
  };

  const submit = async () => {
    setSaving(true);
    try {
      const payload = {
        botName: botName.trim(), greeting: greeting.trim(), systemPrompt: systemPrompt.trim(),
        color: color.trim(),
        allowedOrigins: origins.split('\n').map(s => s.trim()).filter(Boolean),
        allowedTools: Array.from(tools),
      };
      if (tourKitToken.trim()) payload.tourKitToken = tourKitToken.trim();
      if (!isNew) {
        payload.enabled = enabled;
        if (unlinkCrm) payload.unlinkCrm = true;
      }
      await onSubmit(payload);
    } catch (e) {
      window.appAlert('Lỗi lưu: ' + e.message);
    } finally { setSaving(false); }
  };

  const testCrm = async () => {
    if (!onTestCrm) return;
    setTesting(true); setTestRes(null);
    try { setTestRes(await onTestCrm()); }
    catch (e) { setTestRes({ ok: false, message: e.message }); }
    finally { setTesting(false); }
  };

  return (
    <div className="wga-form">
      <div className="wga-form-head">
        <h2>{isNew ? 'Tạo widget mới' : 'Sửa widget'}</h2>
        <button className="wga-btn ghost" onClick={onCancel}><Icon name="close" size={13} /> Huỷ</button>
      </div>
      <div className="wga-form-grid">
        <label className="wga-field">
          <span>Tên bot hiển thị</span>
          <input type="text" value={botName} onChange={e => setBotName(e.target.value)}
                 placeholder="VD: Trợ lý Công ty ABC" maxLength={128} />
        </label>
        <label className="wga-field">
          <span>Màu chủ đạo</span>
          <div className="wga-color">
            <input type="color" value={color} onChange={e => setColor(e.target.value)} />
            <input type="text" value={color} onChange={e => setColor(e.target.value)} placeholder="#F97316" />
          </div>
        </label>
        <label className="wga-field wga-field-full">
          <span>Câu chào đầu (greeting)</span>
          <textarea rows={2} value={greeting} onChange={e => setGreeting(e.target.value)}
                    placeholder="Bot sẽ tự gửi câu này khi khách mở chat lần đầu" maxLength={1024} />
        </label>
        <label className="wga-field wga-field-full">
          <span>System Prompt (định nghĩa bot)</span>
          <textarea rows={6} value={systemPrompt} onChange={e => setSystemPrompt(e.target.value)}
                    placeholder="Bot là ai? Bán dịch vụ gì? Phong cách trả lời thế nào?" maxLength={8000} />
          <small className="wga-hint">Càng chi tiết bot càng tư vấn đúng. Ghi rõ: tên công ty, dịch vụ chính, đối tượng khách, phong cách giao tiếp.</small>
        </label>
        <label className="wga-field wga-field-full">
          <span>Domain được phép embed (mỗi dòng 1 domain — để trống = cho phép mọi nơi)</span>
          <textarea rows={3} value={origins} onChange={e => setOrigins(e.target.value)}
                    placeholder="https://example.com&#10;*.partner.com" />
          <small className="wga-hint">Hỗ trợ wildcard: <code>*.example.com</code> khớp mọi subdomain.</small>
        </label>
        {!isNew && (
          <label className="wga-field wga-toggle">
            <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} />
            <span>Đang kích hoạt</span>
          </label>
        )}

        {/* ──── CRM section ──── */}
        <div className="wga-field-full wga-crm-section">
          <div className="wga-crm-head">
            <h3>Kết nối CRM TourKit</h3>
            <span className={'wga-crm-state' + (initial.crmLinked ? ' linked' : '')}>
              {initial.crmLinked ? '✓ Đã kết nối' : 'Chưa kết nối'}
            </span>
          </div>
          <p className="wga-crm-desc">
            Bot sẽ trả lời <b>dữ liệu THẬT</b> (giá tour, ngày khởi hành, lead chờ xử lý…) thay vì tư vấn chung.
            Paste <b>Crypton token TourKit</b> của bạn (cùng định dạng <code>/login-token</code>) để liên kết.
          </p>
          <label className="wga-field wga-field-full">
            <span>Token TourKit (chỉ paste để rotate / link lần đầu — để trống = giữ nguyên)</span>
            <textarea rows={2} value={tourKitToken} onChange={e => setTourKitToken(e.target.value)}
                      placeholder="VD: ZGV1Z3IzNzM5MzU... (chuỗi Crypton dài)"
                      style={{fontFamily: 'monospace', fontSize: '12px'}} />
            <small className="wga-hint">Backend decrypt → login TourKit → lưu sessionId. KHÔNG lưu password plaintext.</small>
            {!isNew && (
              <button type="button" className="wga-btn" style={{marginTop: 8, alignSelf: 'flex-start'}}
                onClick={async () => {
                  if (!await window.appConfirm('Liên kết widget với tài khoản TourKit anh đang đăng nhập?'))
                    return;
                  try {
                    const r = await window.tourkitAuth.authedFetch(
                      '/api/v1/admin/widget/tokens/' + initial.token + '/link-current-session',
                      { method: 'POST' });
                    if (!r.ok) throw new Error('HTTP ' + r.status);
                    window.location.reload();
                  } catch (e) { window.appAlert('Lỗi: ' + e.message); }
                }}>
                ⚡ Dùng tài khoản đang đăng nhập
              </button>
            )}
          </label>

          <label className="wga-field wga-field-full">
            <span>Tool CRM bot được phép gọi (tick để bật)</span>
            <div className="wga-tools-grid">
              {(defaults?.crmToolCatalog || []).map(t => (
                <label key={t.name} className={'wga-tool-chip' + (tools.has(t.name) ? ' on' : '')}>
                  <input type="checkbox" checked={tools.has(t.name)}
                         onChange={() => toggleTool(t.name)} />
                  <span className="wga-tool-name">{t.label}</span>
                  <span className="wga-tool-key">{t.name}</span>
                </label>
              ))}
            </div>
            <small className="wga-hint">Mặc định an toàn: <code>tours</code>, <code>list_markets</code>, <code>booking_tickets</code>. Bật thêm với cẩn trọng (vd <code>financial_summary</code> lộ doanh thu).</small>
          </label>

          {!isNew && initial.crmLinked && (
            <div className="wga-crm-actions">
              <button type="button" className="wga-btn" onClick={testCrm} disabled={testing}>
                {testing ? 'Đang test…' : '🔌 Test kết nối CRM'}
              </button>
              <label className="wga-field wga-toggle" style={{flexDirection: 'row'}}>
                <input type="checkbox" checked={unlinkCrm} onChange={e => setUnlinkCrm(e.target.checked)} />
                <span>Bỏ liên kết CRM (bot quay về FAQ)</span>
              </label>
            </div>
          )}

          {testRes && (
            <div className={'wga-test-result ' + (testRes.ok ? 'ok' : 'fail')}>
              {testRes.ok ? (
                <>
                  <b>✓ Kết nối OK</b> — tìm thấy {testRes.sampleCount || 0} tour.
                  {testRes.sampleTitles?.length > 0 && (
                    <ul>{testRes.sampleTitles.map((t, i) => <li key={i}>{t}</li>)}</ul>
                  )}
                </>
              ) : (
                <><b>✗ Lỗi:</b> {testRes.message}</>
              )}
            </div>
          )}
        </div>
      </div>
      <div className="wga-form-actions">
        <button className="wga-btn ghost" onClick={onCancel} disabled={saving}>Huỷ</button>
        <button className="wga-btn primary" onClick={submit} disabled={saving || !botName.trim() || !greeting.trim() || !systemPrompt.trim()}>
          {saving ? 'Đang lưu…' : (isNew ? 'Tạo widget' : 'Lưu thay đổi')}
        </button>
      </div>
    </div>
  );
}

// Gate quyền do app.jsx xử lý ở tầng route (gatePerm CH_HT_XEM) — trang chỉ export thường.
window.WidgetAdminPage = WidgetAdminPage;
