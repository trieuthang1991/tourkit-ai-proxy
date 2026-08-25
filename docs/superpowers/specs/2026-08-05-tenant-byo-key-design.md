# Spec — BYO AI key per-tenant ("chỉ bán nền tảng, không bán key AI")

**Ngày:** 2026-08-05 · **Phạm vi:** TẤT CẢ lệnh AI của tenant (chat/travai, review, mail, deal, tour, visa)

> ⏸️ **KHÔNG ƯU TIÊN** (user chốt 2026-08-11). Thiết kế + kế hoạch đã xong, cất chờ — xem [kế hoạch P4](../plans/2026-08-05-tenant-byo-key.md) khi quay lại. Đừng tự khởi động lại nếu không được yêu cầu.
>
> Lưu ý khi mở lại: quyết định về ripple "nạp quota AI qua VietQR" (§8) **vẫn còn để ngỏ**.

## 1. Mô hình kinh doanh & mục tiêu

Nền tảng **bán phần mềm, không bán lượt AI**. Mỗi tenant **tự mang key AI** (tự trả tiền nhà cung cấp). Key hệ thống chỉ dùng cho **TRIAL** (dùng thử có giới hạn); hết trial → tenant **bắt buộc** nhập key riêng để dùng tiếp.

- **Trial:** key hệ thống + trừ `dbo.TenantQuota` (giữ nguyên subsystem quota hiện tại, đổi ngữ nghĩa thành "giới hạn dùng thử").
- **BYO:** tenant cấu hình `{provider, model, apiKey}` → MỌI lệnh AI của tenant chạy bằng key đó, **không trừ quota** (họ trả upstream).

### Non-goals
- Không đổi cách 1 feature chọn path native/json — dual-path đã tự thích ứng provider.
- Không quyết số phận luồng "nạp quota AI" VietQR trong spec này (xem §8 — quyết định riêng).
- Không hỗ trợ nhiều bộ key/tenant ở bản đầu (1 cấu hình active/tenant; nâng lên multi-provider sau nếu cần).

## 2. Hiện trạng (code seam)

- [`ProviderKeyStore.Get(providerId)`](../../../TourkitAiProxy.Services/Providers/ProviderKeyStore.cs) đọc key **chỉ từ `Providers:{X}:ApiKey`/env** — **không có chiều tenant**, 1 key chung mọi tenant.
- Mỗi provider (`OpenCodeProvider`/`NineRoutesProvider`/`OpenAIProvider`/`AnthropicProvider`/…) ở đầu `CompleteAsync`/`StreamAsync`: **check quota** (`EnsureQuota()`) + resolve key (`req.ApiKey` → `ProviderKeyStore`). → **Đây là seam chung cho cả key lẫn quota.**
- Tenant context: `AiCallContext.Push(feature, tenantId[, sessionId])` (nền/workflow) + `HttpTenantContext`/`ITenantContext` (web). Quota consume ở `LogUsage` khi `status=ok` + có tenant. Hết → `QuotaExhaustedException` → [`QuotaExceptionMiddleware`](../../../TourkitAiProxy.Services/Quota/QuotaExceptionMiddleware.cs) → 429.
- `Crypton` (AES-256/CBC) sẵn có để mã hóa key at-rest (giống `MailAccounts.AppPasswordEnc`, `TenantServiceAccounts.PasswordEnc`).

## 3. Kiến trúc

```
AI call (bất kỳ feature)
  └─ Provider.CompleteAsync/StreamAsync (seam chung)
       ├─ TenantAiKeyResolver.Resolve(tenantId, defaultProvider, defaultModel)
       │     → (provider, model, key, usesTenantKey)
       ├─ usesTenantKey=true  → dùng key tenant, SKIP EnsureQuota + không consume
       └─ usesTenantKey=false → key hệ thống + EnsureQuota + consume (trial)
```

- **1 seam:** đặt resolver ở tầng provider (5 provider) → phủ tự động mọi feature, không phải sửa N feature.
- **Override provider/model:** nếu tenant cấu hình provider khác default của feature → resolver trả provider/model của tenant; caller (ProviderRegistry) route sang provider đó. (Chi tiết wiring override để kế hoạch chốt — có thể cần intercept trước `ProviderRegistry.Resolve` cho case đổi provider.)

## 4. Data model — `dbo.TenantAiKeys`

PK `TenantId` (bản đầu 1 config/tenant). Schema idempotent trong `TourkitAiDb.SchemaSql`:

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `TenantId` | nvarchar(128) PK | tenant |
| `Provider` | nvarchar(32) | id provider (`openai`/`anthropic`/`deepseek`/`grok`/…) |
| `Model` | nvarchar(128) | model mặc định tenant chọn (null → default của provider) |
| `ApiKeyEnc` | nvarchar(1024) | key mã hóa Crypton — KHÔNG bao giờ trả client/log |
| `Enabled` | bit | bật BYO (tắt → về trial key hệ thống) |
| `ValidatedAtUtc` | datetime2 | lần cuối validate key OK |
| `UpdatedBy`/`UpdatedAtUtc` | | audit |

Repository Dapper + `TenantAiKeyStore` cache in-mem per-tenant (TTL ngắn + invalidate khi PUT), giống `TenantAiProfileStore`.

## 5. Resolver — hợp đồng

```
record TenantAiResolution(string Provider, string? Model, string? ApiKey, bool UsesTenantKey);
TenantAiResolution Resolve(string? tenantId, string defaultProvider, string? defaultModel);
```
- tenantId rỗng / không có config / `Enabled=false` → `(defaultProvider, defaultModel, null, false)` (giữ hành vi hiện tại → ProviderKeyStore + quota).
- Có config bật + validate OK → `(config.Provider, config.Model ?? defaultModel, Crypton.Decrypt(ApiKeyEnc), true)`.
- Key decrypt lỗi → coi như không có (fallback trial + log warning).

## 6. Quota = TRIAL (đổi ngữ nghĩa, không xóa)

- `EnsureQuota()` + consume **chỉ áp khi `UsesTenantKey=false`**.
- Hết quota + chưa có key tenant → `QuotaExhaustedException` với **thông điệp mới**: "Hết lượt dùng thử — vui lòng nhập key AI riêng để tiếp tục" + payload trỏ trang cấu hình key. Middleware giữ 429, đổi body message + thêm cờ `needsByoKey:true`.
- `UsesTenantKey=true` → không đụng quota (dùng thoải mái). Vẫn **log `AiUsageHistory`** (analytics) nhưng đánh dấu `byo=true` để tách chi phí.

## 7. Bảo mật & validate

- Key **mã hóa Crypton** khi lưu; endpoint GET chỉ trả `{provider, model, configured:true, masked:"sk-…abcd", validatedAtUtc}` — **không** trả key thô.
- **Validate khi lưu:** gọi 1 lệnh AI test ngắn bằng key mới (giống service-account validate login). Fail → không lưu, trả lỗi rõ ("key không hợp lệ / hết hạn / sai provider").
- **Không log** key (kể cả trace/debug). Tuân log-redaction hiện có.
- Tenant chỉ sửa key của **chính mình** (endpoint tenant-scoped `X-Session-Id`).

## 8. Ripple cần quyết riêng — luồng bán quota VietQR

`dbo.QuotaOrders` + Tingee + admin top-up + chip "nạp quota" hiện là **bán lượt AI** — mâu thuẫn "chỉ bán nền tảng". Sau khi chuyển BYO, ba lựa chọn (quyết trong 1 phiên riêng):
1. **Bỏ hẳn** luồng nạp-quota (BYO thay thế hoàn toàn).
2. **Giữ như "gia hạn trial"** (bán thêm lượt trial cho ai chưa muốn BYO).
3. **Đổi thành bán chỗ-ngồi/gói nền tảng** (không liên quan lượt AI).

Spec này **không** thực thi thay đổi đó — chỉ đánh dấu phụ thuộc.

## 9. UI + endpoint

- **Endpoint** (tenant-scoped `X-Session-Id`):
  - `GET  /api/v1/assistant/ai-key` → `{provider, model, configured, masked, enabled, validatedAtUtc}` (không trả key thô).
  - `PUT  /api/v1/assistant/ai-key` → `{provider, model, apiKey, enabled}` → **validate** → lưu (Crypton) → `{ok, validated, masked}`; fail → `{ok:false, error}`.
  - `DELETE /api/v1/assistant/ai-key` → xóa → về trial.
- **UI:** trang/panel cấu hình key: chọn provider (dropdown từ `/api/v1/providers`), chọn model, dán key, nút "Kiểm tra & lưu". Chip topbar: "Trial x/1000" (key hệ thống) ↔ "Key riêng ✓" (BYO). Khi 429 `needsByoKey` → modal mời nhập key.

## 10. Test
- **Unit (không cần DB):** `TenantAiKeyResolver.Resolve` — 4 nhánh (no tenant / disabled / enabled-valid / decrypt-fail); message 429 khi hết trial + không BYO.
- **Manual E2E:** (1) tenant chưa BYO → dùng trial, quota trừ; (2) hết trial → 429 `needsByoKey`; (3) nhập key sai → validate fail, không lưu; (4) nhập key đúng → validate OK, dùng AI không trừ quota; (5) tắt BYO → về trial.

## 11. Câu hỏi mở
- §8 (số phận VietQR nạp-quota) — quyết ở phiên riêng.
- Multi-provider/tenant (nhiều key) — hoãn; bản đầu 1 config/tenant.
