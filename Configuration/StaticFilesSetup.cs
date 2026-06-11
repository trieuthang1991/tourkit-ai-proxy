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

        // Intercept root + index.html → server inject ?v=hash vào local <script src> + <link href>
        // Trình duyệt sẽ thấy URL khác mỗi lần deploy → invalidate cache cũ tự nhiên.
        app.MapGet("/", () => ServeIndex(webRoot));
        app.MapGet("/index.html", () => ServeIndex(webRoot));

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

    // ── Hash của max(mtime) aggregate wwwroot (cap 200 file để khỏi chậm startup).
    //     Đổi 1 byte file static = đổi hash. Restart process = re-compute.
    private static string ComputeBuildVersion(string webRoot)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var f in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
                                       .Where(p => p.EndsWith(".jsx") || p.EndsWith(".js") ||
                                                   p.EndsWith(".css") || p.EndsWith(".html"))
                                       .OrderBy(p => p)
                                       .Take(200))
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

    private static IResult ServeIndex(string webRoot)
    {
        var path = Path.Combine(webRoot, "index.html");
        if (!File.Exists(path)) return Results.NotFound();
        var html = File.ReadAllText(path);
        var v = _buildVersion;
        html = _stampRegex.Replace(html, m => $"{m.Groups[1].Value}{m.Groups[2].Value}?v={v}{m.Groups[3].Value}");
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
