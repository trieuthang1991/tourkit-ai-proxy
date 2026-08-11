# Bộ E2E "Trợ lý số liệu" — ngân hàng câu hỏi + cách test nhanh

**Ngày:** 2026-08-11 · **Mục đích:** sau này sửa bất kỳ thứ gì trong `JsonPlannerAgent` / `ChatTools` / cache, chạy 1 lệnh là biết có phá gì không — thay vì mở app hỏi tay từng câu rồi quên mất ca cũ.

| Thành phần | File |
|---|---|
| Ngân hàng câu hỏi (dữ liệu — dễ thêm) | [`scripts/chat-e2e-cases.json`](../../scripts/chat-e2e-cases.json) |
| Runner (PowerShell) | [`scripts/chat-e2e.ps1`](../../scripts/chat-e2e.ps1) |
| Checklist thủ công | mục §4 dưới đây |

## 1. Chạy nhanh

```powershell
# 0) Lấy SessionId: đăng nhập /assistant → DevTools Console:
#    localStorage.getItem('tourkit_tk_session')

# 1) Xem có những ca nào (KHÔNG tốn quota)
.\scripts\chat-e2e.ps1 -ListOnly

# 2) Smoke 2 câu — kiểm tra app còn sống (~4 lượt AI)
.\scripts\chat-e2e.ps1 -SessionId <sid> -Suite smoke

# 3) Regression — chạy sau khi sửa logic chat (~18 lượt AI)
.\scripts\chat-e2e.ps1 -SessionId <sid> -Suite regression

# 4) Toàn bộ (~44 lượt AI)
.\scripts\chat-e2e.ps1 -SessionId <sid>

# 5) Chạy đúng 1 ca khi đang debug
.\scripts\chat-e2e.ps1 -SessionId <sid> -CaseId reg-02-hoi-nguon-goc-khong-lap
```

Tham số khác: `-BaseUrl` (mặc định `http://localhost:5080`), `-DelayMs` (giãn cách giữa 2 câu, mặc định 800ms), `-CasesFile` (dùng file câu hỏi khác).

**Exit code:** `0` = pass hết · `1` = có FAIL → cắm vào CI được sau này.

> ⚠️ **Tốn quota thật.** Mỗi câu hỏi ≈ **2 lượt AI** (planner + phân tích) trừ vào quota của tenant đang đăng nhập. Bộ đầy đủ 22 câu ≈ 44 lượt. Ngày thường dùng `-Suite smoke`; chỉ chạy full trước khi phát hành.

## 2. Bộ câu hỏi đang có (17 ca / 22 câu)

### Smoke (2 ca) — đường sống
| Ca | Khóa điều gì |
|---|---|
| `smoke-01-doanh-thu` | Hỏi doanh thu ra số + có panel |
| `smoke-02-top-khach` | Top khách hàng + không lộ tên tool |

### Regression (7 ca) — mỗi ca gắn với 1 bug đã sửa
| Ca | Bug gốc | Khóa điều gì |
|---|---|---|
| `reg-01-so-sanh-khong-de-cache` | `ca2d68f` | Câu "so sánh" không bị L2 cache trả nguyên văn câu thường trước đó |
| `reg-02-hoi-nguon-goc-khong-lap` | `cb85862` | Hỏi "số liệu này lấy từ đâu" → nói nguồn **ERP**, GIỮ panel, KHÔNG lặp câu cũ, KHÔNG còn "Bảng bên phải…" |
| `reg-03-khong-lo-ten-tool` | `cb85862` | `ScrubToolNames` — không lọt `cashflow`/`financial_summary`… ra câu trả lời |
| `reg-04-go-khong-dau` | `2731df2` | Gõ **không dấu** ("loi nhuan thang nay") vẫn nhận là câu hỏi số liệu |
| `reg-05-follow-up-giu-ngu-canh` | — | "Còn tháng trước thì sao?" hiểu ngữ cảnh, không lặp |
| `reg-06-follow-up-khong-so-lieu-giu-panel` | `6ddcf65` | Follow-up thường ("giải thích thêm") → **GIỮ** panel |
| `reg-07-hoi-so-lieu-la-khong-copy-cau-cu` | `6ddcf65` | Câu hỏi số liệu lạ mà AI bí → **KHÔNG** copy panel/câu trả lời cũ |

### Routing (8 ca) — AI chọn đúng nguồn dữ liệu
`route-01` dòng tiền · `route-02` top sale · `route-03` tour sắp đi · `route-04` marketing · `route-05` cơ hội bán hàng (BookingTicket, dễ nhầm Lead) · `route-06` lịch hẹn · `route-07` công việc · `route-08` thị trường (name→id resolver).

## 3. Cách thêm ca mới

Sửa `scripts/chat-e2e-cases.json`, thêm 1 phần tử vào `cases`:

```json
{
  "id": "reg-08-ten-ngan-gon",
  "suite": "regression",
  "why": "Bug <hash>: mô tả 1 dòng ca này khóa điều gì.",
  "steps": [
    { "ask": "Câu hỏi bước 1", "expect": { "hasData": true } },
    { "ask": "Câu hỏi bước 2",
      "expect": { "replyDiffersFromPrevious": true, "replyContains": ["ERP"] } }
  ]
}
```

**Các kiểm tra dùng được** (`expect`):

| Khóa | Ý nghĩa |
|---|---|
| `hasData` | `true` = panel phải có số liệu (`stats` hoặc `raw` không rỗng); `false` = phải trống |
| `toolNameIn` | `toolName` trả về phải nằm trong danh sách (liệt kê nhiều giá trị vì AI có thể chọn khác nhau) |
| `replyContains` | Mọi chuỗi phải xuất hiện trong câu trả lời |
| `replyNotContains` | Mọi chuỗi **không được** xuất hiện (dùng bắt lộ tên tool) |
| `replyDiffersFromPrevious` | Câu trả lời phải **khác** bước liền trước (chống lặp) |

**Mẹo:** ca nào phụ thuộc vào việc AI chọn tool (dễ đổi theo model/prompt) thì thêm `"soft": true` vào **step** — sai sẽ hiện WARN vàng, không tính FAIL. Hiện `reg-06`, `reg-07`, `route-08` đang để soft.

Các bước trong cùng 1 ca **nối tiếp hội thoại** (gửi kèm lịch sử); giữa 2 ca runner tự gọi `DELETE /api/v1/chat/memory` để làm sạch bộ nhớ — trừ khi ca đặt `"freshConversation": false`.

## 4. Checklist thủ công (script KHÔNG kiểm được)

Chạy trên `/assistant` sau khi bộ tự động xanh:

- [ ] **Biểu đồ** vẽ đúng loại: câu hỏi phân loại → cột ngang; câu hỏi theo thời gian → cột dọc nhóm.
- [ ] **Nút chuyển chỉ số** (Doanh thu / Chi phí / Lợi nhuận) đổi cả chart lẫn bảng.
- [ ] **Câu "so sánh"** hiện thêm **cột kỳ đối chiếu** + mũi tên ▲/▼ % bên cạnh thẻ số.
- [ ] **Streaming** (`/chat/stream`): chữ chảy mượt, panel hiện **trước** khi chữ chảy xong.
- [ ] **Tiền tệ** format đúng kiểu VN (1.234.567đ), không ra `1234567` hay `NaN`.
- [ ] **Nút "Trò chuyện mới"** xóa sạch ngữ cảnh (hỏi lại "còn tháng trước?" phải không hiểu).
- [ ] **TRAVAI** (`/travai`): đọc được câu trả lời bằng giọng nói, không đọc ký tự markdown thừa.
- [ ] **Mobile**: bảng/chart cuộn ngang được, không vỡ layout.
- [ ] **Hết quota**: tenant hết lượt → hiện thông báo 429 tử tế, không màn hình trắng.

## 5. Khi có FAIL — soi ở đâu

1. `GET /api/v1/chat/unresolved?days=1` — xem AI bí ở câu nào, planner trả gì thô (`plannerRaw`).
2. Trang admin **"AI bí câu hỏi"** (`/admin-trav-ai`) — cùng dữ liệu, dạng bảng.
3. Log file `logs/app-YYYY-MM-DD.log`, grep theo `req=` của request đó (mỗi request 1 RequestId).
4. Thêm `"debug": true` vào body `/api/v1/chat` → response kèm `trace` từng bước (planner → dispatch → compare → analysis).
