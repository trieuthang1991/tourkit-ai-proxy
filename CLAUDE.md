# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ASP.NET Core 8 Minimal API đứng trước nhiều nhà cung cấp AI (OpenCode Go, 9routes, OpenAI, Anthropic) cho hệ TourKit. Backend chia thành **6 project theo tầng** — xem [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Giao diện (React qua UMD + Babel standalone, không bundler lúc dev) nằm trong `wwwroot/`, do chính tiến trình này phục vụ.

Trên nền proxy đó có nhiều tính năng: **báo giá tour** (ca sử dụng gốc) · **chấm hạng khách** · **Trợ lý số liệu** (AI tự chọn API CRM để gọi rồi phân tích số thật) · **Hộp thư AI** (Gmail qua IMAP, phân loại + soạn nháp) · **Hộp thư chat đa kênh** (Zalo/Messenger/Telegram) · **Bản tin AI** (bản tin sáng + Bảng tin) · **Tự động hoá** (tác vụ chạy theo lịch) · **thẩm định visa** · **nhập giá NCC**. Bảng dưới nói mỗi cụm đọc file nào.

## Commands

```bash
# Setup lần đầu: copy template + điền key thật (file appsettings.json đang gitignore)
cp appsettings.example.json appsettings.json
# Sửa appsettings.json: thay REPLACE_WITH_OPENCODE_KEY + REPLACE_WITH_9ROUTES_KEY

# Run locally (binds http://localhost:5080 per Properties/launchSettings.json)
dotnet run --project TourkitAiProxy.csproj

# Build / publish
dotnet build TourkitAiProxy.csproj
dotnet publish TourkitAiProxy.csproj -c Release -o out

# Docker (exposes :8080 inside container)
docker build -t tourkit-ai-proxy .
docker run -p 5080:8080 -e Providers__OpenCode__ApiKey="sk-..." tourkit-ai-proxy

# Frontend bundle (prod mode — speedup ~70× cold start: 3-5s → 50ms)
# THƯỜNG KHÔNG CẦN CHẠY THỦ CÔNG — MSBuild target tự fire khi `dotnet publish -c Release`
.\build-frontend.ps1            # (Tùy chọn) bundle thủ công 1 lần
.\build-frontend.ps1 -Watch     # Watch mode dev — rebuild ~20ms/lần save, F5 thấy ngay
.\build-frontend.ps1 -Clean     # Xóa dist/ → quay về dev mode (Babel in-browser, hot reload)
```

Frontend có **2 mode auto-switch** qua tồn tại của `wwwroot/dist/app.bundle.js`:
- **Dev mode** (`dotnet run` Debug — DEFAULT): 55 file .jsx + Babel standalone → edit 1 file = F5 thấy ngay; cold start 3-5s. MSBuild target SKIP ở Debug.
- **Prod-bundle mode** (`dotnet publish -c Release` HOẶC `dotnet build -c Release`): MSBuild target `BuildFrontendBundle` trong [TourkitAiProxy.csproj](TourkitAiProxy.csproj) tự chạy `npx esbuild`, ghi `wwwroot/dist/app.bundle.js` (~857KB minified). `StaticFilesSetup.ServeIndex` detect dist/ → tự strip toàn bộ thẻ `<script type="text/babel">` + Babel CDN + `babel-cache.js` + `lib/data.js`, inject 1 thẻ `<script src="dist/app.bundle.js?v=hash">`. Cold start ~50ms.
- **Incremental**: MSBuild compare mtime `wwwroot/**/*.jsx` vs `dist/app.bundle.js` → skip nếu bundle còn fresh (lần publish thứ 2 không thay đổi → bỏ qua esbuild ~3s).
- **Docker**: [Dockerfile](Dockerfile) đã install `nodejs` ở stage `build` → `dotnet publish` trong container chạy `npx esbuild` được.

**Khi cần dev nhanh với bundle**: `.\build-frontend.ps1 -Watch` (chạy song song `dotnet run`) — esbuild rebuild ~20ms/lần save, F5 thấy ngay. Hoặc `-Clean` để xóa dist/ về Babel mode (hot reload Babel nhanh hơn nhưng cold start chậm).

**Test:** `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` — hơn 800 test, chạy dưới 1 giây vì chỉ phủ **logic thuần** (luật `Domain`, hàm chuẩn hoá, guard kiến trúc). Chưa có test tích hợp chạm CSDL; phần chạm IMAP/SMTP/kênh chat vẫn kiểm bằng tay trên staging. Chạy TOÀN BỘ, đừng lọc — guard kiến trúc nằm rải trong đó.

`appsettings.json` ở `.gitignore` (chứa API key + chuỗi kết nối `ENC:`); commit `appsettings.example.json` làm template.

---

## Làm việc gì thì đọc file nào

File này **cố ý ngắn**. Trước 25/08/2026 nó dài 1.086 dòng — và một quy ước không ai đọc hết thì
bằng không có quy ước: chính ngày hôm đó có người vi phạm quy ước đặt tên **vài giờ sau khi đọc
nó**. Nay mỗi cụm nằm một file riêng; đọc đúng file cần trước khi sửa.

**Luôn đọc trước khi viết code mới:** [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — sáu tầng, luật
kết nạp từng tầng, và câu trả lời cho "file này để đâu".

| Sắp làm gì | Đọc |
|---|---|
| Thêm/sửa file `.cs` bất kỳ, hỏi "để tầng nào" | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Thêm/sửa endpoint, tra đường dẫn API | [docs/api-surface.md](docs/api-surface.md) |
| Đụng nhà cung cấp AI, function-calling, chọn model | [docs/ai-providers.md](docs/ai-providers.md) |
| Trợ lý số liệu, hành động trợ lý, chấm hạng khách | [docs/features/assistant.md](docs/features/assistant.md) |
| Hộp thư AI (Gmail IMAP/SMTP, phân loại, soạn nháp) | [docs/features/mail.md](docs/features/mail.md) |
| Hộp thư chat đa kênh (Zalo/Messenger/Telegram) | [docs/features/chat-inbox-ky-thuat.md](docs/features/chat-inbox-ky-thuat.md) |
| Tác vụ tự động, worker chạy nền | [docs/features/workflows.md](docs/features/workflows.md) |
| Bản tin sáng, Bảng tin, hàng đợi gửi | [docs/features/digest.md](docs/features/digest.md) |
| Thêm bảng SQL, sửa schema, chống nhắc trùng | [docs/database-schema.md](docs/database-schema.md) |
| Trang quản trị, log, tra cứu code bằng CodeGraph | [docs/operations.md](docs/operations.md) |
| Sửa `wwwroot/`, thêm trang, bundle, SEO | [docs/frontend.md](docs/frontend.md) |
| Cờ tính năng, quota, ngày giờ, CHANGELOG, cách đặt tên | [docs/conventions.md](docs/conventions.md) |

**Ba luật không nằm trong file nào khác — đọc ngay ở đây:**

1. **Ngày giờ là UTC, luôn kèm `Z`.** Chi tiết + bẫy `Kind=Unspecified` ở
   [docs/datetime-convention.md](docs/datetime-convention.md).
2. **Chữ hiển thị, log, chú thích viết tiếng Việt — tên định danh trong mã C# viết TIẾNG ANH.**
   Tệp, lớp, hàm, biến, cột CSDL, khoá JSON: tiếng Anh. Ngoại lệ duy nhất là `wwwroot/**/*.jsx`,
   vốn đặt tên tiếng Việt từ đầu. Chi tiết + lý do ở [docs/conventions.md](docs/conventions.md).
3. **`CHANGELOG.md` là bắt buộc mỗi lần phát hành**, viết cho người dùng cuối: không mã commit,
   không tên file/hàm/bảng, không thuật ngữ kỹ thuật. Thay đổi có ảnh hưởng người dùng mà chưa có
   dòng trong CHANGELOG → **coi như chưa xong**.

Việc gì không tra được ở bảng trên thì hỏi `codegraph explore "<câu hỏi>"` trước khi `grep` — nó
đọc từ mã nguồn thật nên không bao giờ lạc hậu như tài liệu.

---

<!-- Khoi duoi day do `codegraph` tu quan ly qua cap marker. DUNG tach ra file khac:
     lan cai/nang cap ke tiep no tim marker trong CHINH file nay de ghi de. -->

<!-- codegraph:start -->
# CodeGraph — Code Intelligence

This project is indexed by **CodeGraph** (`@colbymchenry/codegraph`) — a local SQLite knowledge graph in `.codegraph/` (no embeddings, no API key, fully offline). The index **auto-syncs as you edit**, so it's normally fresh with no manual re-index step. Use it to understand code, assess impact, and navigate safely before editing.

Two ways in:
- **MCP tool** `mcp__codegraph__codegraph_explore` — one call returns the relevant symbols' verbatim, line-numbered source **plus** their call paths **plus** a blast-radius summary (replaces a grep + Read loop).
- **CLI** `codegraph <cmd>` — `explore` / `query` / `node` / `callers` / `callees` / `impact` / `status`.

## Quy trình mỗi task: test + tối ưu hiệu năng (BẮT BUỘC)

Mỗi task phải tự test và đo theo thứ tự sau. Báo cáo cuối phải ghi rõ đã test gì, số liệu đo, và **chưa test được gì**.

1. **Hiểu yêu cầu, đúng phạm vi** — không tự thêm tính năng ngoài yêu cầu; chỉ hỏi khi mơ hồ về logic.
2. **Đối chiếu nguồn chuẩn** — nghiệp vụ TourKit lấy web (`tourkit/`) làm chuẩn; đọc code hiện có và làm theo đúng quy ước của repo này trước khi viết.
3. **Ước lượng hiệu năng trên dữ liệu lớn** — số round-trip, số dòng đọc, RAM, thời gian request. Gộp ở SQL, chia lô ~2000, keyset paging, việc nặng chạy nền.
4. **Viết code phòng thủ** — null, dữ liệu cũ, DB chưa chạy migration (kiểm tra schema trước khi dùng cột/tham số mới), lỗi từ dịch vụ ngoài (timeout, trả rỗng).
5. **Test logic cô lập** — chạy riêng hàm với bộ input: bình thường / biên (null, rỗng, 0, ngày lỗi, chuỗi dài, Unicode) / bẩn (JSON hỏng, HTML lạ) / **đúng ca lỗi thật user báo** / dữ liệu cũ. Repo có project test thì chạy và bổ sung test cho ca vừa sửa.
6. **Test trên DB thật (môi trường test/Demo)** — ghi dữ liệu thì bọc `BEGIN TRAN … ROLLBACK`. Đo:
   - Plan (`SET STATISTICS PROFILE ON`, tham số đúng kiểu như code gửi): phải **Index Seek**, không Scan bảng lớn, không `CONVERT_IMPLICIT`.
   - `SET STATISTICS IO, TIME ON`: logical reads không tăng theo kích thước bảng.
   - Không bọc hàm quanh cột lọc; `OR` làm mất Seek → tách `UNION ALL`; phân trang keyset thay vì `OFFSET` sâu.
7. **Build** — project bị sửa phải build xanh.
8. **Test đầu cuối + hồi quy** — gọi endpoint/màn hình thật, kiểm cả dữ liệu trả về lẫn mã lỗi; xem impact (ai đang gọi hàm vừa sửa) → test thêm 1–2 luồng khác dùng chung; test phân quyền (user thường / admin / không quyền).
9. **Đo trước/sau** — ghi ms, logical reads, plan (Scan → Seek) vào báo cáo.
10. **Báo cáo trung thực** — đã làm, đã test, số liệu, chưa test được, điểm khác so với nguồn chuẩn.

**Tối ưu tầng code:** không N+1 (không gọi DB/HTTP trong vòng lặp), không ném exception trong vòng lặp nóng, cache dữ liệu đọc nhiều ít đổi, EF dùng `AsNoTracking` + chỉ chọn cột cần, không `ToList()` sớm, async không chặn (`.Result`/`.Wait()`). **UI:** debounce tìm kiếm ~300ms + bỏ kết quả trả về muộn, danh sách phân trang/"Tải thêm".

**Task xuất file (Word/PDF/Excel):** tái hiện bằng đúng bản ghi lỗi và xem dữ liệu gốc trước (lỗi export đa phần do dữ liệu bẩn); làm sạch ở tầng chung; một field hỏng chỉ được làm trống field đó, không làm hỏng cả file; export lại ca lỗi + 1–2 mẫu khác và mở file kiểm tra định dạng.

## Always Do

- **Assess blast radius before editing any symbol.** Run `codegraph impact <Symbol>` (or `codegraph_explore`) and report the direct callers + affected symbols before modifying a function/class/method. Warn the user when the radius is wide.
- When exploring unfamiliar code, use `codegraph explore "<concept>"` (or the `codegraph_explore` MCP tool) instead of grepping — it returns the relevant symbols' source + call paths in one shot.
- For a single symbol's 360° view (source + callers/callees), use `codegraph node <Symbol>`.

## When Debugging

1. `codegraph explore "<error or symptom>"` — surface the relevant symbols + call paths.
2. `codegraph node <suspect function>` — its source, callers, and callees.
3. `codegraph callers <Symbol>` / `codegraph callees <Symbol>` — walk the call graph in either direction.

## When Refactoring

- **Before moving/renaming**: `codegraph impact <Symbol>` to list every caller. CodeGraph has **no automatic safe-rename** — update the callers it reports by hand, then re-check.
- The index auto-syncs; if a result looks stale right after a large change, force it with `codegraph sync` (incremental) or `codegraph index` (full rebuild).

## Never Do

- NEVER edit a function/class/method without first checking `codegraph impact` (or `codegraph_explore`) on it.
- NEVER rename symbols with blind find-and-replace — list callers with `codegraph impact` first, then update each.

## Tools Quick Reference

| Command | When to use |
|---------|-------------|
| `codegraph explore "<q>"` | Answer almost any code question in one call (source + call paths + blast radius) |
| `codegraph query <name>` | Find a symbol by name |
| `codegraph node <sym\|file>` | One symbol's source + callers/callees, or a file with its dependents |
| `codegraph callers <sym>` | Who calls this |
| `codegraph callees <sym>` | What this calls |
| `codegraph impact <sym>` | Blast radius before editing |
| `codegraph status` | Index stats / freshness |

## Keeping the Index Fresh

CodeGraph auto-syncs via its background daemon as files change — there is **no** PostToolUse re-index hook and none is needed. To force it: `codegraph sync` (incremental) or `codegraph index` (full rebuild). Inspect state with `codegraph status`.
<!-- codegraph:end -->
