# Kết nối AI (MCP server)

> Giai đoạn 1: **chỉ đọc**. Nhân viên cắm Claude Code / Cursor / Claude Desktop vào TourKit để hỏi số liệu
> CRM ngay trong công cụ AI của mình. Cờ `Features:Mcp` (mặc định TẮT).
> Kiến trúc chung và luật đặt file: [ARCHITECTURE.md](../ARCHITECTURE.md).

## Là gì

Một máy chủ MCP (Model Context Protocol) ở `POST /mcp` trong chính tiến trình proxy. Công cụ AI ngoài
(Claude Code, Cursor, Claude Desktop) gọi vào đây, thấy **20 tool đọc số liệu CRM** và gọi chúng để lấy số thật.
Không có hành động ghi nào ở giai đoạn này.

## Nhân viên kết nối thế nào

1. Vào menu **Tích hợp → Kết nối AI** (`/mcp-connect`). Mục này không đòi quyền quản trị, chỉ cần cờ bật.
2. Đặt tên thiết bị, bấm **Tạo khoá**. Khoá dạng `tkmcp_…` hiện **một lần** — chép ngay.
3. Dán một trong ba mẫu (trang tự điền khoá và địa chỉ của chính nơi đang mở):

```
# Claude Code
claude mcp add --transport http tourkit https://tourkit-ai.tourkit.vn/mcp --header "Authorization: Bearer <khoá>"

# Cursor — .cursor/mcp.json
{"mcpServers":{"tourkit":{"url":"https://tourkit-ai.tourkit.vn/mcp","headers":{"Authorization":"Bearer <khoá>"}}}}

# Claude Desktop — claude_desktop_config.json (Settings → Developer → Edit Config), dạng biến môi trường
# của mcp-remote để Windows không cắt đối số có dấu cách
{"mcpServers":{"tourkit":{"command":"npx","args":["-y","mcp-remote","https://tourkit-ai.tourkit.vn/mcp","--header","Authorization:${AUTH_HEADER}"],"env":{"AUTH_HEADER":"Bearer <khoá>"}}}}
```

Thu hồi: nút **Thu hồi** trên cùng trang, bấm hai lần (lần một đổi chữ thành "Bấm lần nữa để thu hồi").

Trang có dòng cảnh báo: dữ liệu người dùng hỏi sẽ được gửi tới dịch vụ AI họ đang dùng.

## Tool từ đâu ra

20 tool **sinh từ `ChatTools`** — cùng nguồn với Trợ lý số liệu ([assistant.md](assistant.md)), không khai
song song. Thêm/sửa tool ở `ChatTools` thì MCP tự theo. Mỗi tool gọi API CRM `/api/ai/*` bằng
**phiên TourKit của chính người dùng**, nên quyền xem dữ liệu đúng như khi họ dùng trợ lý.

## Xác thực

- Khoá `tkmcp_…` gắn với một người dùng. Máy chủ chỉ lưu bản **băm** (`dbo.McpTokens`), không lưu khoá thô.
- Mỗi lời gọi: khoá → tra người dùng → phiên TourKit (`TkSessionStore`).
- Thu hồi qua `DELETE /api/v1/mcp/tokens/{id}`. Kết quả tra khoá được **đệm 60 giây**; khi chạy nhiều
  instance, thu hồi có hiệu lực trễ tối đa 60 giây trên máy khác.
- Kết thúc/đá phiên TourKit gốc thì khoá chết trong tối đa 60 giây (đệm tra khoá).
- Tối đa 20 khoá còn hiệu lực mỗi người; nhãn bắt buộc, ≤100 ký tự.

## Giới hạn

- **Tần suất:** 60 lần gọi tool mỗi phút mỗi khoá.
- **Kết quả:** tối đa 20 dòng mỗi lần, khoảng 30KB, `pageSize` ≤ 50 — để AI ngoài không nuốt cả bảng.
- **Đệm dữ liệu CRM theo từng người dùng** (không chung cả công ty), nên hai nhân viên quyền khác nhau không lẫn số liệu.

## Cờ

| Cờ | Mặc định | Ý nghĩa |
|---|---|---|
| `Features:Mcp` | tắt | Bật `/mcp`, `/api/v1/mcp/tokens*` và mục menu. Tắt: trang giao diện hiện "chưa mở", menu ẩn; còn `/mcp` và `/api/v1/mcp/tokens*` trả 404 JSON |
| `Features:McpActions` | tắt | Giai đoạn 1b (hành động ghi). Chưa dùng ở giai đoạn 1 |
| `Features:McpOAuth` | tắt | Kết nối Claude.ai / ChatGPT bằng OAuth. Chỉ có hiệu lực khi `Features:Mcp` bật **và** `Mcp:PublicBaseUrl` hợp lệ — xem mục OAuth bên dưới |

## Bật trên prod

1. Kiểm `TourKit:BaseUrl` của site prod trỏ tới máy chủ **có** `/api/ai/*`. Đã kiểm 10/11/2026: API prod
   `mobile-api.tourkit.vn` **đã có** route `/api/ai/*` (401 khi không xác thực, route lạ trả 404). Còn phải xác nhận
   site prod đang dùng đúng BaseUrl đó.
2. Rồi mới đặt `Features:Mcp=true` và Recycle app pool.

## Giới hạn đã biết

- Một số báo cáo trả chậm ở lần gọi đầu (vd danh sách khách theo `careFilter` khoảng 12 giây).
- Khoá Bearer `tkmcp_` vẫn là cách cho Claude Code / Cursor / Claude Desktop (qua `mcp-remote` có header).
  Claude.ai web / ChatGPT dùng OAuth (mục dưới, sau cờ `Features:McpOAuth`).

## Kết nối Claude.ai / ChatGPT (OAuth)

Claude.ai và ChatGPT không cho dán khoá riêng cho từng người (Claude chỉ có một khoá chung cả tổ chức →
mọi người dùng quyền một người). Nên app tự làm **máy chủ cấp quyền OAuth 2.1** cho `/mcp`: người dùng thêm
connector, đăng nhập TRAV-AI, bấm "Cho phép", và ứng dụng nhận token gắn với **phiên TourKit của chính họ**.

### Luồng (bằng lời)

1. Ứng dụng gọi `POST /mcp` không token → **401** kèm
   `WWW-Authenticate: Bearer resource_metadata="{PublicBaseUrl}/.well-known/oauth-protected-resource", scope="tourkit.read"`.
   Claude chỉ bắt đầu đăng nhập khi gặp đúng 401 + header này.
2. Ứng dụng đọc `/.well-known/oauth-protected-resource` (resource = `{PublicBaseUrl}/mcp`, máy chủ cấp quyền =
   `{PublicBaseUrl}`) rồi `/.well-known/oauth-authorization-server`.
3. Ứng dụng tự giới thiệu: **CIMD** (client_id là URL https trên `claude.ai`/`chatgpt.com`, máy chủ tải tài liệu
   client về kiểm) hoặc **DCR** (`POST /oauth/register`, chỉ client công khai, redirect trong danh sách trắng).
4. Ứng dụng mở trình duyệt tới `GET /oauth/authorize?...` (PKCE S256 bắt buộc, `resource` phải đúng). Trang đồng ý
   (SPA) hỏi `authorize-info`; chưa đăng nhập thì hiện màn đăng nhập rồi quay lại; người dùng bấm **Cho phép** →
   `approve` (header `X-Session-Id`, máy chủ kiểm lại toàn bộ tham số) → mã dùng một lần (5 phút) → chuyển về
   `redirect_uri?code=…&state=…&iss=…`.
5. Ứng dụng đổi mã ở `POST /oauth/token` (PKCE verifier) → access `tkoat_…` (1 giờ) + refresh `tkort_…` (30 ngày).
6. Mỗi lời gọi `/mcp` mang `Bearer tkoat_…` → cổng kiểm: grant chưa thu hồi, access còn hạn (UTC), resource của token
   đúng bằng `{PublicBaseUrl}/mcp`, phiên TourKit của grant còn và đúng chủ → chạy tool bằng quyền CRM của người đó.
   Trượt bất kỳ điều nào → 401 + header ở bước 1 → ứng dụng tự refresh hoặc đăng nhập lại.
7. Hết access → ứng dụng `POST /oauth/token grant_type=refresh_token` → cặp mới (refresh **xoay vòng**).
8. Người dùng ngắt kết nối ở trang **Kết nối AI → Ứng dụng đã kết nối** → `DELETE /api/v1/mcp/oauth/connections/{id}`
   → token chết **ngay** trong tiến trình này (xoá khỏi đệm), instance khác trễ tối đa 60 giây.

### Bảng (CSDL TourKit_Push, chỉ thêm)

| Bảng | Giữ gì | Ghi chú |
|---|---|---|
| `dbo.McpOAuthClients` | Client đăng ký động (DCR): `ClientId`, `ClientName`, `RedirectUrisJson`, `CreatedUtc`, `CreatedIp` | Client CIMD **không** lưu, chỉ đệm 1 giờ trong RAM (tối đa 500 mục) |
| `dbo.McpOAuthCodes` | Mã uỷ quyền: `CodeHash` (SHA-256), client, redirect, `CodeChallenge`, resource, scope, người duyệt + `SessionId`, `ExpiresUtc`, `UsedUtc` | Dùng một lần nguyên tử (`UPDATE … WHERE UsedUtc IS NULL AND ExpiresUtc > now`) |
| `dbo.McpOAuthGrants` | Một lần "Cho phép": client, tenant, username, `SessionId`, resource, scope, `AccessHash` / `RefreshHash` / `PrevRefreshHash` (SHA-256), hạn, `CreatedUtc`, `LastUsedUtc`, `RevokedUtc` | Mỗi dòng = một mục trong "Ứng dụng đã kết nối". Thu hồi mềm |

Chỉ lưu **băm** của mã/token, không bao giờ bản thô. Cột băm `char(64)` nhận tham số ANSI cố định để giữ Index Seek.

### Cấu hình

| Khoá | Ví dụ | Ý nghĩa |
|---|---|---|
| `Features:McpOAuth` | `true` | Bật OAuth. Thiếu = tắt |
| `Mcp:PublicBaseUrl` | `https://<sub>.travelai.vn` | Gốc công khai của **chính site Tour_AI**, https cổng 443, không đường dẫn con. Issuer = giá trị này, resource = `{giá trị}/mcp`. Claude/ChatGPT so khớp **chính xác** — sai một chút (http/https, `www`, `/` cuối, cổng) là kết nối hỏng. Sub cụ thể chưa chốt |
| `Mcp:OAuth:ExtraRedirectUris` | `["https://…/callback"]` | Thêm redirect ngoài danh sách cứng (so khớp chính xác) |
| `Mcp:OAuth:CimdHosts` | `["example.com"]` | Thêm host được tải tài liệu client (CIMD) — chỉ thêm, không thay |

Thiếu/sai `Mcp:PublicBaseUrl` khi bật cờ → log lỗi lúc khởi động và **không map** đường OAuth nào (404 JSON).
Issuer/resource **không bao giờ** lấy từ header `Host`.

**Danh sách trắng redirect (cứng):** `https://claude.ai/api/mcp/auth_callback`,
`https://chatgpt.com/connector_platform_oauth_redirect`, `https://chatgpt.com/connector/oauth/{đoạn an toàn}`, loopback
`http://localhost:{cổng}/…` và `http://127.0.0.1:{cổng}/…` (Claude Code, MCP Inspector — bỏ qua cổng). Khác một chút
(đường dẫn thêm, query, chữ hoa host, http ngoài loopback) → từ chối **trước** trang đồng ý, không chuyển hướng lỗi về URI lạ.

**CIMD:** chỉ tải `client_id` https thuộc `claude.ai`, `chatgpt.com` (+ `Mcp:OAuth:CimdHosts`), timeout 5 giây, tối đa
64 KB, không theo chuyển hướng, chặn địa chỉ nội bộ (SSRF), `client_id` trong tài liệu phải bằng URL đã tải.

### Token

| Loại | Dạng | Sống | Ghi chú |
|---|---|---|---|
| Mã uỷ quyền | `tkoac_` + 32 hex | 5 phút | Dùng một lần, gắn client + redirect + PKCE + resource |
| Access | `tkoat_` + 32 hex | 1 giờ | Gắn `resource` (audience); `/mcp` từ chối resource khác. Đệm xác thực 60 giây nhưng **không bao giờ** quá hạn access |
| Refresh | `tkort_` + 32 hex | 30 ngày | **Xoay vòng** mỗi lần dùng. Dùng lại refresh cũ = **thu hồi cả grant**. Không có cửa sổ ân hạn |

Scope: `tourkit.read` (+ `offline_access` để nhận refresh). Không khai `openid/email/profile`. Phiên TourKit của
người bấm "Cho phép" hết → token 401, refresh `invalid_grant` → ứng dụng đăng nhập lại.

**Người gọi và giới hạn tần suất:** `McpCaller.Kind` phân biệt khoá tay (`ManualKey`, Id trong `dbo.McpTokens`) và
kết nối OAuth (`OAuthGrant`, Id trong `dbo.McpOAuthGrants`). Hai bảng đánh số độc lập nên đệm xác thực, giới hạn
60 lời gọi/phút và log đều khoá theo (loại, Id): thu hồi khoá tay số 5 không đụng kết nối số 5, và mỗi kết nối có ngăn
đếm riêng.

### Bật trên prod

1. Chọn sub của `travelai.vn`, trỏ DNS (Cloudflare) về site **Tour_AI** trên IIS, chứng chỉ https cổng 443.
2. Đặt `Features:Mcp=true`, `Features:McpOAuth=true`, `Mcp:PublicBaseUrl=https://<sub>.travelai.vn` (đúng chính tả,
   không `/` cuối) trong cấu hình site.
3. **Recycle app pool** (cấu hình đọc lúc khởi động).
4. Kiểm: `GET https://<sub>.travelai.vn/.well-known/oauth-protected-resource` → `resource` = `https://<sub>.travelai.vn/mcp`;
   `POST /mcp` không token → 401 có `resource_metadata` trong `WWW-Authenticate`.
5. Thử thật: Claude.ai → Customize → Connectors → Add custom connector → URL `https://<sub>.travelai.vn/mcp`;
   ChatGPT web → https://chatgpt.com/plugins → **+** → Add custom MCP server → Server URL như trên, Authentication **OAuth**.

**Cloudflare / IIS:**
- Không nén / không đệm `text/event-stream` (Streamable HTTP có thể trả SSE): tắt nén động cho kiểu này ở IIS, và ở
  Cloudflare không bật tính năng gom/đệm phản hồi cho `/mcp`. Bị đệm → client treo tới hết thời gian chờ.
- Header `WWW-Authenticate` phải đi qua nguyên vẹn (một số luật WAF/rewrite cắt header của 401) — kiểm ở bước 4.
- Không để trang lỗi tuỳ biến của IIS/Cloudflare đè lên 401/400 của `/mcp` và `/oauth/*` (IIS: `existingResponse="PassThrough"`):
  bị thay bằng trang HTML thì client không đọc được JSON lỗi.
- `/.well-known/*` phải tới được app (kiểm không có luật nào chặn đường bắt đầu bằng dấu chấm).

### Giới hạn đã biết (OAuth)

- **Scope thu hẹp không được lưu**: xin `tourkit.read` hay thêm `offline_access` đều ra cùng grant; refresh xin rộng
  hơn trả lại scope đã cấp.
- **Dùng lại mã uỷ quyền không thu hồi grant đã cấp** từ mã đó (bảng mã chưa giữ GrantId) — lần hai chỉ `invalid_grant`.
- **Không có cửa sổ ân hạn khi xoay refresh**: client gửi refresh, mất phản hồi vì timeout rồi gửi lại cùng refresh cũ
  → coi là dùng lại → thu hồi grant → người dùng phải kết nối lại.
- Sau khi refresh, access cũ không còn tra được trong SQL (cột `AccessHash` bị thay) — chỉ còn sống tối đa 60 giây nhờ đệm.
- **CIMD chỉ tải từ `claude.ai` / `chatgpt.com`** (+ cấu hình). Client khác dùng DCR.
- Nhiều instance: thu hồi trễ tối đa 60 giây ở instance khác (như khoá tay).
- Chưa dọn mã/grant hết hạn tự động.
- Chưa có hành động ghi (giai đoạn 1b, sau cờ `Features:McpActions`).
