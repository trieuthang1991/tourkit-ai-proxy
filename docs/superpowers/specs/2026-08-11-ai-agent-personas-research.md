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

> Số liệu volume thật (bao nhiêu deal/mail/quote per tenant) chạy bằng script read-only `digest-feasibility-stats.ps1` (scratchpad) — cần người có quyền chạy vì script giải mã connection string.

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
| **Đợt 2 — "Hành động"** | **S2 + S4 + C5 + O1** | Đứng trên đợt 1. S2 cần kind `create_deal` cho CrmActionQueue |
| **Đợt 3 — "Tự chủ + thông minh"** | F4 → **S6 + S3**; F3 → **C2 + C4**; **O5** (capacity đã xác minh có sẵn) | Giá trị lớn nhưng cần nền F3/F4; S3 chỉ còn thêm 2 cột vào TourQuotes |
| **Chờ xác minh gap** | **O3, C3** | O3: trạng thái NCC confirm per dịch vụ; C3: nâng cấp planner riêng. Kiểm trước khi hứa |

**3 quick-win = Đợt 1: S1 + C1 + O2 trên nền F1+F2.**

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
