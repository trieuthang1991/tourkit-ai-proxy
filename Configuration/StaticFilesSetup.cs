using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

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

        // SPA deep-link fallback (/customers, /deals, /assistant…) PHẢI cũng qua ServeIndex để
        // nhận bundle-injection + ?v=hash. TRƯỚC ĐÂY Program.cs dùng MapFallbackToFile("index.html")
        // serve file THÔ → deep-link + Ctrl+F5 rớt về DEV-babel mode (44 <script type=text/babel>
        // + Babel CDN, cold start 3-5s) NGAY CẢ KHI đã có prod bundle. Chỉ "/" mới nhanh.
        // MapFallback luôn ưu tiên thấp nhất (order=int.MaxValue) → API + static file vẫn match trước.
        app.MapFallback((HttpContext ctx) => ServeIndex(ctx, webRoot, isDev));

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

        return app;
    }

    // ── Hash mtime aggregate wwwroot — đổi 1 byte file static = đổi hash. Restart = re-compute.
    //     SKIP lib/tinymce/** (3rd-party, ~100 file skin/plugin) — chỉ hash core app code.
    //     Trước có Take(200) → tinymce skin chiếm slot đầu alphabetically, edits ở /pages/, /steps/
    //     hoàn toàn không invalidate hash → browser cache 1-năm immutable bị stale.
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
    // Strip cả các plain .js file đã được bundle vào (vd lib/data.js).
    // tinymce-loader giữ ngoài bundle (lazy load TinyMCE ~5MB chỉ khi mở mail).
    private static readonly Regex _bundledPlainJsRegex = new(
        @"<script\s+src=[""']lib/data\.js[""'][^>]*></script>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IResult ServeIndex(HttpContext ctx, string webRoot, bool recomputeVersion = false)
    {
        var path = Path.Combine(webRoot, "index.html");
        if (!File.Exists(path)) return Results.NotFound();
        ctx.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
        var html = File.ReadAllText(path);
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
