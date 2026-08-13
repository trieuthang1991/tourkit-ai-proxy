# Changelog

Ghi lại các thay đổi đáng chú ý theo từng phiên bản. Mới nhất ở trên cùng.
Tính năng đợt này thuộc spec **Bản tin AI (Đợt 1 + Đợt 2 · C5)** —
[digest-insight design](docs/superpowers/specs/2026-08-11-dot1-digest-insight-design.md) ·
[persona roadmap (C5)](docs/superpowers/specs/2026-08-11-ai-agent-personas-research.md) ·
[plan C5](docs/superpowers/plans/2026-08-13-c5-listen-brief-travai.md).

---

## [2026.08.13] — C5: Nghe bản tin qua TRAVAI + luật "1 người 1 loại bản tin"

**Tóm tắt:** Người dùng bấm 1 nút để **nghe** bản tin sáng (sale-brief / ceo-brief) đọc bằng giọng
máy — tái dùng hạ tầng TTS server sẵn có, đứng trên nội dung bản tin đã có trong Bảng tin. Kèm chốt
quy tắc: mỗi người chỉ nhận **1 loại** bản tin theo vai trò.

### Tính năng mới / thay đổi (checklist)
- [x] **Luật 1 loại/người** — mỗi người theo vai trò chỉ nhận `sale-brief` **HOẶC** `ceo-brief`.
      Bật loại này thì server tự tắt loại kia (`DigestSubscriptionRepository.DeactivateOthersAsync`,
      enforce trong `PUT /api/v1/digest/subscriptions/{briefType}` — không tin client) + hint trên UI
      (`digest.jsx`). _(commit `6a405c8`)_
- [x] **BriefNarration.ToSpeakable** — chuyển nội dung bản tin (markdown-lite: `**đậm**`, gạch đầu
      dòng, emoji, `1.234đ`, `%`, `·`) thành lời đọc sạch cho TTS. Hàm thuần, **10 test**. _(commit `e856827`)_
- [x] **`GET /api/v1/insights` trả thêm `speakText`** — item bản tin (sale/ceo) kèm lời đọc đã làm
      sạch markdown/emoji; loại khác (cảnh báo…) = `null`. _(commit `06fe3f4`)_
- [x] **`window.tourkitTts`** (`wwwroot/lib/tts.js`) — helper phát giọng đọc dùng chung: gọi
      `POST /api/v1/speech/tts`, phát qua 1 phần tử `<audio>` (an toàn iOS), an toàn re-entrancy
      (generation-token + hủy fetch cũ). Thêm icon `volume` / `stop`. _(commit `ed0d016`)_
- [x] **Nút "Nghe" trong Bảng tin** (`insights.jsx`) — trạng thái Nghe → Đang tải… → Dừng; lỗi TTS
      hiện toast; không kích hoạt "đánh dấu đã đọc" khi bấm. _(commit `1df998e`)_
- [x] **CSS** cho hint 1-loại + nút Nghe (trạng thái đang phát) — dùng lại token thiết kế sẵn có.
      _(commit `3744df7`)_
- [x] **Ops:** tạo template email `daily-brief` trong `/admin-trav-ai → Mail Templates`
      (subject `{{title}}`, body dùng `{{bodyHtml}}` — đã escape sẵn ở proxy). Thiếu template thì
      worker vẫn fallback render từ `Params`.

### Tài liệu
- [x] Cập nhật `CLAUDE.md` (API surface `/insights` + mục Bản tin AI). _(commit `ef825ef`)_
- [x] Plan đầy đủ: `docs/superpowers/plans/2026-08-13-c5-listen-brief-travai.md`. _(commit `a1b0838`)_

### Đã kiểm thử (đợt test này) ✅
- [x] **Unit test:** 509 pass / 0 fail / 2 skip (gồm 10 case `BriefNarration`).
- [x] **E2E `features-digest.ps1`:** 30 pass / 0 fail — chạy trên phiên thật (`staging.tourkit.vn`),
      gồm 2 assertion C5 mới: (a) bật ceo-brief → sale-brief tự tắt; (b) item bản tin có `speakText`
      đã bỏ `**`. _(commit `02cf892`)_
- [x] **Build:** web 0 error · worker 0 error.
- [x] **Prod bundle:** `lib/tts.js` vào `app.bundle.js` (grep `tourkitTts` OK).

### ⏳ Còn phải test (thủ công — sau đợt test tự động)
- [ ] **Nghe phát tiếng thật (end-to-end):** E2E mới kiểm *có* `speakText`, **chưa** kiểm audio phát
      thật. Cần cấu hình `Speech:Tts:Provider` (vbee/google/edge/piper/openai) rồi bấm Nghe nghe được tiếng.
- [ ] **iOS Safari:** lần đầu `play()` sau fetch có thể bị chặn (gesture) → hiện "chạm lại để nghe".
      Cần thử trên iPhone thật; nếu dùng nhiều, cân nhắc port cơ chế unlock đầy đủ của `jarvis.jsx`.
- [ ] **Email `daily-brief`:** render đẹp trên Gmail / Outlook (inline CSS, không webfont) — template
      vừa tạo, chưa soi mắt thật. Kiểm cả `{{bodyHtml}}` KHÔNG bị escape 2 lần.
- [ ] **Gửi bản tin sáng thật:** worker drain `dbo.OutboundMails` → SMTP đúng giờ người nhận chọn.
- [ ] **UI 1-loại:** click tay — bật loại này thì card loại kia tự về "tắt" sau khi Lưu; hint hiển thị.
- [ ] **Trạng thái nút `.is-on`** (đang phát) hiển thị đúng màu accent.
- [ ] **Đa thiết bị / mobile:** nút Nghe trên layout mobile của Bảng tin.

### Cấu hình cần có để chạy đủ
- `Speech:Tts:Provider` (+ creds engine tương ứng) — nếu trống, nút Nghe báo "không engine TTS khả dụng".
- `Telegram:BotToken` (kênh Telegram) · `Models:Digest` (thiếu → kế thừa `Models:Primary`).
- Template `daily-brief` (đã tạo đợt này).

### Ngoài phạm vi đợt này (để dev review riêng)
- Nhánh **`TK-4385`** (SSO đăng nhập từ TourkitERP → TRAVAI): **CHƯA merge** — đã đánh giá kỹ thuật
  khả thi (merge sạch, build 0 error, test 509 pass) nhưng cần dev soi (SsoCodeStore InMemory 1-process,
  nửa đối ứng phía CRM, điều kiện phiên đã có, cấu hình secret).
