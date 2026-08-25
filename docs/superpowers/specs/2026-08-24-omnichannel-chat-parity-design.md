# Omnichannel Chat — Feature Parity and Production Design

- **Ngày:** 24/08/2026
- **Trạng thái:** Thiết kế đã được duyệt, chưa triển khai
- **Phạm vi:** Inbox vận hành đa kênh + AI automation
- **Topology mục tiêu:** Web API và `TourkitAiProxy.Worker` chạy riêng, dùng chung PostgreSQL/Redis
- **Phương án:** Foundation-first hybrid

Tài liệu này tổng hợp kết quả đối chiếu phần chat giữa:

- Dự án hiện tại: `D:\MiGroup\tourkitapp\tourkit-ai-proxy`;
- Dự án tham chiếu: `D:\MiGroup\AI\chat-bot-xio\ChatbotX`.

Tài liệu này bổ sung và thay thế các quyết định xung đột trong
`2026-08-20-omnichannel-chat-design.md`. Đặc biệt, thiết kế mới sửa mô hình định danh
multi-account, bổ sung durable inbound processing, realtime, lifecycle, AI policy và
chiến lược kiểm thử khi chưa có worker host riêng.

> **Giấy phép và clean-room:** ChatbotX Community Edition dùng AGPLv3; một số chức năng
> enterprise, gồm phần quản lý inbox team, thuộc giấy phép thương mại. ChatbotX chỉ được
> dùng để khảo sát hành vi và khoảng trống tính năng. Không sao chép mã nguồn hoặc module
> enterprise vào TourKit; mọi chức năng tương đương phải được đặc tả và triển khai clean-room.

---

## 1. Mục tiêu

Xây dựng một hộp thư chat đa kênh đủ tin cậy để vận hành thực tế, cho phép nhiều agent
làm việc trên nhiều Zalo OA, fanpage, bot và các account khác của cùng tenant. Hệ thống
phải kết hợp được AI với CRM nhưng vẫn cho phép agent tiếp quản an toàn.

Thứ tự ưu tiên thiết kế là:

1. Data correctness và account isolation;
2. Durable inbound/outbound processing;
3. Realtime và message lifecycle;
4. Inbox collaboration;
5. Contact/CRM;
6. AI automation và handoff;
7. Mở rộng channel;
8. Vận hành, bảo mật và kiểm thử.

### 1.1 Trong phạm vi

- Củng cố Zalo, Messenger và Telegram hiện có;
- Hỗ trợ một tenant kết nối nhiều account cùng kênh;
- Bổ sung Webchat trước, sau đó WhatsApp, Instagram và TikTok;
- Inbox realtime, cursor pagination, tìm kiếm và bộ lọc nâng cao;
- Assign user/team, follow-up, archive, read/unread, block và handoff;
- Contact profile, tags, notes, custom fields và liên kết CRM;
- Saved replies, emoji, reply-to, multiple attachments và rich media;
- Message lifecycle: queued, sent, delivered, seen và failed;
- Durable webhook, retry, dead-letter và replay;
- AI memory, CRM context, extraction, routing, escalation và policy theo inbox;
- Permission, audit, metrics, alert và runbook vận hành;
- Messenger/Instagram social comments sau khi direct message đạt acceptance criteria.

### 1.2 Ngoài phạm vi

- Visual flow/bot builder;
- Campaign, broadcast và marketing sequence;
- Website/page builder;
- Template marketplace và plugin ecosystem;
- Sao chép code hoặc thiết kế nội bộ thuộc phần enterprise của ChatbotX.

### 1.3 Tiêu chí thành công cấp sản phẩm

- Webhook không trả thành công trước khi event được lưu bền vững;
- Event gửi lại không tạo message hoặc AI reply trùng;
- Contact/conversation được phân biệt theo đúng tenant, channel và channel account;
- Agent nhận message mới trong khoảng 2 giây mà không polling toàn trang;
- Outbound có trạng thái, lỗi rõ ràng, retry được và không mất message;
- AI dùng đúng lịch sử, CRM context và policy của inbox;
- Agent có thể claim, transfer, pause bot hoặc handoff mà không có race condition;
- Mọi thao tác nhạy cảm đều được phân quyền và audit;
- Adapter hiện có phải vượt qua cùng một contract suite trước khi thêm channel mới.

---

## 2. Hiện trạng và khoảng trống

### 2.1 Những gì dự án hiện tại đã có

- Adapter Zalo, Messenger và Telegram;
- Manual credential CRUD và nhiều account trên mỗi channel ở tầng cấu hình;
- Webhook route theo tenant/account và bước kiểm tra secret/signature;
- Conversation list/detail, basic filters, read, claim, status và bot pause;
- Text/media send qua outbox;
- PostgreSQL chat tables và `FOR UPDATE SKIP LOCKED` ở outbound worker;
- UI inbox bốn vùng, attachment preview cơ bản và channel configuration;
- Local/R2/S3 attachment storage;
- AI reply cơ bản sau một khoảng debounce ngắn.

### 2.2 Khoảng trống quan trọng đã xác định

| Lĩnh vực | Hiện trạng | Khoảng trống/rủi ro |
|---|---|---|
| Inbound durability | Webhook trả `200`, sau đó chạy `_ = Task.Run(...)` | Process chết sau response có thể làm mất message |
| Multi-account identity | Credential/webhook có account ID | Contact/conversation unique key thiếu account, có thể gộp hai OA/page và reply sai account |
| Lifecycle | Có enum trạng thái | Messenger bỏ qua delivery/read; Zalo seen marker chưa cập nhật DB; outbound không lưu provider message ID |
| Realtime | UI polling khoảng 4 giây | Không có event reconciliation, presence hoặc reconnect cursor |
| Pagination | Cố định khoảng 60 conversation và 120 message | Không hỗ trợ inbox lớn hoặc load lịch sử vô hạn |
| Collaboration | Claim self, status/read/bot pause cơ bản | Thiếu team, transfer, per-user unread, follow, audit và atomic claim |
| Contact/CRM | Có profile fields và `crm_customer_id` | Chưa có CRM linking, tags, notes, custom field, merge/split hoặc profile precedence |
| Composer | Text và attachment cơ bản | Thiếu saved reply, emoji, reply-to, multi-file flow, typing và capability-driven controls |
| AI | Một system prompt chung, nhóm text hiện tại | Không có history, summary, CRM/RAG context, policy theo inbox, trace hoặc escalation |
| Quick reply | Có table `chat_quick_replies` | Chưa có repository, API hoặc UI sử dụng |
| Webchat | Chỉ có enum | Chưa có widget, adapter, session hoặc domain authorization |
| Operations | Retry outbound giới hạn | Chưa có durable inbound retry, dead-letter UI, circuit breaker, metrics hoặc replay |
| Tests | Chủ yếu rule/attachment tests | Thiếu DB, webhook, adapter contract, lifecycle, concurrency và inbox E2E |

### 2.3 Những năng lực đáng tham chiếu từ ChatbotX

ChatbotX có các nhóm năng lực mà TourKit còn thiếu: realtime event store, cursor pagination,
rich composer, saved replies, user/team assignment, contact notes/tags/custom fields,
inbox connection flows, channel-specific capabilities, delivery callback correlation,
durable worker queues, profile enrichment, social comments và Webchat widget.

Không phải mọi tính năng của ChatbotX đều thuộc phạm vi. Flow builder, sequence, broadcast
và marketing integrations bị loại khỏi thiết kế này.

---

## 3. Kiến trúc mục tiêu

Hệ thống dùng mô hình **at-least-once delivery + idempotent processing**. Không tuyên bố
exactly-once; thay vào đó, mọi event/job phải có thể chạy lại an toàn.

```text
Channel Provider
      │ webhook
      ▼
Web API ── verify ──► chat_inbound_events (PostgreSQL)
      │                         │
      │ 200 sau commit          ▼
      │                  Chat processors
      │                         │
      │                normalize + transaction
      │                         │
      ▼                         ▼
 SignalR ◄── Redis ◄── chat_domain_outbox ──► chat read/write tables
      │
      ▼
 Agent Inbox UI
```

### 3.1 Inbound flow

1. Web API xác thực signature, tenant và channel account;
2. Tính hoặc đọc provider event ID;
3. Ghi raw envelope vào `chat_inbound_events` với unique idempotency key;
4. Chỉ trả `200` sau khi transaction commit;
5. Consumer lấy event bằng lease và `FOR UPDATE SKIP LOCKED`;
6. Normalize event theo adapter contract;
7. Upsert account-scoped identity, conversation và message;
8. Ghi domain event trong cùng transaction;
9. Dispatcher phát realtime event sau commit.

Nếu provider không cấp event ID ổn định, adapter phải tạo fingerprint xác định từ account,
event type và canonical payload. Raw payload chỉ phục vụ replay/audit trong retention window.

### 3.2 Outbound flow

1. API kiểm tra permission, channel capability, messaging window và attachment;
2. Transaction tạo một logical message ở trạng thái `queued` và một outbox job;
3. Consumer gọi đúng adapter của `channel_account_id` sở hữu conversation;
4. Lưu `provider_message_id` ngay khi provider nhận message;
5. Chuyển message sang `sent` và phát realtime event;
6. Status webhook tiếp tục cập nhật `delivered`, `seen` hoặc `failed`;
7. Retry giữ nguyên logical message, không tạo bubble mới.

### 3.3 Retry, ordering và dead-letter

- Exponential backoff kèm jitter;
- Phân loại transient, rate-limited, authentication, window-expired, unsupported và permanent;
- Job giữ `attempt_count`, `next_attempt_at`, `locked_until`, worker ID và lỗi gần nhất;
- Conversation-level coordination giữ thứ tự xử lý;
- Worker chết giữa chừng sẽ hết lease để consumer khác tiếp tục;
- Quá retry limit chuyển sang dead-letter;
- Operator có thể xem, replay, cancel hoặc disable channel account;
- Circuit breaker hoạt động theo từng account, không khóa toàn channel của tenant.

### 3.4 Realtime và vai trò Redis

PostgreSQL là source of truth. Redis chỉ dùng cho:

- SignalR backplane;
- Typing/presence ngắn hạn;
- Cache và distributed coordination;
- Thông báo có dữ liệu mới.

Mỗi realtime event có event ID/version. Khi Redis hoặc connection bị gián đoạn, client dùng
cursor/version để lấy lại thay đổi từ PostgreSQL. Mất Redis không được làm mất message.

### 3.5 Chế độ chạy khi chưa có worker riêng

Processor nghiệp vụ nằm trong service/library dùng chung. Có ba hosting mode:

```yaml
ChatProcessing:
  Mode: Embedded       # Embedded | ApiOnly | WorkerOnly
  AdapterMode: Fake    # Fake | Real
```

- `Embedded`: Web API host một durable `BackgroundService`; dùng cho local, CI và staging nhỏ;
- `ApiOnly`: Web API chỉ nhận request và ghi queue;
- `WorkerOnly`: `TourkitAiProxy.Worker` lấy và xử lý queue.

Embedded mode không được quay lại `Task.Run`. Nó vẫn lease job từ PostgreSQL, có retry,
idempotency và crash recovery như worker riêng. Khi worker host hoàn thiện, production chuyển
Web API sang `ApiOnly` và worker sang `WorkerOnly` mà không viết lại nghiệp vụ.

---

## 4. Mô hình multi-account và dữ liệu

### 4.1 Quan hệ connection, account và inbox

```text
Tenant
  ├─ Channel Connection A — Facebook OAuth của user A
  │    ├─ Fanpage 1 → Inbox 1
  │    ├─ Fanpage 2 → Inbox 2
  │    └─ Fanpage 3 → Inbox 3
  ├─ Channel Connection B — Zalo authorization
  │    ├─ Zalo OA 1 → Inbox 4
  │    └─ Zalo OA 2 → Inbox 5
  └─ Channel Connection C
       ├─ Telegram Bot 1 → Inbox 6
       └─ Telegram Bot 2 → Inbox 7
```

Quy tắc đã chốt:

- Một tenant có nhiều connection cùng loại channel;
- Một OAuth connection có thể cung cấp nhiều account;
- User multi-select các account cần import;
- Mỗi OA/page/bot được import tạo đúng một inbox riêng;
- Agent UI có thể tổng hợp nhiều inbox;
- Unique account: `tenant_id + channel + provider_account_id`;
- Outbound luôn dùng account sở hữu conversation, không có fallback account mặc định;
- Disconnect không xóa lịch sử.

### 4.2 Tách contact khỏi channel identity

`chat_contacts` đại diện cho customer nội bộ của tenant. `chat_contact_identities` đại diện
cho từng danh tính provider:

```text
Contact A
  ├─ Zalo OA 1 / user-123
  ├─ Messenger Page 7 / psid-456
  └─ Telegram Bot 2 / chat-789
```

Khóa identity bắt buộc gồm:

```text
tenant_id + channel_account_id + provider_contact_id
```

Các identity chỉ được merge khi cùng `crm_customer_id`, agent xác nhận, hoặc tenant bật
một matching rule có confidence đủ cao. Không merge chỉ dựa trên display name.

### 4.3 Các nhóm bảng mục tiêu

| Nhóm | Bảng chính | Vai trò |
|---|---|---|
| Kết nối | `chat_channel_connections` | OAuth/token grant, người kết nối, status và refresh metadata |
| Account | `chat_channel_accounts`, `chat_inboxes` | OA/page/bot, capability, health, policy và inbox ownership |
| Contact | `chat_contacts`, `chat_contact_identities` | Customer nội bộ và identity theo account |
| Hội thoại | `chat_conversations`, `chat_conversation_participants` | Workflow, assignment, bot state, per-user read/follow state |
| Tin nhắn | `chat_messages`, `chat_attachments` | Nội dung normalized, reply-to, rich media và provider IDs |
| Lifecycle | `chat_message_status_events` | Append-only status history |
| Xử lý | `chat_inbound_events`, `chat_jobs`, `chat_domain_outbox` | Durable ingest, retry, dead-letter và realtime |
| Vận hành | `chat_saved_replies`, `chat_notes`, `chat_tags`, `chat_audit_logs` | Agent tools và audit |
| AI | `chat_ai_runs`, `chat_conversation_summaries`, `chat_extracted_facts` | Trace, memory và extracted data |

### 4.4 Conversation state

Conversation giữ các trường chính:

- `inbox_id`, `channel_account_id`, `contact_id`, `provider_thread_id`;
- `workflow_status`: new, open, pending, resolved;
- `archived_at` độc lập với workflow status;
- `bot_state`: enabled, paused, disabled;
- `bot_resume_at`, pause/handoff reason;
- `assigned_team_id`, `assigned_user_id`;
- `last_message_at`, `last_customer_message_at`, first-response timestamps;
- `row_version` phục vụ optimistic concurrency.

Read/follow state của agent nằm ở `chat_conversation_participants`, không dùng một unread flag
chung cho toàn tenant. Provider-side read state vẫn được giữ riêng.

### 4.5 Message và event semantics

`chat_messages` giữ current state để đọc nhanh. `chat_message_status_events` giữ lịch sử
append-only để audit và debug.

Message lifecycle gồm received/inbound creation và outbound states queued, sent, delivered,
seen, failed. Click và ref-link là interaction/referral events, không phải delivery status.

Unique constraints tối thiểu:

- Inbound event theo account và provider event ID;
- Message theo inbox/account và provider message ID;
- Conversation theo inbox và provider thread ID;
- Job theo idempotency key.

### 4.6 Credential storage

Secret tiếp tục được mã hóa trong credential store hiện tại. PostgreSQL chỉ giữ stable
`channel_account_id`, metadata không nhạy cảm và `credential_key`. Không copy access token
vào chat database hoặc log.

Do credential metadata và chat data có thể ở hai database khác nhau, một reconciliation job
phải phát hiện orphan, missing credential hoặc status lệch. Không giả lập distributed transaction.

### 4.7 Additive migration

1. Tạo bảng/cột mới không phá schema cũ;
2. Sinh stable channel account ID cho từng credential hiện có;
3. Backfill connection, account, inbox và contact identity;
4. Báo cáo record không xác định được account hoặc bị collision;
5. Dual-write schema cũ/mới trong giai đoạn chuyển tiếp;
6. Chuyển read path bằng feature flag theo tenant;
7. Đối soát count, account ownership và outbound routing;
8. Ngừng dual-write nhưng giữ legacy columns qua ít nhất một release cycle;
9. Cleanup bằng migration riêng sau rollback window.

Record mơ hồ được đánh dấu `migration_attention`; không tự merge và không chọn account ngẫu nhiên.

---

## 5. Inbox, contact và composer

### 5.1 Conversation list

Inbox dùng cursor pagination và SignalR incremental updates. Bộ lọc bắt buộc:

- Channel và một/nhiều channel account;
- New/open/pending/resolved/archived;
- Assigned to me, user khác, team hoặc unassigned;
- Unread, followed, bot/human handling;
- Contact blocked;
- Tags và custom fields;
- Chưa có agent reply;
- Tìm theo tên, phone, email, provider ID hoặc nội dung gần đây.

Mỗi item hiển thị channel/account badge, assignee/team, unread state, bot state, last message,
last activity, SLA/priority nếu có và trạng thái send failure.

### 5.2 Collaboration actions

- Atomic claim conversation chưa có assignee;
- Assign/transfer user hoặc team;
- Mark read/unread theo từng agent;
- Follow/unfollow;
- Pending, resolve, reopen, archive/unarchive;
- Pause bot có thời hạn hoặc vô hạn;
- Block/unblock contact;
- Internal note và audit timeline.

Assignment model cho phép một current team và một current user. Transfer phải dùng optimistic
concurrency/compare-and-set để hai agent không cùng claim. Assignment history được audit.

### 5.3 Contact panel

- Profile tổng hợp và avatar;
- Danh sách identity trên từng account/channel;
- CRM customer/lead link;
- Phone, email, locale, timezone và address;
- Tags, custom fields và notes;
- Conversation history đa kênh;
- Opt-in/subscribed/blocked state;
- Merge/split identity có xác nhận;
- Audit người sửa dữ liệu.

Dữ liệu agent chỉnh có precedence cao hơn provider profile có confidence thấp. Profile sync
không được âm thầm ghi đè dữ liệu đã xác nhận.

### 5.4 Composer

- Text, multiline và emoji;
- Saved reply với shortcut;
- Multiple attachments và preview;
- Image, video, audio và file;
- Reply-to message;
- Drag-and-drop và paste image;
- Upload/send progress;
- Optimistic message và realtime reconciliation;
- Retry failed logical message;
- Messaging-window warning;
- Hiển thị rõ account sẽ gửi.

Quick reply, button, card, carousel, location, typing và read action được bật theo
`ChannelCapabilities`. UI không hiện thao tác adapter/account không hỗ trợ.

### 5.5 Rendering

Mỗi message hiển thị sender type (contact, bot, agent, system hoặc API), channel account,
timestamp, reply context, lifecycle status, attachment, retry action và AI-generated badge.

Social comments dùng `message_type=comment`, parent/thread reference và post context. Like,
hide, edit và delete được để sau luồng DM core.

### 5.6 Permission

Permission tách riêng cho view, reply, claim/assign, contact management, block, channel
connection, AI policy và failed-job replay. User giới hạn theo team chỉ thấy inbox/conversation
được cấp quyền. Permission phải được kiểm tra ở endpoint, repository query và job execution;
không chỉ ẩn UI.

---

## 6. AI automation, CRM context và handoff

### 6.1 Policy hierarchy

```text
Tenant default → Inbox override → Conversation override
```

Mỗi inbox có thể dùng prompt, model, knowledge source, language, business hours, debounce,
pause duration, confidence threshold và handoff strategy khác nhau.

Bot state:

- `enabled`: được auto-reply;
- `paused`: tạm dừng đến thời điểm hoặc chờ resume;
- `disabled`: chỉ human handling.

Agent send không còn buộc pause cứng 30 phút; pause policy được cấu hình theo inbox hoặc chọn
khi handoff.

### 6.2 AI eligibility và race prevention

AI chỉ tạo reply khi:

- Inbox/conversation bật bot;
- Contact không bị block;
- Inbound event chưa tạo AI outcome;
- Không có agent reply mới hơn job;
- Assignment policy cho phép bot;
- Messaging window còn hiệu lực;
- Content type được hỗ trợ;
- Confidence và safety threshold đạt yêu cầu.

AI job có debounce để gộp message liên tiếp. Ngay trước outbound enqueue, processor phải đọc
lại conversation version, bot state và latest agent message. Agent can thiệp sẽ cancel AI send.

### 6.3 Context assembly

AI context gồm:

- System instruction/policy theo inbox;
- Rolling conversation summary;
- Recent messages theo token budget;
- Contact profile và facts đã xác nhận;
- CRM fields được allowlist;
- Knowledge retrieval theo tenant;
- Channel capability và output limit.

Summary có version và được tạo lại khi message thay đổi hoặc identity merge/split.

### 6.4 CRM integration

Qua abstraction như `ICrmContextProvider`, AI có thể đọc customer/lead, booking gần đây,
care status, owner/team và các field tenant cho phép.

Tour/travel extraction schema có thể gồm destination, departure/return date, passenger count,
adult/child, budget, departure point và service type. Giai đoạn đầu, AI ghi extracted facts
hoặc đề xuất cập nhật. Auto-write CRM chỉ được bật theo policy, allowlist và audit; thao tác
nhạy cảm cần agent approval.

### 6.5 AI tasks

- Auto reply;
- Suggested reply cho agent;
- Intent/language/sentiment classification;
- Contact/request extraction;
- Priority classification;
- Team routing và auto-assignment;
- Spam detection;
- Conversation summary;
- Suggested next action.

Không xây visual flow builder. Tenant dùng policy form và priority-ordered rules đơn giản.

### 6.6 Handoff

AI handoff khi user yêu cầu human, confidence thấp, sentiment xấu, câu hỏi lặp, nội dung
nhạy cảm, tool/CRM/knowledge lỗi, media không xử lý được hoặc retry quá giới hạn.

Handoff phải lưu reason, summary, extracted facts và suggested team; pause bot và phát realtime
notification. Không gửi fallback bịa đặt khi model/provider lỗi không xác định.

### 6.7 Guardrail và observability

- Tenant/inbox isolation cho prompt và tools;
- Không đưa credential/internal note vào model context;
- CRM fields dùng allowlist;
- Retrieved content không được override system policy;
- Write tool có allowlist, timeout và audit;
- AI logs mặc định redacted;
- `chat_ai_runs` lưu policy version, model, token, latency, estimated cost, tool calls,
  confidence, outcome và skip/failure reason.

Dashboard theo dõi automation rate, handoff rate, first-response time, AI failure và cost theo
tenant/inbox.

---

## 7. Channel adapter contract và roadmap

### 7.1 Adapter contract

Mỗi adapter phải công bố `ChannelCapabilities` và triển khai các trách nhiệm tương đương:

```text
ValidateWebhook
NormalizeInbound
SendMessage
HandleStatus
FetchContactProfile
GetMessagingWindow
GetAccountHealth
Connect / Refresh / Disconnect
```

Capability mô tả message types, quick reply/button/card/carousel, reply-to, typing, mark-read,
status callbacks, messaging window, template requirement, profile lookup, upload limits và
comment actions.

Không giả lập lifecycle provider không hỗ trợ. Adapter phải trả lỗi normalized để core quyết
định retry, disable hoặc yêu cầu operator xử lý.

### 7.2 Connection wizard

1. User OAuth hoặc nhập token;
2. Hệ thống lấy danh sách account có quyền;
3. User multi-select account cần import;
4. Mỗi account tạo một inbox;
5. Kiểm tra webhook subscription và test connection;
6. Hiển thị health, missing scopes, expiry và reconnect action.

Một account lỗi không làm dừng account khác trong cùng connection.

### 7.3 Channel priority

| Ưu tiên | Channel | Mục tiêu |
|---|---|---|
| P0 | Zalo | Multi-OA, correct routing, text/image/file/button, profile và seen handling |
| P0 | Messenger | OAuth multi-page, text/media/file, delivery/read/postback, typing và window rules |
| P0 | Telegram | Multi-bot, text/media/file/button, callback và honest capability mapping |
| P1 | Webchat | Widget, guest session, domain allowlist, realtime, upload và persistent menu |
| P2 | WhatsApp | Multi-number, template, media, delivery status và policy window |
| P2 | Instagram | Direct message trước, comment/thread sau |
| P3 | TikTok | OAuth/token refresh, direct message và media theo capability hiện hành |

Provider policy và API behavior phải được kiểm tra lại với tài liệu chính thức tại thời điểm
triển khai; không coi hành vi trong repo tham chiếu là nguồn quy định hiện hành.

---

## 8. Vận hành, bảo mật và retention

### 8.1 Operations dashboard

- Queue depth và oldest job age;
- Retry/dead-letter theo channel account;
- Last webhook time;
- Provider latency và error rate;
- Token expiry/revoked permission;
- SignalR event lag;
- AI latency, failure, handoff và cost;
- Attachment storage failure.

Operator có thể replay, cancel, disable account và test connection. Mọi action được audit.

### 8.2 Security

- Verify webhook signature và chống replay;
- Enforce tenant/account scope ở API, repository và worker;
- Encrypt token và cấm log secret;
- Validate attachment size, extension và real MIME;
- Quarantine file chưa xác minh khi cần;
- Private storage và signed URL;
- Redact AI logs;
- Delete/anonymize workflow xử lý contact, message, attachment và AI traces.

### 8.3 Retention

- Raw webhook: cấu hình theo tenant, mặc định đề xuất 30 ngày;
- Message history: theo tenant/data policy, không hard-delete trong migration đầu;
- Attachment: theo message retention và storage policy;
- AI trace: retention riêng, redacted, không mặc định giữ full prompt có PII;
- Audit: retention đủ cho yêu cầu vận hành/compliance của tenant.

### 8.4 Rollout controls

- Feature flag read/write schema mới theo tenant;
- AI có `shadow`, `suggestion` và `auto-reply` mode;
- Canary một OA/page trước khi mở toàn tenant;
- Có thể tắt realtime, AI hoặc một adapter riêng;
- Migration có reconciliation report và rollback path.

---

## 9. Chiến lược kiểm thử

### 9.1 Unit tests

- Webhook verification/normalization;
- Messaging-window rules;
- Capability mapping;
- Retry classification;
- AI eligibility, debounce và handoff;
- Contact identity matching;
- Permission và state transitions.

### 9.2 Adapter contract tests

Một suite dùng chung xác nhận:

- Inbound normalize đúng;
- Provider/event ID được giữ;
- Duplicate không tạo message;
- Unsupported content được báo đúng;
- Auth/rate-limit/permanent error được phân loại;
- Outbound dùng đúng account;
- Adapter không báo delivered/seen nếu provider không xác nhận được.

### 9.3 Integration tests

Chạy với PostgreSQL test database, ưu tiên Testcontainers khi môi trường CI hỗ trợ:

- Transactional inbound/outbox;
- Lease và concurrent consumers;
- Crash/restart recovery;
- Conversation ordering;
- Multi-account isolation;
- Migration/backfill;
- Domain event chỉ phát sau commit;
- Reconnect cursor khi mất Redis/SignalR.

### 9.4 Embedded end-to-end tests

Với `ChatProcessing.Mode=Embedded` và `AdapterMode=Fake`, test toàn tuyến:

1. Gửi cùng webhook giả hai lần;
2. Webhook chỉ thành công sau durable commit;
3. Database chỉ có một logical message;
4. Embedded consumer tạo contact/conversation đúng account;
5. UI nhận SignalR event;
6. Agent reply tạo queued message;
7. Fake adapter chuyển message sang sent;
8. Fake status callback chuyển delivered/seen/failed;
9. Restart giữa persist và process vẫn khôi phục job.

Fake test endpoints chỉ tồn tại trong Development/Test và phải bị vô hiệu hóa ở production.

### 9.5 Provider smoke tests

Mỗi channel có sandbox/test account để kiểm tra webhook thật, token refresh, text, attachment,
status callback, rate limit và window behavior.

---

## 10. Non-functional requirements

- Webhook acknowledgement chỉ sau durable commit và phải đủ nhanh để provider không timeout;
- Realtime event đến connected agent trong khoảng 2 giây sau commit ở tải bình thường;
- Queue processing có backpressure và không làm cạn connection pool;
- API list/detail dùng cursor, index theo tenant/inbox/status/activity;
- Mọi background operation có correlation ID, tenant, account và conversation context;
- Redis outage không làm mất message;
- Một worker/process chết không làm job pending bị kẹt vĩnh viễn;
- Multi-account data không được cross-route hoặc cross-tenant;
- Upload và AI workloads có limit theo tenant;
- Mọi migration có dry-run/reconciliation report trước cutover.

---

## 11. Lộ trình cấp cao

Đây là thứ tự phụ thuộc, chưa phải implementation plan chi tiết:

1. Foundation: schema, multi-account identity, durable inbound/outbound và lifecycle;
2. Embedded processor/fake harness và contract tests;
3. Realtime, cursor pagination và UI store reconciliation;
4. Collaboration, team assignment, per-user read và audit;
5. Contact/CRM, tags, notes, custom fields và saved replies;
6. Composer/rich media/capability-driven UI;
7. AI memory, policy, extraction, handoff và observability;
8. Webchat;
9. WhatsApp, Instagram, social comments và TikTok theo priority;
10. Operational hardening, load tests, runbook và staged rollout.

Implementation plan phải chia các bước nhỏ, có file/symbol cụ thể, CodeGraph impact trước mọi
thay đổi symbol, test-first cho feature/bugfix và checkpoint sau từng vertical slice.

---

## 12. Definition of Done

Một phase chỉ được coi là hoàn thành khi:

- Không còn inbound webhook chạy bằng fire-and-forget `Task.Run`;
- Duplicate/restart/concurrency tests đạt;
- Không gửi sai OA/page/bot;
- Provider message ID và status callback được correlate;
- UI hiển thị lỗi/retry/reconnect rõ ràng;
- Adapter contract tests đạt cho channel trong phase;
- Permission, audit, metrics và structured logs đã có;
- Migration có đối soát và rollback path;
- Runbook cho token expired, queue backlog, provider outage và Redis outage đã được viết;
- Với AI auto-reply, shadow/suggestion acceptance và cancellation-race tests đã đạt.

---

## 13. Rủi ro và biện pháp giảm thiểu

| Rủi ro | Giảm thiểu |
|---|---|
| Dữ liệu cũ không xác định đúng account | Additive migration, collision report, `migration_attention`, không auto-merge |
| Embedded worker tranh tài nguyên với API | Giới hạn concurrency, backpressure, health metrics; production chuyển WorkerOnly |
| Redis outage | PostgreSQL source of truth và reconnect cursor |
| AI trả lời sau khi agent can thiệp | Conversation version check và pre-send eligibility recheck |
| Provider API/policy thay đổi | Capability contract, official-doc verification và sandbox smoke test |
| Credential/chat metadata lệch giữa DB | Stable account ID và reconciliation job |
| Copy nhầm enterprise code | Behavioral clean-room spec, code review và provenance discipline |
| Scope lan sang flow/campaign | Giữ non-goals và tách proposal riêng nếu phát sinh |
