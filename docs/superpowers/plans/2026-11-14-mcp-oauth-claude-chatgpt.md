# MCP OAuth — Kết nối Claude.ai và ChatGPT — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cho nhân viên thêm TourKit làm **connector** trong Claude.ai (web/desktop/mobile) và ChatGPT: bấm
"Kết nối" → đăng nhập TRAV-AI → bấm "Cho phép" → AI gọi 20 tool đọc số liệu bằng **đúng quyền CRM của chính người
đó**. Không phải chép khoá tay.

**Architecture:** Máy chủ cấp quyền OAuth 2.1 **nằm ngay trong app** (cùng tên miền với `/mcp`, issuer =
`https://<host>`). Thêm: tài liệu khám phá (RFC 9728 + RFC 8414), đăng ký client (CIMD — Claude/ChatGPT khuyên dùng —
và DCR dự phòng), `/oauth/authorize` (trang đồng ý trong SPA, dùng phiên TRAV-AI đang đăng nhập), `/oauth/token`
(authorization_code + PKCE S256, refresh_token xoay vòng). Mỗi lần "Cho phép" tạo một **grant** gắn với phiên TourKit
(`TkSessionStore`) của người đồng ý — y như khoá `tkmcp_` của giai đoạn 1 — nên mọi lời gọi CRM vẫn đi bằng quyền của
họ. Middleware `/mcp` nhận thêm access token OAuth (`tkoat_…`) bên cạnh khoá tay (`tkmcp_…`), và 401 trả
`WWW-Authenticate: Bearer resource_metadata="…"` để client tự khởi động đăng nhập.

**Tech Stack:** ASP.NET Core 8 (TFM giữ net8.0), SQL Server `TourkitAiDb` (schema tự áp, chỉ THÊM bảng), React UMD
(`wwwroot/pages`), `ModelContextProtocol.AspNetCore` 2.3.0 sẵn có.

**Spec:** Trao đổi 14/11/2026 ("tích hợp vào claude.ai và chatgpt") + tài liệu nhà cung cấp đọc ngày 14/11/2026:
- Claude: https://claude.com/docs/connectors/building/authentication.md, https://claude.com/docs/connectors/custom/add-unlisted,
  https://support.claude.com/en/articles/11175166 — gọi từ cloud Anthropic, public HTTPS cổng 443; CIMD
  (`client_id_metadata_document_supported: true` + `none` trong `token_endpoint_auth_methods_supported`) hoặc DCR;
  PKCE S256 bắt buộc; redirect `https://claude.ai/api/mcp/auth_callback` (Claude Code: loopback cổng bất kỳ);
  `resource` trong protected-resource metadata phải BẰNG ĐÚNG URL MCP; refresh khi 401 và trước hạn 5 phút, xoay vòng
  refresh token, `invalid_grant` khi refresh chết; token endpoint nhận `application/x-www-form-urlencoded`; chỉ khởi động
  đăng nhập khi HTTP **401** có `WWW-Authenticate: Bearer resource_metadata="…"`; dò dự phòng
  `/.well-known/oauth-protected-resource/<path>` rồi `/.well-known/oauth-protected-resource`; chờ tối đa 10 s.
  Khoá cố định qua header chỉ là **một khoá chung cả tổ chức** (beta) → không đáp ứng "mỗi người một quyền".
- ChatGPT: https://developers.openai.com/apps-sdk/build/auth, https://developers.openai.com/api/docs/guides/developer-mode —
  Streamable HTTP; tool ghi cần xác nhận, tool có `readOnlyHint: true` thì không; OAuth: CIMD (client_id
  `https://chatgpt.com/oauth/client.json` hoặc `https://chatgpt.com/oauth/{callback_id}/client.json`), DCR, hoặc client
  tĩnh; PKCE S256; gửi `resource` ở cả authorize lẫn token (nên đưa vào `aud`); RFC 9207: khai
  `authorization_response_iss_parameter_supported: true` và trả `iss` trong mọi phản hồi authorize → dùng redirect cố
  định `https://chatgpt.com/connector_platform_oauth_redirect` (nếu không thì `https://chatgpt.com/connector/oauth/{callback_id}`);
  không hỗ trợ API key tự chế.
- MCP spec 2025-06-18 Authorization: 401 + WWW-Authenticate bắt buộc; kiểm audience của token; redirect URI khớp
  chính xác; mọi endpoint HTTPS (trừ localhost lúc thử).

## Global Constraints

- Cờ mới **`Features:McpOAuth`** (thiếu key = TẮT), chỉ có tác dụng khi `Features:Mcp` cũng bật. Tắt → không map các
  đường `/.well-known/oauth-*`, `/oauth/*`, trang đồng ý; middleware không nhận `tkoat_`; 401 trả như giai đoạn 1.
- Không đổi hành vi khoá `tkmcp_`, 20 tool, giới hạn tần suất, trợ lý `/assistant`.
- Issuer / resource tính từ cấu hình **`Mcp:PublicBaseUrl`** (vd `https://tourkit-ai.tourkit.vn`), KHÔNG từ header
  `Host` (sau Cloudflare/IIS host có thể sai, và tin header là lỗ hổng). Thiếu cấu hình khi bật cờ → log lỗi + không map.
  `resource` = `{PublicBaseUrl}/mcp` (chữ thường, không dấu `/` cuối).
- Redirect URI chỉ chấp nhận **danh sách trắng**: `https://claude.ai/api/mcp/auth_callback`,
  `https://chatgpt.com/connector_platform_oauth_redirect`, `https://chatgpt.com/connector/oauth/{bất kỳ đoạn an toàn}`,
  loopback `http://localhost:{port}/…` và `http://127.0.0.1:{port}/…` (Claude Code / MCP Inspector). Thêm bằng cấu hình
  `Mcp:OAuth:ExtraRedirectUris`. So khớp chính xác (trừ cổng loopback).
- CIMD: chỉ tải `client_id` là URL **https** thuộc host trong danh sách trắng `claude.ai`, `chatgpt.com` (+ cấu hình
  `Mcp:OAuth:CimdHosts`); timeout 5 s, tối đa 64 KB, không theo redirect sang host khác; `client_id` trong tài liệu phải
  BẰNG URL đã tải; đệm 1 giờ. Chặn SSRF.
- DCR: chỉ client công khai (`token_endpoint_auth_method: none`), redirect_uris phải nằm trong danh sách trắng; giới hạn
  20 lần đăng ký/giờ/IP.
- PKCE: chỉ `S256`, bắt buộc. Mã uỷ quyền dùng **một lần**, sống 5 phút, gắn client_id + redirect_uri + code_challenge +
  resource + grant.
- Token: access `tkoat_` + 32 hex (sống 1 giờ), refresh `tkort_` + 32 hex (sống 30 ngày, **xoay vòng** mỗi lần dùng;
  dùng lại refresh cũ = thu hồi cả grant). Chỉ lưu SHA-256. Token gắn `resource` (audience); `/mcp` từ chối token có
  resource khác.
- Grant gắn phiên TourKit (`SessionId`) của người bấm "Cho phép"; phiên hết → token 401 → client đăng nhập lại.
- Scope: một scope `tourkit.read` (+ `offline_access` để Claude lấy refresh token). Không khai `openid/email/profile`.
- Ngày giờ UTC; định danh C# tiếng Anh; chữ hiển thị/log/chú thích tiếng Việt (`.jsx` đặt tên tiếng Việt theo lệ repo).
  Test tên tiếng Việt không dấu, gạch dưới. Chạy TOÀN BỘ `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`.
- Bảng mới chỉ THÊM (`IF OBJECT_ID … IS NULL`) trên `TourKit_Push` — không cần backup (memory "backup chỉ khi xoá/sửa").
- CHANGELOG mục "🔜 Sắp có" (cờ mặc định tắt).

## Quyết định thiết kế

| # | Quyết định | Vì sao | Cái giá nếu sai |
|---|---|---|---|
| E1 | Máy chủ OAuth trong chính app, không dùng IdP ngoài | TourKit chưa có IdP; đăng nhập TRAV-AI đã có; một lần deploy | Muốn chuyển sang IdP sau thì thay issuer, client phải kết nối lại |
| E2 | Hỗ trợ CẢ CIMD lẫn DCR | Claude & ChatGPT đều ưu tiên CIMD, DCR là dự phòng (Claude Code, MCP Inspector, client khác) | Thêm 1 bảng client |
| E3 | Token mờ (opaque) lưu băm trong SQL, không dùng JWT | Thu hồi tức thì, cùng lối khoá `tkmcp_`, không phải quản lý khoá ký | Mỗi request tra SQL (đã có đệm 60 s như giai đoạn 1) |
| E4 | Trang đồng ý là một route của SPA, dùng phiên TRAV-AI trong trình duyệt | Đăng nhập/quyên mật khẩu đã có sẵn ở SPA; không phải viết form đăng nhập thứ hai | Trang đồng ý nạp SPA (~1 MB bundle) — chấp nhận |
| E5 | Chỉ tool đọc (giữ `readOnlyHint: true`) | ChatGPT không bắt xác nhận cho tool đọc; tool ghi là giai đoạn sau | — |
| E6 | Không hỗ trợ khoá cố định qua header cho Claude.ai | Claude chỉ cho một khoá chung cả tổ chức → mọi người dùng quyền một người | Ai cần thì vẫn dùng khoá `tkmcp_` qua Claude Code/Cursor |

## Review Focus

1. **Chiếm quyền qua redirect**: redirect_uri ngoài danh sách trắng, khác chút ít (`https://claude.ai/api/mcp/auth_callback/x`,
   chữ hoa, có query), hoặc khác cái đã đăng ký → phải từ chối TRƯỚC khi hiện trang đồng ý (không redirect lỗi về URI lạ).
2. **Đánh tráo mã / PKCE**: code dùng hai lần, code của client A đổi ở client B, sai verifier, sai redirect_uri, sai
   resource, quá 5 phút → `invalid_grant`; dùng lại refresh token cũ → thu hồi cả grant.
3. **SSRF qua CIMD**: client_id `https://169.254.169.254/…`, `http://…`, host ngoài danh sách, chuyển hướng sang host lạ,
   tài liệu 10 MB, tài liệu có `client_id` khác URL → từ chối, không treo quá 5 s.
4. **Token sai đối tượng**: access token cấp cho resource khác, hết hạn, grant đã thu hồi, phiên TourKit đã hết → 401
   kèm `WWW-Authenticate: Bearer resource_metadata="…"`, không bao giờ 200.
5. **CSRF trang đồng ý**: lệnh "Cho phép" phải đi kèm phiên TRAV-AI (header `X-Session-Id`, không cookie) và tham số
   được máy chủ kiểm lại lúc approve (không tin dữ liệu đã kiểm ở bước hiện trang).

---

## Bản đồ tệp

| Tệp | Tầng | Việc |
|---|---|---|
| `TourkitAiProxy.Domain/Mcp/OAuth/OAuthRules.cs` | Domain | PKCE S256, kiểm redirect URI (danh sách trắng), định dạng token `tkoat_`/`tkort_`/mã `tkoac_`, kiểm resource |
| `TourkitAiProxy.Domain/Mcp/OAuth/OAuthMetadata.cs` | Domain | Dựng JSON protected-resource + authorization-server metadata từ base URL |
| `TourkitAiProxy.Domain/Mcp/OAuth/ClientMetadataDocument.cs` | Domain | Đọc/kiểm tài liệu CIMD và yêu cầu DCR |
| `TourkitAiProxy.Infrastructure/Mcp/McpOAuthRepository.cs` + `IMcpOAuthStore` | Infrastructure | `dbo.McpOAuthClients` (DCR), `dbo.McpOAuthCodes`, `dbo.McpOAuthGrants` |
| `TourkitAiProxy.Infrastructure/Db/TourkitAiDb.cs` | Infrastructure | 3 bảng mới trong `SchemaSql` |
| `TourkitAiProxy.Services/Mcp/OAuth/ClientMetadataFetcher.cs` | Services | Tải CIMD an toàn (danh sách host, timeout, cỡ, đệm) |
| `TourkitAiProxy.Services/Mcp/OAuth/McpOAuthService.cs` | Services | Xử lý authorize-info / approve / token / register |
| `TourkitAiProxy.Services/Mcp/McpAuthenticator.cs` (sửa) | Services | Nhận thêm `tkoat_`; 401 kèm resource_metadata |
| `TourkitAiProxy.Endpoints/McpOAuthEndpoints.cs` | Endpoints | `/.well-known/*`, `/oauth/register`, `/oauth/token`, `/api/v1/mcp/oauth/authorize-info|approve|deny`, `/api/v1/mcp/oauth/connections` |
| `wwwroot/pages/oauth-consent.jsx` + route `/oauth/authorize` | Frontend | Trang đồng ý |
| `wwwroot/pages/mcp-connect.jsx` (sửa) | Frontend | Mục "Ứng dụng đã kết nối" (Claude, ChatGPT…) + thu hồi; hướng dẫn thêm connector |
| `TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs` | Services | `McpOAuth` |
| `docs/features/mcp.md`, `docs/api-surface.md`, `CHANGELOG.md` | Docs | |

---

### Task 1: Luật OAuth thuần (Domain) + metadata

**Files:** Create `OAuthRules.cs`, `OAuthMetadata.cs`, `ClientMetadataDocument.cs`; Test `TourkitAiProxy.Tests/Mcp/OAuthRulesTests.cs`, `OAuthMetadataTests.cs`

**Interfaces — Produces:**
```csharp
public static class OAuthRules {
    bool VerifyPkceS256(string codeVerifier, string codeChallenge);           // base64url(SHA256(verifier)) == challenge; verifier 43–128 ký tự [A-Za-z0-9-._~]
    bool IsAllowedRedirectUri(string? uri, IReadOnlyCollection<string> extra); // danh sách trắng trong Global Constraints
    bool RedirectMatches(string registered, string requested);                 // khớp chính xác; loopback bỏ qua cổng
    string? NormalizeResource(string? resource);                               // chữ thường scheme+host, bỏ '/' cuối, null nếu không phải http(s)
    string NewAccessToken(); string NewRefreshToken(); string NewAuthCode();   // tkoat_/tkort_/tkoac_ + 32 hex
    string Hash(string token);                                                 // SHA-256 hex (dùng lại McpTokens.Hash nếu cùng thuật toán)
}
public static class OAuthMetadata {
    JsonObject ProtectedResource(string baseUrl);   // resource={base}/mcp, authorization_servers=[base], scopes_supported=["tourkit.read","offline_access"], bearer_methods_supported=["header"], resource_name="TourKit"
    JsonObject AuthorizationServer(string baseUrl); // issuer, authorization_endpoint={base}/oauth/authorize, token_endpoint={base}/oauth/token, registration_endpoint={base}/oauth/register, response_types_supported=["code"], grant_types_supported=["authorization_code","refresh_token"], code_challenge_methods_supported=["S256"], token_endpoint_auth_methods_supported=["none"], client_id_metadata_document_supported=true, authorization_response_iss_parameter_supported=true, scopes_supported
}
public sealed record ClientMetadata(string ClientId, string? ClientName, IReadOnlyList<string> RedirectUris, string? LogoUri);
public static class ClientMetadataDocument {
    (ClientMetadata? Doc, string? Error) ParseCimd(string fetchedUrl, string json);   // client_id phải == fetchedUrl; redirect_uris không rỗng, mọi URI phải hợp lệ; token_endpoint_auth_method none/thiếu
    (ClientMetadata? Doc, string? Error) ParseDcrRequest(string json);                // client công khai; redirect_uris trong danh sách trắng
}
```
- [ ] Test trước (RED): `Pkce_S256_dung_vector_RFC7636` (verifier `dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk` → challenge `E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM`), `Pkce_sai_verifier_bi_tu_choi`, `Redirect_Claude_va_ChatGPT_duoc_nhan`, `Redirect_la_bi_tu_choi` (khác path, có query, http ngoài loopback, chữ hoa host khác), `Loopback_bo_qua_cong`, `Resource_chuan_hoa`, `Metadata_ProtectedResource_dung_truong`, `Metadata_AuthServer_co_CIMD_va_iss`, `Cimd_client_id_phai_bang_URL`, `Cimd_redirect_ngoai_danh_sach_bi_tu_choi`, `Dcr_client_bi_mat_bi_tu_choi`.
- [ ] Cài đặt → GREEN; full suite; commit `feat(mcp-oauth): luật OAuth thuần — PKCE, redirect, metadata, CIMD/DCR`.

### Task 2: Lưu trữ client / mã / grant (SQL)

**Files:** `IMcpOAuthStore` + `McpOAuthRepository.cs` (Infrastructure/Mcp), `TourkitAiDb.cs` SchemaSql; Test `McpOAuthStoreTests.cs` (bản giả + guard SQL)

**Bảng (chỉ THÊM):**
- `dbo.McpOAuthClients(ClientId nvarchar(200) PK, ClientName nvarchar(200), RedirectUrisJson nvarchar(max), CreatedUtc datetime2, CreatedIp nvarchar(64))` — chỉ client DCR (CIMD không lưu, chỉ đệm).
- `dbo.McpOAuthCodes(CodeHash char(64) PK, ClientId, RedirectUri, CodeChallenge, Resource, Scope, TenantId, Username, SessionId, ExpiresUtc, UsedUtc NULL)`.
- `dbo.McpOAuthGrants(Id bigint identity PK, ClientId, ClientName, TenantId, Username, SessionId, Resource, Scope, AccessHash char(64) NULL UNIQUE (lọc NOT NULL), AccessExpiresUtc, RefreshHash char(64) NULL UNIQUE, RefreshExpiresUtc, PrevRefreshHash char(64) NULL, CreatedUtc, LastUsedUtc NULL, RevokedUtc NULL)` + chỉ mục `(TenantId, Username)`.

**Produces:** `CreateClientAsync`, `GetClientAsync`, `CountClientsByIpSinceAsync`, `SaveCodeAsync`, `ConsumeCodeAsync(hash)` (đánh dấu UsedUtc trong cùng câu `UPDATE … OUTPUT … WHERE UsedUtc IS NULL AND ExpiresUtc > now` → dùng một lần nguyên tử), `CreateGrantAsync`, `FindGrantByAccessAsync(hash)`, `RotateRefreshAsync(oldHash, newAccessHash, newRefreshHash, …)` (nguyên tử; nếu oldHash khớp `PrevRefreshHash` → trả "tái sử dụng" để chỗ gọi thu hồi), `RevokeGrantAsync(id, tenant, username)`, `ListGrantsAsync(tenant, username)`, `TouchGrantAsync`.
- [ ] Test (bản giả theo interface + guard: mọi câu thu hồi/liệt kê kẹp TenantId+Username; ConsumeCode nguyên tử có `UsedUtc IS NULL`) → cài → full suite → gọi thật trên cổng 5092 (tạo bảng) → commit.

### Task 3: Endpoint OAuth (well-known, register, token) + tải CIMD

**Files:** `ClientMetadataFetcher.cs`, `McpOAuthService.cs`, `McpOAuthEndpoints.cs`, `FeatureFlags.McpOAuth`; Test `McpOAuthServiceTests.cs`
- `GET /.well-known/oauth-protected-resource` và `/.well-known/oauth-protected-resource/mcp` → `OAuthMetadata.ProtectedResource`.
- `GET /.well-known/oauth-authorization-server` (và `/.well-known/openid-configuration` trả cùng nội dung) → `AuthorizationServer`.
- `POST /oauth/register` (JSON) → 201 `{client_id, client_name, redirect_uris, token_endpoint_auth_method:"none", grant_types, response_types}`; lỗi theo RFC 7591 (`invalid_redirect_uri`, `invalid_client_metadata`); giới hạn 20/giờ/IP.
- `POST /oauth/token` (`application/x-www-form-urlencoded`): `grant_type=authorization_code` (code, code_verifier, redirect_uri, client_id, resource) và `grant_type=refresh_token` (refresh_token, client_id, resource?) → `{access_token, token_type:"Bearer", expires_in:3600, refresh_token, scope}`; lỗi `invalid_grant` / `invalid_client` / `invalid_request` / `unsupported_grant_type` (HTTP 400, JSON RFC 6749 §5.2); `Cache-Control: no-store`. Refresh: xoay vòng; tái sử dụng refresh cũ → thu hồi grant + `invalid_grant`. Grant mà phiên TourKit đã hết → `invalid_grant`.
- Tải CIMD theo Global Constraints (dùng `IHttpClientFactory`, `AllowAutoRedirect=false`, đọc tối đa 64 KB).
- [ ] Test RED→GREEN cho các ca Review Focus 2 và 3 (dịch vụ dùng store giả + fetcher giả); full suite; gọi thật trên 5092: lấy metadata, DCR, đổi mã (dùng mã giả tạo qua store) → commit.

### Task 4: Trang đồng ý `/oauth/authorize`

**Files:** `wwwroot/pages/oauth-consent.jsx`, route trong `app.jsx` (ngoài khung menu, không cần menu), `bundle-entry.js`, `index.html`; endpoint `GET /api/v1/mcp/oauth/authorize-info`, `POST /api/v1/mcp/oauth/approve`, `POST /api/v1/mcp/oauth/deny` (phiên qua `X-Session-Id`)
- Máy chủ map `GET /oauth/authorize` trả về SPA (index) — trang đọc query (`response_type=code`, `client_id`, `redirect_uri`, `code_challenge`, `code_challenge_method=S256`, `state`, `scope`, `resource`).
- `authorize-info`: kiểm client (CIMD hoặc DCR), redirect_uri (khớp tài liệu client + danh sách trắng), PKCE có mặt, resource = resource của mình. Sai redirect/client → trang báo lỗi, **không** chuyển hướng. Sai thứ khác → trả `redirect` lỗi (`error=invalid_request&state=…&iss=…`).
- Chưa đăng nhập → hiện màn đăng nhập sẵn có của SPA rồi quay lại đúng URL (giữ query).
- Đồng ý: hiện tên ứng dụng (Claude / ChatGPT / tên client), tên công ty + tài khoản đang đăng nhập, quyền "Đọc số liệu kinh doanh TourKit theo quyền của bạn", nút **Cho phép** / **Từ chối**. Approve → máy chủ KIỂM LẠI toàn bộ tham số, tạo mã → trả URL `redirect_uri?code=…&state=…&iss=<issuer>` → `window.location.replace`.
- [ ] Test (dịch vụ: approve kiểm lại tham số; deny → `error=access_denied`); kiểm giao diện 1280/393 bằng Playwright trên 5092; commit.

### Task 5: `/mcp` nhận token OAuth + quản lý kết nối + tài liệu

**Files:** `McpAuthenticator.cs` (sửa), `McpAuthMiddleware.cs` (sửa), `mcp-connect.jsx` (sửa), endpoint `GET/DELETE /api/v1/mcp/oauth/connections`, docs, CHANGELOG
- Bearer `tkoat_…` (khi cờ McpOAuth bật) → `FindGrantByAccessAsync` → kiểm hạn, thu hồi, resource == resource của mình, phiên TourKit còn → `McpCaller`; đệm 60 s như khoá tay; thu hồi grant → xoá đệm.
- Mọi 401 của `/mcp` (khi cờ McpOAuth bật) kèm `WWW-Authenticate: Bearer resource_metadata="{base}/.well-known/oauth-protected-resource", scope="tourkit.read"`.
- Trang "Kết nối AI": thêm khối **Ứng dụng đã kết nối** (tên app, kết nối lúc, dùng lần cuối, nút Thu hồi hai bước) + hướng dẫn thêm connector cho Claude.ai (Customize → Connectors → Add custom connector → URL `{base}/mcp`) và ChatGPT (Settings → Plugins/Apps → Add custom MCP server → URL `{base}/mcp`, xác thực OAuth).
- docs/features/mcp.md (mục OAuth: luồng, bảng, cấu hình `Mcp:PublicBaseUrl`, danh sách trắng, cách bật trên prod, kiểm Cloudflare/IIS không nén `text/event-stream`), api-surface, CHANGELOG "🔜 Sắp có".
- [ ] Test RED→GREEN (token sai resource / hết hạn / thu hồi → 401 + header; khoá `tkmcp_` vẫn chạy); full suite; commit.

### Task 6 (controller): Kiểm đầu-cuối

- [ ] Luồng OAuth đầy đủ bằng **MCP Inspector** (DCR + PKCE, chạy với `http://localhost` — spec cho phép loopback) trên 5090 với phiên staging: đăng ký → trang đồng ý → token → `tools/list` → `tools/call` → refresh → thu hồi → 401.
- [ ] Script tự động các ca Review Focus 1–5 bằng HTTP thật.
- [ ] (Cần user) Claude.ai và ChatGPT thật: chỉ thử được khi `/mcp` public HTTPS cổng 443 — sau deploy lên `tourkit-ai.tourkit.vn` với cờ bật, hoặc mở tunnel tạm (`cloudflared`) tới máy dev và user tự thêm connector trong tài khoản của họ.

## Ước lượng
Task 1–2: 1 ngày · Task 3: 1 ngày · Task 4: 0,5–1 ngày · Task 5: 0,5 ngày · Task 6: 0,5 ngày → **~3,5–4 ngày**.
