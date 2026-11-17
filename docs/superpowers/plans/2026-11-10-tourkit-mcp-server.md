# TourKit MCP Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mở bộ công cụ của Trợ lý số liệu (đọc số liệu CRM + hành động) thành một **MCP server** tại `/mcp`, để
nhân viên dùng Claude Code / Cursor / Claude Desktop (sau này Claude.ai, ChatGPT) hỏi đáp và thao tác trên dữ liệu
TourKit của chính họ — **cùng hành vi với `/assistant`**: cùng danh mục tool, cùng quy đổi tên → mã, cùng đệm dữ
liệu, cùng luật "xác nhận trước khi ghi".

**Architecture:** MCP server nằm **trong chính app này** (không project mới), map đường `/mcp` (Streamable HTTP)
bằng SDK MCP chính thức cho .NET. Danh mục tool **sinh từ đúng hai nguồn đang dùng cho trợ lý**:
`ChatTools.All` (đọc) và `ActionTools.Enabled(cfg)` (hành động) — thêm tool cho trợ lý là MCP có luôn, không chép
danh mục. Phần "gọi một tool đọc" (quy đổi `marketName`/`employeeName`, ghép đường dẫn, gọi `/api/ai/*` bằng JWT
của chính người dùng, đệm `d|`, đăng nhập lại khi 401) đang nằm rải trong `JsonPlannerAgent`/`NativeToolUseAgent`
→ **rút ra một dịch vụ chung `CrmToolRunner`** rồi cả trợ lý lẫn MCP gọi nó. Khác biệt duy nhất so với trợ lý: **AI
là của phía người dùng** (Claude/Cursor), app này không gọi LLM cho tool đọc → không tốn quota AI.

Đăng nhập: mỗi người tự tạo **khoá MCP** (thu hồi được) ở trang "Kết nối AI"; khoá ánh xạ tới phiên TourKit
(`TkSessionStore`) của họ → mọi lời gọi CRM đi bằng **quyền CRM của chính người đó**, tách đúng công ty. OAuth (cho
Claude.ai/ChatGPT) là **giai đoạn 2, plan riêng**.

**Tech Stack:** C# .NET 8 (giữ nguyên TFM), ASP.NET Core Minimal API, gói `ModelContextProtocol.AspNetCore`, SQL
Server (`TourkitAiDb`, schema tự áp), Redis (`ChatCache` sẵn có), React UMD (`wwwroot/pages`).

**Spec:** Trao đổi ngày 10/11/2026 (session `01Pp3mBqp6GRdmyaoRMM5893`): "từ bộ API cho AI phát triển thành MCP",
"phương án chi tiết giống như assistant của mình". Tài liệu nền: `docs/features/assistant.md` (§ Chat-Analytics,
§ Trợ lý hành động). Không có file spec riêng — các quyết định nằm ở mục **Quyết định thiết kế** dưới đây.

## Global Constraints

- Cờ **`Features:Mcp`** (đọc) và **`Features:McpActions`** (hành động), **thiếu key = TẮT** (`FeatureFlags`). Tắt
  `Mcp` → `/mcp` và `/api/v1/mcp/*` không map (404), menu "Kết nối AI" ẩn. `McpActions` tắt → MCP chỉ liệt kê tool đọc.
- TFM `net8.0` không đổi. Gói `ModelContextProtocol.AspNetCore`: bản **ổn định** mới nhất, phát hành ≥ 2 tuần, có
  target `net8.0` — ghim đúng số phiên bản trong csproj (không `*`, không preview nếu đã có bản ổn định).
- Định danh C# tiếng Anh; chú thích, log, mô tả tool, thông báo lỗi tiếng Việt. Tên test theo lối repo
  (`Tool_doc_sinh_tu_ChatTools`).
- Ngày giờ UTC `Z` khi lưu; tham số ngày gửi CRM giữ đúng định dạng `ChatTools` đang dùng (`yyyy-MM-dd`).
- **Tool đọc = đúng `ChatTools.All`; tool hành động = đúng `ActionTools.Enabled(cfg)`.** Cấm khai báo danh mục thứ hai.
- **Tham số chỉ nhận đúng `ChatTool.Params`** — đi qua `ChatTools.BuildPath` (đã lọc key). Không chuyển tiếp key lạ.
- Mọi lời gọi CRM dùng `TkSessionStore.GetValidJwtAsync(sessionId)` của **chủ khoá**; không bao giờ trả JWT/sessionId
  trong kết quả tool.
- Khoá MCP: định dạng `tkmcp_` + 32 hex; **chỉ lưu SHA-256**, hiện nguyên văn **một lần** lúc tạo.
- Hành động ghi (`assign_task`, `create_appointment`, `send_mail_reply`, `compose_mail`) **luôn hai bước**: đề xuất
  → xác nhận bằng `actionId`; thực thi qua `ActionExecutor.ExecuteAsync` (idempotent theo `actionId`). Ghi CRM vẫn
  chỉ **enqueue** `dbo.CrmActionQueue` như trợ lý.
- Hành động có gọi AI phía mình (`review_customer`, `prepare_meeting`, `score_deal`, soạn mail) trừ quota công ty
  qua `TenantQuotaStore` như trợ lý; tool đọc không trừ quota.
- Kết quả tool: tối đa **20 dòng** và **~30 KB** văn bản; vượt thì cắt và nói rõ "còn N dòng, thêm `pageIndex`".
- Chạy TOÀN BỘ `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` (không lọc) xanh trước mỗi commit;
  chỉ test trên **staging**, không ghi vào erp.
- CHANGELOG cho người dùng cuối mỗi đợt phát hành.

## Quyết định thiết kế

| # | Quyết định | Vì sao | Cái giá nếu sai |
|---|---|---|---|
| D1 | Server nằm trong app hiện tại, đường `/mcp` | Dùng lại nguyên phiên, đệm, resolver, executor; một lần deploy | Muốn tách sau thì reverse proxy theo tiền tố, không đổi đường |
| D2 | Danh mục sinh từ `ChatTools`/`ActionTools` | Một nguồn sự thật; trợ lý và MCP không lệch | Mô tả viết cho planner nội bộ có thể hơi "lệnh"; chỉnh ở chỗ chung là cả hai cùng hưởng |
| D3 | Khoá MCP riêng, ánh xạ tới phiên | `sessionId` là chìa khoá chủ (30 ngày) — dán vào file cấu hình máy người dùng là lộ cả phiên; khoá riêng thu hồi được, xem lần dùng cuối | Thêm 1 bảng SQL + 1 trang UI |
| D4 | Ghi luôn hai bước (đề xuất → xác nhận) dù client MCP đã hỏi duyệt | Client nào cũng có chế độ "luôn cho phép"; giữ đúng luật của trợ lý: người dùng thấy dữ liệu đã quy đổi (tên nhân viên → đúng người) trước khi ghi | AI phải gọi 2 lần |
| D5 | Tên mơ hồ → trả danh sách lựa chọn, không đoán | Giống `ActionResolver` hiện tại ("mơ hồ thì hỏi lại") | — |
| D6 | OAuth để giai đoạn 2 | Claude Code/Cursor/Desktop (qua `mcp-remote`) dùng header được ngay; OAuth chỉ cần cho connector Claude.ai/ChatGPT | Chưa gắn được vào Claude.ai web ở giai đoạn 1 |
| D7 | Không lọc PII ở giai đoạn 1, nhưng **chỉ người có khoá dùng được, dữ liệu theo quyền CRM của họ** | Tool đọc trả đúng thứ trợ lý đang trả cho chính người đó | Dữ liệu khách đi tới AI bên thứ ba người dùng chọn — cần ghi rõ trên trang tạo khoá + điều khoản nội bộ |

## Review Focus

1. **Lộ dữ liệu chéo công ty / chéo người**: khoá của A tuyệt đối không gọi được bằng phiên của B; khoá thu hồi phải
   chết ngay (không chờ hết đệm); phiên gốc hết hạn/đăng xuất → khoá trả 401 rõ ràng. Test:
   `McpTokenStoreTests.Khoa_thu_hoi_khong_dung_duoc_nua`, kiểm thủ công 2 tài khoản staging.
2. **Ghi CRM không qua xác nhận**: gọi thẳng `confirm_action` với `actionId` tự bịa / của người khác / đã hết hạn /
   đã thực thi → từ chối hoặc trả đúng kết quả cũ (idempotent), không ghi lần hai. Test:
   `McpActionStoreTests.Xac_nhan_actionId_cua_nguoi_khac_bi_tu_choi`.
3. **Trợ lý hiện tại hỏng sau khi rút `CrmToolRunner`**: quy đổi `marketName`/`employeeName`, đệm `d|`, đăng nhập lại
   khi 401 phải giữ y nguyên. Chạy `scripts/e2e/run-e2e.ps1` (features) trước/sau Task 1, so kết quả.
4. **Kết quả quá to làm hỏng phiên AI của client**: `customers`, `booking_tickets` với `pageSize` lớn. Test:
   `McpResultFormatterTests.Cat_toi_da_20_dong_va_bao_con_bao_nhieu`; chặn `pageSize > 50`.
5. **AI client gửi tham số sai kiểu** (số dạng chuỗi, ngày `dd/MM/yyyy`, `true` dạng chữ): không 500, trả lỗi tiếng
   Việt nói rõ tham số nào sai. Test: `McpToolSchemaTests.Ngay_sai_dinh_dang_bao_loi_ro`.

---

## Bản đồ tệp

| Tệp | Tầng | Việc |
|---|---|---|
| `TourkitAiProxy.Services/Chat/CrmToolRunner.cs` (mới) | Services | Chạy 1 tool đọc: resolve tên → mã, `BuildPath`, gọi `/api/ai/*`, đệm `d|`, re-login 401 |
| `TourkitAiProxy.Services/Chat/JsonPlannerAgent.cs` (sửa) | Services | Bỏ `ResolveMarketAsync`/`ResolveEmployeeAsync`/`GetMarketsAsync` riêng → gọi `CrmToolRunner` |
| `TourkitAiProxy.Services/Chat/NativeToolUseAgent.cs` (sửa) | Services | Dùng `CrmToolRunner` (nay được quy đổi tên → mã như planner JSON) |
| `TourkitAiProxy.Domain/Mcp/McpToolSchemas.cs` (mới) | Domain | Kiểu + mô tả từng tham số → JSON Schema; kiểm/ép kiểu đầu vào |
| `TourkitAiProxy.Domain/Mcp/McpResultFormatter.cs` (mới) | Domain | Envelope `/api/ai` → văn bản gọn (bảng markdown) + cắt dòng |
| `TourkitAiProxy.Domain/Mcp/McpTokens.cs` (mới) | Domain | Sinh khoá, băm, kiểm định dạng |
| `TourkitAiProxy.Infrastructure/Mcp/McpTokenRepository.cs` (mới) | Infrastructure | `dbo.McpTokens` (Dapper) |
| `TourkitAiProxy.Infrastructure/Db/TourkitAiDb.cs` (sửa) | Infrastructure | Thêm bảng `dbo.McpTokens` vào `SchemaSql` (idempotent) |
| `TourkitAiProxy.Services/Mcp/McpCaller.cs` (mới) | Services | Ngữ cảnh người gọi: tokenId, sessionId, tenant, username |
| `TourkitAiProxy.Services/Mcp/McpToolCatalog.cs` (mới) | Services | Sinh danh sách tool MCP từ `ChatTools` + `ActionTools.Enabled` |
| `TourkitAiProxy.Services/Mcp/McpActionStore.cs` (mới) | Services | Đề xuất chờ xác nhận (15 phút, gắn tokenId) |
| `TourkitAiProxy.Endpoints/McpEndpoints.cs` (mới) | Endpoints | Map `/mcp`, xác thực Bearer, giới hạn tần suất; `/api/v1/mcp/tokens` |
| `TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs` (sửa) | Services | `Mcp`, `McpActions` |
| `wwwroot/pages/mcp-connect.jsx` (mới) + menu | Frontend | Trang "Kết nối AI": tạo / xem / thu hồi khoá, mẫu cấu hình |
| `TourkitAiProxy.Tests/Mcp/*.cs` (mới) | Tests | Test thuần + guard |
| `docs/features/mcp.md`, `docs/api-surface.md`, `CHANGELOG.md` | Docs | |

Bán kính ảnh hưởng (chạy `codegraph impact` lại lúc bắt đầu Task 1 và dán vào ledger): `ResolveMarketAsync`,
`ResolveEmployeeAsync`, `GetMarketsAsync` là `private` của `JsonPlannerAgent` — chỉ gọi nội bộ (dòng 256–257,
743–744, 1300). `ChatTools.BuildPath` có 4 nơi gọi (`JsonPlannerAgent`, `NativeToolUseAgent`, `ChatTourLookup`,
`WidgetChatCrmService`) — Task 1 **không đổi** chữ ký hàm này.

---

## Giai đoạn 1 — MCP chỉ đọc (Task 1–5)

### Task 1: Rút `CrmToolRunner` dùng chung cho trợ lý và MCP

**Files:**
- Create: `TourkitAiProxy.Services/Chat/CrmToolRunner.cs`
- Modify: `TourkitAiProxy.Services/Chat/JsonPlannerAgent.cs` (bỏ 3 hàm resolver riêng, gọi runner)
- Modify: `TourkitAiProxy.Services/Chat/NativeToolUseAgent.cs:330-400` (khối thực thi tool)
- Modify: DI đăng ký `CrmToolRunner` (scoped, cạnh `JsonPlannerAgent`)
- Test: `TourkitAiProxy.Tests/Chat/CrmToolRunnerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record CrmToolCall(ChatTool Tool, string Path, JsonElement? ResolvedParams);
  public sealed record CrmToolOutcome(ChatTool Tool, string Path, JsonElement Data, ChatData View, bool FromCache);
  public class CrmToolRunner {
      Task<JsonElement?> ResolveNamesAsync(string sessionId, JsonElement? prms, CancellationToken ct); // marketName→marketId, employeeName→employeeId
      Task<CrmToolOutcome> RunAsync(string sessionId, string tenantId, ChatTool tool, JsonElement? prms, CancellationToken ct);
  }
  ```
  `RunAsync` = `ResolveNamesAsync` → `ChatTools.BuildPath` → đệm `d|{tenant}|{path}` → `GetValidJwtAsync` + `_api.GetAsync`
  → 401 thì `ForceReloginAsync` thử lại **một** lần → `ChatDataBuilder.Build`. Không đệm kết quả rỗng (`IsUsableData`).

- [ ] **Step 1: Ghi lại hành vi trợ lý trước khi sửa** — chạy `scripts/e2e/run-e2e.ps1` (features) trên staging, lưu
  kết quả vào ledger làm mốc so sánh.
- [ ] **Step 2: Viết test thất bại**
  - `Quy_doi_marketName_thanh_marketId_va_bo_ten_khoi_query` (danh sách thị trường giả qua seam `Func<Task<List<(int,string)>>>`)
  - `Khong_doi_khi_da_co_marketId`
  - `Ten_thi_truong_khong_khop_thi_giu_nguyen_khong_doan`
  - `Guard_JsonPlannerAgent_khong_con_resolver_rieng` (đọc nguồn: không còn `private async Task<JsonElement?> ResolveMarketAsync`)
- [ ] **Step 3:** `dotnet test` → FAIL (chưa có `CrmToolRunner`).
- [ ] **Step 4:** Chuyển nguyên văn `GetMarketsAsync`/`MatchMarket`/`ResolveMarketAsync`/`ResolveEmployeeAsync` sang
  `CrmToolRunner` (giữ đệm thị trường 6h/tenant), cho hai agent gọi qua runner. `NativeToolUseAgent` nay cũng được
  quy đổi tên → mã (trước đây không có) — ghi vào ledger như một **thay đổi hành vi có chủ đích**.
- [ ] **Step 5:** `dotnet test` toàn bộ → PASS; chạy lại e2e features, so với mốc Step 1 → không lệch.
- [ ] **Step 6: Commit** `refactor(assistant): rút CrmToolRunner dùng chung (chuẩn bị MCP)`

### Task 2: Khoá MCP (`dbo.McpTokens`) + API quản lý

**Files:**
- Create: `TourkitAiProxy.Domain/Mcp/McpTokens.cs`, `TourkitAiProxy.Infrastructure/Mcp/McpTokenRepository.cs`
- Modify: `TourkitAiProxy.Infrastructure/Db/TourkitAiDb.cs` (`SchemaSql`)
- Create: một phần `TourkitAiProxy.Endpoints/McpEndpoints.cs` (nhóm `/api/v1/mcp/tokens`)
- Test: `TourkitAiProxy.Tests/Mcp/McpTokenTests.cs`, `McpTokenStoreTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class McpTokens { string New(); bool LooksValid(string? t); string Hash(string token); } // "tkmcp_"+32 hex, SHA-256 hex
  public record McpTokenRow(long Id, string TenantId, string Username, string SessionId, string Label,
      DateTime CreatedUtc, DateTime? LastUsedUtc, DateTime? RevokedUtc);
  public class McpTokenRepository {
      Task<(long Id, string Token)> CreateAsync(string tenant, string username, string sessionId, string label, CancellationToken ct);
      Task<McpTokenRow?> FindActiveAsync(string tokenHash, CancellationToken ct); // RevokedUtc null
      Task<IReadOnlyList<McpTokenRow>> ListAsync(string tenant, string username, CancellationToken ct);
      Task<bool> RevokeAsync(long id, string tenant, string username, CancellationToken ct);
      Task TouchAsync(long id, CancellationToken ct); // LastUsedUtc, tối đa 1 lần/phút/khoá
  }
  ```
- Bảng: `dbo.McpTokens(Id bigint identity PK, TokenHash char(64) UNIQUE, TenantId nvarchar(128), Username nvarchar(128),
  SessionId nvarchar(64), Label nvarchar(100), CreatedUtc datetime2, LastUsedUtc datetime2 NULL, RevokedUtc datetime2 NULL)`,
  chỉ mục `(TenantId, Username)`. Tạo bằng `IF OBJECT_ID('dbo.McpTokens') IS NULL` trong `SchemaSql`.
- API (đọc phiên bằng `SessionAuth.Read`, chỉ thao tác khoá của chính mình):
  `POST /api/v1/mcp/tokens {label}` → `{id, token, createdUtc}` (token hiện **một lần**);
  `GET /api/v1/mcp/tokens` → danh sách **không** có token; `DELETE /api/v1/mcp/tokens/{id}` → 204 / 404.
- Khoá gắn với `SessionId` lúc tạo. Phiên đó hết (đăng xuất / bị xoá) → khoá báo 401 "Phiên gốc đã hết — tạo khoá mới".

- [ ] **Step 1: Test thất bại:** `Khoa_dung_dinh_dang_tkmcp_32_hex`, `Hai_lan_tao_ra_hai_khoa_khac_nhau`,
  `Chi_luu_bam_khong_luu_khoa` (guard: repository không có cột/biến `Token` ngoài giá trị trả về lúc tạo),
  `Khoa_thu_hoi_khong_dung_duoc_nua` (repo giả trong bộ nhớ theo đúng giao diện), `Guard_cau_SQL_kep_TenantId_Username`.
- [ ] **Step 2:** `dotnet test` → FAIL.
- [ ] **Step 3:** Cài đặt `McpTokens`, repository, `SchemaSql`, 3 endpoint (sau cờ `Features:Mcp`).
- [ ] **Step 4:** `dotnet test` → PASS. **Backup CSDL TourkitAi trước khi chạy app có bảng mới** (bảng mới, không đụng
  bảng cũ) → chạy app cổng riêng, gọi thật 3 endpoint bằng phiên staging; khoá của tài khoản A không xoá được bằng
  phiên B (404).
- [ ] **Step 5: Commit** `feat(mcp): khoá MCP thu hồi được (dbo.McpTokens + /api/v1/mcp/tokens)`

### Task 3: Khung MCP server tại `/mcp` (xác thực, giới hạn, cờ)

**Files:**
- Modify: `TourkitAiProxy.csproj` / `TourkitAiProxy.Endpoints.csproj` (thêm `ModelContextProtocol.AspNetCore`, ghim phiên bản)
- Create: `TourkitAiProxy.Services/Mcp/McpCaller.cs`; hoàn thiện `TourkitAiProxy.Endpoints/McpEndpoints.cs`
- Modify: `FeatureFlags.cs` (`Mcp`, `McpActions`), nơi map endpoint (`EndpointRegistration`)
- Test: `TourkitAiProxy.Tests/Mcp/McpEndpointGuardTests.cs`

**Interfaces:**
- Produces: `public sealed record McpCaller(long TokenId, string SessionId, string TenantId, string Username);` lấy
  qua `HttpContext.Items["mcp.caller"]` (middleware đặt) — tool handler đọc từ đây, **không** đọc header lần hai.
- Xác thực: `Authorization: Bearer tkmcp_…` → `McpTokens.Hash` → `FindActiveAsync` → `TkSessionStore.Get(SessionId)`;
  thiếu/sai/thu hồi/phiên gốc hết → 401 JSON tiếng Việt + `WWW-Authenticate: Bearer`. Đệm tra khoá 60 giây trong bộ
  nhớ, **xoá ngay khi thu hồi** (cùng tiến trình) — nhiều instance thì chấp nhận trễ ≤ 60 giây, ghi vào docs.
- Giới hạn: 60 lời gọi tool/phút/khoá (cửa sổ trượt trong bộ nhớ; có Redis thì dùng Redis). Vượt → lỗi tool
  "Bạn gọi quá nhanh, thử lại sau N giây".
- `instructions` của server (tiếng Việt, rút từ luật planner): hôm nay là ngày nào (giờ VN), "tháng này" = đầu → cuối
  tháng, ngày `yyyy-MM-dd`, gọi `tour_catalog` trước `tour_detail`, hỏi về NHÂN VIÊN dùng `employee_performance` chứ
  không `top_sellers`, tên thị trường/nhân viên cứ điền tên (máy chủ tự đổi sang mã).
- Map `/mcp` **chỉ khi** `Features:Mcp` bật; tắt → 404.

- [ ] **Step 1: Test thất bại:** `Mcp_chi_map_khi_bat_co` (guard nguồn: `MapMcp` nằm trong nhánh `FeatureFlags.Mcp(cfg)`),
  `Khong_co_Bearer_tra_401_tieng_Viet`, `Khoa_dung_dat_McpCaller_dung_tenant` (test middleware với `DefaultHttpContext`
  + repo giả).
- [ ] **Step 2:** `dotnet test` → FAIL. **Step 3:** cài đặt. **Step 4:** `dotnet test` → PASS.
- [ ] **Step 5: Kiểm thật** — chạy app cổng 5090 với `Features__Mcp=true`; `npx @modelcontextprotocol/inspector`
  nối `http://localhost:5090/mcp` + header khoá staging: `initialize` OK, `tools/list` trả rỗng (chưa có tool), không
  khoá → 401.
- [ ] **Step 6: Commit** `feat(mcp): khung MCP server /mcp — xác thực khoá, giới hạn tần suất, cờ Features:Mcp`

### Task 4: 20 tool đọc sinh từ `ChatTools`

**Files:**
- Create: `TourkitAiProxy.Domain/Mcp/McpToolSchemas.cs`, `TourkitAiProxy.Domain/Mcp/McpResultFormatter.cs`,
  `TourkitAiProxy.Services/Mcp/McpToolCatalog.cs`
- Test: `TourkitAiProxy.Tests/Mcp/McpToolSchemaTests.cs`, `McpResultFormatterTests.cs`

**Interfaces:**
- Consumes: `CrmToolRunner.RunAsync` (Task 1), `McpCaller` (Task 3).
- Produces:
  ```csharp
  public enum McpParamType { Date, Integer, Boolean, Text }
  public static class McpToolSchemas {
      // Bảng kiểu cho MỌI tham số xuất hiện trong ChatTools.All (startDate/endDate/fromDate/toDate=Date;
      // pageIndex/pageSize/trangThai/nguon/tourType/status/marketId/employeeId/... =Integer;
      // birthdayThisMonth/onlyWaiting=Boolean; còn lại Text) kèm mô tả tiếng Việt ngắn.
      JsonElement InputSchemaFor(ChatTool tool);                       // {"type":"object","properties":{...},"additionalProperties":false}
      (JsonElement? Params, string? Error) Coerce(ChatTool tool, JsonElement? args); // "15/10/2026"→lỗi rõ; "3"→3; "true"→true; pageSize>50→50
  }
  public static class McpResultFormatter {
      const int MaxRows = 20; const int MaxChars = 30_000;
      string ToText(ChatTool tool, JsonElement envelope);  // tiêu đề + kỳ + summary + bảng markdown ≤20 dòng + "còn N dòng…"
  }
  ```
- Mỗi `ChatTool` → 1 tool MCP: `name` = `ChatTool.Name`, `title` = `ChatTool.Title`, `description` = `ChatTool.Description`,
  `inputSchema` = `InputSchemaFor`, annotations `readOnlyHint: true, openWorldHint: false`. Kết quả: `content` = văn bản
  từ `ToText`; `structuredContent` = envelope gốc (đã cắt ≤ 20 item).
- Lỗi CRM (`TourKitApiException`) → kết quả tool `isError: true` với câu tiếng Việt (không ném ra ngoài).

- [ ] **Step 1: Test thất bại:**
  - `Moi_tham_so_trong_ChatTools_deu_co_kieu` (duyệt `ChatTools.All` — thêm tool mới mà quên khai kiểu là đỏ)
  - `Schema_khong_cho_tham_so_la` (`additionalProperties:false`)
  - `Ngay_sai_dinh_dang_bao_loi_ro` (`"15/10/2026"` → lỗi nêu tên tham số + định dạng đúng)
  - `So_dang_chuoi_duoc_ep_kieu`, `PageSize_qua_50_bi_kep`
  - `Cat_toi_da_20_dong_va_bao_con_bao_nhieu`, `Envelope_rong_tra_cau_khong_co_du_lieu`,
    `Envelope_hong_khong_nem` (JSON không phải object → văn bản báo lỗi)
- [ ] **Step 2:** `dotnet test` → FAIL. **Step 3:** cài đặt + đăng ký tool động trong `McpToolCatalog`.
- [ ] **Step 4:** `dotnet test` → PASS.
- [ ] **Step 5: Kiểm thật trên staging** bằng MCP Inspector, rồi bằng **Claude Code**:
  `claude mcp add --transport http tourkit http://localhost:5090/mcp --header "Authorization: Bearer tkmcp_…"`.
  Hỏi lại **đúng bộ câu của e2e features** (doanh thu tháng này, top sale, lead chờ xử lý, khách chưa chăm sóc 30
  ngày, tour đi Nhật còn chỗ, hiệu suất của <tên nhân viên>, khách thuộc thị trường <tên>) — so số liệu với
  `/assistant` cùng tài khoản: **phải khớp**. Đo: thời gian mỗi tool (lần đầu / lần đệm), cỡ kết quả.
- [ ] **Step 6: Commit** `feat(mcp): 20 tool đọc số liệu CRM sinh từ ChatTools`

### Task 5: Trang "Kết nối AI" + tài liệu + phát hành giai đoạn 1

**Files:**
- Create: `wwwroot/pages/mcp-connect.jsx`; Modify: router/menu (mục "Tích hợp"), `/api/v1/features` thêm `mcp`
- Create: `docs/features/mcp.md`; Modify: `docs/api-surface.md`, `CLAUDE.md` (bảng "đọc file nào"), `CHANGELOG.md`

- Trang: nút "Tạo khoá" (đặt tên thiết bị) → hiện khoá **một lần** + nút chép; danh sách khoá (tên, tạo lúc, dùng lần
  cuối, nút Thu hồi hai bước); ba mẫu cấu hình chép được:
  - Claude Code: `claude mcp add --transport http tourkit https://tourkit-ai.tourkit.vn/mcp --header "Authorization: Bearer <khoá>"`
  - Cursor (`.cursor/mcp.json`): `{"mcpServers":{"tourkit":{"url":"…/mcp","headers":{"Authorization":"Bearer <khoá>"}}}}`
  - Claude Desktop (qua `mcp-remote`): `npx mcp-remote …/mcp --header "Authorization: Bearer <khoá>"`
  - Một dòng cảnh báo: dữ liệu bạn hỏi sẽ được gửi tới dịch vụ AI bạn đang dùng.
- [ ] **Step 1:** Dựng trang, thử tạo/thu hồi trên 5090 (máy tính + 393px).
- [ ] **Step 2: Kiểm prod trước khi bật:** gọi `GET {TourKit:BaseUrl}/api/ai/cashflow` bằng phiên prod **chỉ đọc** —
  lần kiểm gần nhất prod **chưa có** `/api/ai/*`. Chưa có → giữ `Features:Mcp=false` trên prod, ghi rõ trong docs.
- [ ] **Step 3:** Docs + CHANGELOG ("Bạn có thể hỏi số liệu TourKit ngay trong Claude / Cursor…").
- [ ] **Step 4:** `dotnet test` toàn bộ → PASS; e2e features trợ lý vẫn xanh.
- [ ] **Step 5: Commit** `feat(mcp): trang Kết nối AI + tài liệu (giai đoạn 1 — chỉ đọc)`

**Mốc dừng duyệt:** sau Task 5, cho vài nhân viên dùng thử 1–2 tuần (log Task 8 đo tool nào được dùng) rồi mới
quyết định làm Giai đoạn 1b.

---

## Giai đoạn 1b — Hành động như trợ lý (Task 6–8, sau cờ `Features:McpActions`)

### Task 6: Hành động không ghi CRM — chạy thẳng

**Files:** `TourkitAiProxy.Services/Mcp/McpToolCatalog.cs` (mở rộng), test `McpActionCatalogTests.cs`

- Tool: `check_mail`, `review_customer`, `prepare_meeting`, `score_deal` — danh sách lấy từ
  `ActionTools.Enabled(cfg)` lọc `!NeedsConfirm` (tức không ghi CRM). Thực thi qua
  `ChatAgentService.BuildActionEnvelopeAsync` (đường trợ lý đang dùng) với phiên của `McpCaller`.
- Kết quả có `ActionClarify` (tên mơ hồ) → trả **danh sách lựa chọn** (id + tên + gợi ý phân biệt) và câu hướng dẫn
  "gọi lại với customerId = …". Không đoán.
- Trừ quota công ty như trợ lý; hết quota → lỗi tiếng Việt từ `QuotaExhaustedException`.
- Annotations: `readOnlyHint: true` (không đổi dữ liệu CRM), `openWorldHint: false`.
- [ ] Test thất bại → cài → PASS: `Chi_liet_ke_action_dang_bat` (tắt `MeetingBrief` → không có `prepare_meeting`),
  `Ten_mo_ho_tra_lua_chon_khong_doan`, `Het_quota_bao_loi_ro`.
- [ ] Kiểm thật: "đánh giá khách <SĐT>" trong Claude Code ra cùng hạng với `/assistant`.
- [ ] Commit `feat(mcp): hành động chấm hạng / chuẩn bị gặp / chấm cơ hội / kiểm mail`

### Task 7: Hành động ghi — đề xuất rồi xác nhận

**Files:** `TourkitAiProxy.Services/Mcp/McpActionStore.cs`, `McpToolCatalog.cs`, test `McpActionStoreTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record McpPendingAction(string ActionId, long TokenId, string TenantId, string Action,
      Dictionary<string, object?> Params, DateTime ExpiresUtc);
  public class McpActionStore {   // Redis nếu có, không thì bộ nhớ; TTL 15 phút
      string Put(McpCaller who, string action, Dictionary<string, object?> resolvedParams);
      McpPendingAction? Take(McpCaller who, string actionId);   // sai token / sai tenant / hết hạn → null
  }
  ```
- Mỗi action ghi (`assign_task`, `create_appointment`, `send_mail_reply`, `compose_mail`) → **một tool đề xuất cùng
  tên** (`assign_task` …): dựng đề xuất qua đúng `BuildAssignTaskProposalAsync`/… của trợ lý (tên nhân viên → đúng
  người, giờ VN, mặc định), **không ghi gì**, trả bản xem trước + `actionId` + câu "Hỏi người dùng xác nhận rồi gọi
  `confirm_action`".
- Tool `confirm_action {actionId, overrides?}`: `Take` → trộn `overrides` (chỉ các field mà thẻ xác nhận của trợ lý
  cho sửa) → `ActionExecutor.ExecuteAsync(new ActionExecuteRequest(actionId, action, params), …)` (idempotent).
  Annotations: `destructiveHint: true`.
- [ ] Test thất bại → cài → PASS: `De_xuat_khong_ghi_gi` (executor giả đếm lời gọi = 0),
  `Xac_nhan_actionId_cua_nguoi_khac_bi_tu_choi`, `Xac_nhan_hai_lan_chi_ghi_mot_lan`, `Het_15_phut_thi_het_hieu_luc`,
  `Override_field_khong_cho_sua_bi_bo_qua`.
- [ ] Kiểm thật trên staging: "giao việc gọi lại khách X cho <nhân viên> trước 17h mai" → đề xuất đúng người, đúng giờ
  → xác nhận → có đúng **một** dòng `dbo.CrmActionQueue`; gọi `confirm_action` lần hai → không thêm dòng.
- [ ] Commit `feat(mcp): giao việc / tạo lịch hẹn / mail — đề xuất rồi xác nhận`

### Task 8: Nhật ký, đo đạc, phát hành 1b

- Ghi mỗi lời gọi tool MCP: tokenId, tenant, username, tool, thời gian, cỡ kết quả, ok/lỗi (log4net
  `RequestLoggingMiddleware` + một dòng `[mcp]`); KHÔNG ghi nội dung tham số chứa SĐT/email (chỉ tên tham số).
- `LastUsedUtc` hiện trên trang "Kết nối AI".
- Docs (`docs/features/mcp.md` § Hành động), CHANGELOG.
- [ ] `dotnet test` toàn bộ → PASS; e2e features trợ lý xanh.
- [ ] Commit `feat(mcp): nhật ký lời gọi + phát hành hành động MCP`

---

## Giai đoạn 2 — OAuth cho Claude.ai / ChatGPT (plan riêng, chưa làm)

Gồm: metadata `/.well-known/oauth-protected-resource`, máy chủ ủy quyền (đăng nhập TourKit → cấp token ngắn hạn
gắn phiên), đăng ký client động, refresh token, màn đồng ý. Chỉ viết plan khi Giai đoạn 1 có người dùng thật.

## Ước lượng

| Phần | Công |
|---|---|
| Task 1 (rút runner, giữ trợ lý không đổi) | 0,5–1 ngày |
| Task 2–3 (khoá + khung server) | 1 ngày |
| Task 4 (20 tool đọc + kiểm khớp số với trợ lý) | 1 ngày |
| Task 5 (trang Kết nối AI + docs) | 0,5–1 ngày |
| **Giai đoạn 1** | **~3–4 ngày** |
| Task 6–8 (hành động) | 2–3 ngày |
| Giai đoạn 2 (OAuth) | ~1 tuần, plan riêng |
