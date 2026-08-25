# Quy ước và việc xuyên suốt

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Cross-cutting

**Cờ tính năng chưa ra mắt — `Features:*`, 1 nguồn ở [`FeatureFlags`](../TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs).**
KHÁC phân quyền: quyền trả lời "người này được xem gì", cờ trả lời "tính năng đã ra mắt chưa" — tắt là
tắt cho tất cả, kể cả admin. **Thiếu key = TẮT** (cố ý sai theo hướng an toàn: quên khai lúc deploy thì
tính năng bị ẩn — phiền nhưng sửa 1 dòng; mặc định bật thì thứ chưa ra mắt lọt thẳng ra bản public).

| Cờ | Che cái gì | Phụ thuộc |
|---|---|---|
| `Features:Digest` | Cụm bản tin: `sale-brief` · `ceo-brief` · `payment-watchdog` + Bảng tin | — |
| `Features:TourReadiness` | Tác vụ `tour-readiness` (kiểm tra sẵn sàng khởi hành) | **CẦN `Digest`** — nó ghi vào Bảng tin; bật riêng thì cảnh báo nằm đó không ai đọc được |
| `Features:MeetingBrief` | Action `prepare_meeting` (thẻ chuẩn bị gặp khách) | — |
| `Features:AnomalyWatchdog` | Tác vụ `anomaly-watchdog` (canh doanh thu bất thường) | **CẦN `Digest`** — ghi vào Bảng tin |
| `Features:AutoCare` | Tác vụ `customer-auto-care` (nhắc chăm lại khách ngủ quên) | **CẦN `Digest`** — ghi vào Bảng tin |
| `Features:Chat` | Hộp thư chat đa kênh: `/chat-inbox` + webhook 3 kênh + worker gửi + khai kết nối | — (có CSDL riêng, không ghi Bảng tin) |

⚠️ `AutoCare` là cờ **quan trọng nhất**: tính năng duy nhất của cả hệ đụng tới KHÁCH HÀNG THẬT. Mọi
thứ khác chỉ ghi vào Bảng tin cho người trong công ty đọc. Bản hiện tại **KHÔNG gửi gì cho khách** —
xem ghi chú trong [`CustomerAutoCareWorkflow`](../TourkitAiProxy.Services/Workflows/CustomerAutoCareWorkflow.cs): đo
thật thấy số điện thoại có ở 100/100 khách còn email chỉ 14/100, nên việc đúng với dữ liệu là **nhắc
nhân viên gọi**. Nếu sau này thêm khâu gửi, cờ này là chỗ chặn.

⚠️ **Riêng `Features:Chat`: KHÔNG chặn được bằng tiền tố `/api/v1/chat`.** `POST /api/v1/chat` và
`/api/v1/chat/stream` là **Trợ lý số liệu** — tính năng khác, không nằm sau cờ này; chặn cả cụm là giết
nhầm thứ đang chạy thật. Vì vậy nhánh tắt phải liệt kê đúng các nhóm đường của hộp thư chat, và danh
sách đó là **một nguồn** ở [`ChatInboxEndpoints.DuongRieng`](../TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs) dùng chung
cho cả nhánh bật lẫn nhánh tắt. Liệt kê tay ở `Program.cs` **đã lệch một lần** (thêm `/channels` và
`/messages/{id}/file` mà quên) — hai đường đó rơi vào `MapFallback` và trả `index.html` kèm **200**.
`ChatFeatureFlagCoverageTests` canh cả hai chiều: mọi route mới phải được phủ, và không được phủ nhầm
đường của Trợ lý số liệu.

⚠️ **Ẩn mục menu là CHƯA ĐỦ.** Route `/chat-inbox` trong [app.jsx](../wwwroot/app.jsx) phải tự gate: gõ tay
URL vẫn mở được trang, rồi trang gọi API nhận 404 và hiện lỗi kỹ thuật khó hiểu. Cờ tắt → render
`FeatureOffPage` nói rõ "chưa mở", **khác hẳn** trang "không có quyền" (quyền là chuyện riêng tài khoản;
cờ tắt là tắt cho tất cả, xin cấp quyền cũng vô ích).

**Tắt một tính năng phải chặn ở chỗ nó SINH RA, không phải chỗ nó chạy.** Workflow → không đăng ký DI
([`WorkflowStackRegistration`](../TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs)) nên scheduler + `GET
/api/v1/workflows` không thấy → thẻ tự mất khỏi giao diện. Action tool → gỡ khỏi danh mục gửi cho AI
(`ActionTools.Enabled(cfg)`) nên **AI không biết là có nó để mà gọi**; chặn lúc thực thi thôi là muộn,
AI đã hứa với người dùng rồi mới báo lỗi. Vẫn giữ chốt chặn thứ hai ở `ActionExecutor` cho tab mở từ
trước lúc tắt cờ — ném [`FeatureDisabledException`](../TourkitAiProxy.Services/Bootstrap/FeatureDisabledException.cs) →
**403**, KHÔNG để rơi vào bộ bắt lỗi chung thành 500 (nói sai với người dùng, và trộn cảnh báo giả vào
log lỗi thật).

Thêm cờ mới: thêm 1 method vào `FeatureFlags` → gate chỗ sinh ra → thêm 1 field vào `GET
/api/v1/features` (giao diện đọc qua [`window.tourkitFeatures`](../wwwroot/core/features.js)) → khai key ở
**CẢ** `appsettings.example.json` lẫn bản của worker. Action tool thì thêm 1 dòng vào `ActionTools.Gated`.

**Frontend reaches AI via `window.claude.complete` or `window.tourkit.ai.complete`/`completeStream`.** `core/ai-provider.jsx` shims `window.claude.complete` to delegate to `window.tourkit.ai`, which POSTs to `/api/v1/completions`. **ALL provider keys (OpenCode/9routes/OpenAI/Anthropic) live server-side** in `appsettings.json` (`Providers:{X}:ApiKey` or `Models:Primary/Review:ApiKey`) or env vars. The AI Settings UI lets users pick provider/model only — no key input. `localStorage["tourkit_ai_config"]` only holds `{provider, model, _v}` (v9). Bump `CONFIG_VERSION` in `ai-provider.jsx` when changing the shape. (Pre-v9: had client-side localStorage key store + dialog input — removed because operationally fragile; see v8→v9 migration comment.)

**Static files.** `UseStaticFiles` has `ServeUnknownFileTypes = true` + `DefaultContentType = "text/plain"` so `.jsx` loads without a registered MIME type. `.jsx`/`.js`/`.css`/`.html` are served with `Cache-Control: no-cache` so edits show on a plain reload.

**Cấu hình model AI — khai ĐỦ 14 feature, đừng để rơi ngầm.** `AiModelRegistry.Resolve` đi theo
`Models:{Feature}` → `Models:Primary` → default của provider. Nghĩa là **thiếu một khoá thì tính năng đó
âm thầm chạy bằng `Models:Primary`** — không log, không cảnh báo, chỉ hoá đơn cuối tháng biết. Đã dính
thật (14/08): appsettings prod thiếu `Models:MailClassify` nên phân loại mail chạy bằng `claude-haiku`
suốt, mà đó là task chạy **hàng trăm lần mỗi lần đồng bộ hộp thư**; `Models:Digest` cũng thiếu tương tự.
Danh sách 14 = enum `AiFeature` ([AiModelRegistry.cs](../TourkitAiProxy.Services/Providers/AiModelRegistry.cs)) — khai đủ
ở **CẢ** `appsettings.json` của web **VÀ** của worker (worker mới là nơi chạy `mail-auto-sync`,
`deal-auto-review`, `customer-auto-review`, `ceo-brief`).

⚠️ **Cấu hình đúng KHÔNG chứng minh được là nó đang chạy đúng.** Hai file appsettings nằm trên 2 máy,
đều gitignore, nên bản trên server có thể là bản cũ mà không chỗ nào lộ ra. Cách duy nhất biết chắc là
**đọc ngược từ log dùng thật**: [`scripts/check-model-drift.ps1`](../scripts/check-model-drift.ps1) gom
`dbo.AiUsageHistory` theo (feature, provider, model) rồi so với cấu hình web tại chỗ. Read-only, chạy
được mọi lúc. Đã bắt được thật (15/08): worker chạy `mail-auto-sync` bằng `claude-haiku-4-5` và 2 tác vụ
tự chấm bằng `ds/deepseek-chat` — model KHÔNG có trong cấu hình hiện tại, tức worker cầm bản cũ.
Lưu ý khi đọc kết quả: feature nào **trùng đúng `Models:Primary`** thì không phân biệt được là khai đúng
hay đang rơi ngầm về Primary — script đánh dấu riêng, đừng coi là đã xác nhận.

⚠️ Đổi provider cho một feature thì phải có khoá provider đó trong `Providers:*`. Thiếu khoá, provider
ném lỗi — mà vài chỗ **bắt lỗi rồi đi tiếp** (vd `MailClassifier` ghi Warning rồi trả nhóm `khac` cho
mọi thư, giao diện nhìn vẫn bình thường). Nên thiếu khoá còn tệ hơn chọn sai model. DeepSeek đi **qua
`nine-routes`** (`ds/deepseek-v4-flash`) chứ không gọi `api.deepseek.com` trực tiếp — khoá nine-routes
đã có sẵn và đã chạy thật cho Chat, `Providers:DeepSeek:ApiKey` thường để rỗng.

**Usage tracking trong SQL** `dbo.AiUsageCounters` (daily aggregate per-model, MERGE upsert). `UsageTracker.Track` fire-and-forget UPSERT (không block AI call); `Snapshot()` đọc cache in-mem 10s, miss → `UsageRepository.ReadAggregateAsync(30 ngày)` → SUM GROUP BY Model. Cross-process: 2 instance cùng SQL share counter tự động. Cost estimate hardcode DeepSeek V4 Pro retail ($0.27/$1.10 per Mtok) bất kể model. Streaming chỉ Track khi `outTok > 0`. Key dạng `"{providerId}:{model}"`.

**Tenant AI quota** ([Services/Quota/TenantQuotaStore.cs](../TourkitAiProxy.Services/Quota/TenantQuotaStore.cs)). Mỗi tenant mặc định 1000 lượt AI (lĩnh 1 lần, KHÔNG tự reset). Storage: in-mem `ConcurrentDictionary` source of truth + ghi đè file `data/tenant-quota.json` mỗi lần thay đổi + mirror Redis best-effort (cross-instance visibility). Provider check ở đầu `CompleteAsync`/`StreamAsync` (5 providers — `EnsureQuota()`); consume ở `LogUsage`/sau khi `_usage.Append` khi status=ok và có tenant. Hết quota → throw `QuotaExhaustedException` → middleware [`QuotaExceptionMiddleware`](../TourkitAiProxy.Services/Quota/QuotaExceptionMiddleware.cs) convert → 429 JSON `{error, quota}`. Frontend: chip `.tb-quota` ở topbar (`AI <used>/<limit>`), warn ở 90%, pulse đỏ ở 100%. Endpoints: `GET /api/v1/quota` (user), `GET /api/v1/admin/quota` + `POST /api/v1/admin/quota/{tenant}/topup` (admin gate qua `Admin:Token` config). System calls không có tenant (no session) → skip check.

**Cost UI hidden by default.** Menu "Chi phí AI" + page `/ai-usage` chỉ hiện khi user toggle debug ON (icon info ở topbar). URL `/ai-usage` vẫn accessible trực tiếp (giữ cho admin xem nhanh).

**CORS is wide open in dev.** `CorsSetup.cs` lists allowed origins but calls `SetIsOriginAllowed(_ => true)`, which overrides the allowlist. Remove that line before production.

## Conventions

- User-facing strings, log messages, comments, and README are in Vietnamese — preserve that when editing.
- `appsettings.json` currently contains real-looking API keys. Treat them as secrets: don't echo them, and prefer env vars (e.g. `Providers__OpenCode__ApiKey`, `OPENCODE_API_KEY`, `NINE_ROUTES_API_KEY`) for any production-bound change.
- Frontend exposes singletons via `window.tourkit*` namespaces (`tourkit.ai`, `tourkitStorage`, `tourkitParsers`, `tourkitRouter`, `tourkitHistory`, `tourkitHooks`, `tourkitUtil`).
- **DateTime = UTC, luôn kèm `Z`** (STRICT — xem [docs/datetime-convention.md](datetime-convention.md)). Lưu DB bằng `DateTime.UtcNow` / SQL `SYSUTCDATETIME()` (KHÔNG `DateTime.Now`/`GETDATE()`). Parse chuỗi ngày để lưu → `DateTimeStyles.AssumeUniversal | AdjustToUniversal` (TryParse trần ra `Kind=Local` → lưu sai). Trả client: field `DateTime` tự có `Z` qua [`UtcDateTimeConverter`](../TourkitAiProxy.Shared/Json/UtcDateTimeConverter.cs) (global); chuỗi `ToString("o")` từ SQL phải `DateTime.SpecifyKind(x, DateTimeKind.Utc)` trước (Dapper đọc DATETIME2 ra `Kind=Unspecified` → thiếu `Z` → frontend lệch +7h). Frontend dùng `window.tourkitUtil.fmtAgo/fmtDate`, không tự cộng/trừ giờ.
- **Viết tài liệu hướng dẫn người dùng** (`docs/features/*.md`): dùng agent [`tourkit-doc-writer`](../.claude/agents/tourkit-doc-writer.md). Quy tắc: ưu tiên sự rõ ràng, dễ hiểu hơn chi tiết kỹ thuật; dùng CodeGraph kiểm flow THẬT trước khi viết + tham khảo internal knowledge base (claude-memory-compiler, nếu có) để giải thích ngắn gọn "tại sao"; mỗi trang tối thiểu có **Mô tả / Hướng dẫn từng bước / Lưu ý / FAQ**; luôn viết tiếng Việt, giọng thân thiện; viết xong **đề xuất các ảnh chụp màn hình cần bổ sung**.
- **CHANGELOG.md — BẮT BUỘC cập nhật mỗi lần public code** (STRICT). Bất cứ khi nào chuẩn bị phát hành (merge vào `main`/`dev`, tạo bản release, hoặc user nói "public/ra mắt/deploy"), PHẢI thêm/cập nhật một mục trong [`CHANGELOG.md`](../CHANGELOG.md) mô tả **tính năng mới** + **lỗi đã sửa** của đợt đó. Nếu một thay đổi có ảnh hưởng tới người dùng mà chưa có dòng trong CHANGELOG → coi như **chưa xong**, đừng phát hành.
  - **Viết CHO NGƯỜI DÙNG CUỐI, không phải cho dev**: mô tả theo *trải nghiệm người dùng* ("Bạn có thể…", "Trước đây … nay …"). TUYỆT ĐỐI không đưa mã commit/SHA, tên file/hàm/class, tên bảng SQL, hay thuật ngữ kỹ thuật (Dapper, TINYINT, race, token…) vào CHANGELOG.
  - **Mỗi mục** = tiêu đề `## Phiên bản dd/MM/yyyy — <tên ngắn>`, rồi `### ✨ Tính năng mới`, `### 🔧 Đã khắc phục` (nói rõ *người dùng gặp vấn đề gì, nay hết thế nào*), tùy chọn `### 📌 Lưu ý` / `## 🔜 Sắp có`. Mới nhất ở TRÊN CÙNG. Tiếng Việt, giọng thân thiện.
  - Chi tiết kỹ thuật/nội bộ (SHA, tên hàm, lý do sâu) để trong commit message hoặc plan/spec — KHÔNG để trong CHANGELOG.
