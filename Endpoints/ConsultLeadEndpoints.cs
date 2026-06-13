using System.Text.Json;

namespace TourkitAiProxy.Endpoints;

/// Đăng ký tư vấn từ landing page (KHÔNG cần auth — public).
/// Lưu append vào data/consult-leads.jsonl, mỗi dòng là 1 lead JSON.
/// Pattern khớp data/visa-leads.jsonl (cũng JSONL append-only).
public static class ConsultLeadEndpoints
{
    public record ConsultLeadReq(
        string FullName,
        string Phone,
        string? Email,
        string? Company,
        string? Feature,
        string? Note);

    public static IEndpointRouteBuilder MapConsultLeadEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/consult-leads", async (ConsultLeadReq req, HttpContext ctx, ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(req.FullName) || string.IsNullOrWhiteSpace(req.Phone))
                return Results.BadRequest(new { error = "Vui lòng nhập Họ tên và Số điện thoại" });

            // Chặn payload quá khổ (anti-spam)
            if (req.FullName.Length > 200 || req.Phone.Length > 30
                || (req.Email?.Length ?? 0) > 200 || (req.Company?.Length ?? 0) > 200
                || (req.Feature?.Length ?? 0) > 80 || (req.Note?.Length ?? 0) > 2000)
                return Results.BadRequest(new { error = "Một trong các trường nhập quá dài" });

            var dir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "consult-leads.jsonl");

            var row = new
            {
                ts = DateTime.UtcNow.ToString("o"),
                ip = ctx.Connection.RemoteIpAddress?.ToString(),
                ua = ctx.Request.Headers["User-Agent"].FirstOrDefault(),
                fullName = req.FullName.Trim(),
                phone = req.Phone.Trim(),
                email = req.Email?.Trim(),
                company = req.Company?.Trim(),
                feature = req.Feature?.Trim(),
                note = req.Note?.Trim()
            };
            await File.AppendAllTextAsync(file, JsonSerializer.Serialize(row) + "\n");

            log.LogInformation("[consult-lead] {Name} · {Phone} · feature={Feature}",
                row.fullName, row.phone, row.feature ?? "(none)");
            return Results.Ok(new { ok = true });
        });

        return routes;
    }
}
