# Pipeline gửi bản tin v3 — hàng đợi đa kênh (chuẩn bị trước · gửi đúng giờ · retry theo dòng)

**Ngày:** 2026-08-13 · **Trạng thái:** chờ user duyệt spec
**Tài liệu kèm:** [Phân tích lưu trữ](2026-08-13-digest-db-storage-analysis.md) (bảng/cột/vòng đời — đọc trước)
**Nền:** Đợt 1 digest ([spec](2026-08-11-dot1-digest-insight-design.md)) + C5 nghe bản tin ([plan](../plans/2026-08-13-c5-listen-brief-travai.md))

## 1. Vấn đề (vì sao làm)

1. **Dồn tải đúng giờ gửi:** hiện dựng nội dung (fetch CRM + AI) và gửi cùng lúc tại mốc giờ →
   nhiều người cùng giờ là spike, nguy cơ treo/timeout, gửi trễ.
2. **Retry mù:** `SentMask`/`SentAttempts` là cờ bit + bộ đếm LƯỢT chung — không nói được "kênh
   nào lỗi vì sao, đã thử mấy lần"; theo dõi phải dịch mask.
3. **Mất bản tin khi sập đúng khung giờ:** `DigestDue` so `vn.Hour == SendHourLocal` — server chết
   trọn khung giờ đó là mất bản tin cả ngày.
4. **Nghe/xem lại phụ thuộc kênh in-app đang là kênh TẮT ĐƯỢC:** ai chỉ bật Zalo/Telegram thì
   không có bản lưu nào trong app để nghe lại (C5).
5. **Ngưỡng hardcode:** trần thử lại, chu kỳ… nằm trong const, sau public khó chỉnh.

## 2. Quyết định chốt (từ brainstorm với user, 13/08)

| # | Quyết định | Ghi chú |
|---|---|---|
| Q1 | **Queue-based**: mở rộng `OutboundMails` thành hàng đợi gửi ĐA KÊNH (+cột `Channel`) | đề xuất của user |
| Q2 | **Nội dung ở `AgentInsights`**: PREPARE ghi bản tin vào đây (1 dòng/người/ngày, có Id) — vừa nguồn nội dung vừa archive | đề xuất của user |
| Q3 | **`DigestSubscriptions` = chỉ cấu hình**, không giữ state xử lý (ngừng dùng SentMask/SentAttempts/LastSent*) | đề xuất của user |
| Q4 | **KHÔNG gộp** `DigestSubscriptions` vào `UserWorkflows` | 3 lý do trong tài liệu lưu trữ §3 |
| Q5 | **Retry: HOÃN — đợt này KHÔNG retry.** Gửi 1 lần/kênh/ngày; lỗi → `Status=2` + `ErrorMessage` + log ERROR để theo dõi. Nền queue (Status/RetryCount/ErrorMessage riêng từng dòng) chuẩn hoá sẵn; phương án retry user sẽ thiết kế riêng sau (khi đó chỉ thêm chính sách "lật 2→0", không đổi schema) | user chốt 13/08: "tạm bỏ qua retry, chuẩn hoá trước" |
| Q6 | **In-app = kho lưu luôn-bật**; email/telegram/zalo = kênh đẩy tuỳ chọn | giải "mai muốn nghe lại" |
| Q7 | Cảnh báo chủ động (digest-alert khi kênh hỏng dai) → **dời sang phương án retry** (đi cùng khái niệm "bỏ cuộc"). Đợt này: theo dõi qua log ERROR + dòng queue Status=2 trên endpoint outbound-mails | gắn với Q5 |
| Q8 | **Mọi ngưỡng vào config** `appsettings.json` mục `Digest` — không hardcode | user chốt |
| Q9 | Tới giờ mà chưa dựng sẵn → **dựng-tại-chỗ rồi gửi** (không bao giờ âm thầm mất tin, kể cả gửi muộn) | user chốt |
| Q10 | Dữ liệu dựng sớm 5–10 phút: chấp nhận (số tổng hợp) | user chốt |

## 3. Kiến trúc

```
┌ WORKFLOW sale-brief / ceo-brief (PerTenant, tick theo IntervalMinutes ~5') ────────────┐
│ CHỈ CÒN 1 VIỆC: PREPARE                                                                │
│  với mỗi đăng ký Enabled: nếu now(VN) ≥ T − LeadMinutes VÀ hôm nay chưa có bản tin      │
│  (chưa tồn tại dòng AgentInsights của tenant+user+kind trong ngày VN):                  │
│    1. dựng nội dung (fetch CRM bằng phiên người nhận; ceo: AI viết lời, dedup theo bộ số)│
│    2. INSERT AgentInsights  ← nội dung + archive (in-app "giao" xong tại đây)           │
│    3. INSERT OutboundMails: 1 dòng/kênh-ngoài-đang-bật, ScheduledUtc = T (VN→UTC),      │
│       SourceId = Id insight; email mang Params (hợp đồng worker giữ nguyên),            │
│       telegram/zalo mang Data{chatId|zaloUserId}, KHÔNG mang body                       │
│  (nếu now ≥ T mà chưa dựng — server vừa sống lại: vẫn dựng ngay, ScheduledUtc=now)      │
└────────────────────────────────────────────────────────────────────────────────────────┘
                          │ hàng đợi tự chạy, workflow không gửi gì
                          ▼
┌ GỬI ──────────────────────────────────────────────────────────────────────────────────┐
│ email:          worker toutkit-app sẵn có (poll Status=0 & ScheduledUtc≤now) — không đổi│
│ telegram/zalo:  OutboundChannelDrainer (BackgroundService MỚI, tick ~60s, cùng cờ       │
│                 Workflows:RunScheduler): poll Status=0 & Channel≠email & Sched≤now      │
│                 → gửi qua TelegramChannel/ZaloOaChannel sẵn có → Status=1/2             │
└────────────────────────────────────────────────────────────────────────────────────────┘
                          │
                          ▼
┌ LỖI & THEO DÕI (đợt này KHÔNG retry — xem Q5) ────────────────────────────────────────┐
│ gửi lỗi → Status=2 + ErrorMessage + RetryCount=1 + LogError (đủ ngữ cảnh tenant/user/   │
│ kênh/lý do) — dòng nằm lại làm bằng chứng, soi qua GET /workflows/outbound-mails        │
│ (lọc channel/status). Phương án retry (lật 2→0 + cảnh báo bỏ cuộc) thiết kế riêng sau — │
│ khi đó CHỈ thêm chính sách vào drainer, không đổi schema.                               │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### 3.1 `DigestDue` viết lại (thuần, test được)
- Bỏ `PendingFor` bit-mask. API mới: `ShouldPrepare(sub, utcNow, leadMinutes)` → bool
  (Enabled && nowVN ≥ mốc `SendHourLocal:00 − lead`; so theo PHÚT, không so `Hour ==`).
- "Hôm nay đã dựng chưa" KHÔNG nằm trong hàm thuần — workflow kiểm bằng
  `InsightRepository.ExistsTodayAsync(tenant, user, kind, todayVn)` (method mới, dùng index sẵn).
- Hết ngày VN vẫn chưa dựng được (lỗi build suốt) → thôi, mai dựng bản mới (bản tin hôm qua
  không còn giá trị).

### 3.2 Kênh gửi (tái dùng, đổi chỗ gọi)
- `TelegramChannel`/`ZaloOaChannel` giữ nguyên logic HTTP; drainer gọi chúng theo dòng queue
  (đọc nội dung từ `AgentInsights` qua `SourceId`, format như hiện tại).
- `EmailChannel` (enqueue) đổi thành enqueue kèm `Channel='email'`, `ScheduledUtc=T`.
- `InAppChannel` KHÔNG còn là kênh — PREPARE ghi thẳng insight (xoá class hoặc để trống vai).
- `DigestDispatcher` teo vai: đường gửi THẬT không dùng nữa (fan-out = insert N dòng queue);
  GIỮ LẠI chỉ để phục vụ `POST /subscriptions/{type}/test` (Gửi thử cần kết quả ngay, đi thẳng).

### 3.3 Config — `appsettings.json` mục `Digest` (+ `appsettings.example.json`)
```json
"Digest": {
  "LeadMinutes": 10,          // dựng trước giờ gửi bao lâu
  "CheckIntervalMinutes": 5,  // default IntervalMinutes khi tạo config workflow bản tin mới
  "InsightKeepDays": 30       // giữ bản tin trong Bảng tin bao lâu (nghe/xem lại)
}
```
(`MaxAttemptsPerDay` / `AlertOnGiveUp` CỐ Ý chưa có — thuộc phương án retry sẽ thiết kế sau;
thêm lúc đó, tránh config chết không ai dùng.)

KHÔNG migrate `dbo.UserWorkflows` (user chốt 13/08): tenant hiện có giữ nguyên interval họ đã đặt;
`CheckIntervalMinutes` chỉ là default cho config TẠO MỚI. Hệ quả chấp nhận: tenant để interval >
LeadMinutes (vd 15' > 10') có thể lỡ cửa sổ PREPARE → rơi về dựng-tại-chỗ lúc gửi (vẫn đúng, chỉ
không được lợi "chuẩn bị trước") — muốn tận dụng thì tự hạ interval trong UI Tự động hoá.

### 3.4 Schema (idempotent trong `TourkitAiDb.SchemaSql`)
```sql
ALTER TABLE dbo.OutboundMails ADD Channel NVARCHAR(16) NOT NULL DEFAULT 'email';  -- IF NOT EXISTS
```
Không xoá cột nào (SentMask/SentAttempts/LastSent* giữ nguyên trong DB, code ngừng dùng).
Cập nhật `docs/database-schema.md` + hợp đồng worker `docs/mail-templates/README.md`.

## 4. Vết dầu loang phải sửa cùng (ripple)

| Chỗ | Sửa gì |
|---|---|
| `PUT /digest/subscriptions` | bỏ validate "bật mà 0 kênh → 400" (in-app luôn có); vẫn validate kênh bật thiếu nơi nhận |
| `digest.jsx` | ô "Trong app" → khoá luôn-bật + ghi chú "luôn lưu để xem/nghe lại" |
| Admin "Bản tin" (`AdminDigestRepository`) | `DetectProblem`/cột "đã gửi hôm nay" đọc từ **queue** (JOIN OutboundMails theo ngày) thay vì SentMask |
| `GET /workflows/outbound-mails` | thêm lọc `channel`; hiện cột Channel |
| E2E `features-digest.ps1` | assertion "0 kênh = 400" đổi theo validate mới; giữ các assertion C5 |
| `DigestDueTests` | viết lại theo `ShouldPrepare` (phút + lead) |
| `ChannelMask` | teo vai: giữ `EnabledOf` (liệt kê kênh ngoài đang bật khi enqueue); bỏ dần mask machinery |
| Test-send (`POST /subscriptions/{type}/test`) | gửi thử vẫn ĐI THẲNG (không qua queue) để trả kết quả ngay — ghi chú rõ khác đường thật ở 1 điểm: đường thật giờ là queue |
| File nháp `Services/Digest/DigestAlert.cs` (chưa commit) | đã xoá — cảnh báo bỏ cuộc dời sang phương án retry (sau) |

## 5. Thứ tự deploy (BẮT BUỘC — cross-repo)
1. toutkit-app worker: poll thêm `AND Channel='email'` (deploy trước, vô hại vì cột default email).
2. Proxy: schema + PREPARE + drainer + UI (deploy sau).

## 6. Kiểm thử
- **Thuần (xUnit):** `DigestDue.ShouldPrepare` (biên lead/phút/ngày VN) · build queue-row
  (đủ Channel/Sched/Data theo cấu hình kênh; đổi giờ VN→UTC đúng).
- **Giữ xanh:** SaleBriefBuilder/CeoBriefBuilder/BriefNarration/TelegramFormat (không đổi).
- **E2E:** cập nhật `features-digest.ps1` (mục 4) — gửi thử, luật 1-loại, speakText giữ nguyên.
- **Tay:** 1 lượt thật với 1 đăng ký telegram: thấy dòng queue Pending lúc T−10, Sent lúc T;
  giả lập token hỏng → thấy RetryCount tăng, digest-alert xuất hiện.

## 7. Ngoài phạm vi
ZNS trả phí · gửi lại thủ công từ admin (bấm re-send 1 dòng queue — để đợt sau, giờ chỉ theo dõi)
· gộp `DigestSubscriptions` vào `UserWorkflows` (đã cân nhắc, quyết KHÔNG — §3 tài liệu lưu trữ)
· đổi cơ chế các workflow khác (payment-watchdog vẫn ghi insight trực tiếp như cũ).
