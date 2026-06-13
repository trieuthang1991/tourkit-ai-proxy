# CRM ↔ TRAV-AI — Quick Integration

Tài liệu rút gọn cho CRM (.NET 4.8) tích hợp TRAV-AI:

1. Nhúng widget script lên website.
2. Mã hoá Crypton (.NET 4.8) — dùng khi gọi API cần token.
3. Gọi 2 endpoint để load review **khách hàng** + **cơ hội (deal)**.

Base URL: `https://tourkit-ai.tourkit.vn`

---

## 1. Nhúng widget

2 cách, chọn 1:

### Cách A — Auto-init bằng Crypton token (đơn giản nhất)

```html
<script async src="https://tourkit-ai.tourkit.vn/widget.js"
  data-login-token="<Crypton-encrypted JSON {username,password,domain}>"></script>
```

Widget tự gọi `/api/v1/widget/init` lần đầu, cache `trav_xxx` vào `localStorage` của browser, lần sau reuse. CRM chỉ cần gen Crypton token (xem §2) rồi paste.

### Cách B — Widget token sẵn (đã gọi /init trước)

```html
<script async src="https://tourkit-ai.tourkit.vn/widget.js"
  data-token="trav_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"></script>
```

`trav_xxx` lấy từ response của `POST /api/v1/widget/init`. Dùng khi không muốn để Crypton token (chứa creds) hiện trong HTML của trang.

### Optional data-attrs

```html
data-bot-name="Trợ lý ABC"   data-greeting="Xin chào!"
data-color="#F97316"          data-position="br"   (br | bl)
data-z-index="2147483600"
```

---

## 2. Mã hoá Crypton (.NET 4.8)

Khi gọi `POST /api/v1/widget/init` (onboard) — body cần `token = Crypton.Encrypt(JSON{username,password,domain})`. Hằng số bắt buộc khớp 100% với server:

| | Giá trị |
|---|---|
| PassPhrase | `Pas5pr@se` |
| Salt | `s@1tValue` |
| IV | `@1B2c3D4e5F6g7H8` (16 bytes ASCII) |
| Key derivation | `PasswordDeriveBytes` (SHA1, iterations=2) |
| Mode / Padding | CBC / PKCS7 |
| Output | Base64 |

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class Crypton
{
    private const string PassPhrase = "Pas5pr@se";
    private const string SaltValue  = "s@1tValue";
    private const string InitVector = "@1B2c3D4e5F6g7H8";
    private const int    KeySize    = 256;
    private const int    Iterations = 2;

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        byte[] iv    = Encoding.ASCII.GetBytes(InitVector);
        byte[] salt  = Encoding.ASCII.GetBytes(SaltValue);
        byte[] plain = Encoding.UTF8.GetBytes(plainText);

        using (var pwd = new PasswordDeriveBytes(PassPhrase, salt, "SHA1", Iterations))
        {
            byte[] key = pwd.GetBytes(KeySize / 8);
            using (var aes = new RijndaelManaged { Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 })
            using (var enc = aes.CreateEncryptor(key, iv))
            using (var ms  = new MemoryStream())
            {
                using (var cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                {
                    cs.Write(plain, 0, plain.Length);
                    cs.FlushFinalBlock();
                }
                return Convert.ToBase64String(ms.ToArray());
            }
        }
    }
}
```

---

## 3. Review Khách hàng — JSON & cách render

Proxy trả về object `review` với các field sau. CRM map sang HTML để hiển thị trong popup/drawer chi tiết KH.

### 3.1 Đặc tả từng field

| Field | Kiểu | Giá trị | Render gợi ý |
|---|---|---|---|
| `rank` | string | `A` \| `B` \| `C` \| `D` | Badge tròn 26px: A=`#16a34a` · B=`#2563eb` · C=`#f59e0b` · D=`#dc2626` (chữ trắng) |
| `rankReason` | string | Lý do xếp hạng (1-2 câu) | Dòng italic nhỏ dưới badge: *"Lý do xếp hạng A: …"* |
| `summaryLine` | string | Tóm tắt 1 câu | Subtitle 13px dưới tên KH |
| `alert.level` | string | `high` \| `medium` \| `none` | `none` → không render. `high` → banner đỏ `#fef2f2` viền trái `#dc2626`. `medium` → vàng `#fef3c7` viền `#f59e0b` |
| `alert.message` | string \| null | Nội dung cảnh báo | Body của banner (chỉ render khi `level ≠ none`) |
| `portrait` | string | Đoạn văn 2-4 câu mô tả chân dung KH | Section "Chân dung" — `<p>` 13px line-height 1.6 |
| `strengths` | string[] | Mảng điểm sáng | Card xanh `#f0fdf4`, header "✓ ĐIỂM SÁNG", `<ul>` bullet |
| `concerns` | string[] | Mảng điểm cần lưu ý | Card vàng `#fefce8`, header "⚠ CẦN LƯU Ý", `<ul>` |
| `preferences` | string | Sở thích du lịch | Section "Sở thích & thói quen" — paragraph |
| `actionNow.task` | string | Việc cần làm NGAY | Card nền gradient xanh `#ecfdf5→#d1fae5`, viền trái `#10b981`, chữ 14px bold |
| `actionNow.reason` | string | Vì sao nên làm | Dòng italic 12px dưới task |
| `action30Days` | string[] | Mảng gợi ý 30 ngày | Section "Gợi ý 30 ngày tới" — `<ul>` bullet |
| `productSuggestions` | string[] | Mảng tên tour gợi ý | Section "Gợi ý sản phẩm" — chip pill 12px (`border-radius: 14px`, border xám) |
| `generatedAt` | string ISO | Thời điểm review | Footer 11px màu xám: "Cập nhật N giờ trước" |
| `aiModel` / `aiProvider` | string | Tên model + provider | Footer 11px: "deepseek:deepseek-chat" — debug only |
| `tokensIn` / `tokensOut` | int | Token tiêu | Footer debug |
| `feedback` | object \| null | `{rating, note, submittedAt}` | Đã có feedback → ẩn 2 nút 👍/👎, hiện text rating |

### 3.2 Khi review = null

KH chưa được chấm AI → render khung trống + nút **"Chấm AI"**. Nút này gọi:
```
POST https://tourkit-ai.tourkit.vn/api/v1/reviews/customer/{customerId}
```
→ Trả `{review, fromCache}`. Tốn 1 lượt quota AI.

### 3.3 Thứ tự render (khớp UI thực tế)

```
┌─────────────────────────────────────────────────┐
│ [T]  Tên KH                          [Mới] [C] × │  ← Header
│      55905 · Việt Nam · KH từ —                  │
├─────────────────────────────────────────────────┤
│ CHÂN DUNG                                        │
│ <portrait>                                       │
│ Lý do xếp hạng C: <rankReason>                   │
│                                                  │
│ ┌────────┬────────┬────────┬────────┐            │
│ │TỔNG CHI│SỐ TOUR │  AOV   │ĐƠN CUỐI│  ← 4-stat │
│ │  0đ    │   0    │   0đ   │   —    │   grid    │
│ └────────┴────────┴────────┴────────┘            │
│                                                  │
│ ╔════ 🎯 VIỆC CẦN LÀM NGAY ═══════════════════╗  │
│ ║ <actionNow.task>                            ║  │  ← Box xanh
│ ║ <actionNow.reason>                          ║  │
│ ╚═════════════════════════════════════════════╝  │
│                                                  │
│ GỢI Ý 30 NGÀY TỚI                                │
│ • <action30Days[i]>                              │
│                                                  │
│ ┌── ✓ ĐIỂM SÁNG ──┐  ┌── ⚠ CẦN LƯU Ý ──┐         │
│ │ • <strengths>   │  │ • <concerns>     │         │
│ └─────────────────┘  └──────────────────┘        │
│                                                  │
│ SỞ THÍCH & THÓI QUEN                             │
│ <preferences>                                    │
│                                                  │
│ GỢI Ý SẢN PHẨM                                   │
│ ( <productSuggestions[i]> )                      │
│                                                  │
├─────────────────────────────────────────────────┤
│ 👍 Hữu ích  👎 Chưa chính xác   [↻ Cập nhật]    │  ← Footer
│ deepseek:deepseek-chat · 482 tokens · 23h trước │
└─────────────────────────────────────────────────┘
```

### 3.4 HTML template

```html
<div class="ai-review-drawer">
  <!-- Header — avatar + tên + meta + segment + rank + close -->
  <header class="ai-rv-head">
    <div class="ai-rv-avatar">T</div>                  <!-- 1 ký tự đầu tên, nền orange -->
    <div class="ai-rv-id">
      <div class="ai-rv-name">tạo đi tạo từ tại từ hcieens địch</div>
      <div class="ai-rv-meta">55905 · Việt Nam · KH từ —</div>
    </div>
    <span class="ai-rv-seg">Mới</span>                 <!-- customer.segment -->
    <span class="ai-rv-rank ai-rv-rank-C">C</span>     <!-- review.rank -->
    <button class="ai-rv-x">×</button>
  </header>

  <!-- CHÂN DUNG -->
  <section>
    <h4 class="ai-rv-label">CHÂN DUNG</h4>
    <p>Khách nam, ở Việt Nam, chưa từng mua tour…</p>
    <em class="ai-rv-reason">Lý do xếp hạng C: Khách mới, chưa từng mua tour…</em>
  </section>

  <!-- 4-stat grid (DỮ LIỆU CỦA CUSTOMER, KHÔNG PHẢI REVIEW) -->
  <div class="ai-rv-stats">
    <div><span>TỔNG CHI</span><b>0đ</b></div>          <!-- customer.metrics.totalSpent -->
    <div><span>SỐ TOUR</span><b>0</b></div>            <!-- customer.metrics.totalTours -->
    <div><span>AOV</span><b>0đ</b></div>               <!-- customer.metrics.aov -->
    <div><span>ĐƠN CUỐI</span><b>—</b></div>           <!-- lastPurchaseDaysAgo + 'd' -->
  </div>

  <!-- VIỆC CẦN LÀM NGAY (xanh) -->
  <div class="ai-rv-action-now">
    <strong>🎯 VIỆC CẦN LÀM NGAY</strong>
    <p class="task">Gọi điện xác thực thông tin và giới thiệu tour khuyến mãi đầu năm.</p>
    <em>Khách mới từ Facebook, cần xác nhận khách thật và kích hoạt nhu cầu.</em>
  </div>

  <!-- GỢI Ý 30 NGÀY TỚI -->
  <section>
    <h4 class="ai-rv-label">GỢI Ý 30 NGÀY TỚI</h4>
    <ul>
      <li>Gửi email/SMS chào mừng kèm voucher giảm 5% cho tour đầu tiên.</li>
      <li>Gọi lại sau 7 ngày nếu chưa phản hồi để hỏi nhu cầu.</li>
      <li>Thêm vào danh sách nhận tin khuyến mãi hàng tháng.</li>
    </ul>
  </section>

  <!-- ĐIỂM SÁNG / CẦN LƯU Ý — ẨN khi mảng rỗng -->
  <div class="ai-rv-2col">
    <div class="ai-rv-col-good">
      <h4>✓ ĐIỂM SÁNG</h4>
      <ul><li>Khách mới từ Facebook MKT, có tiềm năng…</li></ul>
    </div>
    <div class="ai-rv-col-warn">
      <h4>⚠ CẦN LƯU Ý</h4>
      <ul>
        <li>Chưa có bất kỳ dữ liệu mua hàng hay tương tác nào.</li>
        <li>Tên khách hàng nhập không chuẩn, có thể là data lỗi hoặc spam.</li>
      </ul>
    </div>
  </div>

  <!-- SỞ THÍCH -->
  <section>
    <h4 class="ai-rv-label">SỞ THÍCH & THÓI QUEN</h4>
    <p>Chưa đủ dữ liệu để đánh giá.</p>
  </section>

  <!-- GỢI Ý SẢN PHẨM — chip -->
  <section>
    <h4 class="ai-rv-label">GỢI Ý SẢN PHẨM</h4>
    <span class="ai-rv-chip">Tour combo 3 ngày 2 đêm Đà Lạt hoặc Nha Trang giá rẻ…</span>
    <span class="ai-rv-chip">Tour nghỉ dưỡng Phú Quốc 4 ngày 3 đêm nếu khách có ngân sách.</span>
  </section>

  <!-- Footer -->
  <footer class="ai-rv-foot">
    <button>👍 Hữu ích</button>
    <button>👎 Chưa chính xác</button>
    <button class="ai-rv-refresh">↻ Cập nhật review</button>
  </footer>
  <div class="ai-rv-meta-foot">deepseek:deepseek-chat · 482 tokens · 23h trước</div>
</div>
```

### 3.5 CSS gợi ý

```css
.ai-rv-rank-A { background: #16a34a; }
.ai-rv-rank-B { background: #2563eb; }
.ai-rv-rank-C { background: #f59e0b; }   /* cam như screenshot */
.ai-rv-rank-D { background: #dc2626; }
.ai-rv-rank   { width: 26px; height: 26px; line-height: 26px; border-radius: 50%;
                color: white; text-align: center; font-weight: 700; }

.ai-rv-seg    { background: #dcfce7; color: #166534; padding: 3px 8px; border-radius: 4px;
                font-size: 11px; font-weight: 700; }  /* "Mới" — xanh */

.ai-rv-action-now { background: linear-gradient(135deg, #ecfdf5, #d1fae5);
                    border-left: 3px solid #10b981; padding: 14px; border-radius: 10px; }

.ai-rv-col-good { background: #f0fdf4; border: 1px solid #bbf7d0; border-radius: 8px; padding: 12px; }
.ai-rv-col-warn { background: #fefce8; border: 1px solid #fde68a; border-radius: 8px; padding: 12px; }

.ai-rv-chip   { display: inline-block; padding: 6px 10px; border-radius: 14px;
                border: 1px solid #e5e7eb; font-size: 12px; margin: 2px; }

.ai-rv-stats  { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; }
.ai-rv-stats > div { background: #f9fafb; border-radius: 8px; padding: 10px; text-align: center; }
.ai-rv-stats span  { font-size: 10px; color: #6b7280; letter-spacing: 0.05em; text-transform: uppercase; }
.ai-rv-stats b     { font-size: 14px; display: block; margin-top: 4px; }
```

---

## 4. Review Cơ hội (Deal) — JSON & cách render

Object `score` trong mỗi item của list deal:

### 4.1 Đặc tả từng field

**Field deal chính (luôn có):**

| Field | Kiểu | Mô tả | Render |
|---|---|---|---|
| `id` | int | ID deal | Khóa nội bộ |
| `code` | string \| null | Mã hiển thị BK-... | Tiêu đề phụ |
| `customerName` | string | Tên KH | Tiêu đề chính |
| `title` | string \| null | Tên tour | Subtitle |
| `totalPrice` | long | Giá trị (VND) | `fmtVND(totalPrice)` |
| `statusName` | string | Trạng thái pipeline | Chip xám |
| `ageDays` | int | Tuổi cơ hội (ngày) | "13 ngày" |
| `assignees` | string \| null | Tên NV phụ trách | Avatar/initial |
| `coolingDays` | int | Số ngày nguội | Tooltip |
| `isCooling` | bool | Cờ đang nguội | Chip "🥶 nguội" khi true |
| `scoreStatus` | string | `none` \| `fresh` | `none` → ẩn block AI, hiện nút "Chấm AI" |

**Field `score` (chỉ có khi `scoreStatus === "fresh"`):**

| Field | Kiểu | Giá trị | Render |
|---|---|---|---|
| `score.winRate` | int | 0-100 | Số to 36px màu theo level. Hậu tố `%` + caption "khả năng thắng" |
| `score.level` | string | `cao` \| `trung_binh` \| `thap` | Màu winRate: cao=`#16a34a`, trung_binh=`#f59e0b`, thap=`#dc2626` |
| `score.signals` | string[] | Tín hiệu tích cực | Card xanh, header "✓ DẤU HIỆU TÍCH CỰC", `<ul>` |
| `score.risks` | string[] | Rủi ro | Card vàng/đỏ, header "⚠ RỦI RO", `<ul>` |
| `score.nextAction` | string | Hành động nên làm | Card cam nổi bật, icon ⚡, "HÀNH ĐỘNG NÊN LÀM" |
| `score.reason` | string | Lý do tổng | Block xám dưới cùng, italic |
| `score.priorityScore` | float | 0-100 | Progress bar mỏng, dùng làm sort key |
| `score.expectedValue` | long | VND = winRate% × totalPrice | Hiển thị cạnh `totalPrice`: "EV 53.6tr" |
| `score.riskFlag` | string \| null | `nguoi` \| `sap_khoi_hanh` \| `null` | Chip đỏ: "nguội" / "⏰ sắp khởi hành" |
| `score.aiModel` / `score.aiProvider` | string \| null | Debug | Footer nhỏ |

### 4.2 Khi `scoreStatus === "none"`

Deal chưa chấm — hiện nút **"Chấm AI"**. Gọi:
```
POST https://tourkit-ai.tourkit.vn/api/v1/deals/analyze
Body: { "dealIds": ["98123"] }
```
→ Trả `{jobId, streamUrl}`. Đợi job xong (poll list lại sau 30-60s) hoặc nghe SSE `GET {streamUrl}` để cập nhật real-time.

### 4.3 Thứ tự render (khớp UI thực tế)

```
┌─────────────────────────────────────────────────┐
│ test                                          × │  ← title=customerName
│ CH0533                                          │     sub=title || code
├─────────────────────────────────────────────────┤
│ ┌─────────────────────────────────────────────┐ │
│ │  5    Giá trị        Doanh thu kỳ vọng      │ │
│ │  %    0              0                      │ │
│ │       Trạng thái     Tuổi cơ hội            │ │  ← Stat box
│ │khả    Chờ xử lý ha   0 ngày                 │ │
│ │năng   Phụ trách      Nguồn                  │ │
│ │thắng  Admin Thoa 123 Nội bộ                 │ │
│ └─────────────────────────────────────────────┘ │
│                                                  │
│ ╔════ ⚡ Hành động nên làm ═══════════════════╗  │
│ ║ <nextAction>                                ║  │  ← Box cam/peach
│ ╚═════════════════════════════════════════════╝  │
│                                                  │
│ ╔════ ⚠ Rủi ro ═══════════════════════════════╗  │
│ ║ • <risks[i]>                                ║  │  ← Box vàng
│ ╚═════════════════════════════════════════════╝  │
│                                                  │
│ <reason — italic, không box>                     │
└─────────────────────────────────────────────────┘
```

> ⚠️ **Block ẨN khi mảng rỗng:** `signals = []` → KHÔNG render block "Dấu hiệu tích cực" (như screenshot deal "test"). Tương tự với `risks`.

### 4.4 HTML template

```html
<aside class="deal-drawer">
  <!-- Header -->
  <header class="deal-head">
    <h2>test</h2>                                  <!-- customerName -->
    <p class="sub">CH0533</p>                      <!-- title || code -->
    <button class="deal-x">×</button>
  </header>

  <!-- Stat box: winRate big + KV grid 2x3 -->
  <div class="deal-stat">
    <div class="deal-win deal-win-thap">
      <span class="num">5</span>
      <span class="pct">%</span>
      <em>khả năng thắng</em>
    </div>
    <div class="deal-kv">
      <div><span>Giá trị</span><b>0</b></div>
      <div><span>Doanh thu kỳ vọng</span><b>0</b></div>
      <div><span>Trạng thái</span><b>Chờ xử lý ha</b></div>
      <div><span>Tuổi cơ hội</span><b>0 ngày</b></div>
      <div><span>Phụ trách</span><b>Admin Thoa 123</b></div>
      <div><span>Nguồn</span><b>Nội bộ</b></div>
    </div>
  </div>

  <!-- Hành động nên làm (cam/peach) -->
  <div class="deal-next">
    <strong>⚡ Hành động nên làm</strong>
    <p>Gọi điện ngay hôm nay để làm quen, hiểu nhu cầu khách và đặt lịch tư vấn chi tiết</p>
  </div>

  <!-- Signals — CHỈ render khi score.signals.length > 0 (ẨN nếu rỗng) -->
  <!-- <div class="deal-block deal-block-good">
    <h4>✓ Dấu hiệu tích cực</h4>
    <ul>...</ul>
  </div> -->

  <!-- Risks — render khi score.risks.length > 0 -->
  <div class="deal-block deal-block-warn">
    <h4>⚠ Rủi ro</h4>
    <ul>
      <li>Chưa chăm sóc khách hàng</li>
      <li>Giá trị deal 0 đ không rõ nhu cầu</li>
      <li>Lâu 0 ngày chưa có tương tác</li>
      <li>Khách im lặng chưa phản hồi</li>
    </ul>
  </div>

  <!-- Reason — italic, không có box -->
  <p class="deal-reason"><em>Cơ hội mới không có tương tác nào, cần chủ động chăm sóc ngay để tránh mất khách</em></p>
</aside>
```

### 4.5 CSS gợi ý

```css
.deal-head h2  { margin: 0; font-size: 18px; font-weight: 700; }
.deal-head .sub{ color: #6b7280; font-size: 13px; }

.deal-stat     { background: #f9fafb; border-radius: 10px; padding: 16px;
                 display: flex; align-items: center; gap: 24px; }
.deal-win      { text-align: center; min-width: 90px; }
.deal-win .num { font-size: 36px; font-weight: 700; line-height: 1; }
.deal-win .pct { font-size: 14px; vertical-align: top; }
.deal-win em   { display: block; font-size: 11px; color: #6b7280;
                 font-style: normal; margin-top: 4px; }

/* Màu winRate theo level */
.deal-win-cao        { color: #16a34a; }
.deal-win-trung_binh { color: #f59e0b; }
.deal-win-thap       { color: #dc2626; }   /* như screenshot "5%" đỏ */

.deal-kv       { display: grid; grid-template-columns: 1fr 1fr; gap: 12px 24px; flex: 1; }
.deal-kv span  { display: block; font-size: 11px; color: #6b7280; }
.deal-kv b     { font-size: 13px; font-weight: 600; }

/* Action box — peach/cam (KHÔNG xanh) */
.deal-next     { background: #fef3e8; border-left: 3px solid #f97316;
                 padding: 12px 14px; border-radius: 10px; margin: 14px 0; }
.deal-next strong { color: #c2410c; font-size: 14px; }
.deal-next p      { color: #7c2d12; margin: 4px 0 0; }

/* Risk/Signal block */
.deal-block          { border-radius: 10px; padding: 12px 14px; margin: 10px 0; }
.deal-block-good     { background: #f0fdf4; border-left: 3px solid #16a34a; }
.deal-block-warn     { background: #fefce8; border-left: 3px solid #f59e0b; }
.deal-block h4       { margin: 0 0 6px; font-size: 13px; }
.deal-block ul       { margin: 0; padding-left: 18px; }
.deal-block li       { font-size: 13px; line-height: 1.6; }

.deal-reason   { color: #6b7280; font-style: italic; font-size: 13px; margin-top: 12px; }
```

### 4.4 Format helpers (.NET 4.8)

```csharp
// VND có dấu chấm: 86.500.000đ
public static string FmtVnd(long n) =>
    n.ToString("N0", new System.Globalization.CultureInfo("vi-VN")) + "đ";

// VND rút gọn: 86.5tr / 1.2tỷ
public static string FmtVndShort(long n) {
    if (n >= 1_000_000_000) return (n / 1_000_000_000d).ToString("0.#") + "tỷ";
    if (n >= 1_000_000)     return (n / 1_000_000d).ToString("0.#") + "tr";
    return n.ToString("N0");
}

// "N giờ trước" / "N ngày trước"
public static string FmtRel(string iso) {
    if (!DateTime.TryParse(iso, out var d)) return iso;
    var min = (int)(DateTime.UtcNow - d.ToUniversalTime()).TotalMinutes;
    if (min < 1)    return "vừa xong";
    if (min < 60)   return min + " phút trước";
    if (min < 1440) return (min / 60) + " giờ trước";
    return (min / 1440) + " ngày trước";
}
```

---

## 5. Lỗi thường gặp

| Status | Nghĩa |
|---|---|
| `401` | `sessionId` thiếu / hết hạn → login lại |
| `404` | Không có KH/deal đó (sai id hoặc khác tenant) |
| `429` | Hết quota AI tenant (1000 lượt). Body kèm `quota` chi tiết |
| `502` | TourKit upstream lỗi |

Liên hệ: `support@tourkit.vn`.
