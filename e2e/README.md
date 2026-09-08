# E2E Tests — TourKit AI Proxy

Playwright (Node-based) tests for frontend UI flows + API smoke checks.

> ⚠️ **MẶC ĐỊNH BỘ NÀY TRỎ VÀO BẢN CHẠY THẬT** (`https://mobile-api2.tourkit.vn`). Gõ `npm test`
> trần là chạy ở đó. Bài chỉ ĐỌC thì không sao; bài GHI (nhóm `07*`) tự chặn mình, xem
> [helpers/chat-phan-cong.js](helpers/chat-phan-cong.js). Đừng thêm bài ghi mà không đi qua cửa đó.
>
> Chạy ở máy mình: `E2E_TARGET=local` (cổng 5080, tự dựng app) hoặc `E2E_BASE_URL=http://localhost:<cổng>`
> khi bạn tự dựng app ở cổng khác — 5080 thường là bản của người đang ngồi máy, chiếm mất là họ đứt việc.

## Setup

```bash
cd e2e
npm install
npx playwright install chromium
```

## Run

```bash
# Need proxy running ở localhost:5080 trước
cd .. && dotnet run --project TourkitAiProxy.csproj &

cd e2e
npm test                   # All tests
npm run test:headed        # See browser
npm run test:ui            # Interactive UI mode
npm run report             # View last HTML report
```

## Test files

| File | Coverage |
|---|---|
| `01-smoke.spec.js` | 8 page load + JS error + 5xx check |
| `02-assistant-suggestions.spec.js` | 4 quick chips + toggle + 23 chip expand + SVG icon non-empty + click chip dispatch |
| `03-home-logout.spec.js` | Logout button + greeting + search |
| `04-customers-deals.spec.js` | List load + PageHero + checkbox + auto toggle |
| `05-api-direct.spec.js` | Providers list + Session check + tool catalog |
| `07-chat-phan-cong-api.spec.js` | 12 bài: ngữ nghĩa request · vai trò & luật xem · vòng đời cấu hình phân công |
| `07b-chat-phan-cong-giao-dien.spec.js` | 3 bài: bấm nút "Nhận chăm sóc" thật, không lượt gọi nào rơi xuống trang SPA |

## Nhóm phân công hội thoại (`07*`) — nhóm GHI đầu tiên

Đây là nhóm đầu tiên trong bộ này có ghi dữ liệu (nhận việc, giao việc, lưu cấu hình phân công của
cả công ty), nên nó có cửa an toàn riêng và **đỏ chứ không bỏ qua** khi thiếu điều kiện:

```powershell
cd e2e
$env:E2E_TARGET='local'
$env:E2E_BASE_URL='http://localhost:5099'     # cổng bạn tự dựng, để 5080 cho người dùng máy
$env:E2E_SESSION='<phiên quản trị>'
$env:E2E_SESSION_NV='<phiên nhân viên thường>'
$env:E2E_CHO_PHEP_GHI='1'
npx playwright test tests/07-chat-phan-cong-api.spec.js tests/07b-chat-phan-cong-giao-dien.spec.js
```

Nhóm này **không bao giờ gọi `/send` hay `/send-template`** — bot Telegram đang nối THẬT, một tin
"test" là một tin tới điện thoại một người thật. Trạng thái bị đụng (cấu hình phân công, người phụ
trách của hội thoại đem ra thử) được chụp trước, trả lại sau, và **kiểm lại xem đã trả được chưa**.

### Vì sao có nhóm này

Ngày 08/09/2026 nhánh phân công đã qua 13 việc, 3 vòng review và 1219 test xanh — mà vẫn còn hai
lỗi chặn, cả hai tìm ra trong nửa giờ gọi API thật:

1. Nút "Nhận chăm sóc" chết hoàn toàn (request thiếu `Content-Type` bị loại ở tầng định tuyến rồi
   rơi xuống trang SPA → 404 kèm HTML; bấm nút không có gì xảy ra).
2. Bấm Lưu ở màn hình Cấu hình phân công **một lần** là mọi request chat trả 500.

Cả hai vô hình với bộ test C# của repo — bộ đó đọc văn bản nguồn, còn hai lỗi này là hành vi của
khung và của driver. Đã trả mã về trạng thái hỏng để kiểm chứng: bài `A1` + cả nhóm 07 đỏ với lỗi
(1), toàn bộ 15 bài đỏ với lỗi (2), bài `C5` đỏ khi ràng buộc đội trực áp nhầm cho cả quản trị.

## Session config

Phiên truyền qua biến môi trường `E2E_SESSION` / `E2E_SESSION_NV`, **không hardcode vào file**:
mỗi máy mỗi phiên, và phiên là thứ mở được dữ liệu công ty thật.

Lấy sessionId: đăng nhập app rồi mở DevTools Console → `localStorage.getItem('tourkit_tk_session')`.

⚠️ `05-api-direct.spec.js` còn ghim cứng một session trong mã (file cũ). Đó là **nợ**: nó vừa hết
hạn là bài tự bỏ qua trong im lặng, vừa là một khoá truy cập nằm trong repo. Cần chuyển sang biến
môi trường như các file sau này.
