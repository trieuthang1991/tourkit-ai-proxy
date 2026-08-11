// pages/widget-admin.jsx — Cấu hình Widget Chat nhúng website.
//
// BỐ CỤC: mỗi công ty chỉ có ĐÚNG 1 widget (backend idempotent: 1 tenant = 1 widget, tạo lần 2 sẽ
// ghi đè cái cũ). Nên trang này là TRANG CẤU HÌNH, không phải danh sách — trước đây UI dựng theo kiểu
// "quản lý nhiều widget" (nút Tạo mới + danh sách + KPI đếm) nên nói sai sự thật với người dùng.

const { useState: _wUS, useEffect: _wUE, useMemo: _wUM } = React;

// Nguồn dữ liệu AN TOÀN để khách vãng lai trên website xem (thông tin bán hàng).
// Mọi nguồn khác đều là dữ liệu NỘI BỘ — bật là khách lạ hỏi được.
const WGA_SAFE_TOOLS = ['tours', 'list_markets', 'departures'];

// Vì sao nguồn này nhạy cảm — hiện ngay cạnh ô tick, không giấu trong chú thích cuối khối.
const WGA_RISK_NOTE = {
  financial_summary: 'Lộ doanh thu, công nợ, lợi nhuận',
  cashflow: 'Lộ doanh thu, chi phí, lợi nhuận',
  booking_tickets: 'Lộ tên và số điện thoại khách hàng khác',
  customers: 'Lộ danh sách khách hàng',
  top_customers: 'Lộ khách hàng lớn nhất của công ty',
  top_sellers: 'Lộ doanh số từng nhân viên',
  employee_performance: 'Lộ hiệu suất từng nhân viên',
  marketing: 'Lộ nguồn khách và hiệu quả quảng cáo',
  vouchers: 'Lộ phiếu thu chi',
  branch_performance: 'Lộ doanh số từng chi nhánh',
  product_line_revenue: 'Lộ doanh thu từng dòng sản phẩm',
  market_analysis: 'Lộ doanh thu từng thị trường',
  tasks: 'Lộ công việc nội bộ',
  appointments: 'Lộ lịch hẹn với khách',
  notifications: 'Lộ thông báo nội bộ',
};

function WidgetAdminPage({ pushToast }) {
  const [widget, setWidget] = _wUS(null);        // null = chưa có widget nào
  const [defaults, setDefaults] = _wUS(null);
  const [loading, setLoading] = _wUS(true);
  const [creating, setCreating] = _wUS(false);

  const load = async () => {
    setLoading(true);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/admin/widget/tokens');
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const data = await r.json();
      // Backend đảm bảo tối đa 1 widget/công ty — lấy cái đầu tiên.
      setWidget((data.items || [])[0] || null);
      setDefaults(data.defaults || null);
    } catch (e) {
      pushToast('Không tải được cấu hình: ' + e.message, 'error');
    } finally { setLoading(false); }
  };
  _wUE(() => { load(); }, []);

  const createWidget = async () => {
    if (!defaults) return;
    setCreating(true);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/admin/widget/tokens', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          botName: defaults.botName, greeting: defaults.greeting,
          systemPrompt: defaults.systemPrompt, color: defaults.color,
          allowedOrigins: [], allowedTools: defaults.allowedTools,
        }),
      });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      setWidget(await r.json());
      pushToast('Đã tạo widget. Sao chép mã nhúng ở cuối trang để gắn vào website.');
    } catch (e) {
      pushToast('Không tạo được widget: ' + e.message, 'error');
    } finally { setCreating(false); }
  };

  if (loading) return <WidgetSkeleton />;

  return (
    <main className="page wga">
      <header className="wga-head">
        <div>
          <p className="wga-eyebrow">Tích hợp</p>
          <h1>Widget chat trên website</h1>
          <p className="wga-sub">
            Hộp chat AI gắn vào website hoặc trang đích của công ty. Mỗi công ty dùng chung một widget —
            sửa ở đây là mọi trang đã gắn đều đổi theo.
          </p>
        </div>
      </header>

      {!widget ? (
        <section className="wga-empty">
          <h2>Chưa có widget</h2>
          <p>
            Tạo widget để lấy đoạn mã nhúng vào website. Sau khi tạo, bạn có thể đổi tên bot, câu chào,
            cách trả lời và chọn nguồn dữ liệu mà bot được phép dùng.
          </p>
          <button className="wga-btn primary lg" onClick={createWidget} disabled={creating}>
            <Icon name="plus" size={15} /> {creating ? 'Đang tạo…' : 'Tạo widget'}
          </button>
        </section>
      ) : (
        <WidgetConfig
          widget={widget}
          defaults={defaults}
          pushToast={pushToast}
          onSaved={(w) => setWidget(prev => ({ ...prev, ...w }))}
          onDeleted={() => setWidget(null)}
        />
      )}
    </main>
  );
}

// ─── Khung xương lúc tải (thay cho chữ "Đang tải…") ──────────────────────────
function WidgetSkeleton() {
  return (
    <main className="page wga" aria-busy="true" aria-label="Đang tải cấu hình widget">
      <div className="wga-sk wga-sk-title" />
      <div className="wga-sk wga-sk-sub" />
      <div className="wga-sk wga-sk-hero" />
      <div className="wga-sk wga-sk-card" />
      <div className="wga-sk wga-sk-card" />
    </main>
  );
}

function WidgetConfig({ widget, defaults, pushToast, onSaved, onDeleted }) {
  const [botName, setBotName] = _wUS(widget.botName || '');
  const [greeting, setGreeting] = _wUS(widget.greeting || '');
  const [systemPrompt, setSystemPrompt] = _wUS(widget.systemPrompt || '');
  const [color, setColor] = _wUS(widget.color || '#F97316');
  const [enabled, setEnabled] = _wUS(widget.enabled !== false);
  const [origins, setOrigins] = _wUS(() => {
    try {
      if (Array.isArray(widget.allowedOrigins)) return widget.allowedOrigins.join('\n');
      if (typeof widget.allowedOrigins === 'string' && widget.allowedOrigins.startsWith('['))
        return JSON.parse(widget.allowedOrigins).join('\n');
    } catch { /* cấu hình cũ sai định dạng — coi như để trống */ }
    return '';
  });
  const [tools, setTools] = _wUS(() => new Set(
    Array.isArray(widget.allowedTools) ? widget.allowedTools : (defaults?.allowedTools || [])));
  const [tourKitToken, setTourKitToken] = _wUS('');
  const [unlinkCrm, setUnlinkCrm] = _wUS(false);

  const [saving, setSaving] = _wUS(false);
  const [saveError, setSaveError] = _wUS(null);
  const [testing, setTesting] = _wUS(false);
  const [testRes, setTestRes] = _wUS(null);

  const catalog = defaults?.crmToolCatalog || [];
  const safeTools = catalog.filter(t => WGA_SAFE_TOOLS.includes(t.name));
  const riskyTools = catalog.filter(t => !WGA_SAFE_TOOLS.includes(t.name));
  const riskyOn = riskyTools.filter(t => tools.has(t.name));

  // Có thay đổi chưa lưu? — quyết định hiện thanh lưu dưới đáy.
  const dirty = _wUM(() => {
    const origOrigins = (() => {
      try {
        if (Array.isArray(widget.allowedOrigins)) return widget.allowedOrigins.join('\n');
        if (typeof widget.allowedOrigins === 'string' && widget.allowedOrigins.startsWith('['))
          return JSON.parse(widget.allowedOrigins).join('\n');
      } catch { /* bỏ qua */ }
      return '';
    })();
    const origTools = new Set(Array.isArray(widget.allowedTools) ? widget.allowedTools : []);
    const sameTools = origTools.size === tools.size && [...tools].every(t => origTools.has(t));
    return botName !== (widget.botName || '')
      || greeting !== (widget.greeting || '')
      || systemPrompt !== (widget.systemPrompt || '')
      || color !== (widget.color || '#F97316')
      || enabled !== (widget.enabled !== false)
      || origins !== origOrigins
      || !sameTools
      || !!tourKitToken.trim()
      || unlinkCrm;
  }, [botName, greeting, systemPrompt, color, enabled, origins, tools, tourKitToken, unlinkCrm, widget]);

  const toggleTool = (name) => {
    const s = new Set(tools);
    if (s.has(name)) s.delete(name); else s.add(name);
    setTools(s);
  };

  const save = async () => {
    setSaving(true); setSaveError(null);
    try {
      const payload = {
        botName: botName.trim(), greeting: greeting.trim(), systemPrompt: systemPrompt.trim(),
        color: color.trim(), enabled,
        allowedOrigins: origins.split('\n').map(s => s.trim()).filter(Boolean),
        allowedTools: Array.from(tools),
      };
      if (tourKitToken.trim()) payload.tourKitToken = tourKitToken.trim();
      if (unlinkCrm) payload.unlinkCrm = true;

      const r = await window.tourkitAuth.authedFetch('/api/v1/admin/widget/tokens/' + widget.token, {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (!r.ok) {
        const er = await r.json().catch(() => ({}));
        throw new Error(er.error || ('Máy chủ trả lỗi ' + r.status));
      }
      onSaved(await r.json());
      setTourKitToken(''); setUnlinkCrm(false);
      pushToast('Đã lưu cấu hình');
    } catch (e) {
      setSaveError(e.message);
    } finally { setSaving(false); }
  };

  const testCrm = async () => {
    setTesting(true); setTestRes(null);
    try {
      const r = await window.tourkitAuth.authedFetch(
        '/api/v1/admin/widget/tokens/' + widget.token + '/test-crm', { method: 'POST' });
      setTestRes(await r.json());
    } catch (e) {
      setTestRes({ ok: false, message: e.message });
    } finally { setTesting(false); }
  };

  const linkCurrentSession = async () => {
    if (!await window.appConfirm(
      'Cho widget dùng tài khoản TourKit bạn đang đăng nhập để lấy dữ liệu?')) return;
    try {
      const r = await window.tourkitAuth.authedFetch(
        '/api/v1/admin/widget/tokens/' + widget.token + '/link-current-session', { method: 'POST' });
      if (!r.ok) throw new Error('Máy chủ trả lỗi ' + r.status);
      onSaved(await r.json());
      pushToast('Đã kết nối dữ liệu. Bot sẽ trả lời bằng số liệu thật.');
    } catch (e) { pushToast('Không kết nối được: ' + e.message, 'error'); }
  };

  const remove = async () => {
    if (!await window.appConfirm(
      'Xoá widget này? Hộp chat trên website sẽ ngừng hoạt động ngay lập tức.',
      { danger: true, confirmLabel: 'Xoá widget' })) return;
    try {
      const r = await window.tourkitAuth.authedFetch(
        '/api/v1/admin/widget/tokens/' + widget.token, { method: 'DELETE' });
      if (!r.ok) throw new Error('Máy chủ trả lỗi ' + r.status);
      onDeleted();
      pushToast('Đã xoá widget');
    } catch (e) { pushToast('Không xoá được: ' + e.message, 'error'); }
  };

  return (
    <>
      {/* ── 1. Trạng thái: đang chạy? lấy dữ liệu từ đâu? hệ quả là gì? ── */}
      <section className={'wga-status' + (enabled ? '' : ' is-off')}>
        <div className="wga-status-main">
          <p className="wga-status-state">
            <span className={'wga-dot' + (enabled ? ' on' : '')} />
            {enabled ? 'Đang hoạt động trên website' : 'Đang tắt — hộp chat không hiện'}
          </p>
          <p className="wga-status-mode">
            {widget.crmLinked
              ? 'Bot trả lời bằng số liệu thật lấy từ hệ thống ERP của công ty.'
              : 'Bot chỉ tư vấn chung, chưa đọc được số liệu thật — nên khi khách hỏi cụ thể, bot sẽ mời khách để lại liên hệ.'}
          </p>
          <dl className="wga-status-facts">
            <div><dt>Tin nhắn đã trả lời</dt><dd>{(widget.totalMessages || 0).toLocaleString('vi-VN')}</dd></div>
            <div><dt>Nguồn dữ liệu đang bật</dt><dd>{tools.size}</dd></div>
          </dl>
        </div>
        <div className="wga-status-side">
          <button className="wga-btn" onClick={() => window.open(
            `/widget-demo.html?token=${encodeURIComponent(widget.token)}`, '_blank')}>
            <Icon name="arrowRight" size={14} /> Thử hộp chat
          </button>
          <label className="wga-switch">
            <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} />
            <span>Bật hộp chat</span>
          </label>
        </div>
      </section>

      {/* ── 2. Danh tính bot ── */}
      <section className="wga-sec">
        <div className="wga-sec-head">
          <h2>Bot của bạn</h2>
          <p>Những gì khách nhìn thấy và cách bot xưng hô, trả lời.</p>
        </div>
        <div className="wga-grid">
          <label className="wga-field">
            <span className="wga-label">Tên hiển thị</span>
            <input type="text" value={botName} maxLength={128}
                   onChange={e => setBotName(e.target.value)} placeholder="Trợ lý Công ty ABC" />
          </label>
          <label className="wga-field">
            <span className="wga-label">Màu hộp chat</span>
            <div className="wga-color">
              <input type="color" value={color} onChange={e => setColor(e.target.value)}
                     aria-label="Chọn màu hộp chat" />
              <input type="text" value={color} onChange={e => setColor(e.target.value)} />
            </div>
          </label>
          <label className="wga-field wga-col-2">
            <span className="wga-label">Câu chào khi khách mở hộp chat</span>
            <textarea rows={2} value={greeting} maxLength={1024}
                      onChange={e => setGreeting(e.target.value)} />
          </label>
          <label className="wga-field wga-col-2">
            <span className="wga-label">Cách bot trả lời</span>
            <textarea rows={6} value={systemPrompt} maxLength={8000}
                      onChange={e => setSystemPrompt(e.target.value)}
                      placeholder="Bot là ai, công ty bán gì, giọng điệu ra sao, khi nào thì hỏi thêm thông tin khách…" />
            <small className="wga-hint">
              Viết càng cụ thể bot tư vấn càng đúng. Nên ghi rõ tên công ty, dịch vụ chính,
              và bạn muốn bot hỏi thêm gì trước khi tư vấn.
            </small>
          </label>
        </div>
      </section>

      {/* ── 3. Nguồn dữ liệu ── */}
      <section className="wga-sec">
        <div className="wga-sec-head">
          <h2>Dữ liệu bot được dùng</h2>
          <p>Quyết định bot trả lời bằng số liệu thật hay chỉ tư vấn chung.</p>
        </div>

        <div className={'wga-conn' + (widget.crmLinked ? ' is-linked' : '')}>
          <div className="wga-conn-text">
            <strong>{widget.crmLinked ? 'Đã kết nối dữ liệu công ty' : 'Chưa kết nối dữ liệu công ty'}</strong>
            <span>
              {widget.crmLinked
                ? 'Bot đọc được tour, thị trường và các nguồn bạn bật bên dưới.'
                : 'Chưa kết nối thì bot không biết công ty có tour nào, giá bao nhiêu.'}
            </span>
          </div>
          {!widget.crmLinked && (
            <button className="wga-btn primary" onClick={linkCurrentSession}>
              <Icon name="zap" size={14} /> Dùng tài khoản đang đăng nhập
            </button>
          )}
          {widget.crmLinked && (
            <button className="wga-btn" onClick={testCrm} disabled={testing}>
              <Icon name="refresh" size={14} /> {testing ? 'Đang kiểm tra…' : 'Kiểm tra kết nối'}
            </button>
          )}
        </div>

        {testRes && (
          <p className={'wga-note ' + (testRes.ok ? 'ok' : 'bad')} role="status">
            <Icon name={testRes.ok ? 'checkCircle' : 'warning'} size={15} />
            <span>{testRes.ok
              ? `Kết nối tốt — đọc được ${testRes.sampleCount || 0} tour.`
              : `Kết nối lỗi: ${testRes.message}`}</span>
          </p>
        )}

        <div className="wga-tools">
          <div className="wga-tools-group">
            <h3>Khách xem được — an toàn</h3>
            <p className="wga-tools-desc">Thông tin bán hàng, để khách tự tra cứu.</p>
            <div className="wga-tool-list">
              {safeTools.map(t => (
                <label key={t.name} className={'wga-tool' + (tools.has(t.name) ? ' on' : '')}>
                  <input type="checkbox" checked={tools.has(t.name)} onChange={() => toggleTool(t.name)} />
                  <span className="wga-tool-name">{t.label}</span>
                </label>
              ))}
            </div>
          </div>

          <div className="wga-tools-group risky">
            <h3>Dữ liệu nội bộ — cân nhắc kỹ</h3>
            <p className="wga-tools-desc">
              Bật là <b>bất kỳ ai vào website</b> cũng hỏi được, kể cả đối thủ. Chỉ bật khi thật sự cần.
            </p>
            <div className="wga-tool-list">
              {riskyTools.map(t => (
                <label key={t.name} className={'wga-tool risky' + (tools.has(t.name) ? ' on' : '')}>
                  <input type="checkbox" checked={tools.has(t.name)} onChange={() => toggleTool(t.name)} />
                  <span className="wga-tool-name">{t.label}</span>
                  {WGA_RISK_NOTE[t.name] && (
                    <span className="wga-tool-risk">{WGA_RISK_NOTE[t.name]}</span>
                  )}
                </label>
              ))}
            </div>
          </div>
        </div>

        {riskyOn.length > 0 && (
          <p className="wga-note bad" role="alert">
            <Icon name="warning" size={15} />
            <span>
              Đang mở {riskyOn.length} nguồn dữ liệu nội bộ cho khách vãng lai:{' '}
              <b>{riskyOn.map(t => t.label).join(', ')}</b>. Hãy chắc chắn bạn muốn điều này.
            </span>
          </p>
        )}

        {widget.crmLinked && (
          <label className="wga-switch danger">
            <input type="checkbox" checked={unlinkCrm} onChange={e => setUnlinkCrm(e.target.checked)} />
            <span>Ngắt kết nối dữ liệu — bot quay lại chỉ tư vấn chung</span>
          </label>
        )}
      </section>

      {/* ── 4. Bảo mật ── */}
      <section className="wga-sec">
        <div className="wga-sec-head">
          <h2>Website được phép gắn</h2>
          <p>Chặn người khác lấy mã nhúng của bạn dán sang trang của họ.</p>
        </div>
        <label className="wga-field">
          <span className="wga-label">Danh sách tên miền — mỗi dòng một cái</span>
          <textarea rows={3} value={origins} onChange={e => setOrigins(e.target.value)}
                    placeholder={'https://congty.com\n*.congty.com'} />
          <small className="wga-hint">
            Dùng <code>*.congty.com</code> để cho phép mọi trang con.
          </small>
        </label>
        {origins.trim() === '' && (
          <p className="wga-note warn">
            <Icon name="warning" size={15} />
            <span>Đang để trống nên <b>mọi website đều gắn được</b> widget này. Nên điền tên miền công ty bạn.</span>
          </p>
        )}
      </section>

      {/* ── 5. Mã nhúng ── */}
      <section className="wga-sec">
        <div className="wga-sec-head">
          <h2>Mã nhúng</h2>
          <p>Dán đoạn này vào website, ngay trước thẻ đóng <code>&lt;/body&gt;</code>.</p>
        </div>
        <div className="wga-snippet">
          <pre><code>{widget.embedSnippet}</code></pre>
          <button className="wga-btn" onClick={() => {
            window.tourkitUtil.copyText(widget.embedSnippet);
            pushToast('Đã sao chép mã nhúng');
          }}>
            <Icon name="copy" size={14} /> Sao chép
          </button>
        </div>
      </section>

      <section className="wga-danger">
        <div>
          <strong>Xoá widget</strong>
          <span>Hộp chat trên website ngừng hoạt động ngay. Không khôi phục được.</span>
        </div>
        <button className="wga-btn danger" onClick={remove}>
          <Icon name="trash" size={14} /> Xoá widget
        </button>
      </section>

      {/* Thanh lưu — chỉ hiện khi có thay đổi, để người dùng không quên bấm lưu */}
      {dirty && (
        <div className="wga-savebar" role="region" aria-label="Thay đổi chưa lưu">
          <span className="wga-savebar-text">
            <Icon name="info" size={15} /> Có thay đổi chưa lưu
          </span>
          {saveError && <span className="wga-savebar-err">{saveError}</span>}
          <button className="wga-btn primary" onClick={save} disabled={saving || !botName.trim()}>
            {saving ? 'Đang lưu…' : 'Lưu thay đổi'}
          </button>
        </div>
      )}
    </>
  );
}

// Gate quyền do app.jsx xử lý ở tầng route (gatePerm CH_HT_XEM) — trang chỉ export thường.
window.WidgetAdminPage = WidgetAdminPage;
