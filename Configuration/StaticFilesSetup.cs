using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace TourkitAiProxy.Configuration;

public static class StaticFilesSetup
{
    // BUILD_VERSION: hash của mtime aggregate wwwroot — đổi 1 file là đổi hash.
    // Tính 1 lần lúc startup; deploy mới = process restart = hash mới.
    private static string _buildVersion = "dev";

    public static WebApplication UseTourkitStaticFiles(this WebApplication app)
    {
        var webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        _buildVersion = ComputeBuildVersion(webRoot);
        app.Logger.LogInformation("Frontend BUILD_VERSION: {V} (wwwroot: {Path})", _buildVersion, webRoot);
        _bundledPlainJsRegex = LoadBundledPlainJsRegex(webRoot, app.Logger);

        // DEV: recompute hash MỖI request /index.html — sửa .jsx + F5 là ?v đổi → browser
        // bypass cache immutable ngay, không cần restart server. Prod: hash tính 1 lần lúc
        // startup (deploy mới = restart = hash mới), không tốn mtime-scan mỗi request.
        var isDev = app.Environment.IsDevelopment();

        // Intercept root + index.html → server inject ?v=hash vào local <script src> + <link href>
        // Trình duyệt sẽ thấy URL khác mỗi lần deploy → invalidate cache cũ tự nhiên.
        // index.html BẮT BUỘC no-cache: nếu browser heuristic-cache html (kèm ?v cũ) thì
        // toàn bộ cơ chế versioned-cache vô hiệu — assets immutable cũ được dùng mãi.
        app.MapGet("/", (HttpContext ctx) => ServeIndex(ctx, webRoot, isDev));
        app.MapGet("/index.html", (HttpContext ctx) => ServeIndex(ctx, webRoot, isDev));

        // robots.txt + sitemap.xml — phải đăng ký TRƯỚC MapFallback, không thì fallback nuốt và trả
        // index.html kèm status 200 (đúng cái bẫy đã ghi trong Program.cs cho /api/**).
        app.MapSeoEndpoints();

        // SPA deep-link fallback (/customers, /deals, /assistant…) PHẢI cũng qua ServeIndex để
        // nhận bundle-injection + ?v=hash. TRƯỚC ĐÂY Program.cs dùng MapFallbackToFile("index.html")
        // serve file THÔ → deep-link + Ctrl+F5 rớt về DEV-babel mode (44 <script type=text/babel>
        // + Babel CDN, cold start 3-5s) NGAY CẢ KHI đã có prod bundle. Chỉ "/" mới nhanh.
        // MapFallback luôn ưu tiên thấp nhất (order=int.MaxValue) → API + static file vẫn match trước.
        //
        // Đường dẫn LẠ trả 404 THẬT (kèm chính trang này để người dùng còn thấy giao diện). Trước đây
        // mọi đường đều 200 — kể cả /khong-ton-tai-abcxyz — nên máy tìm kiếm index cả URL rác và coi
        // đó là "soft 404". Danh sách đường hợp lệ ở SeoSetup.Routes; bộ kiểm seo-prerender.check.js
        // đối chiếu với các <Route path> trong app.jsx để không lệch.
        app.MapFallback((HttpContext ctx) =>
        {
            var known = SeoSetup.IsKnownRoute(ctx.Request.Path.Value ?? "/");
            if (!known) ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return ServeIndex(ctx, webRoot, isDev, known);
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            // .jsx / .babel không có MIME chuẩn → fallback text/plain để browser load
            ServeUnknownFileTypes = true,
            DefaultContentType    = "text/plain",
            OnPrepareResponse     = ctx =>
            {
                var p = ctx.File.Name;
                var isAsset = p.EndsWith(".jsx") || p.EndsWith(".js") || p.EndsWith(".css") ||
                              p.EndsWith(".png") || p.EndsWith(".svg") || p.EndsWith(".jpg") || p.EndsWith(".webp");
                var hasVersion = ctx.Context.Request.Query.ContainsKey("v");

                if (isAsset && hasVersion)
                {
                    // URL đã được stamp ?v=hash từ index.html → cache 1 năm + immutable
                    // (URL khác = hash đổi = file mới, không bao giờ collide)
                    ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                    ctx.Context.Response.Headers.Remove("Pragma");
                    ctx.Context.Response.Headers.Remove("Expires");
                }
                else if (p.EndsWith(".jsx") || p.EndsWith(".js") || p.EndsWith(".css") || p.EndsWith(".html"))
                {
                    // Truy cập trực tiếp không có ?v= → no-cache (an toàn cho dev hot-reload + URL bookmark)
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                    ctx.Context.Response.Headers["Pragma"]        = "no-cache";
                    ctx.Context.Response.Headers["Expires"]       = "0";
                }
            }
        });

        // ── Guide cho trang /help — phục vụ 2 thư mục con của docs/ ──────────────────
        // CHỈ features (markdown guide) + images (ảnh minh họa) → KHÔNG lộ docs nội bộ khác
        // (database-schema, superpowers/specs…). Trang /help fetch /docs/features/<slug>.md
        // rồi render markdown client-side; ảnh trong md rewrite ../images/ → /docs/images/.
        var docsRoot = Path.Combine(app.Environment.ContentRootPath, "docs");
        foreach (var (sub, reqPath, defType) in new[]
        {
            ("features", "/docs/features", "text/markdown; charset=utf-8"),
            ("images",   "/docs/images",   "application/octet-stream"),
        })
        {
            var dir = Path.Combine(docsRoot, sub);
            if (!Directory.Exists(dir)) continue;
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider          = new PhysicalFileProvider(dir),
                RequestPath           = reqPath,
                ServeUnknownFileTypes = true,   // .md không có MIME chuẩn
                DefaultContentType    = defType,
            });
        }

        return app;
    }

    // ── Hash mtime aggregate wwwroot — đổi 1 byte file static = đổi hash. Restart = re-compute.
    //     SKIP lib/tinymce/** (3rd-party, ~100 file skin/plugin) — chỉ hash core app code.
    //     Trước có Take(200) → tinymce skin chiếm slot đầu alphabetically, edits ở /pages/, /steps/
    //     hoàn toàn không invalidate hash → browser cache 1-năm immutable bị stale.
    /// <summary>
    /// Bóc danh sách plain <c>.js</c> mà bundle đã nuốt, từ nguồn <c>bundle-entry.js</c>
    /// (các dòng <c>import "./lib/util.js";</c>).
    /// </summary>
    /// <remarks>
    /// Bỏ qua <c>.jsx</c>: thẻ của chúng khai <c>type="text/babel"</c> nên đã bị
    /// <c>_babelScriptRegex</c> gỡ rồi — gỡ lần nữa thì thừa, mà lọt vào đây thì che mất lỗi thật.
    /// </remarks>
    public static IReadOnlyList<string> ParseBundledPlainJs(string bundleEntrySource)
    {
        if (string.IsNullOrWhiteSpace(bundleEntrySource)) return Array.Empty<string>();
        return Regex.Matches(bundleEntrySource, @"^\s*import\s+[""']\./([^""']+\.js)[""']\s*;",
                             RegexOptions.Multiline)
                    .Select(m => m.Groups[1].Value.Trim())
                    .Where(p => p.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>Ghép 1 regex gỡ đúng những thẻ &lt;script src&gt; của các file đã vào bundle.</summary>
    public static Regex BuildBundledPlainJsRegex(IReadOnlyList<string> bundledPaths)
    {
        // Rỗng = không đọc được bundle-entry.js. Vẫn phải gỡ 2 file mà chạy đôi là CÓ HẠI thật:
        // core/features.js gọi /api/v1/features lúc nạp → hỏi server 2 lần rồi thay luôn
        // window.tourkitFeatures; lib/data.js là nền của mọi trang.
        var list = bundledPaths.Count > 0 ? bundledPaths : _fallbackBundledPlainJs;
        var alt = string.Join("|", list.Select(Regex.Escape));
        return new Regex($@"<script\s+src=[""'](?:{alt})[""'][^>]*></script>\s*",
                         RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static Regex LoadBundledPlainJsRegex(string webRoot, ILogger log)
    {
        IReadOnlyList<string> paths = Array.Empty<string>();
        var entry = Path.Combine(webRoot, "bundle-entry.js");
        try
        {
            if (File.Exists(entry)) paths = ParseBundledPlainJs(File.ReadAllText(entry));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Không đọc được bundle-entry.js — dùng danh sách dự phòng");
        }

        if (paths.Count == 0)
            log.LogWarning("bundle-entry.js không có dòng import .js nào (hoặc đọc lỗi) → dự phòng {N} file",
                           _fallbackBundledPlainJs.Length);
        else
            log.LogInformation("Bundle mode: gỡ {N} thẻ <script> plain .js đã nằm trong bundle", paths.Count);
        return BuildBundledPlainJsRegex(paths);
    }

    private static string ComputeBuildVersion(string webRoot)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var f in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
                                       .Where(p => (p.EndsWith(".jsx") || p.EndsWith(".js") ||
                                                    p.EndsWith(".css") || p.EndsWith(".html")) &&
                                                   !p.Contains("lib" + Path.DirectorySeparatorChar + "tinymce") &&
                                                   !p.Contains("lib/tinymce"))
                                       .OrderBy(p => p))
            {
                sb.Append(File.GetLastWriteTimeUtc(f).Ticks).Append('|');
            }
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes).Substring(0, 10).ToLowerInvariant();
        }
        catch
        {
            return DateTime.UtcNow.Ticks.ToString("x")[..10];
        }
    }

    // Regex stamp ?v=hash vào local <script src> + <link href> (relative URLs).
    // KHÔNG đụng absolute URLs (https://, //), data:, /api/*, fragment-only (#).
    private static readonly Regex _stampRegex = new(
        @"(<(?:script|link)\b[^>]*\b(?:src|href)=[""'])(?!https?://|//|/api/|data:|#)([^""'?]+)([""'])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex bắt mọi <script type="text/babel" src="...">…</script> để strip ở prod-bundle mode.
    private static readonly Regex _babelScriptRegex = new(
        @"<script\s+type\s*=\s*[""']text/babel[""'][^>]*></script>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Bắt CDN babel-standalone + babel-cache helper — bỏ khi prod bundle (Babel không còn cần).
    private static readonly Regex _babelStandaloneRegex = new(
        @"<script\s+src=[""'][^""']*babel(?:-standalone|/standalone|\.min)[^""']*[""'][^>]*></script>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _babelCacheRegex = new(
        @"<script\s+src=[""']core/babel-cache\.js[""'][^>]*></script>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Strip các plain .js đã nằm trong bundle. DANH SÁCH ĐỌC THẲNG TỪ wwwroot/bundle-entry.js,
    // KHÔNG khai tay nữa.
    //
    // Trước đây đây là một danh sách viết tay đặt cạnh MỘT danh sách viết tay khác (bundle-entry.js),
    // kèm sẵn dòng dặn "thêm file mới thì nhớ thêm vào đây" — và vẫn lệch 12 file (lib/util.js,
    // lib/tts.js, flows/*.js). Hậu quả không phải "chạy đôi cho tốn": bản trong bundle nạp SAU nên nó
    // THẮNG, tức là sửa một file plain .js mà chưa dựng lại bundle thì bản sửa IM LẶNG không có tác
    // dụng ở prod — dev không bao giờ lộ ra vì dev không có bundle. Đã xảy ra thật với bản vá lỗi mở
    // CRM (20/08/2026): lib/util.js mới nằm trong thẻ script, bundle cũ đè lên, không lỗi nào hiện ra.
    //
    // tinymce-loader / chart-loader / flow-loader KHÔNG có trong bundle-entry.js (cố ý lazy-load) nên
    // tự động không bị gỡ — thêm một lý do để đọc từ đó thay vì chép tay.
    private static readonly string[] _fallbackBundledPlainJs = { "lib/data.js", "core/features.js" };

    // Đọc 1 lần lúc startup (bundle-entry.js chỉ đổi khi dựng lại frontend).
    private static Regex _bundledPlainJsRegex = BuildBundledPlainJsRegex(Array.Empty<string>());

    // Thẻ <title> gốc trong index.html — thay bằng tiêu đề theo từng trang.
    private static readonly Regex _titleRegex = new(
        @"<title>.*?</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static IResult ServeIndex(HttpContext ctx, string webRoot, bool recomputeVersion = false,
        bool knownRoute = true)
    {
        var path = Path.Combine(webRoot, "index.html");
        if (!File.Exists(path)) return Results.NotFound();
        ctx.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
        var html = File.ReadAllText(path);

        // ── SEO: tiêu đề + thẻ meta theo từng trang, và nội dung chữ dựng sẵn cho trang chủ ──
        //
        // Làm ở server vì trang vẽ bằng JS: HTML gốc không có chữ nào của nội dung, nên bộ xem trước
        // link (Zalo/Facebook/LinkedIn) và các máy tìm kiếm không chạy JS chỉ thấy trang trắng.
        var reqPath = ctx.Request.Path.Value ?? "/";
        var seo = SeoSetup.For(reqPath);
        // Escape tối thiểu (xem SeoSetup.Esc) — HtmlEncode sẽ biến chữ có dấu thành &#7897; nên
        // tiêu đề tab và kết quả tìm kiếm thành một dãy số trong mã nguồn.
        html = _titleRegex.Replace(html, $"<title>{SeoSetup.EscapeText(seo.Title)}</title>", 1);

        var headTags = knownRoute
            ? SeoSetup.Head(ctx, reqPath)
            // Đường lạ (đang trả 404): chỉ cần chặn index, không cần canonical/OG cho một URL không
            // tồn tại — khai canonical cho nó là nói với Google rằng URL đó có thật.
            : "<meta name=\"robots\" content=\"noindex\" />\n";
        var headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headClose > 0) html = html.Insert(headClose, headTags);

        // Nội dung dựng sẵn CHỈ cho trang chủ, nhét vào #root. React thay toàn bộ khi khởi động nên
        // người dùng thấy đúng trang thật; chữ y hệt nhau nên không phải chuyện che mắt máy tìm kiếm.
        if (seo.Index)
            html = html.Replace("<div id=\"root\"></div>", $"<div id=\"root\">{SeoSetup.LandingBody()}</div>");
        if (recomputeVersion) _buildVersion = ComputeBuildVersion(webRoot);
        var v = _buildVersion;

        // Prod bundle mode: nếu dist/app.bundle.js tồn tại → thay 35 thẻ <script type="text/babel">
        // + babel-standalone CDN + babel-cache.js bằng 1 thẻ duy nhất <script src="dist/app.bundle.js">.
        // Bundle là IIFE — không cần defer/async; React/Chart.js/TinyMCE CDN giữ nguyên (vẫn cần).
        var bundlePath = Path.Combine(webRoot, "dist", "app.bundle.js");
        if (File.Exists(bundlePath))
        {
            html = _babelScriptRegex.Replace(html, string.Empty);
            html = _babelStandaloneRegex.Replace(html, string.Empty);
            html = _babelCacheRegex.Replace(html, string.Empty);
            html = _bundledPlainJsRegex.Replace(html, string.Empty);
            // Inject 1 thẻ bundle ngay trước </body>. ?v= sẽ được stamp ở bước dưới.
            var bundleTag = "<script src=\"dist/app.bundle.js\"></script>\n";
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = bodyClose > 0
                ? html.Insert(bodyClose, bundleTag)
                : html + bundleTag;
        }

        html = _stampRegex.Replace(html, m => $"{m.Groups[1].Value}{m.Groups[2].Value}?v={v}{m.Groups[3].Value}");
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
