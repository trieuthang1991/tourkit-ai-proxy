# Phân tích lưu trữ — Pipeline gửi bản tin v3 (hàng đợi đa kênh)

> Tài liệu đi kèm spec [digest-queue-pipeline](2026-08-13-digest-queue-pipeline-design.md), 13/08/2026.
> Kiến trúc chốt sau brainstorm: **queue-based** theo đề xuất của người dùng — thay bản nháp
> "staged-columns" trước đó (đã bỏ). Nguyên tắc giữ nguyên: KHÔNG bảng mới, KHÔNG đổi khoá chính,
> chỉ THÊM cột — mọi thay đổi additive, an toàn kể cả sau khi public.

## 1. Bức tranh 3 bảng — 3 vai trò, không chồng vai

```
 dbo.DigestSubscriptions ─── CHỈ CẤU HÌNH ────────────────────────────────────────────
 │  (1 dòng/người/loại — PK TenantId+Username+BriefType, sống lâu dài)
 │  ai nhận · loại nào · giờ nào (SendHourLocal) · kênh nào (email/telegram/zalo + nơi nhận)
 │  KHÔNG giữ state xử lý nào — giống dbo.UserWorkflows chỉ giữ lịch engine.
 │
 │  PREPARE (T − LeadMinutes) đọc cấu hình, dựng nội dung:
 ▼
 dbo.AgentInsights ─── NỘI DUNG + KHO LƯU ─────────────────────────────────────────────
 │  (1 dòng/bản tin/người/ngày — Id BIGINT IDENTITY: "Id theo ngày" tự nhiên)
 │  • PREPARE ghi bản tin vào đây = vừa là NGUỒN NỘI DUNG, vừa là archive trong app
 │  • in-app LUÔN-BẬT (kho lưu, không phải kênh tắt được) → xem lại + nghe lại (C5) luôn chạy
 │  • cảnh báo digest-alert (kênh bỏ cuộc) cũng ghi vào đây, dedup bằng AlertKey
 │  • prune theo config Digest:InsightKeepDays (mặc định 30 ngày)
 │
 │  PREPARE enqueue mỗi (người × kênh ngoài đang bật) = 1 dòng, SourceId = Id bản tin:
 ▼
 dbo.OutboundMails ─── HÀNG ĐỢI GỬI ĐA KÊNH (mở rộng) ────────────────────────────────
    (mỗi dòng = 1 lần giao 1 kênh cho 1 người — Status/RetryCount/ErrorMessage RIÊNG từng dòng)
    • ScheduledUtc = giờ người chọn (đã có sẵn cột + worker email đã tôn trọng)
    • email  → worker toutkit-app drain như hiện tại (hợp đồng giữ nguyên)
    • telegram/zalo → proxy tự drain (drainer mới)
    • gửi 1 lần/kênh/ngày; lỗi → Status=2 nằm lại để theo dõi (retry HOÃN — phương án riêng sau,
      khi làm chỉ thêm chính sách "lật 2→0" dựa trên chính bảng này)
```

| Bảng | Vai trò | Vòng đời | Thay đổi đợt này |
|---|---|---|---|
| `DigestSubscriptions` | Cấu hình đăng ký thuần | Dòng sống lâu dài | **Ngừng dùng** SentMask/SentAttempts/LastSent* (giữ cột, không ghi nữa) |
| `AgentInsights` | Nội dung + archive + cảnh báo | 1 dòng/bản tin/ngày, prune N ngày | Không đổi schema; in-app → luôn-ghi |
| `OutboundMails` | Hàng đợi gửi đa kênh | Theo Status, dòng là bản ghi lịch sử giao | **+1 cột `Channel`** (TINYINT, default `0`=email) |

## 2. `dbo.OutboundMails` — trung tâm của thiết kế

Schema hiện tại đã có sẵn gần đủ: `Status (0=Pending 1=Sent 2=Failed 3=Cancelled 4=Skipped)`,
`RetryCount`, `ErrorMessage`, `ScheduledUtc`, `SourceId`, `Kind`, index poll
`(Status, ScheduledUtc, CreatedUtc)`. Chỉ thêm:

```sql
IF NOT EXISTS (... 'Channel')
    ALTER TABLE dbo.OutboundMails ADD Channel TINYINT NOT NULL
        CONSTRAINT DF_OutboundMails_Channel DEFAULT 0;   -- 0=email 1=telegram 2=zalo; dòng cũ tự thành 0 (email)
```
(User chốt 13/08: Channel là SỐ để tránh lỗi gõ chuỗi, default 0 = email. Toàn hệ chỉ còn MỘT
enum kênh gửi: `OutboundChannel {Email=0, Telegram=1, Zalo=2}` — enum cũ `DigestChannel` +
`ChannelMask` (cờ bit) GỠ BỎ trong đợt này (thuộc đám state khai tử), để KHÔNG tồn tại hai bộ số
chồng nhau. In-app không nằm trong enum vì không còn là kênh — nó là kho lưu luôn-bật.)

Cách dùng cho bản tin (Kind = `daily-brief`, SourceId = Id dòng AgentInsights):

| Cột | email | telegram | zalo |
|---|---|---|---|
| `Channel` | `0` (email) | `1` (telegram) | `2` (zalo) |
| `ToEmail` | địa chỉ nhận | NULL | NULL |
| `Data` (JSON) | — | `{"chatId":"..."}` | `{"zaloUserId":"..."}` |
| `Params` | title/bodyHtml/briefType/date (hợp đồng worker giữ NGUYÊN) | NULL — nội dung đọc từ AgentInsights qua `SourceId` | như telegram |
| `ScheduledUtc` | giờ người chọn (VN→UTC) | như email | như email |
| Ai gửi | worker toutkit-app (SMTP) | proxy drainer | proxy drainer |

**Vì sao nội dung telegram/zalo KHÔNG nhét vào queue:** 1 nguồn ở `AgentInsights` (đọc qua
`SourceId`), dòng queue nhẹ. Riêng email phải mang `Params` vì hợp đồng worker hiện tại render từ
`Params` — giữ nguyên để không sửa worker ngoài 1 chỗ (xem §5).

**Tài khoản GỬI ĐI (OA token, bot token) cũng KHÔNG nằm trong queue — resolve LÚC GỬI:**
- Zalo: `TenantId` trên dòng → `dbo.TenantChannelSettings` (OaId + AccessToken Crypton-enc,
  per-tenant — chính cấu hình "OA Zalo của công ty" Đợt 1). Đúng cách `ZaloOaChannel.SendAsync`
  đang làm (`GetZaloConfigAsync(tenantId)`), drainer tái dùng nguyên.
- Telegram: `Telegram:BotToken` appsettings (bot chung hệ thống).
- Email: worker toutkit-app tự có SMTP.
Lý do: token là secret (không rải bản sao vào bảng lịch sử) + token đổi giữa lúc enqueue và lúc
gửi thì bản mới nhất được dùng ngay.

### Trạng thái dòng — gửi 1 lần, lỗi nằm lại làm bằng chứng (retry HOÃN — thiết kế riêng sau)

```
 Status=0 Pending ──gửi ok──▶ Status=1 Sent (ProcessedUtc)
                      │
                      └─gửi lỗi─▶ Status=2 Failed (ErrorMessage, RetryCount=1) + log ERROR
                                   ── ĐỢT NÀY DỪNG Ở ĐÂY: không lật lại, không cảnh báo thêm ──
```

- "Gửi gì lỗi?" = `SELECT ... WHERE Status=2` — thấy ngay kênh, người, lỗi gì, lúc nào.
- Theo dõi: endpoint `GET /api/v1/workflows/outbound-mails` sẵn có, thêm lọc `channel`.
- **Nền cho retry sau này (đã kiểm hợp đồng):** worker email không tự retry (chỉ poll Status=0)
  → mai kia muốn retry chỉ cần chính sách "lật 2→0" trong drainer — không đổi schema, không sửa
  worker. Cột `RetryCount` sẵn sàng cho việc đó.

## 3. `dbo.DigestSubscriptions` — về đúng vai "chỉ cấu hình"

```
GIỮ (cấu hình):   Enabled · SendHourLocal · ChannelEmail/Email · ChannelTelegram/TelegramChatId
                  · ChannelZalo/ZaloUserId · (ChannelInApp giữ cột nhưng đối xử luôn-true)
NGỪNG DÙNG:       SentMask · SentAttempts · LastSentUtc · LastSentLocalDate
                  (giữ cột trong DB — không xoá để không phá gì; code không đọc/ghi nữa)
```

- Chống gửi trùng KHÔNG cần cột ngày ở đây nữa: "hôm nay đã chuẩn bị chưa" = **đã tồn tại dòng
  `AgentInsights` của (tenant, user, kind) trong ngày VN hôm nay chưa** (query theo index sẵn có
  `IX_AgentInsights_Tenant_User_Created`).
- **KHÔNG gộp vào `dbo.UserWorkflows`** (đã cân nhắc theo yêu cầu, kết luận giữ 2 bảng):
  1. Scheduler coi mỗi dòng UserWorkflows là 1 đơn vị chạy (`ListDue`/`NextRunUtc`/auto-pause) —
     dòng "người nhận" không phải thứ để "chạy".
  2. Convention `Username=''` = per-tenant sẽ va với dòng per-user cùng `WorkflowType`.
  3. Trang admin cần cột typed (kênh/giờ/nơi nhận) để lọc bằng SQL — OptionsJson làm mất khả năng đó.
  Hai bảng cùng khuôn PK `(Tenant, Username, Type)` — cùng "kiểu" nhưng khác vai: lịch engine vs sổ
  người nhận.

## 4. Vòng đời 1 NGÀY (người chọn 7:00 · lead 10' · trần 3 · zalo hỏng)

```
 06:50  PREPARE (workflow, tick ~5')
        chưa có AgentInsights hôm nay → dựng nội dung (fetch CRM + AI nếu ceo — TẢI NẶNG Ở ĐÂY)
        → INSERT AgentInsights (Id=123, archive + nguồn nội dung)
        → INSERT OutboundMails: (email, Sched=07:00) + (zalo, Sched=07:00, SourceId=123)
 07:00  SEND (queue tự chạy — workflow không làm gì)
        worker toutkit-app:  email  Status 0→1 ✓
        proxy drainer:       zalo   Status 0→2 ✗ (RetryCount=1, ErrorMessage="token hết hạn")
                             + log ERROR — dòng Failed nằm lại để theo dõi (ĐỢT NÀY KHÔNG RETRY)
 ─── sang ngày mới: PREPARE thấy chưa có insight của ngày mới → chu kỳ lặp lại ───
```

Ca server sập 06:45–08:20: lỡ PREPARE → tick 08:20 thấy `now ≥ 07:00` & chưa có insight hôm nay
→ dựng-tại-chỗ + enqueue `ScheduledUtc=now` → gửi ngay. Muộn nhưng KHÔNG mất (đồng thời vá bug
hiện tại: so `Hour ==` làm mất bản tin nếu sập đúng khung giờ).

## 5. Rủi ro cross-repo (ĐÃ KIỂM hợp đồng thật) + thứ tự deploy

Worker toutkit-app poll: `WHERE Status=0 AND (ScheduledUtc IS NULL OR ScheduledUtc<=now)` —
**không lọc Kind/Channel** → nếu enqueue telegram/zalo trước khi worker biết `Channel`, worker sẽ
nhặt nhầm. Bắt buộc:

1. **Deploy TRƯỚC:** sửa worker (toutkit-app) thêm `AND Channel=0` vào poll (cột default
   `0`=email nên deploy sớm vô hại) + cập nhật hợp đồng `docs/mail-templates/README.md`.
2. **Deploy SAU:** proxy bắt đầu enqueue telegram/zalo.

Điểm cộng có sẵn: worker đã tôn trọng `ScheduledUtc` → "email hẹn giờ" chạy được ngay, không sửa
logic gửi của worker.

## 6. Dung lượng & chỉ mục

- Queue: +2–3 dòng/người/ngày (kênh ngoài); dòng telegram/zalo nhẹ (không mang body). Index poll
  `(Status, ScheduledUtc, CreatedUtc)` dùng được cho cả drainer mới (thêm điều kiện Channel — bảng
  nhỏ, chưa cần index riêng; xem lại khi volume tăng).
- `AgentInsights`: +1 dòng/người/ngày (như trước) + prune 30 ngày.
- `DigestSubscriptions`: teo lại về thuần cấu hình — nhẹ hơn thiết kế cũ.

## 7. So nhanh với bản nháp staged-columns (vì sao đổi)

| | Staged-columns (bỏ) | Queue v3 (chốt) |
|---|---|---|
| Biết kênh nào lỗi | suy từ cờ bit `EnabledOf & ~SentMask` | đọc thẳng dòng `Status=2` + ErrorMessage |
| Retry | đếm LƯỢT chung (SentAttempts) | RetryCount RIÊNG từng kênh |
| Gửi đúng giờ | workflow phải tick trúng giờ | `ScheduledUtc` — hạ tầng queue sẵn có |
| Theo dõi | trang admin dịch mask | endpoint outbound-mails sẵn có |
| State rải ở | 3 nhóm cột trên subscription | queue (giao hàng) + insights (nội dung) |
| Cột mới | 4 cột NVARCHAR(MAX) trên subscription | 1 cột `Channel` trên queue |
