// pages/visa.jsx — Wizard "Kiểm tra tỉ lệ đậu Visa" 9 câu hỏi + upload + AI score.
// Design Read (taste skill): internal B2B SaaS form, dials 3/3/3, Typeform-conversational
// + Stripe-clean restraint. Lock: 1 accent (orange), 1 radius scale (12px tile / 10px button / pill),
// hierarchy bằng typography weight + spacing, KHÔNG eyebrow middle-dot, KHÔNG decorative dots, KHÔNG em-dash.

(function () {
  const { useState: _vS, useMemo: _vM, useEffect: _vE } = React;
  const Icon = window.Icon;
  // Auto-save draft câu trả lời vào localStorage → F5 / đóng tab / quay lại tab khác không mất bài.
  // KHÔNG save file binary (quá lớn, không có cách restore từ localStorage).
  // Schema: { step, answers, savedAt }. TTL 7 ngày.
  const DRAFT_KEY = 'tourkit_visa_wizard_draft';
  const DRAFT_TTL_MS = 7 * 24 * 60 * 60 * 1000;
  function loadDraft() {
    try {
      const raw = localStorage.getItem(DRAFT_KEY);
      if (!raw) return null;
      const d = JSON.parse(raw);
      if (!d || (Date.now() - (d.savedAt || 0)) > DRAFT_TTL_MS) { localStorage.removeItem(DRAFT_KEY); return null; }
      return d;
    } catch { return null; }
  }
  function saveDraft(step, answers) {
    try { localStorage.setItem(DRAFT_KEY, JSON.stringify({ step, answers, savedAt: Date.now() })); } catch {}
  }
  function clearDraft() { try { localStorage.removeItem(DRAFT_KEY); } catch {} }

  // ── Bộ 9 câu mặc định (DEFAULT). Frontend tự fetch /api/v1/visa/questions on mount —
  // tenant có override sẽ dùng đó, không có thì fallback DEFAULT này. ──
  const DEFAULT_QUESTIONS = [
    { id: 'country', type: 'radio', required: true,
      label: 'Quốc gia bạn muốn xin visa là nước nào?',
      options: ['Mỹ', 'Anh', 'Khối Schengen, EU', 'Úc', 'New Zealand', 'Canada', 'Hàn Quốc', 'Đài Loan', 'Trung Quốc', 'Nhật Bản'] },

    { id: 'maritalStatus', type: 'radio', required: true,
      label: 'Tình trạng hôn nhân của bạn',
      options: ['Kết hôn', 'Ly hôn', 'Cha/mẹ đơn thân', 'Goá chồng/vợ', 'Độc thân'] },

    { id: 'highRiskProvince', type: 'radio', required: true,
      label: 'Nơi sinh bạn ở các tỉnh "rủi ro cao"?',
      hint: 'Thanh Hoá, Hà Tĩnh, Quảng Trị, Quảng Ngãi, Nghệ An, Quảng Bình, Đắk Nông, Vũng Tàu, Tây Ninh, An Giang, Kiên Giang, Hải Dương, Hoà Bình, Lào Cai.',
      options: ['Đúng', 'Sai'] },

    { id: 'travelHistory', type: 'checkbox', required: true,
      label: 'Lịch sử du lịch quốc tế của bạn',
      sub: 'Chọn những khu vực bạn đã từng đi.',
      options: [
        'Đông Nam Á (Singapore, Malaysia, Indonesia, Thái Lan)',
        'Trung Quốc',
        'Châu Á (Hàn Quốc, Nhật Bản, Đài Loan, Hongkong,...)',
        'Châu Âu (Khối EU,...)',
        'Châu Úc (Úc, New Zealand,...)',
        'Anh Quốc',
        'Châu Mỹ (Mỹ, Canada,...)',
        'Chưa từng đi du lịch quốc tế',
      ] },

    { id: 'visaRefusal', type: 'radio', required: true,
      label: 'Bạn đã từng bị từ chối Visa lần nào chưa?',
      options: [
        'Đã bị từ chối visa trên 2 lần',
        'Đã bị từ chối visa từ 1-2 lần',
        'Chưa từng bị từ chối visa',
      ] },

    { id: 'occupation', type: 'radio', required: true,
      label: 'Công việc hiện tại của bạn',
      options: [
        'Kinh doanh tự do (có chứng minh được thu nhập)',
        'Kinh doanh tự do (không chứng minh được thu nhập)',
        'Nhân viên (có Bảo hiểm xã hội)',
        'Nhân viên (không có Bảo hiểm xã hội)',
        'Chủ doanh nghiệp, hộ kinh doanh (có giấy phép KD, hoá đơn nộp thuế)',
        'Chủ doanh nghiệp, hộ kinh doanh (không có giấy phép KD)',
        'Viên chức, công chức nhà nước',
        'Bác sĩ/Dược sĩ/Y sĩ',
        'Nội trợ',
        'Nghỉ hưu (có thẻ/quyết định hưu trí và lương hưu)',
        'Học sinh/sinh viên',
        'Không có việc làm',
      ] },

    { id: 'income', type: 'radio', required: true,
      label: 'Thu nhập trung bình hàng tháng',
      options: ['Trên 50 triệu', 'Từ 20-50 triệu', 'Từ 10-20 triệu', 'Dưới 10 triệu'] },

    { id: 'financialAssets', type: 'checkbox', required: true,
      label: 'Khả năng tài chính, tài sản của bạn',
      sub: 'Chọn tất cả giấy tờ bạn có thể chứng minh.',
      options: [
        'Giấy tờ nhà đất (sổ hồng, sổ đỏ, hợp đồng mua bán)',
        'Giấy tờ sở hữu xe ô tô, cổ phiếu, trái phiếu,...',
        'Sổ tiết kiệm trên 200 triệu',
        'Sổ tiết kiệm dưới 200 triệu',
        'Không có tài sản, tài chính',
      ] },

    { id: 'contact', type: 'contact', required: true,
      label: 'Thông tin khách hàng',
      sub: 'Lưu thông tin khách hàng để tạo đơn hàng.',
      fields: [
        { id: 'fullName', label: 'Họ tên', required: true, type: 'text', placeholder: 'Nguyễn Văn A' },
        { id: 'phone', label: 'Số điện thoại', required: true, type: 'tel', placeholder: '0xxxxxxxxx' },
        { id: 'email', label: 'Email (tuỳ chọn)', required: false, type: 'email', placeholder: 'you@example.com' },
      ] },
  ];

  function getSuggestedDocs(answers) {
    const docs = [];
    const a = answers || {};
    docs.push({ key: 'passport', label: 'Hộ chiếu (trang thông tin)', required: true,
      desc: 'Scan rõ trang đầu hộ chiếu có ảnh và mã số.' });
    docs.push({ key: 'idcard', label: 'CCCD/CMND 2 mặt', required: true,
      desc: 'Đối chiếu thông tin với hộ chiếu.' });

    const hasTravel = Array.isArray(a.travelHistory) && a.travelHistory.length && !a.travelHistory.includes('Chưa từng đi du lịch quốc tế');
    if (hasTravel) docs.push({ key: 'visaStamps', label: 'Trang hộ chiếu có visa/dấu xuất nhập cảnh cũ', required: false,
      desc: 'Lịch sử du lịch quốc tế giúp tăng điểm tin cậy.' });

    if (a.visaRefusal && a.visaRefusal.startsWith('Đã bị')) docs.push({ key: 'visaRefusalLetter', label: 'Thư từ chối visa (nếu có)', required: false,
      desc: 'Bộ phận visa cần phân tích lý do và chuẩn bị giải trình.' });

    const job = a.occupation || '';
    if (job.includes('Bảo hiểm xã hội')) docs.push({ key: 'bhxh', label: 'Sổ BHXH / Sao kê BHXH', required: true,
      desc: 'Chứng minh có việc làm ổn định và đóng thuế.' });
    if (job.includes('giấy phép KD')) docs.push({ key: 'businessLicense', label: 'Giấy phép kinh doanh + 3 tháng nộp thuế gần nhất', required: true,
      desc: 'Bản chính hoặc bản sao công chứng.' });
    if (job.includes('Kinh doanh tự do')) docs.push({ key: 'incomeProof', label: 'Sao kê tài khoản 6 tháng (chứng minh thu nhập)', required: !job.includes('không chứng minh'),
      desc: 'Sao kê ngân hàng có dòng tiền vào ra rõ ràng.' });
    if (job.includes('Viên chức') || job.includes('Bác sĩ')) docs.push({ key: 'workConfirm', label: 'Giấy xác nhận công tác + bảng lương 3 tháng', required: true,
      desc: 'Có dấu cơ quan/bệnh viện.' });
    if (job.includes('Nghỉ hưu')) docs.push({ key: 'retiredCert', label: 'Quyết định/thẻ hưu trí + 3 tháng lương hưu', required: true,
      desc: 'Bản gốc hoặc bản sao công chứng.' });
    if (job.includes('Học sinh') || job.includes('sinh viên')) docs.push({ key: 'studentCard', label: 'Thẻ HS/SV + xác nhận trường', required: true,
      desc: 'Có dấu nhà trường. Bảng điểm gần nhất nếu có.' });

    const assets = Array.isArray(a.financialAssets) ? a.financialAssets : [];
    if (assets.some(x => x.includes('nhà đất'))) docs.push({ key: 'realEstate', label: 'Sổ đỏ/sổ hồng/hợp đồng mua bán', required: false,
      desc: 'Bản sao có công chứng.' });
    if (assets.some(x => x.includes('xe ô tô') || x.includes('cổ phiếu'))) docs.push({ key: 'vehicleStock', label: 'Đăng ký xe / chứng nhận sở hữu', required: false,
      desc: 'Bản sao có công chứng.' });
    if (assets.some(x => x.startsWith('Sổ tiết kiệm'))) docs.push({ key: 'savings', label: 'Sao kê sổ tiết kiệm (kỳ hạn từ 3 tháng)', required: true,
      desc: 'Sổ tiết kiệm phong toả ưu tiên.' });

    if (a.maritalStatus === 'Kết hôn') docs.push({ key: 'marriageCert', label: 'Giấy đăng ký kết hôn', required: false,
      desc: 'Nếu đi cùng vợ/chồng/con.' });
    if (a.maritalStatus === 'Cha/mẹ đơn thân') docs.push({ key: 'singleParentDocs', label: 'Giấy khai sinh con + sổ hộ khẩu', required: false,
      desc: 'Chứng minh quan hệ và trách nhiệm tại Việt Nam.' });

    return docs;
  }

  const pad2 = (n) => String(n).padStart(2, '0');

  function VisaPage({ pushToast }) {
    // Fetch tenant override khi mount (1 lần). Có override → dùng, không có → DEFAULT_QUESTIONS.
    const [questions, setQuestions] = _vS(DEFAULT_QUESTIONS);
    _vE(() => {
      let alive = true;
      (async () => {
        try {
          const r = await window.tourkitAuth.authedFetch('/api/v1/visa/questions');
          if (!r.ok || !alive) return;
          const data = await r.json();
          if (data.hasOverride && data.questionsJson) {
            try {
              const parsed = JSON.parse(data.questionsJson);
              if (Array.isArray(parsed) && parsed.length > 0) setQuestions(parsed);
            } catch (e) { console.warn('[visa] tenant questions JSON xấu, fallback DEFAULT', e); }
          }
        } catch {}
      })();
      return () => { alive = false; };
    }, []);

    const total = questions.length;
    const TOTAL_STEPS = total + 2;

    // Init từ draft (nếu có) — banner "Đã khôi phục bài làm dở" hiện vài giây rồi tự tắt.
    const draft = _vM(() => loadDraft(), []);
    const [step, setStep] = _vS(draft?.step || 0);
    const [answers, setAnswers] = _vS(draft?.answers || {});
    const [files, setFiles] = _vS({});
    const [submitting, setSubmitting] = _vS(false);
    const [result, setResult] = _vS(null);
    const [draftBanner, setDraftBanner] = _vS(!!draft);

    // Auto-save mỗi khi answers/step đổi — debounce 400ms để khỏi spam localStorage.
    _vE(() => {
      if (result) return;   // sau khi đã chấm xong thì không save nữa
      const id = setTimeout(() => saveDraft(step, answers), 400);
      return () => clearTimeout(id);
    }, [step, answers, result]);

    // Sau khi có result thành công → xoá draft (đã hoàn thành luồng).
    _vE(() => { if (result) clearDraft(); }, [result]);

    // Tự ẩn banner khôi phục sau 4s.
    _vE(() => { if (!draftBanner) return; const t = setTimeout(() => setDraftBanner(false), 4000); return () => clearTimeout(t); }, [draftBanner]);

    const cur = questions[step];
    const isQuestionStep = step < total;
    const isUploadStep = step === total;
    const isReviewStep = step === total + 1;

    const suggested = _vM(() => getSuggestedDocs(answers), [answers]);

    const setAnswer = (qid, value) => setAnswers(a => ({ ...a, [qid]: value }));
    const setContactField = (fid, value) => setAnswers(a => ({ ...a, contact: { ...(a.contact || {}), [fid]: value } }));

    function isStepValid() {
      if (!isQuestionStep) return true;
      const q = cur;
      const v = answers[q.id];
      if (q.type === 'radio') return !!v;
      if (q.type === 'checkbox') return Array.isArray(v) && v.length > 0;
      if (q.type === 'contact') {
        const c = v || {};
        return q.fields.every(f => !f.required || (c[f.id] || '').trim());
      }
      return true;
    }

    function next() {
      if (!isStepValid()) { pushToast?.('Vui lòng trả lời câu này trước khi tiếp tục.', 'error'); return; }
      setStep(s => Math.min(s + 1, TOTAL_STEPS - 1));
    }
    const back = () => setStep(s => Math.max(s - 1, 0));

    async function submit() {
      setSubmitting(true);
      try {
        // Build multipart: answers (JSON) + filesMeta (JSON metadata) + files binary.
        // Backend hiện CHƯA đọc OCR file → chỉ cần metadata. File binary gửi kèm để
        // sau phase 2 wire vision được (FE không phải đổi).
        const fd = new FormData();
        fd.append('answers', JSON.stringify(answers));
        // suggested doc list (key→label) để map docLabel cho AI prompt
        const labelMap = {};
        suggested.forEach(d => { labelMap[d.key] = d.label; });
        const filesMeta = Object.entries(files).map(([docKey, arr]) => ({
          docKey,
          docLabel: labelMap[docKey] || docKey,
          count: arr?.length || 0,
          totalBytes: (arr || []).reduce((s, f) => s + (f.size || 0), 0),
        })).filter(m => m.count > 0);
        fd.append('filesMeta', JSON.stringify(filesMeta));
        // (Phase 2) attach binaries
        Object.entries(files).forEach(([docKey, arr]) => {
          (arr || []).forEach(f => fd.append('files', f, `${docKey}__${f.name}`));
        });
        const r = await window.tourkitAuth.authedFetch('/api/v1/visa/score-wizard', {
          method: 'POST', body: fd
        });
        if (!r.ok) {
          const err = await r.json().catch(() => ({ error: 'HTTP ' + r.status }));
          throw new Error(err.error || ('HTTP ' + r.status));
        }
        const data = await r.json();
        // Map backend VisaResult → UI shape (rank A/B/C/D, winRate, summary, strengths, concerns)
        const rankFromLevel = data.level === 'cao' ? 'A' : data.level === 'trung_binh' ? 'B' : 'C';
        setResult({
          rank: rankFromLevel,
          winRate: data.passRate ?? 0,
          summary: data.summary || '',
          strengths: data.strengths || [],
          concerns: [...(data.weaknesses || []), ...(data.missingDocs || []).map(d => `Thiếu: ${d}`)],
          suggestions: data.suggestions || [],
        });
      } catch (e) {
        pushToast?.('Lỗi chấm điểm: ' + e.message, 'error');
      } finally { setSubmitting(false); }
    }

    let stepLabel;
    if (isQuestionStep) stepLabel = `Câu hỏi ${step + 1} trên ${total}`;
    else if (isUploadStep) stepLabel = 'Bổ sung hồ sơ';
    else stepLabel = 'Xem lại và chấm điểm';

    function discardDraft() {
      clearDraft();
      setStep(0); setAnswers({}); setFiles({}); setDraftBanner(false);
    }

    return (
      <main className="page vw-page">
        <div className="vw-shell">
          <button onClick={() => window.tourkitRouter.navigate('/visa/history')}
            style={{ display: 'inline-flex', alignItems: 'center', gap: 6, marginBottom: 14, background: 'none', border: 'none', padding: 0, color: 'var(--text-3, #64748b)', cursor: 'pointer', fontSize: 13, fontWeight: 600, fontFamily: 'inherit' }}>
            <Icon name="arrowLeft" size={14} stroke={2.2} /> Danh sách hồ sơ Visa
          </button>
          {draftBanner && (
            <div className="vw-draft-banner" role="status">
              <Icon name="refresh" size={12} stroke={2.4} />
              <span>Đã khôi phục bài làm dở (lưu tự động). Bấm <button className="vw-draft-link" onClick={discardDraft}>Bắt đầu lại từ đầu</button> nếu muốn xoá.</span>
            </div>
          )}
          <header className="vw-head">
            <div className="vw-head-meta">
              <div className="vw-head-title">Kiểm tra tỉ lệ đậu Visa</div>
              <div className="vw-head-step">{stepLabel}</div>
            </div>
            <div className="vw-stepper" aria-label="Tiến độ">
              {Array.from({ length: TOTAL_STEPS }, (_, i) => (
                <span key={i} className={'vw-stepper-dot' + (i <= step ? ' done' : '') + (i === step ? ' active' : '')} />
              ))}
            </div>
          </header>

          <section className="vw-sheet" key={step}>
            {isQuestionStep && <QuestionView
              index={step + 1}
              q={cur}
              value={answers[cur.id]}
              onChange={v => setAnswer(cur.id, v)}
              contactValue={cur.type === 'contact' ? answers.contact : null}
              onContactChange={setContactField}
            />}

            {isUploadStep && <UploadView
              suggested={suggested}
              files={files}
              onFilesChange={(key, fl) => setFiles(f => ({ ...f, [key]: fl }))}
            />}

            {isReviewStep && <ReviewView
              answers={answers}
              files={files}
              result={result}
              submitting={submitting}
              onSubmit={submit}
              onEdit={() => setStep(0)}
              questions={questions}
              pushToast={pushToast}
            />}
          </section>

          <footer className="vw-foot">
            <button className="vw-btn vw-btn-ghost" onClick={back} disabled={step === 0 || submitting}>
              <Icon name="arrowLeft" size={13} stroke={2.2} /> Quay lại
            </button>
            {!isReviewStep && (
              <button className="vw-btn vw-btn-primary" onClick={next}>
                {isUploadStep ? 'Xem lại và Chấm điểm' : 'Tiếp theo'}
                <Icon name="arrowRight" size={13} stroke={2.2} />
              </button>
            )}
            {isReviewStep && !result && (
              <button className="vw-btn vw-btn-primary" onClick={submit} disabled={submitting}>
                {submitting
                  ? <><span className="vw-spin" /> Đang chấm điểm</>
                  : <>Chấm điểm bằng AI <Icon name="sparkle" size={13} stroke={2.2} /></>}
              </button>
            )}
          </footer>
        </div>
      </main>
    );
  }

  function QuestionView({ index, q, value, onChange, contactValue, onContactChange }) {
    const num = pad2(index);

    if (q.type === 'radio') return (
      <div className="vw-q">
        <div className="vw-q-head">
          <div className="vw-q-num" aria-hidden="true">{num}</div>
          <div className="vw-q-text">
            <h2 className="vw-q-label">{q.label}</h2>
            {q.hint && <p className="vw-q-hint">{q.hint}</p>}
          </div>
        </div>
        <div className="vw-tiles">
          {q.options.map(opt => (
            <button key={opt} type="button"
              className={'vw-tile' + (value === opt ? ' selected' : '')}
              onClick={() => onChange(opt)}>
              <span className="vw-tile-mark" aria-hidden="true">
                {value === opt && <Icon name="check" size={12} stroke={3} />}
              </span>
              <span className="vw-tile-text">{opt}</span>
            </button>
          ))}
        </div>
      </div>
    );

    if (q.type === 'checkbox') {
      const arr = Array.isArray(value) ? value : [];
      const toggle = (opt) => onChange(arr.includes(opt) ? arr.filter(x => x !== opt) : [...arr, opt]);
      return (
        <div className="vw-q">
          <div className="vw-q-head">
            <div className="vw-q-num" aria-hidden="true">{num}</div>
            <div className="vw-q-text">
              <h2 className="vw-q-label">{q.label}</h2>
              {q.sub && <p className="vw-q-hint">{q.sub}</p>}
            </div>
          </div>
          <div className="vw-tiles">
            {q.options.map(opt => (
              <button key={opt} type="button"
                className={'vw-tile vw-tile-check' + (arr.includes(opt) ? ' selected' : '')}
                onClick={() => toggle(opt)}>
                <span className="vw-tile-mark" aria-hidden="true">
                  {arr.includes(opt) && <Icon name="check" size={12} stroke={3} />}
                </span>
                <span className="vw-tile-text">{opt}</span>
              </button>
            ))}
          </div>
        </div>
      );
    }

    if (q.type === 'contact') {
      const c = contactValue || {};
      return (
        <div className="vw-q">
          <div className="vw-q-head">
            <div className="vw-q-num" aria-hidden="true">{num}</div>
            <div className="vw-q-text">
              <h2 className="vw-q-label">{q.label}</h2>
              {q.sub && <p className="vw-q-hint">{q.sub}</p>}
            </div>
          </div>
          <div className="vw-fields">
            {q.fields.map(f => (
              <div key={f.id} className="vw-field">
                <label htmlFor={'f-' + f.id}>
                  {f.label}{f.required && <span className="vw-req"> *</span>}
                </label>
                <input id={'f-' + f.id} type={f.type}
                  value={c[f.id] || ''} placeholder={f.placeholder || ''}
                  onChange={ev => onContactChange(f.id, ev.target.value)} />
              </div>
            ))}
          </div>
        </div>
      );
    }
    return null;
  }

  // 6 file PDF mẫu để demo nhanh — generated by scripts/gen-visa-demo-files.py
  const DEMO_FILES = [
    { name: '01-ho-chieu-mau.pdf', url: '/demo/visa/01-ho-chieu-mau.pdf', label: 'Hộ chiếu mẫu' },
    { name: '02-cccd-mau.pdf', url: '/demo/visa/02-cccd-mau.pdf', label: 'CCCD mẫu' },
    { name: '03-so-bhxh-mau.pdf', url: '/demo/visa/03-so-bhxh-mau.pdf', label: 'Sổ BHXH mẫu' },
    { name: '04-sao-ke-ngan-hang-6-thang-mau.pdf', url: '/demo/visa/04-sao-ke-ngan-hang-6-thang-mau.pdf', label: 'Sao kê ngân hàng 6 tháng mẫu' },
    { name: '05-so-tiet-kiem-mau.pdf', url: '/demo/visa/05-so-tiet-kiem-mau.pdf', label: 'Sổ tiết kiệm mẫu' },
    { name: '06-giay-xac-nhan-cong-tac-mau.pdf', url: '/demo/visa/06-giay-xac-nhan-cong-tac-mau.pdf', label: 'Giấy xác nhận công tác mẫu' },
  ];

  function UploadView({ suggested, files, onFilesChange }) {
    return (
      <div className="vw-q">
        <div className="vw-q-head">
          <div className="vw-q-num" aria-hidden="true">10</div>
          <div className="vw-q-text">
            <h2 className="vw-q-label">Bổ sung hồ sơ kèm theo</h2>
            <p className="vw-q-hint">Dựa trên câu trả lời, AI gợi ý các giấy tờ bên dưới. Bỏ qua mục không có cũng được, AI vẫn chấm dựa trên thông tin đã điền (điểm sẽ thấp hơn).</p>
          </div>
        </div>

        {/* "Bộ hồ sơ mẫu" đã bỏ theo yêu cầu (VS3) */}
        <div className="vw-docs">
          {suggested.map(doc => {
            const list = files[doc.key] || [];
            return (
              <div key={doc.key} className={'vw-doc' + (doc.required ? ' required' : '')}>
                <div className="vw-doc-row">
                  <div className="vw-doc-info">
                    <div className="vw-doc-title">
                      {doc.label}
                      <span className={'vw-doc-tag' + (doc.required ? '' : ' vw-doc-tag-opt')}>
                        {doc.required ? 'BẮT BUỘC' : 'TUỲ CHỌN'}
                      </span>
                    </div>
                    <div className="vw-doc-desc">{doc.desc}</div>
                  </div>
                  <label className="vw-doc-upload">
                    <input type="file" multiple accept="image/*,.pdf"
                      onChange={ev => onFilesChange(doc.key, Array.from(ev.target.files || []))} />
                    <Icon name="paperclip" size={13} stroke={2.2} />
                    {list.length > 0 ? `${list.length} file` : 'Chọn file'}
                  </label>
                </div>
                {list.length > 0 && (
                  <div className="vw-files">
                    {list.map((f, i) => (
                      <div key={i} className="vw-file-pill">
                        <Icon name="paper" size={11} stroke={2.2} />
                        <span className="vw-file-name">{f.name}</span>
                        <span className="vw-file-sz">{(f.size / 1024).toFixed(0)} KB</span>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            );
          })}

          {/* VS3: ô upload tài liệu bổ sung khác — gửi kèm cho AI phân tích (ngoài các mục gợi ý) */}
          <div className="vw-doc">
            <div className="vw-doc-row">
              <div className="vw-doc-info">
                <div className="vw-doc-title">
                  Tài liệu bổ sung khác
                  <span className="vw-doc-tag vw-doc-tag-opt">TUỲ CHỌN</span>
                </div>
                <div className="vw-doc-desc">Thêm bất kỳ giấy tờ nào khác (hợp đồng lao động, sao kê lương, thư mời, lịch trình…) để AI phân tích kỹ hơn. Chọn được nhiều file.</div>
              </div>
              <label className="vw-doc-upload">
                <input type="file" multiple accept="image/*,.pdf"
                  onChange={ev => onFilesChange('extra', Array.from(ev.target.files || []))} />
                <Icon name="paperclip" size={13} stroke={2.2} />
                {(files['extra'] || []).length > 0 ? `${(files['extra'] || []).length} file` : 'Chọn file'}
              </label>
            </div>
            {(files['extra'] || []).length > 0 && (
              <div className="vw-files">
                {(files['extra'] || []).map((f, i) => (
                  <div key={i} className="vw-file-pill">
                    <Icon name="paper" size={11} stroke={2.2} />
                    <span className="vw-file-name">{f.name}</span>
                    <span className="vw-file-sz">{(f.size / 1024).toFixed(0)} KB</span>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      </div>
    );
  }

  function ReviewView({ answers, files, result, submitting, onSubmit, onEdit, questions, pushToast }) {
    const fileCount = Object.values(files).reduce((s, arr) => s + (arr?.length || 0), 0);
    if (result) return <ResultView result={result} onRetry={onEdit} answers={answers} pushToast={pushToast} />;

    return (
      <div className="vw-q">
        <div className="vw-q-head">
          <div className="vw-q-num" aria-hidden="true">11</div>
          <div className="vw-q-text">
            <h2 className="vw-q-label">Xem lại trước khi chấm điểm</h2>
            <p className="vw-q-hint">Kiểm tra nhanh thông tin. Bấm "Chấm điểm" để AI phân tích và đưa ra tỉ lệ đậu visa.</p>
          </div>
        </div>
        <div className="vw-summary">
          {questions.map(q => {
            const v = answers[q.id];
            if (!v) return null;
            let display;
            if (q.type === 'radio') display = v;
            else if (q.type === 'checkbox') display = (v || []).join(', ');
            else if (q.type === 'contact') display = Object.entries(v).filter(([_, vv]) => vv).map(([k, vv]) => `${k}: ${vv}`).join('  /  ');
            return (
              <div key={q.id} className="vw-summary-row">
                <div className="vw-summary-q">{q.label}</div>
                <div className="vw-summary-a">{display}</div>
              </div>
            );
          })}
          <div className="vw-summary-row vw-summary-files">
            <div className="vw-summary-q">Hồ sơ kèm theo</div>
            <div className="vw-summary-a">{fileCount > 0 ? `${fileCount} file` : 'Không có'}</div>
          </div>
        </div>
        <button className="vw-edit-link" onClick={onEdit}>
          <Icon name="edit" size={11} stroke={2.2} /> Quay lại sửa câu trả lời
        </button>
      </div>
    );
  }

  function ResultView({ result, onRetry, answers, pushToast }) {
    const tone = result.rank === 'A' ? 'good' : result.rank === 'B' ? 'fair' : 'poor';
    const [leadSending, setLeadSending] = _vS(false);
    const [leadSent, setLeadSent] = _vS(false);
    async function requestConsultation() {
      setLeadSending(true);
      try {
        const r = await window.tourkitAuth.authedFetch('/api/v1/visa/lead', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            contact: answers?.contact || {},
            country: answers?.country,
            answers,
            score: { rank: result.rank, winRate: result.winRate, summary: result.summary },
            requestedAt: new Date().toISOString()
          })
        });
        if (!r.ok) { const err = await r.json().catch(() => ({})); throw new Error(err.error || 'HTTP ' + r.status); }
        const data = await r.json();
        setLeadSent(true);
        pushToast?.(data.message || 'Đã ghi nhận, bộ phận visa sẽ liên hệ sớm.', 'success');
      } catch (e) { pushToast?.('Lỗi gửi yêu cầu: ' + e.message, 'error'); }
      finally { setLeadSending(false); }
    }
    return (
      <div className="vw-result">
        <div className={'vw-result-rank vw-result-' + tone}>
          <div className="vw-result-rate">{result.winRate}<span>%</span></div>
          <div className="vw-result-label">Tỉ lệ đậu dự kiến · Hạng {result.rank}</div>
        </div>
        <p className="vw-result-summary">{result.summary}</p>
        <div className="vw-result-cols">
          {result.strengths?.length > 0 && (
            <div className="vw-result-col">
              <h3 className="vw-result-h">Điểm mạnh</h3>
              <ul>{result.strengths.map(s => <li key={s}>{s}</li>)}</ul>
            </div>
          )}
          {result.concerns?.length > 0 && (
            <div className="vw-result-col vw-result-col-warn">
              <h3 className="vw-result-h">Cần lưu ý</h3>
              <ul>{result.concerns.map(s => <li key={s}>{s}</li>)}</ul>
            </div>
          )}
        </div>
        {result.suggestions?.length > 0 && (
          <div className="vw-result-col vw-result-suggest">
            <h3 className="vw-result-h">Đề xuất nâng tỉ lệ đậu</h3>
            <ul>{result.suggestions.map(s => <li key={s}>{s}</li>)}</ul>
          </div>
        )}
        <div className="vw-result-cta">
          {leadSent ? (
            <div className="vw-result-cta-done">
              <Icon name="check" size={14} stroke={3} />
              <span>Yêu cầu đã được ghi nhận. Bộ phận visa sẽ liên hệ trong 1-2 giờ làm việc.</span>
            </div>
          ) : (
            <button className="vw-btn vw-btn-primary vw-cta-lead" onClick={requestConsultation} disabled={leadSending}>
              {leadSending
                ? <><span className="vw-spin" /> Đang gửi…</>
                : <><Icon name="phone" size={13} stroke={2.2} /> Để bộ phận visa liên hệ tư vấn</>}
            </button>
          )}
        </div>
        <button className="vw-btn vw-btn-ghost" onClick={onRetry}>
          <Icon name="refresh" size={12} stroke={2.2} /> Làm lại từ đầu
        </button>
      </div>
    );
  }

  window.VisaPage = VisaPage;
})();
