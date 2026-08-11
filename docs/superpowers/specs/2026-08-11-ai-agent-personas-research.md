# Nghiên cứu AI Agent theo persona: Sale / CEO / TourOperation

**Ngày:** 2026-08-11 · **Loại:** roadmap kỹ thuật nội bộ (KHÔNG phải spec triển khai — mỗi tính năng khi làm sẽ có spec + plan riêng theo quy trình)
**Trạng thái:** đã duyệt cấu trúc + nội dung từng chương với người dùng.

## Mục tiêu & ràng buộc đã chốt

- **Deliverable:** 1 doc nghiên cứu cả 3 persona để chọn thứ tự làm — chưa design chi tiết tính năng nào.
- **Đối tượng đọc:** nội bộ dev — mỗi tính năng nêu rõ nền tận dụng, effort thật, rủi ro/gap.
- **Trần tự chủ:** agent **tự làm việc an toàn** (phân tích/tổng hợp/nhắc trong app); mọi hành động **hướng ngoại** (gửi mail khách/NCC, ghi CRM) → soạn sẵn + người duyệt, hoặc enqueue (giữ nguyên tắc confirm-first + `CrmActionQueue` sẵn có).
  - **Ngoại lệ duy nhất (đã duyệt):** S6 Auto-Care được chạy **mức 2 — auto-send template duyệt sẵn**: tenant duyệt template 1 lần, AI chỉ điền biến, tự gửi theo hạn mức + log + kill-switch per kịch bản. Kịch bản chưa duyệt template → rơi về hàng đợi duyệt (mức 1). Tiền lệ trong hệ thống: `mail-auto-sync` auto-reply opt-in.

## Bối cảnh thị trường (nghiên cứu 08/2026)

Chuyển dịch lớn nhất 2026: **từ AI "gợi ý" sang AI "hành động"** — agent tự qualify lead, tự follow-up, tự book meeting ([Lindy](https://www.lindy.ai/blog/ai-agents-sales), [monday CRM](https://monday.com/blog/crm-and-sales/ai-lead-follow-up/)). Với ngành tour: báo giá chiếm ~40% thời gian nhân viên; tour operator là nhóm hưởng lợi lớn nhất vì sản phẩm nhiều NCC nhất → nhiều thao tác thủ công nhất ([Orchestra Intelligence](https://www.orchestraintelligence.fr/en/blog/agents-ia-agences-voyage-2026), [Tredence](https://www.tredence.com/blog/ai-agents-for-travel)). Phía điều hành/CEO: mọi platform BI lớn đã có agent tự quét báo cáo, phát hiện bất thường, giải thích "tại sao số lệch", gửi digest theo lịch ([noimos](https://noimosai.com/en/blog/the-7-best-ai-agents-for-data-analysis-in-2026-proactive-insights-and-autonomy), [Improvado](https://improvado.io/blog/top-ai-reporting-tools)).

---

## Chương 1 — Nền hiện có (điểm xuất phát, KHÔNG xây lại)

| Persona | Đã có | Khoảng trống chính |
|---------|-------|--------------------|
| Sale | Chấm deal + cảnh báo deal nguội, đánh giá KH A–D, Hộp thư AI (phân loại + soạn trả lời), giao việc/lịch hẹn qua trợ lý (confirm-first), báo giá tour wizard | AI **chủ động**: "sáng nay gọi ai trước", đeo bám báo giá, mail hỏi tour → tự tạo deal, tự chăm sóc KH |
| CEO | Trợ lý số liệu (hỏi–đáp pull) + TRAVAI voice | Toàn bộ là pull. Thiếu **push**: bản tin sáng, phát hiện bất thường, truy "tại sao", dự phóng |
| TourOperation | Tour Builder, giá NCC, `/api/ai/departures` ("Điều hành khởi hành") có sẵn upstream | Chưa có agent nào: readiness khởi hành, nhắc NCC, canh thanh toán/slot, tổng kết sau tour |

Hạ tầng agent dùng chung đã chạy production: `WorkflowSchedulerService` (tick 60s, PerUser/PerTenant, auto-pause), `TenantServiceAccounts` (login nền không cần user online), `AiCallContext.Push` (quota + log per feature — STRICT), `OutboundMails` (hàng đợi mail, worker app-side gửi), `CrmActionQueue` (ghi CRM qua enqueue, không POST thẳng), `ActionExecutor` (thực thi hành động idempotent), TRAVAI TTS/STT.

Upstream `/api/ai/*` (toutkit-app, đã kiểm bằng codegraph 2026-08-11): financial-summary, cashflow, marketing, **departures**, top-customers, top-sellers, tours (+ POST tạo GIT), booking-tickets (+ batch context). `BookingListItem` mang đủ `Revenue/ActualRevenue/TotalExpense/DepartureDate/EndDate/Status/StatusCloseTour/AmountAdults` → phần lớn tính năng dưới đây **có data sẵn 100%**.

---

## Chương 2 — AI Agent for Sale (6 tính năng)

| # | Tính năng | Ý tưởng | Nền tận dụng | Effort | Gap/Rủi ro |
|---|-----------|---------|--------------|--------|------------|
| S1 | **Bản tin sáng "Hôm nay làm gì trước"** | Mỗi sáng gom per-sale: deal nguội cần gọi, báo giá chưa phản hồi, mail hỏi tour chưa trả lời, lịch hẹn hôm nay, KH hạng A lâu không chăm → 1 card xếp ưu tiên | Deal cooling + Reviews + SmartMail + `/api/ai/appointments`; chạy trên F2 (Digest Engine), hiển thị qua F1 (Insight Feed) | **M** | Data sẵn 100% |
| S2 | **Mail hỏi tour → tự tạo cơ hội** | Mail phân loại `hoi_dat_tour`/`xin_bao_gia` → trích tên/SĐT/nhu cầu → đề xuất tạo booking-ticket (confirm-first, enqueue) | MailClassifier + ActionResolver + CrmActionQueue | **M** | Cần thêm kind `create_deal` vào CrmActionQueue + worker app-side xử lý |
| S3 | **Đeo bám báo giá** | Báo giá gửi N ngày chưa hồi âm → soạn sẵn mail nhắc theo tone, sale duyệt gửi | MailReplyService + OutboundMails + **`dbo.TourQuotes` đã server-side** (`CreatedBy/UpdatedAt`); có thể auto-send template (F4) | **M** | Thêm 2 cột `SentAt`/`ReplyStatus` vào TourQuotes (idempotent migration — việc nhỏ). Xem mục "Phân tích sâu" |
| S4 | **Thẻ chuẩn bị gặp khách** | Trước lịch hẹn X giờ: gom review KH, lịch sử deal, mail gần nhất → talking points | Reviews + Mails + booking-context batch endpoint (upstream có sẵn) | **S-M** | — |
| S5 | **Vệ sinh pipeline** | Quét deal thiếu ngày hẹn tiếp / kẹt 1 trạng thái quá lâu / thiếu giá trị → dòng nhắc trong S1 | Deal repo sẵn — rule thêm vào S1, không phải feature độc lập | **S** | — |
| S6 | **Tự chăm sóc khách (Auto-Care)** — MỨC 2 đã duyệt | Kịch bản: sinh nhật, hỏi thăm sau tour + xin đánh giá, đánh thức KH ngủ quên (hạng A/B im lặng X tháng), chúc Tết/lễ theo lô. Template duyệt sẵn → auto-send có hạn mức; kịch bản khác → hàng đợi duyệt (duyệt từng cái hoặc cả lô) | SMTP Gmail sẵn (SmartMail) + F4 (Template Store + guard) + EndDate departures | **M-L** | Field ngày sinh KH trong `/api/ai/customers` — cần kiểm; Zalo ZNS chỉ có stack cũ — muốn chăm qua Zalo phải mở kênh (gap) |

Xương sống chương: **S1** — S3/S5/S6 là nguồn cấp thêm dòng/kịch bản vào cùng bản tin + feed.

---

## Chương 3 — AI Agent for CEO (5 tính năng)

| # | Tính năng | Ý tưởng | Nền tận dụng | Effort | Gap/Rủi ro |
|---|-----------|---------|--------------|--------|------------|
| C1 | **Bản tin điều hành sáng/tuần** | Doanh thu–chi phí–lợi nhuận hôm qua & lũy kế tháng vs cùng kỳ, top biến động, deal lớn mới, cảnh báo dòng tiền. AI viết prose ngắn; **số tính server-side, AI không bịa số** | `ChatTools` + `BuildChatData` + F2 (PerTenant, service account). Kênh: F1 + mail | **M** | Data sẵn 100% |
| C2 | **Watchdog bất thường** | So metric với baseline trượt 4–8 tuần; lệch quá ngưỡng → cảnh báo + AI giải thích khả năng nguyên nhân (tự drill cashflow/marketing/top-*) | F3 (`dbo.MetricSnapshots`) + F1 | **M-L** | **Phụ thuộc F3** — không có lịch sử metric thì "bình thường" không định nghĩa được |
| C3 | **Hỏi "tại sao?" (root-cause)** | "Tại sao lợi nhuận giảm?" → agent tự gọi nhiều section, so 2 kỳ, chỉ cấu phần đổi lớn nhất | Mở rộng planner đa bước có kiểm soát — cùng pattern `ResolveMarketAsync` | **M** | Nâng cấp planner riêng, tách khỏi các đợt đầu |
| C4 | **Dự phóng cuối tháng/quý** | Run-rate + departures đã chốt → "khả năng đạt X% kế hoạch". **Thống kê đơn giản, KHÔNG ML** | `cashflow` + `departures` + F3 | **S-M** | Cần chỗ nhập "kế hoạch/target" per tenant (bảng nhỏ) |
| C5 | **Nghe bản tin qua TRAVAI** | "Đọc bản tin sáng" khi di chuyển — reuse C1 + TTS | TRAVAI + Speech stack nguyên vẹn | **S** | Sau C1 |

Chuyển CEO từ *pull* (phải hỏi) sang *push* (được báo). **C1 dùng chung Digest Engine với S1** — xây 1 lần.

---

## Chương 4 — AI Agent for TourOperation (5 tính năng)

| # | Tính năng | Ý tưởng | Nền tận dụng | Effort | Gap/Rủi ro |
|---|-----------|---------|--------------|--------|------------|
| O1 | **Kiểm tra sẵn sàng khởi hành (D-7/D-3/D-1)** | Quét tour sắp đi, chấm checklist: thanh toán đủ chưa (`ActualRevenue < Revenue`), hồ sơ visa thiếu (tour_type 102), đủ khách tối thiểu chưa → card xếp theo ngày khởi hành | `/api/ai/departures` sẵn + booking detail đủ field | **M** | — |
| O2 | **Watchdog thanh toán trước khởi hành** | Khách sắp đi còn nợ → nhắc sale phụ trách + kế toán (feed F1 + giao việc `CrmActionQueue`) | `Revenue/ActualRevenue/DepartureDate` sẵn 100%. **Rule thuần — không tốn quota AI** (AI chỉ khi soạn lời nhắc) | **S** | — |
| O3 | **Nhắc NCC (supplier chasing)** | Dịch vụ gần khởi hành NCC chưa xác nhận → soạn mail nhắc, ops duyệt (hoặc template auto-send F4) | MailReplyService + OutboundMails | **M-L** | **Gap phải kiểm:** CRM có lưu trạng thái "NCC đã xác nhận" per dịch vụ không — chưa có thì upstream phải mở field/endpoint trước |
| O4 | **Tổng kết sau tour** | Tour kết thúc → so lãi/lỗ thực tế vs dự kiến, nhắc đóng tour (`StatusCloseTour`), kích hoạt kịch bản "hỏi thăm sau tour" của S6 | Field close-tour + expense/revenue sẵn; nối vòng đời sang Auto-Care | **M** | — |
| O5 | **Canh slot GIT (đầy/vắng)** | Tour ghép gần khởi hành quá vắng (cân nhắc hủy/dồn) hoặc sắp đầy (đẩy bán nốt) → cảnh báo ops + gợi ý sale | **`TourDtos.Slots/Booked/OnHold/Available` có sẵn** (đã kiểm code 11/08) | **S-M** | ~~Gap capacity~~ ĐÓNG — đưa vào Đợt 3 |

Lõi chương: **O1+O2** — chạy ngay bằng data sẵn. O4 nối sang S6 thành chu trình khép kín: bán → vận hành → chăm sóc lại.

---

## Phân tích sâu: "Bản tin sáng" — và bài toán "gửi cho ai" (kiểm chứng bằng code + DB thật, 2026-08-11)

Hai câu hỏi kiểm chứng roadmap (từ review nội bộ): *bản tin sáng có chạy được thật không, và hệ thống lấy đâu ra khái niệm "ai là giám đốc, ai là sale" để gửi?* Mọi khẳng định dưới đây đã **đối chiếu code thật** (codegraph trên cả proxy lẫn toutkit-app, 11/08/2026) — không suy đoán.

### 1. Nhận diện vai trò — hạ tầng quyền ĐÃ CHẠY, chỉ thiếu "sổ người nhận"

Đã kiểm chứng: `TkSession.Permissions` lưu SQL (`dbo.TkSessions.PermissionsJson`, lấy lúc login qua `GetPermissionsAsync`), `TkSessionStore.HasPermission(sessionId, code)` + `EnsurePermissionsAsync` **đang được ActionExecutor/WorkflowEndpoints dùng production**. `TkPermissionCodes` hiện khai 3 mã (`CV_TAOMOI`, `CS_KH_TAOMOI`, `CH_HT_XEM`). Upstream `PermissionCodes.cs:242-244`: `CH_XEM_ALL` = thấy toàn bộ, `CH_XEM` (không ALL) = chỉ thấy của mình.

Giải pháp **F5 — Digest Subscriptions**:
- User **tự đăng ký bản tin** trên `/workflows` (pattern per-user config y hệt `mail-auto-sync`): chọn `sale-brief` / `ceo-brief` / `ops-brief`.
- **Gate `ceo-brief` = `HasPermission(CH_XEM_ALL)`** — đúng semantics "người được thấy toàn bộ" của CRM, chỉ cần thêm 1 const vào `TkPermissionCodes`. KHÔNG hardcode vai trò, không cần CRM thêm gì.
- **Kênh nhận user tự khai**: email (nhập + xác minh mã), Telegram (link bot → `/start` → lưu chat id), Zalo khi có OA. Bắt buộc tự khai vì `/api/ai/reference` sellers chỉ trả `{id, name}` — CRM không expose email/SĐT user (gap #7).
- Tùy chọn: admin tenant đăng ký hộ/gán cho nhân viên.

### 2. Data chạy bản tin — ai fetch, bằng quyền gì

- **`ceo-brief` (C1): đáp ứng NGAY.** Service account per-tenant (`CH_XEM_ALL`) — pattern deal-auto-review sẵn; so cùng kỳ = gọi 2 kỳ (compare logic đã có).
- **`sale-brief` (S1):** mặc định service account kéo toàn bộ rồi **lọc theo người nhận** — `BookingTicketSearchRequest.SellerId` có sẵn ✅; lịch hẹn: `GetAiAppointmentsAsync` có `DateFilter=1` ("Hôm nay") + mỗi item mang `Assignee` ✅ → lọc phía proxy. Không phụ thuộc user từng login proxy. Fallback: session user trong `TkSessionStore` (tự re-login).

### 3. Soi từng dòng bản tin sáng Sale — mức đáp ứng Đợt 1 (đã đối chiếu schema thật)

| Dòng bản tin | Bằng chứng code/DB | Đợt 1? |
|---|---|---|
| Deal nguội cần gọi | `dbo.DealScores` + cooling logic sẵn (có assignee) | ✅ |
| Lịch hẹn hôm nay | `GetAiAppointmentsAsync` `DateFilter=1` + `Assignee` per item | ✅ |
| KH hạng A lâu không chăm | `dbo.Reviews` + lần mua cuối | ⚠️ mức thô: "không có booking mới X ngày" |
| Báo giá cần đeo bám | **`dbo.TourQuotes` ĐÃ server-side** (`CreatedBy`, `UpdatedAt`) — khác nhận định ban đầu "client-side" | ⚠️ Đợt 1 mức thô: "báo giá tạo N ngày chưa cập nhật"; "khách chưa phản hồi" đúng nghĩa cần thêm 2 cột `SentAt`/`ReplyStatus` (S3 — nhỏ hơn dự tính, hạ effort M-L → **M**) |
| Mail hỏi tour chưa trả lời | `dbo.Mails` per-tenant (hộp thư chung, chưa gán per-sale) | ⚠️ mức "công ty còn N mail chờ"; gán người cần assign-to-staff (Phase 2 SmartMail, gap #9) |

**Kết luận:** Đợt 1 phát hành `sale-brief` với 2 dòng chuẩn + 3 dòng mức thô — đủ giá trị dùng hằng ngày. `ceo-brief` (C1) và O2 đáp ứng đầy đủ ngay. Trước khi code S1 phải làm F5 (không có sổ người nhận thì không có "gửi cho ai").

### 4. Gap đóng được nhờ kiểm chứng code thật (11/08/2026)

- ~~Gap #1 ngày sinh KH~~ **ĐÓNG**: `AiCustomerDtos.Birthday` + filter `CustomerDtos.BirthdayThisMonth` có sẵn upstream → kịch bản sinh nhật S6 chạy ngay.
- ~~Gap #3 capacity tour~~ **ĐÓNG**: `TourDtos` có `Slots/Booked/OnHold/Available` → **O5 chuyển từ "Chờ xác minh" sang Đợt 3**.
- ~~Gap #8 filter lịch hẹn theo seller~~ **ĐÓNG**: `DateFilter=1` + `Assignee` per item.
- ~~Gap #4 báo giá client-side~~ **SAI, sửa lại**: `dbo.TourQuotes` đã persist server (bảng #6, có `CreatedBy/UpdatedAt/IsSync`); chỉ thiếu cột trạng thái gửi/phản hồi.
- Còn mở thật sự: NCC confirm (#2), email user không expose (#7), mail chưa gán per-sale (#9), chỗ nhập target doanh thu (#6).

### 5. Số liệu volume THẬT (production DB, chạy `digest-feasibility-stats.ps1` 11/08/2026)

| Nguồn | Số thật | Kết luận |
|---|---|---|
| `dbo.TkSessions` | 11 phiên / 7 tenant; **10/11 phiên có `PermissionsJson`** | Gate quyền `ceo-brief` khả thi ngay |
| `dbo.TenantServiceAccounts` | **5/7 tenant** đã cấu hình | `ceo-brief` chạy được ngay cho 5 tenant |
| `dbo.DealScores` / `dbo.Reviews` | **1.751 / 2.978** bản ghi | Bản tin sáng CÓ nội dung ngay ngày đầu (deal nguội + KH hạng A dày) |
| `dbo.UserWorkflows` | 13 config, **11 enabled** (4 loại workflow đang chạy thật) | F2 cắm vào scheduler là chạy, user đã quen bật workflow |
| `dbo.TourQuotes` | **28** quote, admin chiếm 26, mới nhất 03/08 | ⚠️ **S3 hạ ưu tiên**: wizard báo giá chưa được sale dùng thật → "đeo bám báo giá" hiện không có gì để nhắc. Điều kiện tiên quyết: tăng adoption wizard trước |
| `dbo.Mails` | 1.127 mail: `khac` 551 + `spam` 520 = **95%**; `xin_bao_gia` 6, `hoi_dat_tour` **0** | ⚠️ **S2 hạ ưu tiên + thêm việc mới**: hiện KHÔNG có input thật cho "mail → cơ hội"; 551 mail `khac` đáng ngờ → **audit MailClassifier** (có thể phân loại kém chứ không phải không có mail hỏi tour) trước khi xây S2 |

**Điều chỉnh lộ trình theo số thật:** S1 + C1 + O2 (Đợt 1) càng được củng cố — data dày sẵn. S2/S3 (Đợt 2/3) bị số thật hạ ưu tiên: S2 chờ audit classifier, S3 chờ adoption wizard. Việc mới chèn vào backlog: **audit chất lượng MailClassifier trên 551 mail `khac`**.

---

## Chương 5 — Nền tảng chung: xây 1 lần, 3 persona cùng dùng

| # | Mảnh nền | Là gì | Ai dùng |
|---|----------|-------|---------|
| F1 | **Insight Feed** — `dbo.AgentInsights` + UI feed | Bảng per-tenant/per-user `{kind, severity, title, body, actions[], đọc/chưa}` + badge chuông + trang feed. **Mọi agent ghi cảnh báo/bản tin vào đây** — không feature nào tự chế chỗ hiển thị riêng | Tất cả S/C/O |
| F2 | **Digest Engine** — `IScheduledWorkflow` mới | Khung "gom nhiều nguồn → AI viết prose → phát đa kênh": F1 / mail / TRAVAI đọc / **chat ngoài (Telegram Bot, Zalo)**. Config per-user (Sale) hoặc per-tenant (CEO). Đăng ký trong `WorkflowStackRegistration` (web + worker cùng pickup). Kênh chat: **Telegram Bot API = quick-win** (miễn phí, chỉ cần bot token + chat id per user/tenant); **Zalo OA/ZNS = có điều kiện** (đăng ký OA, template duyệt, phí ZNS) — thiết kế `IDigestChannel` để cắm dần từng kênh | S1, C1, C5, O1 |
| F3 | **Metric Baseline** — `dbo.MetricSnapshots` | Job chụp metric ngày (doanh thu, chi phí, deal mới…) → lịch sử để so lệch | C2, C4, O5 |
| F4 | **Template Store + Auto-Send Guard** — `dbo.CareTemplates` | Template tenant duyệt 1 lần + hạn mức/ngày + log gửi + kill-switch per kịch bản — thi hành "mức 2" có kiểm soát | S6, S3, O3 |
| F5 | **Digest Subscriptions** — `dbo.DigestSubscriptions` | Sổ người nhận: user đăng ký loại bản tin (`sale-brief`/`ceo-brief`/`ops-brief`) + kênh tự khai (email xác minh / Telegram chat id / Zalo). **Gate bằng quyền CRM thật** khi đăng ký (ceo-brief đòi quyền xem tài chính) — trả lời bài toán "ai là giám đốc, ai là sale". Xem mục "Phân tích sâu" ở trên | S1, C1, C5, O1 (mọi digest) |

Thứ tự phụ thuộc: **F1 trước tiên** → F2 → F3/F4 song song khi cần. Ràng buộc STRICT giữ nguyên cho mọi workflow nền: `AiCallContext.Push("<feature>", tenantId[, sessionId])` bao quanh AI call (quota + log đúng feature); DateTime UTC+Z; schema mới khai trong `TourkitAiDb.SchemaSql` + cập nhật `docs/database-schema.md`.

---

## Chương 6 — Ma trận ưu tiên + lộ trình đề xuất

| Đợt | Gồm | Vì sao |
|-----|-----|--------|
| **Đợt 1 — "Bản tin + cảnh báo"** | F1 + F2 → **S1 + C1 + O2** | 1 engine ra 3 tính năng thấy được ngay cho 3 vai trò — demo/bán mạnh nhất trên mỗi đồng effort. Data sẵn 100%, không chờ upstream. O2 không tốn quota AI |
| **Đợt 2 — "Hành động"** | **S4 + C5 + O1**; S2 **có điều kiện** | Đứng trên đợt 1. S2 cần kind `create_deal` cho CrmActionQueue **và** phải qua audit MailClassifier trước (số thật 11/08: `hoi_dat_tour`=0, `khac`=551 — không có input thì xây vô nghĩa) |
| **Đợt 3 — "Tự chủ + thông minh"** | F4 → **S6**; F3 → **C2 + C4**; **O5** (capacity đã xác minh có sẵn); S3 **có điều kiện** | Giá trị lớn nhưng cần nền F3/F4. S3 kỹ thuật chỉ còn thêm 2 cột TourQuotes, nhưng số thật (28 quote, 26 của admin) cho thấy phải **tăng adoption wizard trước** thì mới có gì để đeo bám |
| **Chờ xác minh gap** | **O3, C3** | O3: trạng thái NCC confirm per dịch vụ; C3: nâng cấp planner riêng. Kiểm trước khi hứa |

**3 quick-win = Đợt 1: S1 + C1 + O2 trên nền F1+F2.**

## Đánh giá khả thi & mức ảnh hưởng (11/08/2026 — dựa trên code kiểm chứng + DB thật)

Định nghĩa: **Khả thi %** = xác suất giao được ĐÚNG GIÁ TRỊ như spec (build xong + có giá trị thật với data hiện có). **Ảnh hưởng** = giá trị sử dụng ↔ rủi ro/blast radius.

### Nền tảng F1–F5

| Mảnh | Khả thi | Căn cứ | Ảnh hưởng |
|------|--------:|--------|-----------|
| F1 Insight Feed | **95%** | Bảng + trang mới, pattern sẵn; zero phụ thuộc ngoài | Nền cho tất cả; additive, rủi ro thấp |
| F2 Digest Engine | **85%** | Scheduler chạy production (11 config enabled thật); trừ điểm: kênh email phụ thuộc worker OutboundMails app-side, Telegram là hạ tầng mới | Trung tâm giá trị; lỗi = bản tin sai giờ/trùng, không phá flow cũ |
| F3 Metric Baseline | **75%** | Build dễ (~90%) nhưng ngưỡng "bất thường" ngành du lịch CÓ MÙA VỤ — trung bình trượt dễ báo nhầm cao/thấp điểm | Báo nhầm nhiều → alert fatigue, mất niềm tin |
| F4 Template + Guard | **90%** | `dbo.MailTemplates` + admin CRUD đã có tiền lệ | **Rủi ro vận hành cao nhất hệ** — cổng gửi tự động ra ngoài |
| F5 Digest Subscriptions | **90%** | `HasPermission` chạy production, 10/11 phiên có PermissionsJson | Thiếu nó thì mọi digest bất khả thi ("gửi cho ai") |

### Theo tính năng

| Đợt | Tính năng | Khả thi | Ảnh hưởng (giá trị / rủi ro) |
|-----|-----------|--------:|------------------------------|
| 1 | S1 bản tin Sale | **85%** | Cao — chạm sale mỗi sáng / thấp (read-only). 2 dòng chuẩn + 3 dòng thô như mục "Phân tích sâu" |
| 1 | C1 bản tin CEO | **90%** | **Cao nhất về giá trị bán hàng** — 5/7 tenant chạy ngay / thấp (số server-side, AI chỉ diễn giải) |
| 1 | O2 canh thanh toán | **95%** | Cao — đụng thẳng dòng tiền / thấp nhất (rule thuần, 0 quota AI) |
| 1 | S5 vệ sinh pipeline | 95% | Nhẹ nhưng gần như miễn phí (rule trong S1) |
| 2 | S4 thẻ gặp khách | 85% | Khá / thấp (on-demand, batch context sẵn) |
| 2 | C5 TRAVAI đọc bản tin | 90% | Demo-value cao / thấp (reuse C1 + TTS) |
| 2 | O1 readiness D-7 | 78% | Cao cho điều hành / thấp; checklist visa cần kiểm field sâu hơn |
| 2* | S2 mail → cơ hội | **40% hiện trạng** | Điều kiện chưa thỏa (`hoi_dat_tour`=0, 551 mail `khac`); sau audit classifier tốt → ~75% |
| 3 | S6 auto-care mức 2 | 70% | **Giá trị cao / rủi ro nghiệp vụ cao nhất** — mail sai tên/ngữ cảnh tới khách thật = mất mặt tenant; sống chết ở F4 guard |
| 3 | C2 watchdog bất thường | 65% | Cao nếu đúng / phản tác dụng nếu báo nhầm (phụ thuộc F3 + mùa vụ) |
| 3 | C4 dự phóng | 75% | Trung bình; cần bảng target (gap #6) |
| 3 | O4 tổng kết sau tour | 80% | Trung bình + mở khóa S6 |
| 3 | O5 canh slot | 85% | Khá / thấp (Slots/Booked/Available đã verify) |
| 3* | S3 đeo bám báo giá | **35% hiện trạng** | Build dễ (~90%) nhưng 28 quote (26 của admin) = không có gì để nhắc — chờ adoption wizard |
| Chờ | O3 nhắc NCC | **30%** | Gap NCC-confirm chưa xác minh; nếu CRM không có data gốc → KHÔNG làm được (legacy đã khóa tham khảo tuyệt đối) |
| Chờ | C3 hỏi "tại sao" | 55% | Planner đa bước — khó nhất về AI engineering |

### Kết luận tổng

- **Đợt 1 (F1+F2+F5 → S1+C1+O2+S5): khả thi ~85–90%** — cao bất thường cho một đợt AI vì đứng trọn trên hạ tầng production + data đã đếm được. Cam kết được với lãnh đạo.
- **Blast radius kỹ thuật toàn roadmap: THẤP** — gần như 100% additive (bảng mới, workflow mới qua `WorkflowStackRegistration`, endpoint mới). Chỉ 2 điểm chạm code cũ: thêm 2 cột `TourQuotes` + thêm const `TkPermissionCodes` (đều additive/idempotent).
- **Rủi ro thật nằm ở 3 chỗ (không phải "code khó")**: (1) chi phí quota AI tăng đều — digest × user × ngày (riêng O2 miễn phí); (2) chất lượng ngưỡng cảnh báo C2/F3 với mùa vụ du lịch; (3) cổng auto-send S6/F4 — rủi ro thương hiệu tenant, bắt buộc hạn mức + log + kill-switch.
- **2 tính năng KHÔNG hứa với lãnh đạo ở thời điểm này**: O3 (30%) và S2 (40% hiện trạng) — đúng nhãn "có điều kiện/chờ xác minh" đã gắn.

## Danh sách gap (cập nhật sau kiểm chứng code 11/08/2026)

1. ~~Field **ngày sinh KH**~~ **ĐÓNG** — `AiCustomerDtos.Birthday` + `BirthdayThisMonth` filter có sẵn (S6).
2. Trạng thái **NCC đã xác nhận** per dịch vụ trong CRM (O3) — **còn mở**, phải kiểm upstream/legacy trước khi hứa.
3. ~~**Capacity/slot**~~ **ĐÓNG** — `TourDtos.Slots/Booked/OnHold/Available` (O5 → Đợt 3).
4. ~~Bảng trạng thái báo giá~~ **SỬA NHẬN ĐỊNH** — `dbo.TourQuotes` đã server-side; chỉ cần thêm 2 cột `SentAt`/`ReplyStatus` (S3).
5. Kênh chat ngoài: **Telegram Bot** (quick-win — chỉ cần lưu bot token + chat id) vs **Zalo OA/ZNS** (cần đăng ký OA + duyệt template + phí; ZNS hiện chỉ có stack cũ) — cho bản tin F2 lẫn Auto-Care S6. **Còn mở** (quyết định kênh).
6. Chỗ nhập **kế hoạch/target doanh thu** per tenant (C4) — **còn mở** (bảng nhỏ mới).
7. CRM **không expose email/SĐT user** (`/api/ai/reference` sellers chỉ `{id,name}`) → kênh nhận bản tin phải user tự khai trong F5 — **còn mở** (chấp nhận tự khai, hoặc đề xuất upstream thêm field).
8. ~~Filter lịch hẹn theo seller~~ **ĐÓNG** — `GetAiAppointmentsAsync` `DateFilter=1` + `Assignee` per item.
9. **Mail chưa gán per-sale** (`dbo.Mails` là hộp thư chung tenant) → dòng "mail chờ trả lời" trong sale-brief chỉ ở mức toàn công ty; gán người cần assign-to-staff (Phase 2 SmartMail) — **còn mở**.

## Nguồn tham khảo

- [Lindy — 8 Best AI Agents for Sales in 2026](https://www.lindy.ai/blog/ai-agents-sales)
- [monday — AI lead follow up 2026](https://monday.com/blog/crm-and-sales/ai-lead-follow-up/)
- [Orchestra Intelligence — AI Agents for Travel Agencies 2026](https://www.orchestraintelligence.fr/en/blog/agents-ia-agences-voyage-2026)
- [Tredence — AI Agents for Travel: Use Cases 2026](https://www.tredence.com/blog/ai-agents-for-travel)
- [noimos — 7 Best AI Agents for Data Analysis 2026](https://noimosai.com/en/blog/the-7-best-ai-agents-for-data-analysis-in-2026-proactive-insights-and-autonomy)
- [Improvado — Top AI Reporting Tools 2026](https://improvado.io/blog/top-ai-reporting-tools)
