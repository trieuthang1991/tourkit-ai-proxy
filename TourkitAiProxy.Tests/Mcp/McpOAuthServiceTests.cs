using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.Mcp;
using TourkitAiProxy.Services.Mcp;
using TourkitAiProxy.Services.Mcp.OAuth;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>
/// Máy chủ OAuth của MCP với kho giả + fetcher giả: kiểm tham số authorize (redirect không được lệch một
/// ký tự), đổi mã (một lần, đúng client, đúng PKCE, đúng redirect/resource, còn hạn, phiên còn sống),
/// xoay refresh (dùng lại refresh cũ = thu hồi cả grant) và đăng ký client động.
/// </summary>
public class McpOAuthServiceTests
{
    private const string Base = "https://tourkit-ai.example.vn";
    private const string Resource = Base + "/mcp";
    private const string ClaudeCb = "https://claude.ai/api/mcp/auth_callback";
    private const string ClaudeClient = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    private const string ChatGptClient = "https://chatgpt.com/oauth/client.json";
    private const string ChatGptCb = "https://chatgpt.com/connector_platform_oauth_redirect";
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private static readonly McpOAuthApprover Approver = new("sid-a", "t1", "a");

    private sealed class FakeFetcher : IClientMetadataFetcher
    {
        public readonly Dictionary<string, ClientMetadata> Docs = new(StringComparer.Ordinal);
        public int Fetches;
        public bool CanFetch(string? url) => url != null && ClientMetadataFetcherAllow(url);
        public Task<ClientMetadata?> FetchAsync(string url, CancellationToken ct)
        {
            Fetches++;
            return Task.FromResult(Docs.TryGetValue(url, out var d) ? d : null);
        }
        private static bool ClientMetadataFetcherAllow(string url)
            => url.StartsWith("https://claude.ai/", StringComparison.Ordinal) || url.StartsWith("https://chatgpt.com/", StringComparison.Ordinal);
    }

    private sealed class T
    {
        public FakeMcpOAuthStore Store = new();
        public FakeFetcher Fetcher = new();
        public HashSet<string> LiveSessions = new() { "sid-a" };
        public List<long> Evicted = new();
        public McpOAuthService Svc = null!;
    }

    private static T Make()
    {
        var t = new T();
        t.Fetcher.Docs[ClaudeClient] = new ClientMetadata(ClaudeClient, "Claude", new[] { ClaudeCb }, null);
        t.Fetcher.Docs[ChatGptClient] = new ClientMetadata(ChatGptClient, "ChatGPT", new[] { ChatGptCb }, null);
        t.Svc = new McpOAuthService(t.Store, t.Fetcher,
            sid => t.LiveSessions.Contains(sid) ? new McpSessionOwner("t1", "a") : null,
            new McpOAuthOptions(Base), NullLogger<McpOAuthService>.Instance, onGrantRevoked: id => t.Evicted.Add(id));
        return t;
    }

    private static Dictionary<string, StringValues> Q(params (string K, string V)[] kv)
        => kv.ToDictionary(x => x.K, x => new StringValues(x.V));

    private static Dictionary<string, StringValues> Authorize(string clientId = ClaudeClient, string redirect = ClaudeCb, string? state = "st-1",
        string? method = "S256", string? resource = Resource, string? scope = "tourkit.read offline_access", string? responseType = "code",
        string? challenge = Challenge)
    {
        var d = new Dictionary<string, StringValues> { ["client_id"] = clientId, ["redirect_uri"] = redirect };
        if (responseType != null) d["response_type"] = responseType;
        if (state != null) d["state"] = state;
        if (challenge != null) d["code_challenge"] = challenge;
        if (method != null) d["code_challenge_method"] = method;
        if (resource != null) d["resource"] = resource;
        if (scope != null) d["scope"] = scope;
        return d;
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var q = new Uri(url).Query.TrimStart('?');
        return q.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }

    private static async Task<string> IssueCode(T t, string clientId = ClaudeClient, string redirect = ClaudeCb)
    {
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(clientId, redirect), default);
        Assert.Equal(McpAuthorizeOutcome.Valid, v.Outcome);
        var r = await t.Svc.IssueCodeAsync(v.Request!, Approver, default);
        Assert.Null(r.Error);
        return ParseQuery(r.RedirectUrl!)["code"];
    }

    private static Dictionary<string, StringValues> CodeForm(string code, string clientId = ClaudeClient, string redirect = ClaudeCb,
        string verifier = Verifier, string? resource = Resource)
    {
        var d = Q(("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("redirect_uri", redirect), ("client_id", clientId));
        if (resource != null) d["resource"] = resource;
        return d;
    }

    private static string Err(McpOAuthResponse r) => r.Body["error"]!.GetValue<string>();

    private static async Task<JsonObject> Exchange(T t, string code)
    {
        var r = await t.Svc.TokenAsync(CodeForm(code), default);
        Assert.Equal(200, r.Status);
        return r.Body;
    }

    // ── Authorize: tham số ─────────────────────────────────────────────────

    [Fact]
    public async Task Authorize_hop_le_tra_yeu_cau_da_kiem()
    {
        var t = Make();
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(), default);
        Assert.Equal(McpAuthorizeOutcome.Valid, v.Outcome);
        var r = v.Request!;
        Assert.Equal(ClaudeClient, r.ClientId);
        Assert.Equal("Claude", r.ClientName);
        Assert.Equal(ClaudeCb, r.RedirectUri);
        Assert.Equal("st-1", r.State);
        Assert.Equal(Challenge, r.CodeChallenge);
        Assert.Equal(Resource, r.Resource);
        Assert.Equal("tourkit.read offline_access", r.Scope);
    }

    [Fact]
    public async Task Authorize_khong_gui_resource_scope_thi_dung_mac_dinh()
    {
        var t = Make();
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(resource: null, scope: null), default);
        Assert.Equal(McpAuthorizeOutcome.Valid, v.Outcome);
        Assert.Equal(Resource, v.Request!.Resource);
        Assert.Equal("tourkit.read", v.Request.Scope);
    }

    [Theory]
    [InlineData("https://TOURKIT-AI.example.vn/mcp")]
    [InlineData("https://tourkit-ai.example.vn/mcp/")]
    public async Task Authorize_resource_chuan_hoa_van_khop(string resource)
    {
        var v = await Make().Svc.ValidateAuthorizeRequestAsync(Authorize(resource: resource), default);
        Assert.Equal(McpAuthorizeOutcome.Valid, v.Outcome);
    }

    [Theory]
    [InlineData(ClaudeCb + "/x")]
    [InlineData("https://CLAUDE.ai/api/mcp/auth_callback")]
    [InlineData(ClaudeCb + "?a=1")]
    [InlineData(ChatGptCb)]                 // nằm trong danh sách trắng nhưng KHÔNG phải redirect của client Claude
    [InlineData("https://evil.com/cb")]
    [InlineData("")]
    public async Task Authorize_redirect_lech_thi_hien_loi_khong_redirect(string redirect)
    {
        var v = await Make().Svc.ValidateAuthorizeRequestAsync(Authorize(redirect: redirect), default);
        Assert.Equal(McpAuthorizeOutcome.ShowError, v.Outcome);
        Assert.Null(v.RedirectUrl);
        Assert.Null(v.Request);
        Assert.False(string.IsNullOrWhiteSpace(v.Error));
    }

    [Fact]
    public async Task Authorize_client_sai_thi_hien_loi_khong_redirect()
    {
        var t = Make();
        foreach (var cid in new[] { "", "tkdcr_khongco", "https://evil.com/client.json", "http://claude.ai/x", "https://claude.ai/khong-co-tai-lieu" })
        {
            var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(clientId: cid), default);
            Assert.Equal(McpAuthorizeOutcome.ShowError, v.Outcome);
            Assert.Null(v.RedirectUrl);
        }
        // Host ngoài danh sách trắng: không bao giờ tới bước tải.
        Assert.Equal(1, t.Fetcher.Fetches);
    }

    [Fact]
    public async Task Authorize_thieu_client_id_hoac_trung_tham_so_thi_hien_loi()
    {
        var t = Make();
        var q = Authorize(); q.Remove("client_id");
        Assert.Equal(McpAuthorizeOutcome.ShowError, (await t.Svc.ValidateAuthorizeRequestAsync(q, default)).Outcome);
        var dup = Authorize(); dup["redirect_uri"] = new StringValues(new[] { ClaudeCb, "https://evil.com/cb" });
        Assert.Equal(McpAuthorizeOutcome.ShowError, (await t.Svc.ValidateAuthorizeRequestAsync(dup, default)).Outcome);
    }

    [Theory]
    [InlineData("plain", null, "invalid_request")]
    [InlineData(null, null, "invalid_request")]                 // thiếu method = plain theo RFC 7636 → từ chối
    [InlineData("S256", "", "invalid_request")]                  // thiếu challenge
    [InlineData("S256", "ngan-qua", "invalid_request")]
    public async Task Authorize_PKCE_khong_phai_S256_thi_redirect_loi(string? method, string? challenge, string error)
    {
        var v = await Make().Svc.ValidateAuthorizeRequestAsync(Authorize(method: method, challenge: challenge == "" ? null : challenge ?? Challenge), default);
        AssertRedirectError(v, error);
    }

    [Fact]
    public async Task Authorize_response_type_resource_scope_sai_thi_redirect_loi()
    {
        var t = Make();
        AssertRedirectError(await t.Svc.ValidateAuthorizeRequestAsync(Authorize(responseType: "token"), default), "unsupported_response_type");
        AssertRedirectError(await t.Svc.ValidateAuthorizeRequestAsync(Authorize(responseType: null), default), "invalid_request");
        AssertRedirectError(await t.Svc.ValidateAuthorizeRequestAsync(Authorize(resource: "https://evil.com/mcp"), default), "invalid_target");
        AssertRedirectError(await t.Svc.ValidateAuthorizeRequestAsync(Authorize(resource: Base + "/khac"), default), "invalid_target");
        AssertRedirectError(await t.Svc.ValidateAuthorizeRequestAsync(Authorize(scope: "openid tourkit.read"), default), "invalid_scope");
        var dup = Authorize(); dup["scope"] = new StringValues(new[] { "tourkit.read", "offline_access" });
        AssertRedirectError(await t.Svc.ValidateAuthorizeRequestAsync(dup, default), "invalid_request");
    }

    private static void AssertRedirectError(McpAuthorizeValidation v, string error)
    {
        Assert.Equal(McpAuthorizeOutcome.RedirectError, v.Outcome);
        Assert.StartsWith(ClaudeCb + "?", v.RedirectUrl);
        var q = ParseQuery(v.RedirectUrl!);
        Assert.Equal(error, q["error"]);
        Assert.Equal("st-1", q["state"]);
        Assert.Equal(Base, q["iss"]);
        Assert.False(q.ContainsKey("code"));
    }

    // ── Cấp mã / từ chối ──────────────────────────────────────────────────

    [Fact]
    public async Task Cap_ma_tra_redirect_co_code_state_iss_va_luu_rang_buoc()
    {
        var t = Make();
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(), default);
        var r = await t.Svc.IssueCodeAsync(v.Request!, Approver, default);
        Assert.Null(r.Error);
        Assert.StartsWith(ClaudeCb + "?", r.RedirectUrl);
        var q = ParseQuery(r.RedirectUrl!);
        Assert.StartsWith("tkoac_", q["code"]);
        Assert.Equal("st-1", q["state"]);
        Assert.Equal(Base, q["iss"]);

        var saved = await t.Store.ConsumeCodeAsync(OAuthRules.Hash(q["code"]), default);
        Assert.NotNull(saved);
        Assert.Equal(ClaudeClient, saved!.ClientId);
        Assert.Equal(ClaudeCb, saved.RedirectUri);
        Assert.Equal(Challenge, saved.CodeChallenge);
        Assert.Equal(Resource, saved.Resource);
        Assert.Equal("tourkit.read offline_access", saved.Scope);
        Assert.Equal(("t1", "a", "sid-a"), (saved.TenantId, saved.Username, saved.SessionId));
        Assert.InRange(saved.ExpiresUtc, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(5.1));
    }

    [Fact]
    public async Task Cap_ma_kiem_lai_tham_so_khong_tin_du_lieu_buoc_truoc()
    {
        var t = Make();
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(), default);
        // Kẻ xấu sửa redirect sau bước hiện trang: phải bị kiểm lại và từ chối.
        var tampered = v.Request! with { RedirectUri = "https://evil.com/cb" };
        var r = await t.Svc.IssueCodeAsync(tampered, Approver, default);
        Assert.Null(r.RedirectUrl);
        Assert.NotNull(r.Error);
        var plain = v.Request! with { CodeChallenge = "x" };
        Assert.Null((await t.Svc.IssueCodeAsync(plain, Approver, default)).RedirectUrl);
    }

    [Fact]
    public async Task Cap_ma_khi_phien_da_het_thi_tu_choi()
    {
        var t = Make();
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(), default);
        t.LiveSessions.Clear();
        var r = await t.Svc.IssueCodeAsync(v.Request!, Approver, default);
        Assert.Null(r.RedirectUrl);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Tu_choi_tra_access_denied_kem_state_iss()
    {
        var t = Make();
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(), default);
        var r = await t.Svc.DenyAsync(v.Request!, default);
        Assert.Null(r.Error);
        var url = r.RedirectUrl!;
        var q = ParseQuery(url);
        Assert.StartsWith(ClaudeCb + "?", url);
        Assert.Equal("access_denied", q["error"]);
        Assert.Equal("st-1", q["state"]);
        Assert.Equal(Base, q["iss"]);
    }

    [Fact]
    public async Task Tu_choi_kiem_lai_redirect_khong_thanh_cau_chuyen_huong()
    {
        // Task 4 dựng yêu cầu từ POST của trình duyệt — redirect bị sửa thì không được sinh URL chuyển hướng.
        var t = Make();
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(), default);
        foreach (var forged in new[]
        {
            v.Request! with { RedirectUri = "https://evil.com/cb" },
            v.Request! with { RedirectUri = ChatGptCb },                          // hợp lệ nhưng không phải của client này
            v.Request! with { ClientId = "https://evil.com/c.json", RedirectUri = "https://evil.com/cb" },
            v.Request! with { ClientId = "tkdcr_khongco" },
        })
        {
            var r = await t.Svc.DenyAsync(forged, default);
            Assert.Null(r.RedirectUrl);
            Assert.False(string.IsNullOrWhiteSpace(r.Error));
        }
    }

    // ── Token: authorization_code ─────────────────────────────────────────

    [Fact]
    public async Task Doi_ma_dung_tra_token_va_tao_grant()
    {
        var t = Make();
        var body = await Exchange(t, await IssueCode(t));
        Assert.StartsWith("tkoat_", body["access_token"]!.GetValue<string>());
        Assert.StartsWith("tkort_", body["refresh_token"]!.GetValue<string>());
        Assert.Equal("Bearer", body["token_type"]!.GetValue<string>());
        Assert.Equal(3600, body["expires_in"]!.GetValue<int>());
        Assert.Equal("tourkit.read offline_access", body["scope"]!.GetValue<string>());

        var g = await t.Store.FindGrantByAccessAsync(OAuthRules.Hash(body["access_token"]!.GetValue<string>()), default);
        Assert.NotNull(g);
        Assert.Equal((ClaudeClient, "Claude", "t1", "a", "sid-a", Resource), (g!.ClientId, g.ClientName, g.TenantId, g.Username, g.SessionId, g.Resource));
        Assert.InRange(g.AccessExpiresUtc, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
        Assert.InRange(g.RefreshExpiresUtc, DateTime.UtcNow.AddDays(29.9), DateTime.UtcNow.AddDays(30.1));
    }

    [Fact]
    public async Task Doi_ma_khong_gui_resource_van_duoc()
    {
        var t = Make();
        var r = await t.Svc.TokenAsync(CodeForm(await IssueCode(t), resource: null), default);
        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task Ma_dung_hai_lan_thi_invalid_grant()
    {
        var t = Make();
        var code = await IssueCode(t);
        await Exchange(t, code);
        var again = await t.Svc.TokenAsync(CodeForm(code), default);
        Assert.Equal(400, again.Status);
        Assert.Equal("invalid_grant", Err(again));
    }

    [Fact]
    public async Task Ma_cua_client_A_doi_o_client_B_thi_invalid_grant()
    {
        var t = Make();
        var code = await IssueCode(t);   // cấp cho Claude
        var r = await t.Svc.TokenAsync(CodeForm(code, clientId: ChatGptClient, redirect: ChatGptCb), default);
        Assert.Equal("invalid_grant", Err(r));
    }

    [Fact]
    public async Task Sai_verifier_thi_invalid_grant_va_ma_bi_dot()
    {
        var t = Make();
        var code = await IssueCode(t);
        var wrong = await t.Svc.TokenAsync(CodeForm(code, verifier: new string('a', 43)), default);
        Assert.Equal(400, wrong.Status);
        Assert.Equal("invalid_grant", Err(wrong));
        // Đoán sai một lần là mất mã — không cho dò verifier.
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(CodeForm(code), default)));
    }

    [Fact]
    public async Task Sai_redirect_hoac_resource_thi_invalid_grant()
    {
        var t = Make();
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(CodeForm(await IssueCode(t), redirect: ClaudeCb + "/x"), default)));
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(CodeForm(await IssueCode(t), resource: "https://evil.com/mcp"), default)));
        Assert.Equal(200, (await t.Svc.TokenAsync(CodeForm(await IssueCode(t), resource: "https://TOURKIT-AI.example.vn/mcp/"), default)).Status);
    }

    [Fact]
    public async Task Ma_qua_5_phut_thi_invalid_grant()
    {
        var t = Make();
        const string code = "tkoac_0123456789abcdef0123456789abcdef";
        await t.Store.SaveCodeAsync(new McpOAuthCode(OAuthRules.Hash(code), ClaudeClient, ClaudeCb, Challenge, Resource, "tourkit.read",
            "t1", "a", "sid-a", DateTime.UtcNow.AddSeconds(-1), null), default);
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(CodeForm(code), default)));
    }

    [Fact]
    public async Task Phien_TourKit_het_luc_doi_ma_thi_invalid_grant()
    {
        var t = Make();
        var code = await IssueCode(t);
        t.LiveSessions.Clear();
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(CodeForm(code), default)));
    }

    [Fact]
    public async Task Client_id_la_thi_invalid_client()
    {
        var t = Make();
        var code = await IssueCode(t);
        Assert.Equal("invalid_client", Err(await t.Svc.TokenAsync(CodeForm(code, clientId: "tkdcr_khongco"), default)));
        Assert.Equal("invalid_client", Err(await t.Svc.TokenAsync(CodeForm(code, clientId: "https://evil.com/c.json"), default)));
        // Client lạ không được đốt mã của người khác.
        Assert.Equal(200, (await t.Svc.TokenAsync(CodeForm(code), default)).Status);
    }

    // ── Token: client_assertion (ChatGPT khai private_key_jwt) ────────────
    // Ta coi mọi client là CÔNG KHAI: assertion KHÔNG BAO GIỜ là xác thực, không thay PKCE. Chỉ khi thiếu
    // client_id thì đọc "iss" (không kiểm chữ ký) để biết tra client nào — mã vẫn gắn client + PKCE.

    private const string JwtBearerType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private static string B64Url(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// JWT có chữ ký rác — máy chủ không kiểm chữ ký nên vẫn đọc được iss.
    private static string Assertion(string payloadJson)
        => B64Url("""{"alg":"RS256","typ":"JWT"}""") + "." + B64Url(payloadJson) + ".chu-ky-rac";

    private static Dictionary<string, StringValues> WithAssertion(Dictionary<string, StringValues> form, string assertion, bool dropClientId)
    {
        if (dropClientId) form.Remove("client_id");
        form["client_assertion_type"] = JwtBearerType;
        form["client_assertion"] = assertion;
        return form;
    }

    [Fact]
    public async Task Doi_ma_co_assertion_va_client_id_van_chay_va_khong_doc_assertion()
    {
        var t = Make();
        var code = await IssueCode(t, ChatGptClient, ChatGptCb);
        // Assertion rác vẫn chạy: có client_id thì assertion bị bỏ qua hoàn toàn.
        var r = await t.Svc.TokenAsync(WithAssertion(CodeForm(code, clientId: ChatGptClient, redirect: ChatGptCb), "rac", dropClientId: false), default);
        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task Assertion_khong_thay_duoc_PKCE()
    {
        var t = Make();
        var code = await IssueCode(t, ChatGptClient, ChatGptCb);
        var form = WithAssertion(CodeForm(code, clientId: ChatGptClient, redirect: ChatGptCb, verifier: new string('a', 43)),
            Assertion($$"""{"iss":"{{ChatGptClient}}","sub":"{{ChatGptClient}}"}"""), dropClientId: false);
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(form, default)));
        var noVerifier = WithAssertion(CodeForm(await IssueCode(t, ChatGptClient, ChatGptCb), clientId: ChatGptClient, redirect: ChatGptCb),
            Assertion($$"""{"iss":"{{ChatGptClient}}"}"""), dropClientId: true);
        noVerifier.Remove("code_verifier");
        Assert.Equal("invalid_request", Err(await t.Svc.TokenAsync(noVerifier, default)));
    }

    [Fact]
    public async Task Doi_ma_thieu_client_id_co_assertion_thi_dung_iss_de_tra_client()
    {
        var t = Make();
        var code = await IssueCode(t, ChatGptClient, ChatGptCb);
        var form = WithAssertion(CodeForm(code, clientId: ChatGptClient, redirect: ChatGptCb),
            Assertion($$"""{"iss":"{{ChatGptClient}}","sub":"{{ChatGptClient}}","aud":"{{Base}}/oauth/token","exp":1}"""), dropClientId: true);
        var r = await t.Svc.TokenAsync(form, default);
        Assert.Equal(200, r.Status);
        var g = await t.Store.FindGrantByAccessAsync(OAuthRules.Hash(r.Body["access_token"]!.GetValue<string>()), default);
        Assert.Equal(ChatGptClient, g!.ClientId);
    }

    [Fact]
    public async Task Refresh_thieu_client_id_co_assertion_thi_dung_iss()
    {
        var t = Make();
        var first = await t.Svc.TokenAsync(CodeForm(await IssueCode(t, ChatGptClient, ChatGptCb), clientId: ChatGptClient, redirect: ChatGptCb), default);
        var form = WithAssertion(RefreshForm(first.Body["refresh_token"]!.GetValue<string>(), clientId: ChatGptClient),
            Assertion($$"""{"iss":"{{ChatGptClient}}"}"""), dropClientId: true);
        Assert.Equal(200, (await t.Svc.TokenAsync(form, default)).Status);
    }

    [Theory]
    [InlineData("rac")]
    [InlineData("a.b")]
    [InlineData("a.!!!.c")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("e30.bm90LWpzb24.c")]                     // payload "not-json"
    [InlineData("e30.e30.c")]                             // payload {} — thiếu iss
    [InlineData("e30.eyJpc3MiOjEyM30.c")]                 // iss là số
    [InlineData("e30.WzFd.c")]                            // payload là mảng
    [InlineData("e30.eyJpc3MiOiIifQ.c")]                  // iss rỗng
    public async Task Assertion_rac_va_thieu_client_id_thi_invalid_request(string assertion)
    {
        var t = Make();
        var code = await IssueCode(t, ChatGptClient, ChatGptCb);
        var r = await t.Svc.TokenAsync(WithAssertion(CodeForm(code, clientId: ChatGptClient, redirect: ChatGptCb), assertion, dropClientId: true), default);
        Assert.Equal(400, r.Status);
        Assert.Equal("invalid_request", Err(r));
        // Mã chưa bị đốt: client hợp lệ vẫn đổi được.
        Assert.Equal(200, (await t.Svc.TokenAsync(CodeForm(code, clientId: ChatGptClient, redirect: ChatGptCb), default)).Status);
    }

    [Fact]
    public async Task Assertion_qua_dai_thi_invalid_request()
    {
        var t = Make();
        var code = await IssueCode(t, ChatGptClient, ChatGptCb);
        var huge = Assertion($$"""{"iss":"{{ChatGptClient}}","pad":"{{new string('x', 20_000)}}"}""");
        var r = await t.Svc.TokenAsync(WithAssertion(CodeForm(code, clientId: ChatGptClient, redirect: ChatGptCb), huge, dropClientId: true), default);
        Assert.Equal("invalid_request", Err(r));
    }

    [Fact]
    public async Task Assertion_iss_khac_client_cua_ma_thi_invalid_grant()
    {
        var t = Make();
        var code = await IssueCode(t, ChatGptClient, ChatGptCb);   // mã cấp cho ChatGPT
        var form = WithAssertion(CodeForm(code, clientId: ChatGptClient, redirect: ChatGptCb),
            Assertion($$"""{"iss":"{{ClaudeClient}}"}"""), dropClientId: true);
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(form, default)));
    }

    [Fact]
    public async Task Tham_so_token_sai_tra_dung_ma_loi()
    {
        var t = Make();
        Assert.Equal("invalid_request", Err(await t.Svc.TokenAsync(Q(("code", "x")), default)));
        Assert.Equal("unsupported_grant_type", Err(await t.Svc.TokenAsync(Q(("grant_type", "password")), default)));
        Assert.Equal("unsupported_grant_type", Err(await t.Svc.TokenAsync(Q(("grant_type", "client_credentials")), default)));
        var missing = CodeForm(await IssueCode(t)); missing.Remove("code_verifier");
        Assert.Equal("invalid_request", Err(await t.Svc.TokenAsync(missing, default)));
        var dup = CodeForm("tkoac_x"); dup["code"] = new StringValues(new[] { "a", "b" });
        var r = await t.Svc.TokenAsync(dup, default);
        Assert.Equal(400, r.Status);
        Assert.Equal("invalid_request", Err(r));
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(CodeForm("rac"), default)));
    }

    [Fact]
    public async Task Mo_ta_loi_chi_gom_ky_tu_ASCII_theo_RFC6749()
    {
        var t = Make();
        var r = await t.Svc.TokenAsync(Q(("grant_type", "password")), default);
        var desc = r.Body["error_description"]?.GetValue<string>() ?? "";
        Assert.All(desc, c => Assert.True(c is >= ' ' and <= '~' && c != '"' && c != '\\'));
    }

    // ── Token: refresh_token ──────────────────────────────────────────────

    private static Dictionary<string, StringValues> RefreshForm(string refresh, string clientId = ClaudeClient, string? scope = null)
    {
        var d = Q(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId));
        if (scope != null) d["scope"] = scope;
        return d;
    }

    [Fact]
    public async Task Refresh_xoay_vong_va_dung_lai_refresh_cu_thi_thu_hoi_ca_grant()
    {
        var t = Make();
        var first = await Exchange(t, await IssueCode(t));
        var r1 = first["refresh_token"]!.GetValue<string>();

        var rot = await t.Svc.TokenAsync(RefreshForm(r1), default);
        Assert.Equal(200, rot.Status);
        var r2 = rot.Body["refresh_token"]!.GetValue<string>();
        var a2 = rot.Body["access_token"]!.GetValue<string>();
        Assert.NotEqual(r1, r2);
        Assert.NotEqual(first["access_token"]!.GetValue<string>(), a2);
        Assert.Equal(3600, rot.Body["expires_in"]!.GetValue<int>());

        // Dùng lại refresh cũ = có người khác đang giữ nó → thu hồi CẢ grant.
        var reuse = await t.Svc.TokenAsync(RefreshForm(r1), default);
        Assert.Equal("invalid_grant", Err(reuse));
        Assert.NotNull((await t.Store.ListGrantsAsync("t1", "a", default)).Single().RevokedUtc);
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(RefreshForm(r2), default)));
        Assert.Null(await t.Store.FindGrantByAccessAsync(OAuthRules.Hash(a2), default));
    }

    // ── Thu hồi phía dịch vụ phải báo cổng /mcp xoá đệm NGAY (C7b) ──────

    private static async Task<long> OnlyGrantId(T t) => (await t.Store.ListGrantsAsync("t1", "a", default)).Single().Id;

    [Fact]
    public async Task Dung_lai_refresh_cu_thu_hoi_va_bao_xoa_dem()
    {
        var t = Make();
        var r1 = (await Exchange(t, await IssueCode(t)))["refresh_token"]!.GetValue<string>();
        Assert.Equal(200, (await t.Svc.TokenAsync(RefreshForm(r1), default)).Status);
        Assert.Empty(t.Evicted);                                         // xoay bình thường không thu hồi gì
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(RefreshForm(r1), default)));
        Assert.Equal(new[] { await OnlyGrantId(t) }, t.Evicted);
    }

    [Fact]
    public async Task Refresh_sai_client_thu_hoi_va_bao_xoa_dem()
    {
        var t = Make();
        var first = await Exchange(t, await IssueCode(t));
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(RefreshForm(first["refresh_token"]!.GetValue<string>(), clientId: ChatGptClient), default)));
        Assert.Equal(new[] { await OnlyGrantId(t) }, t.Evicted);
    }

    [Fact]
    public async Task Refresh_khi_phien_het_thu_hoi_va_bao_xoa_dem()
    {
        var t = Make();
        var first = await Exchange(t, await IssueCode(t));
        t.LiveSessions.Clear();
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(RefreshForm(first["refresh_token"]!.GetValue<string>()), default)));
        Assert.Equal(new[] { await OnlyGrantId(t) }, t.Evicted);
    }

    [Fact]
    public async Task Refresh_sai_client_thi_invalid_grant_va_thu_hoi()
    {
        var t = Make();
        var first = await Exchange(t, await IssueCode(t));
        var r = await t.Svc.TokenAsync(RefreshForm(first["refresh_token"]!.GetValue<string>(), clientId: ChatGptClient), default);
        Assert.Equal("invalid_grant", Err(r));
        Assert.NotNull((await t.Store.ListGrantsAsync("t1", "a", default)).Single().RevokedUtc);
    }

    [Fact]
    public async Task Refresh_khi_phien_TourKit_het_thi_invalid_grant_va_thu_hoi()
    {
        var t = Make();
        var first = await Exchange(t, await IssueCode(t));
        t.LiveSessions.Clear();
        var r = await t.Svc.TokenAsync(RefreshForm(first["refresh_token"]!.GetValue<string>()), default);
        Assert.Equal("invalid_grant", Err(r));
        Assert.NotNull((await t.Store.ListGrantsAsync("t1", "a", default)).Single().RevokedUtc);
    }

    [Fact]
    public async Task Refresh_scope_chi_duoc_thu_hep()
    {
        var t = Make();
        var first = await Exchange(t, await IssueCode(t));
        var narrow = await t.Svc.TokenAsync(RefreshForm(first["refresh_token"]!.GetValue<string>(), scope: "tourkit.read"), default);
        Assert.Equal(200, narrow.Status);
        Assert.Equal("tourkit.read", narrow.Body["scope"]!.GetValue<string>());
        var wide = await t.Svc.TokenAsync(RefreshForm(narrow.Body["refresh_token"]!.GetValue<string>(), scope: "openid"), default);
        Assert.Equal("invalid_scope", Err(wide));
    }

    [Fact]
    public async Task Refresh_xin_scope_rong_hon_grant_van_tra_cap_moi_voi_scope_da_cap()
    {
        // Grant chỉ có tourkit.read; refresh xin thêm offline_access. Đã xoay rồi thì PHẢI trả cặp mới
        // (scope thực = scope đã cấp) — vứt cặp mới đi là lần refresh sau của client bị coi là dùng lại → thu hồi oan.
        var t = Make();
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(scope: "tourkit.read"), default);
        var code = ParseQuery((await t.Svc.IssueCodeAsync(v.Request!, Approver, default)).RedirectUrl!)["code"];
        var first = await Exchange(t, code);
        Assert.Equal("tourkit.read", first["scope"]!.GetValue<string>());

        var wide = await t.Svc.TokenAsync(RefreshForm(first["refresh_token"]!.GetValue<string>(), scope: "tourkit.read offline_access"), default);
        Assert.Equal(200, wide.Status);
        Assert.Equal("tourkit.read", wide.Body["scope"]!.GetValue<string>());

        var next = await t.Svc.TokenAsync(RefreshForm(wide.Body["refresh_token"]!.GetValue<string>()), default);
        Assert.Equal(200, next.Status);
        Assert.Null((await t.Store.ListGrantsAsync("t1", "a", default)).Single().RevokedUtc);
    }

    [Fact]
    public async Task Refresh_rac_hoac_thieu_thi_loi()
    {
        var t = Make();
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(RefreshForm("tkort_" + new string('0', 32)), default)));
        Assert.Equal("invalid_grant", Err(await t.Svc.TokenAsync(RefreshForm("rac"), default)));
        Assert.Equal("invalid_request", Err(await t.Svc.TokenAsync(Q(("grant_type", "refresh_token"), ("client_id", ClaudeClient)), default)));
    }

    // ── Đăng ký client động (DCR) ─────────────────────────────────────────

    [Fact]
    public async Task Dang_ky_loopback_tra_201_va_dung_duoc_de_xin_ma()
    {
        var t = Make();
        var r = await t.Svc.RegisterAsync("""{"client_name":"MCP Inspector","redirect_uris":["http://localhost:6274/oauth/callback"]}""", "1.2.3.4", default);
        Assert.Equal(201, r.Status);
        var cid = r.Body["client_id"]!.GetValue<string>();
        Assert.StartsWith("tkdcr_", cid);
        Assert.Equal("none", r.Body["token_endpoint_auth_method"]!.GetValue<string>());
        Assert.Equal("MCP Inspector", r.Body["client_name"]!.GetValue<string>());
        Assert.Equal("http://localhost:6274/oauth/callback", r.Body["redirect_uris"]![0]!.GetValue<string>());
        Assert.Contains("authorization_code", r.Body["grant_types"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("code", r.Body["response_types"]![0]!.GetValue<string>());
        Assert.Null(r.Body["client_secret"]);

        // Loopback được đổi cổng (app máy tính chọn cổng ngẫu nhiên).
        var v = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(clientId: cid, redirect: "http://localhost:51000/oauth/callback"), default);
        Assert.Equal(McpAuthorizeOutcome.Valid, v.Outcome);
        Assert.Equal("MCP Inspector", v.Request!.ClientName);
        var code = ParseQuery((await t.Svc.IssueCodeAsync(v.Request, Approver, default)).RedirectUrl!)["code"];
        var tok = await t.Svc.TokenAsync(CodeForm(code, clientId: cid, redirect: "http://localhost:51000/oauth/callback"), default);
        Assert.Equal(200, tok.Status);
        // Đường dẫn khác thì không được.
        var bad = await t.Svc.ValidateAuthorizeRequestAsync(Authorize(clientId: cid, redirect: "http://localhost:6274/khac"), default);
        Assert.Equal(McpAuthorizeOutcome.ShowError, bad.Outcome);
    }

    [Theory]
    [InlineData("""{"redirect_uris":["https://evil.com/cb"]}""", "invalid_redirect_uri")]
    [InlineData("""{"redirect_uris":[]}""", "invalid_redirect_uri")]
    [InlineData("""{"redirect_uris":["http://localhost:1/cb"],"token_endpoint_auth_method":"client_secret_basic"}""", "invalid_client_metadata")]
    [InlineData("""{"redirect_uris":["http://localhost:1/cb"],"token_endpoint_auth_method":"private_key_jwt","token_endpoint_auth_methods_supported":["none","private_key_jwt"]}""", "invalid_client_metadata")]
    [InlineData("""{"redirect_uris":["http://localhost:1/cb"],"grant_types":["implicit"]}""", "invalid_client_metadata")]
    [InlineData("""{"redirect_uris":["http://localhost:1/cb"],"response_types":["token"]}""", "invalid_client_metadata")]
    [InlineData("khong phai json", "invalid_client_metadata")]
    [InlineData("[]", "invalid_client_metadata")]
    public async Task Dang_ky_sai_tra_400_dung_ma_RFC7591(string json, string error)
    {
        var t = Make();
        var r = await t.Svc.RegisterAsync(json, "1.2.3.4", default);
        Assert.Equal(400, r.Status);
        Assert.Equal(error, Err(r));
        Assert.Equal(0, await t.Store.CountClientsByIpSinceAsync("1.2.3.4", DateTime.UtcNow.AddHours(-1), default));
    }

    [Fact]
    public async Task Dang_ky_qua_20_lan_mot_gio_tu_mot_IP_bi_chan()
    {
        var t = Make();
        const string json = """{"redirect_uris":["http://127.0.0.1:9/cb"]}""";
        for (var i = 0; i < 20; i++) Assert.Equal(201, (await t.Svc.RegisterAsync(json, "9.9.9.9", default)).Status);
        var over = await t.Svc.RegisterAsync(json, "9.9.9.9", default);
        Assert.Equal(429, over.Status);
        Assert.Equal(201, (await t.Svc.RegisterAsync(json, "8.8.8.8", default)).Status);
    }

    [Fact]
    public async Task Dang_ky_ten_dai_bi_cat_khong_ten_thi_mac_dinh()
    {
        var t = Make();
        var longName = new string('x', 500);
        var r = await t.Svc.RegisterAsync("{\"client_name\":\"" + longName + "\",\"redirect_uris\":[\"http://127.0.0.1:9/cb\"]}", "1.1.1.1", default);
        Assert.Equal(201, r.Status);
        Assert.True(r.Body["client_name"]!.GetValue<string>().Length <= 100);
        var noName = await t.Svc.RegisterAsync("""{"redirect_uris":["http://127.0.0.1:9/cb"]}""", "1.1.1.1", default);
        Assert.False(string.IsNullOrWhiteSpace(noName.Body["client_name"]!.GetValue<string>()));
    }
}
