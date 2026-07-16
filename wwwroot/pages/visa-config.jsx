// pages/visa-config.jsx — Trang admin tenant chỉnh bộ câu hỏi wizard /visa.
// JSON editor (textarea + validate) — simple, không full visual editor.
// Tenant không có cấu hình → wizard dùng DEFAULT (9 câu embedded ở visa.jsx).

(function () {
  const { useState: _vS, useEffect: _vE } = React;
  const Icon = window.Icon;
  const PageHero = window.PageShell?.PageHero;

  function VisaConfigPage({ pushToast }) {
    const [loading, setLoading] = _vS(true);
    const [saving, setSaving] = _vS(false);
    const [jsonText, setJsonText] = _vS('');
    const [hasOverride, setHasOverride] = _vS(false);
    const [updatedBy, setUpdatedBy] = _vS(null);
    const [updatedAt, setUpdatedAt] = _vS(null);
    const [parseError, setParseError] = _vS(null);
    const [questionCount, setQuestionCount] = _vS(0);

    async function load() {
      setLoading(true);
      try {
        const r = await window.tourkitAuth.authedFetch('/api/v1/visa/questions');
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const data = await r.json();
        setHasOverride(data.hasOverride);
        setUpdatedBy(data.updatedBy);
        setUpdatedAt(data.updatedAt);
        if (data.hasOverride && data.questionsJson) {
          try {
            const obj = JSON.parse(data.questionsJson);
            setJsonText(JSON.stringify(obj, null, 2));
            setQuestionCount(Array.isArray(obj) ? obj.length : 0);
          } catch { setJsonText(data.questionsJson); }
        } else {
          setJsonText(JSON.stringify(EXAMPLE_TEMPLATE, null, 2));
          setQuestionCount(EXAMPLE_TEMPLATE.length);
        }
      } catch (e) { pushToast?.('Lỗi tải config: ' + e.message, 'error'); }
      finally { setLoading(false); }
    }
    _vE(() => { load(); }, []);

    // Validate JSON khi user gõ
    _vE(() => {
      try {
        const obj = JSON.parse(jsonText);
        if (!Array.isArray(obj)) { setParseError('Phải là array'); setQuestionCount(0); return; }
        if (obj.length === 0) { setParseError('Cần ít nhất 1 câu hỏi'); setQuestionCount(0); return; }
        const missingId = obj.find(q => !q.id || !q.type || !q.label);
        if (missingId) { setParseError('Mỗi câu cần có id, type, label'); setQuestionCount(0); return; }
        setParseError(null);
        setQuestionCount(obj.length);
      } catch (e) { setParseError(e.message); setQuestionCount(0); }
    }, [jsonText]);

    async function save() {
      if (parseError) { pushToast?.('JSON còn lỗi: ' + parseError, 'error'); return; }
      const ok = await window.appConfirm(`Lưu cấu hình ${questionCount} câu hỏi? Người dùng tiếp theo vào /visa sẽ thấy bộ câu hỏi mới này.`,
        { title: 'Lưu cấu hình câu hỏi Visa', confirmLabel: 'Lưu' });
      if (!ok) return;
      setSaving(true);
      try {
        const r = await window.tourkitAuth.authedFetch('/api/v1/visa/questions', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: jsonText
        });
        if (!r.ok) { const err = await r.json().catch(() => ({})); throw new Error(err.error || 'HTTP ' + r.status); }
        pushToast?.('Đã lưu cấu hình câu hỏi.', 'success');
        await load();
      } catch (e) { pushToast?.('Lưu lỗi: ' + e.message, 'error'); }
      finally { setSaving(false); }
    }

    async function reset() {
      const ok = await window.appConfirm('Xoá cấu hình tenant và quay về 9 câu hỏi MẶC ĐỊNH? Hành động này không revert được.',
        { title: 'Reset về mặc định', confirmLabel: 'Reset', danger: true });
      if (!ok) return;
      setSaving(true);
      try {
        const r = await window.tourkitAuth.authedFetch('/api/v1/visa/questions', { method: 'DELETE' });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        pushToast?.('Đã reset về mặc định.', 'success');
        await load();
      } catch (e) { pushToast?.('Reset lỗi: ' + e.message, 'error'); }
      finally { setSaving(false); }
    }

    function loadDefault() { setJsonText(JSON.stringify(EXAMPLE_TEMPLATE, null, 2)); }

    return (
      <main className="page vcfg-page">
        {PageHero && <PageHero
          icon="shield"
          title="Cấu hình câu hỏi Visa"
          badge="admin"
          sub="Tuỳ chỉnh 9 câu hỏi wizard chấm điểm visa cho riêng tenant. Không chỉnh → dùng bộ mặc định."
          status={{ label: hasOverride ? 'CÓ OVERRIDE' : 'DÙNG MẶC ĐỊNH', detail: hasOverride && updatedAt ? `Sửa lần cuối ${new Date(updatedAt).toLocaleString('vi-VN')}${updatedBy ? ' bởi ' + updatedBy : ''}` : null, tone: hasOverride ? 'live' : 'idle' }}
          actions={<>
            <button className="vcfg-btn" onClick={loadDefault}>
              <Icon name="refresh" size={12} stroke={2.2} /> Nạp template mặc định
            </button>
            {hasOverride && (
              <button className="vcfg-btn vcfg-btn-danger" onClick={reset} disabled={saving}>
                <Icon name="trash" size={12} stroke={2.2} /> Reset về mặc định
              </button>
            )}
            <button className="vcfg-btn vcfg-btn-primary" onClick={save} disabled={saving || !!parseError}>
              <Icon name="check" size={13} stroke={2.4} /> {saving ? 'Đang lưu…' : 'Lưu cấu hình'}
            </button>
          </>}
        />}

        <section className="vcfg-card">
          <div className="vcfg-status">
            <span className={'vcfg-count' + (parseError ? ' err' : '')}>
              {parseError ? `⚠ Lỗi: ${parseError}` : `✓ ${questionCount} câu hỏi hợp lệ`}
            </span>
            <a className="vcfg-helplink" href="/visa" target="_blank">Mở trang /visa xem trước</a>
          </div>
          {loading ? (
            <div className="vcfg-loading">Đang tải cấu hình…</div>
          ) : (
            <textarea
              className={'vcfg-editor' + (parseError ? ' err' : '')}
              value={jsonText}
              onChange={e => setJsonText(e.target.value)}
              spellCheck={false}
              rows={28}
            />
          )}
          <div className="vcfg-hint">
            <b>Schema mỗi câu:</b> {`{ id, type: 'radio'|'checkbox'|'contact', label, options?, hint?, sub?, fields? }`}<br/>
            <b>Type 'contact'</b> dùng cho form họ tên/SĐT/email (chỉ 1 câu cuối). Type 'radio' = chọn 1, 'checkbox' = chọn nhiều.
          </div>
        </section>
      </main>
    );
  }

  // 3 câu hỏi gọn để làm template ví dụ (user xoá rồi điền 9 câu thật)
  const EXAMPLE_TEMPLATE = [
    { id: 'country', type: 'radio', required: true,
      label: 'Quốc gia bạn muốn xin visa là nước nào?',
      options: ['Mỹ', 'Anh', 'Khối Schengen, EU', 'Úc', 'Canada', 'Hàn Quốc', 'Nhật Bản'] },
    { id: 'occupation', type: 'radio', required: true,
      label: 'Công việc hiện tại của bạn',
      options: ['Nhân viên có BHXH', 'Chủ doanh nghiệp có giấy phép KD', 'Tự do', 'Học sinh/Sinh viên', 'Hưu trí', 'Khác'] },
    { id: 'contact', type: 'contact', required: true,
      label: 'Thông tin liên hệ',
      sub: 'AI sẽ gửi báo cáo qua các kênh này.',
      fields: [
        { id: 'fullName', label: 'Họ tên', required: true, type: 'text' },
        { id: 'phone', label: 'Số điện thoại', required: true, type: 'tel' },
        { id: 'email', label: 'Email', required: false, type: 'email' },
      ] },
  ];

  window.VisaConfigPage = function VisaConfigPageGate(props) {
    if (!window.tourkitAuth.hasPerm('CH_HT_THAOTAC'))
      return <window.NoPermissionBox feature="Câu hỏi Visa" />;
    return <VisaConfigPage {...props} />;
  };
})();
