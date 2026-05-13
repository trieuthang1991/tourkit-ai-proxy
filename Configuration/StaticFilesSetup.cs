using Microsoft.AspNetCore.StaticFiles;

namespace TourkitAiProxy.Configuration;

public static class StaticFilesSetup
{
    public static WebApplication UseTourkitStaticFiles(this WebApplication app)
    {
        // Truy cập http://localhost:5080 sẽ tự load index.html từ wwwroot/
        app.UseDefaultFiles();

        app.UseStaticFiles(new StaticFileOptions
        {
            // .jsx / .babel không có MIME chuẩn → fallback text/plain để browser load
            ServeUnknownFileTypes = true,
            DefaultContentType    = "text/plain",
            OnPrepareResponse     = ctx =>
            {
                // Dev: tắt cache cho .jsx / .js / .css / .html để edit là refresh thấy ngay
                var p = ctx.File.Name;
                if (p.EndsWith(".jsx") || p.EndsWith(".js") || p.EndsWith(".css") || p.EndsWith(".html"))
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                    ctx.Context.Response.Headers["Pragma"]        = "no-cache";
                    ctx.Context.Response.Headers["Expires"]       = "0";
                }
            }
        });

        app.Logger.LogInformation("Frontend wwwroot: {Path}",
            Path.Combine(app.Environment.ContentRootPath, "wwwroot"));

        return app;
    }
}
