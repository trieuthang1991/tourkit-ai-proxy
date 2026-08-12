# Tingee — Tài liệu tham chiếu từ bản WEB (source of truth)

> Trích xuất từ codebase web `tourkit/` (KojiCRM) — bản **đã chạy thật, xử lý tiền thật**.
> Mục đích: làm chuẩn để soi lại + sửa phần Tingee trong `tourkit-ai-proxy` (bản AI).
> Nguồn: [`WebhookController.cs`](../../tourkit/CMS/KojiCRM/PublicAPI/WebhookController.cs) (class `TingeeWebhookController`, dòng 834–1091) + [`AppSettings.config`](../../tourkit/CMS/KojiCRM/Configs/AppSettings.config) dòng 70.

---

## 0. Kết luận quan trọng nhất (đọc trước)

**Tingee KHÔNG phải cổng tạo QR.** Trong bản web, Tingee chỉ đóng vai trò **giám sát biến động số dư tài khoản ngân hàng + bắn webhook IPN** khi có tiền vào. Cụ thể:

- Bản web **không có** bất kỳ call outbound nào tới `api.tingee.vn` để "tạo QR". Grep toàn repo `tourkit/` chỉ thấy Tingee ở đúng 2 chỗ: `WebhookController.cs` (nhận IPN) + `AppSettings.config` (1 secret key).
- QR thanh toán ở web được sinh bởi **BankHub** (`bankhub.mistudio.asia`) hoặc **ABBank** (`apiconnectabbank.tourkit.vn`) — KHÔNG phải Tingee.
- Tài khoản nhận (kể cả VA - virtual account) là **cố định, pre-provisioned**, lưu sẵn trong bảng `PaymentMethods`. Không gọi API tạo VA lúc runtime.

➡️ **Hệ quả cho bản AI:** phần "cần code cho thật" KHÔNG phải là `CreateQrAsync` gọi Tingee. Sinh VietQR động qua `img.vietqr.io` (mock đang làm) đã đúng bản chất. Cái phải làm chuẩn để "ăn tiền" là **verify chữ ký webhook + parse đúng payload + trả đúng response code** theo hợp đồng Tingee dưới đây.

---

## 1. Endpoint webhook

| Thuộc tính | Giá trị (web) |
|---|---|
| Route | `POST /api/hooks/tingee` |
| Controller | `TingeeWebhookController.Receive()` |
| Auth | KHÔNG có session — xác thực bằng **HMAC-SHA512** trên header |
| Content-Type | `application/json` (đọc raw body) |
| HTTP status trả về | **LUÔN `200 OK`** — kể cả khi lỗi/sai chữ ký. Kết quả nằm trong body (`code`). |

> ⚠️ Tingee dùng **HTTP status để biết đã nhận**, và **`code` trong body để biết xử lý ra sao**. Trả non-200 → Tingee coi là fail và **retry** (bắn lại webhook) → nguy cơ cộng tiền/quota nhiều lần. **Phải luôn 200.**

---

## 2. Xác thực chữ ký (HMAC — BẮT BUỘC đúng từng chi tiết)

```csharp
// Headers Tingee gửi kèm:
//   x-signature           : chữ ký hex
//   x-request-timestamp   : timestamp (chuỗi)
//
// Chuỗi ký = "{timestamp}:{rawBody}"   (timestamp + dấu ':' + nguyên văn body)
// Thuật toán = HMAC-SHA512
// Secret     = AppSettings "ApiKeyTingee"  (plain-text UTF8, KHÔNG base64-decode)
// Output     = BitConverter hex, ToLower(), so sánh OrdinalIgnoreCase

var expectedSig = ComputeHmacSha512($"{timestamp}:{rawBody}", secretKey);
if (!string.Equals(signature, expectedSig, StringComparison.OrdinalIgnoreCase)) { /* reject */ }

private static string ComputeHmacSha512(string data, string secret)
{
    var keyBytes = Encoding.UTF8.GetBytes(secret ?? "");        // secret dạng plain UTF8
    using (var hmac = new HMACSHA512(keyBytes))
    {
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return BitConverter.ToString(hash).Replace("-", "").ToLower();  // hex lowercase
    }
}
```

**Điểm chết người nếu làm sai:**
1. Thuật toán là **SHA512**, KHÔNG phải SHA256.
2. Chuỗi ký gồm **cả timestamp** (`"{timestamp}:{rawBody}"`), KHÔNG phải chỉ body.
3. Header là **`x-signature`** + **`x-request-timestamp`** (chữ thường), KHÔNG phải `X-Tingee-Signature`.
4. Secret là **UTF8 plain-text** đưa thẳng vào HMAC key (không base64-decode) — dù giá trị trông giống base64.
5. Thiếu 1 trong 2 header → trả `code="09"` (vẫn HTTP 200).

---

## 3. Schema payload (Tingee → mình)

```csharp
public class RequestCreatePaymentVoucherTingee
{
    public string  clientId        { get; set; }  // Merchant ID
    public string  transactionCode { get; set; }  // Mã giao dịch Tingee (idempotency key)
    public decimal amount          { get; set; }  // Số tiền (DECIMAL, không phải long)
    public string  content         { get; set; }  // Nội dung CK (chứa mã đối soát ở cuối)
    public string  bank            { get; set; }  // Tên ngân hàng
    public string  accountNumber   { get; set; }  // Số TK nhận (thật)
    public string  vaAccountNumber { get; set; }  // Số TK ảo (virtual account)
    public string  transactionDate { get; set; }  // yyyyMMddHHmmss
    public List<object> additionalData { get; set; }  // [{name, value}]
}
```

Ghi chú:
- **`amount` là `decimal`** (tiền VND). Bản AI đang dùng `long?` → cần cân nhắc, ít nhất parse an toàn.
- **`content`** là trường mấu chốt để match đơn (không có field `orderId` riêng).
- **`transactionCode`** nên dùng làm **khóa idempotency** (chống cộng tiền 2 lần khi Tingee retry).
- Tài khoản nhận có thể là `accountNumber` **hoặc** `vaAccountNumber` — web match cả hai.

---

## 4. Schema response (mình → Tingee)

```csharp
public class ResponseTingee
{
    public string code    { get; set; } = "00";       // mã kết quả
    public string message { get; set; } = "success";
}
```

Bảng mã dùng ở web:

| code | Ý nghĩa | Khi nào |
|---|---|---|
| `"00"` | Thành công | Ghi nhận tiền OK |
| `"01"` | Không tìm thấy tài khoản nhận | `accountNumber`/`vaAccountNumber` không khớp bảng TK |
| `"02"` | Đã cập nhật (idempotent) | (định nghĩa trong comment) |
| `"09"` | Sai/thiếu chữ ký | HMAC fail hoặc thiếu header |
| `"99"` | Lỗi hệ thống | Exception |

> Tất cả đều trả kèm **HTTP 200**.

---

## 5. Logic match giao dịch → đơn (web)

Web bóc mã đối soát (`reservation_code`) từ `content` theo thứ tự ưu tiên:

1. Split `content` theo khoảng trắng, tìm token **`ND`**; lấy token kế tiếp, **bỏ qua nhiễu**: `QR`, `-`, và `FT\d+` (mã giao dịch ngân hàng tự chèn).
2. Fallback: regex `\b([A-Z0-9]{6})\b` (mã 6 ký tự).
3. Fallback cuối: token cuối cùng của `content`.

Sau khi có `reservation_code` → tra `TourCustomers` → dựng `PaymentVoucher` gắn vào `Order`. Nếu không match được (catch) → vẫn tạo phiếu "treo" `Status=106`, `Note=content` để người dùng xử lý tay (**không mất tiền, không bỏ sót**).

> Với bản AI, memo có format riêng (`TKAI-xxxxxx-xxxxxxxx-xxxx`) nên logic bóc token khác — nhưng **triết lý phải giữ**: match được thì cộng; không match thì ghi nhận "treo" + log, KHÔNG nuốt mất giao dịch.

---

## 6. Audit & side-effects (web làm gì khi nhận tiền)

1. Lưu **raw payload** vào `TransferHistory` (`type = Tingee = 3`) để đối soát/debug — làm TRƯỚC khi xử lý.
2. Tạo `PaymentVoucher` (phiếu thu) gắn Order.
3. Tăng `ScanQuotaUsed` trong `config_company` + `CacheStore.InvalidateConfig()` (đồng bộ UI).
4. Nếu bật auto-accept → tự tạo `LichSuTheoDoi` (lịch sử phê duyệt).

---

## 7. Cấu hình

```xml
<!-- AppSettings.config -->
<add key="ApiKeyTingee" value="p177PpOi17sLxqfqYJw+0HX71MSG3oWtmijzwjgGPLA=" />
<!-- Secret token lấy từ https://app.tingee.vn/m/developers -->
```

- **Chỉ 1 secret duy nhất** (`ApiKeyTingee`) — dùng cho verify HMAC webhook. KHÔNG có "ApiKey riêng để gọi outbound" vì không có call outbound.
- Portal quản lý merchant/secret: `https://app.tingee.vn/m/developers`.

---

## 8. Bảng đối chiếu WEB ↔ AI (những chỗ AI đang LỆCH)

| Hạng mục | WEB (đúng, thật) | AI proxy hiện tại | Cần sửa? |
|---|---|---|---|
| Vai trò Tingee | Webhook IPN-only | Giả định có "CreateQR API" (`TingeeHttpClient`) | ✅ Bỏ giả định; QR = VietQR |
| Thuật toán HMAC | **SHA512** | SHA256 | ✅ **PHẢI sửa** |
| Chuỗi ký | `"{timestamp}:{rawBody}"` | chỉ `rawBody` | ✅ **PHẢI sửa** |
| Header chữ ký | `x-signature` | `X-Tingee-Signature` | ✅ **PHẢI sửa** |
| Header timestamp | `x-request-timestamp` | (không dùng) | ✅ **PHẢI thêm** |
| Secret encoding | UTF8 plain | UTF8 plain | ✔️ khớp |
| Config key | `ApiKeyTingee` | `Tingee:WebhookSecret` + `Tingee:ApiKey` | ⚠️ gộp/đổi tên cho rõ |
| Payload field tiền | `amount` (decimal) | `Amount` (long?) | ✅ đổi tên+kiểu |
| Payload field nội dung | `content` | `Description` | ✅ **PHẢI sửa** |
| Payload field mã GD | `transactionCode` | `TransactionId` | ✅ đổi tên (idempotency) |
| Payload field TK | `accountNumber` + `vaAccountNumber` | `AccountNumber` | ✅ thêm VA |
| Response body | `{code, message}` | `{ok, error}` / `{error}` | ✅ **PHẢI sửa** |
| HTTP status | **luôn 200** | trả 400/401/500 khi lỗi | ✅ **PHẢI sửa** (tránh Tingee retry) |
| Idempotency | qua `transactionCode` | qua order status `paid` | ⚠️ nên thêm khóa `transactionCode` |
| Audit raw | lưu `TransferHistory` trước xử lý | có `TingeeRaw` trên order khi paid | ⚠️ nên lưu raw cả khi không match |

Các mục ✅ là **bắt buộc sửa để webhook thật của Tingee hoạt động** (nếu không, mọi webhook thật sẽ bị AI reject vì sai chữ ký/sai field, tiền vào mà quota không cộng).
