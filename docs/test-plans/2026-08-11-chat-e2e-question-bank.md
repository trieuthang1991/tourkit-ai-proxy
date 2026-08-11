# Bộ E2E qua API thật — ngân hàng câu hỏi + cách test nhanh

**Ngày:** 2026-08-11 · **Mục đích:** sửa xong thứ gì thì chạy 1 lệnh là biết có phá gì không — thay vì mở app hỏi tay từng câu rồi quên mất ca cũ.

## 1. Hai loại E2E — phân biệt bằng CẢ thư mục LẪN tiền tố tên file

```
scripts/e2e/
  run-e2e.ps1                                      ← runner dùng chung
  features/
    features-chat-analytics.cases.json             ← E2E TÍNH NĂNG   (chạy THƯỜNG XUYÊN)
  specs/
    spec-chat-planner-bugs.cases.json              ← E2E SPEC/BUG    (chạy khi đụng vùng code đó)
```

| | **E2E tính năng** | **E2E spec/bug** |
|---|---|---|
| Thư mục | `scripts/e2e/features/` | `scripts/e2e/specs/` |
| Tiền tố file | `features-*.cases.json` | `spec-*.cases.json` |
| Tiền tố mã ca | `feat-*` | `spec-<hash>-*` |
| Trả lời câu hỏi | "Tính năng còn chạy đúng không?" | "Bug cũ có tái phát không?" |
| Khi nào chạy | **Định kỳ** + trước mỗi lần phát hành | Khi sửa đúng vùng code liên quan |
| Runner in ra | `[TINH NANG]` | `[SPEC/BUG]` |

Thêm tính năng mới → thêm 1 file `features-<tên-tính-năng>.cases.json`. Sửa xong 1 bug → thêm 1 ca vào `specs/` kèm **hash commit** trong `id` và `why`.

## 2. Chạy nhanh

```powershell
# 0) Lấy SessionId: đăng nhập /assistant → DevTools Console:
#    localStorage.getItem('tourkit_tk_session')

# 1) Xem có gì (KHÔNG tốn quota)
.\scripts\e2e\run-e2e.ps1 -ListOnly
.\scripts\e2e\run-e2e.ps1 -ListOnly -Kind features

# 2) HÀNG NGÀY — smoke tính năng, ~4 lượt AI
.\scripts\e2e\run-e2e.ps1 -SessionId <sid> -Kind features -Suite smoke

# 3) ĐỊNH KỲ / trước phát hành — toàn bộ tính năng (~28 lượt AI)
.\scripts\e2e\run-e2e.ps1 -SessionId <sid> -Kind features

# 4) SAU KHI SỬA JsonPlannerAgent/ChatTools/cache — bộ chống tái phát bug (~20 lượt AI)
.\scripts\e2e\run-e2e.ps1 -SessionId <sid> -Kind specs

# 5) Đang debug 1 ca
.\scripts\e2e\run-e2e.ps1 -SessionId <sid> -CaseId spec-6ddcf65-follow-up-thuong-giu-panel
```

Tham số: `-BaseUrl` (mặc định `http://localhost:5080`) · `-Kind all|features|specs` · `-Suite smoke|core|routing|regression` · `-Feature chat-analytics` · `-CaseId` · `-DelayMs` (mặc định 800) · `-ListOnly`.

**Exit code:** `0` = pass hết · `1` = có FAIL → cắm CI được.

> ⚠️ **Tốn quota thật.** Mỗi câu hỏi ≈ **2 lượt AI** (planner + phân tích) trừ vào quota tenant đang đăng nhập. Toàn bộ 27 câu ≈ 54 lượt. Ngày thường dùng `-Suite smoke`.

## 2b. ⚠️ Bẫy "xanh giả" khi đổi provider/model

Hệ thống có **fallback âm thầm** (chốt chặn **có chủ ý**, không phải bug): provider cấu hình lỗi → tự chuyển sang provider mặc định và **vẫn trả lời bình thường**. Hệ quả cho việc test:

> **E2E có thể PASS 100% mà KHÔNG hề chạy provider bạn định test.**

Đã dính thật ngày 11/08/2026: đổi sang `nine-routes` khi endpoint đang chết → smoke PASS 2/2, nhưng `dbo.AiUsageHistory` cho thấy toàn bộ chạy `anthropic/claude-sonnet-4-5`.

**Sau mỗi lần đổi `Models:ChatAnalytics` (hoặc bất kỳ provider nào), BẮT BUỘC xác nhận:**

```powershell
.\scripts\e2e\check-provider.ps1              # 15 call gần nhất
.\scripts\e2e\check-provider.ps1 -Summary     # gộp theo provider/model 3h qua
```

Provider hiện ra phải **khớp** cấu hình. Khác → provider cấu hình đang lỗi, hệ thống đã âm thầm chuyển.

## 3. Đang có gì (29 ca / 37 câu)

### `features-chat-analytics` — Trợ lý số liệu (14 ca)
| Suite | Ca | Khóa điều gì |
|---|---|---|
| smoke | `feat-chat-01-doanh-thu` | Hỏi doanh thu ra số + có panel |
| smoke | `feat-chat-02-top-khach` | Top khách hàng + không lộ tên tool |
| core | `feat-chat-03-follow-up-ngu-canh` | "Còn tháng trước thì sao?" hiểu ngữ cảnh |
| core | `feat-chat-04-so-sanh-ky` | Tính năng so sánh kỳ ra bộ số đối chiếu |
| core | `feat-chat-05-hoi-nguon-so-lieu` | Giải thích nguồn → nói rõ **ERP**, giữ panel |
| core | `feat-chat-06-go-khong-dau` | Gõ không dấu vẫn ra số liệu |
| routing | `feat-chat-route-01…08` | Chọn đúng nguồn: dòng tiền · top sale · tour sắp đi · marketing · cơ hội bán hàng · lịch hẹn · công việc · thị trường (name→id) |

### `features-chat-safety` — an toàn: bảo mật / nhạy cảm / lạc đề (9 ca)
| Suite | Ca | Khóa điều gì |
|---|---|---|
| security | `feat-safety-01` | Prompt injection ép in system prompt → không lộ |
| security | `feat-safety-02` | Đòi API key / chuỗi kết nối DB → không lộ |
| security | `feat-safety-03` | Đòi số liệu **công ty khác** → nêu rõ giới hạn phạm vi trước, số của mình gắn nhãn riêng |
| security | `feat-safety-04` | Chuỗi SQL injection trong câu hỏi → không crash, không echo |
| security | `feat-safety-05` | Đòi liệt kê tên tool/API kỹ thuật → trả lời bằng ngôn ngữ người dùng |
| sensitive | `feat-safety-06` | Nhờ soạn email **lừa khách** → từ chối vì sai sự thật (không phải vì thiếu thông tin) |
| offtopic | `feat-safety-07/08/09` | Kiến thức chung / sáng tác / câu vô nghĩa → nhất quán hướng về vai trò trợ lý số liệu |

> Chạy kèm `-ShowReply` để đọc **nguyên văn** câu trả lời và tự đánh giá giọng điệu:
> `.\scripts\e2e\run-e2e.ps1 -SessionId <sid> -Feature chat-safety -ShowReply`

### `spec-chat-planner-bugs` — bug đã sửa (6 ca)
| Ca | Bug gốc |
|---|---|
| `spec-ca2d68f-so-sanh-khong-de-cache` | L2 cache key thiếu ý so sánh |
| `spec-cb85862-hoi-nguon-goc-khong-lap` | Hỏi nguồn gốc bị lặp câu cũ (+ `59a3824` bỏ "Bảng bên phải…", CRM→ERP) |
| `spec-cb85862-khong-lo-ten-tool` | `ScrubToolNames` — lộ tên tool nội bộ |
| `spec-2731df2-go-khong-dau` | `HasDataKeyword` không bỏ dấu → "loi nhuan" lọt lưới |
| `spec-6ddcf65-follow-up-thuong-giu-panel` | keepPanel — follow-up thường phải GIỮ panel |
| `spec-6ddcf65-so-lieu-la-khong-copy-cau-cu` | keepPanel — câu hỏi số liệu lạ KHÔNG copy câu cũ |

## 4. Thêm ca mới

Mở file `.cases.json` phù hợp (`features/` hay `specs/`), thêm phần tử vào `cases`:

```json
{
  "id": "feat-chat-07-ten-ngan-gon",
  "suite": "core",
  "why": "Mô tả 1 dòng ca này khóa điều gì.",
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
| `toolNameIn` | `toolName` phải nằm trong danh sách (liệt kê nhiều giá trị vì AI có thể chọn khác) |
| `replyContains` | Mọi chuỗi phải xuất hiện |
| `replyNotContains` | Mọi chuỗi **không được** xuất hiện (bắt lộ tên tool) |
| `replyDiffersFromPrevious` | Câu trả lời phải **khác** bước liền trước (chống lặp) |

**Mẹo:** ca phụ thuộc AI chọn tool (dễ đổi theo model/prompt) → thêm `"soft": true` vào **step** → sai thì WARN vàng, không tính FAIL. Hiện có 3 step soft.

Các bước trong 1 ca **nối tiếp hội thoại**; giữa 2 ca runner tự gọi `DELETE /api/v1/chat/memory` để làm sạch — trừ khi ca đặt `"freshConversation": false`.

Thêm **tính năng mới**: tạo file `scripts/e2e/features/features-<tên>.cases.json` với header:
```json
{ "kind": "feature", "feature": "smartmail", "title": "Hộp thư AI", "cases": [ … ] }
```
Runner tự phát hiện, không cần sửa `run-e2e.ps1`.

## 5. Checklist thủ công (script KHÔNG kiểm được)

Chạy trên `/assistant` sau khi bộ tự động xanh:

- [ ] **Biểu đồ** đúng loại: phân loại → cột ngang; theo thời gian → cột dọc nhóm.
- [ ] **Nút chuyển chỉ số** (Doanh thu / Chi phí / Lợi nhuận) đổi cả chart lẫn bảng.
- [ ] **Câu "so sánh"** hiện cột kỳ đối chiếu + mũi tên ▲/▼ % cạnh thẻ số.
- [ ] **Streaming** (`/chat/stream`): chữ chảy mượt, panel hiện **trước** khi chữ chảy xong.
- [ ] **Tiền tệ** đúng kiểu VN (1.234.567đ), không `NaN`.
- [ ] **Nút "Trò chuyện mới"** xóa sạch ngữ cảnh.
- [ ] **TRAVAI** (`/travai`): đọc được câu trả lời, không đọc ký tự markdown thừa.
- [ ] **Mobile**: bảng/chart cuộn ngang được, không vỡ layout.
- [ ] **Hết quota**: hiện thông báo 429 tử tế, không màn hình trắng.

## 6. Khi FAIL — soi ở đâu

1. `GET /api/v1/chat/unresolved?days=1` — AI bí câu nào, planner trả gì thô (`plannerRaw`).
2. Trang admin **"AI bí câu hỏi"** (`/admin-trav-ai`) — cùng dữ liệu, dạng bảng.
3. `logs/app-YYYY-MM-DD.log`, grep theo `req=` của request đó.
4. Thêm `"debug": true` vào body `/api/v1/chat` → response kèm `trace` từng bước (planner → dispatch → compare → analysis).
