using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// CRUD template mail dùng chung (dbo.MailTemplates, global PK=Code) — nguồn nội dung cho hàng đợi
/// dbo.OutboundMails. Worker (toutkit-app) đọc bảng này theo Code rồi render {{key}} + {{#if key}}…{{/if}}
/// từ [Params] của từng dòng, fallback template code cũ nếu không có row/Disabled.
///
/// Admin sửa Subject/BodyHtml KHÔNG cần deploy lại worker. Repo thuần Dapper, KHÔNG cache (admin ít gọi).
/// </summary>
public class MailTemplateRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<MailTemplateRepository> _log;

    public MailTemplateRepository(TourkitAiDb db, ILogger<MailTemplateRepository> log)
    {
        _db = db; _log = log;
    }

    public async Task<List<MailTemplate>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<MailTemplate>(@"
SELECT Code, Name, Subject, BodyHtml, Description, SampleParams, Enabled, UpdatedBy, UpdatedUtc
FROM dbo.MailTemplates
ORDER BY Code;");
        return rows.AsList();
    }

    public async Task<MailTemplate?> GetAsync(string code, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QueryFirstOrDefaultAsync<MailTemplate>(@"
SELECT Code, Name, Subject, BodyHtml, Description, SampleParams, Enabled, UpdatedBy, UpdatedUtc
FROM dbo.MailTemplates WHERE Code = @code;", new { code });
    }

    /// Upsert (MERGE) theo Code. Trả về bản ghi sau khi lưu.
    public async Task<MailTemplate> UpsertAsync(MailTemplate t, string? by, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.MailTemplates AS tgt
USING (SELECT @Code AS Code) AS src ON tgt.Code = src.Code
WHEN MATCHED THEN UPDATE SET
    Name = @Name, Subject = @Subject, BodyHtml = @BodyHtml, Description = @Description,
    SampleParams = @SampleParams, Enabled = @Enabled, UpdatedBy = @By, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (Code, Name, Subject, BodyHtml, Description, SampleParams, Enabled, UpdatedBy, UpdatedUtc)
    VALUES (@Code, @Name, @Subject, @BodyHtml, @Description, @SampleParams, @Enabled, @By, SYSUTCDATETIME());",
            new { t.Code, t.Name, t.Subject, t.BodyHtml, t.Description, t.SampleParams, t.Enabled, By = by });
        return (await GetAsync(t.Code, ct))!;
    }

    public async Task<bool> DeleteAsync(string code, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var n = await c.ExecuteAsync("DELETE FROM dbo.MailTemplates WHERE Code = @code;", new { code });
        return n > 0;
    }

    /// Seed template mặc định CHỈ khi bảng RỖNG (provision lần đầu). Gọi 1 lần lúc startup.
    /// Cố ý KHÔNG seed lại khi đã có ít nhất 1 template — để admin XÓA "deal-cooling-alert"
    /// (ép worker fallback template code) không bị restart resurrect lại.
    public async Task SeedDefaultsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var existing = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.MailTemplates;");
            if (existing > 0) return;   // đã có template (kể cả admin đã xóa bớt) → tôn trọng, không seed lại
            foreach (var t in DefaultTemplates)
            {
                await c.ExecuteAsync(@"
INSERT INTO dbo.MailTemplates (Code, Name, Subject, BodyHtml, Description, SampleParams, Enabled, UpdatedBy, UpdatedUtc)
VALUES (@Code, @Name, @Subject, @BodyHtml, @Description, @SampleParams, 1, 'system-seed', SYSUTCDATETIME());",
                    new { t.Code, t.Name, t.Subject, t.BodyHtml, t.Description, t.SampleParams });
            }
            _log.LogInformation("Seed {N} MailTemplates mặc định (bảng rỗng → provision lần đầu)", DefaultTemplates.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Seed MailTemplates mặc định lỗi (bỏ qua — admin có thể tạo tay)");
        }
    }

    // ── Template mặc định (khớp output OutboundMailTemplates.DealCoolingAlert của worker) ──
    private static readonly MailTemplate[] DefaultTemplates =
    {
        new MailTemplate
        {
            Code = "deal-cooling-alert",
            Name = "Cảnh báo deal nguội",
            Subject = "[TourKit] Deal nguội {{coolingDays}} ngày — {{customerName}}{{#if dealCode}} ({{dealCode}}){{/if}}",
            Description = "Gửi nhân viên phụ trách khi cơ hội bán hàng không có hoạt động mới trong N ngày. " +
                          "Tham số do workflow deal-auto-review cung cấp.",
            SampleParams = """
{"dealId":"123","dealCode":"BK-0123","customerName":"Nguyễn Văn A","phone":"0901234567","title":"Tour Đà Nẵng 4N3Đ","totalPriceFormatted":"32.000.000 đ","statusName":"Đang tư vấn","sourceName":"Facebook","assigneeNames":"Trần Thị B","coolingDays":"7","lastInteractionAt":"2026-06-20","winRate":"65","level":"trung_binh","nextAction":"Gọi điện xác nhận nhu cầu và gửi lại báo giá."}
""",
            BodyHtml = """
<div style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;max-width:560px;margin:0 auto;color:#111827">
  <div style="padding:16px 0;border-bottom:2px solid #f59e0b">
    <span style="font-size:18px;font-weight:700">⚠️ Cơ hội bán hàng đang nguội</span>
  </div>
  <p style="color:#374151;margin:16px 0">Deal dưới đây không có hoạt động mới trong <b>{{coolingDays}} ngày</b>. Vui lòng liên hệ lại khách hàng.</p>
  <table style="border-collapse:collapse;width:100%;font-size:14px;background:#f9fafb;border-radius:8px">
    {{#if customerName}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Khách hàng</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{customerName}}</td></tr>{{/if}}
    {{#if phone}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Điện thoại</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{phone}}</td></tr>{{/if}}
    {{#if title}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Tiêu đề</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{title}}</td></tr>{{/if}}
    {{#if totalPriceFormatted}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Giá trị</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{totalPriceFormatted}}</td></tr>{{/if}}
    {{#if statusName}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Trạng thái</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{statusName}}</td></tr>{{/if}}
    {{#if sourceName}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Nguồn</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{sourceName}}</td></tr>{{/if}}
    {{#if assigneeNames}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Phụ trách</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{assigneeNames}}</td></tr>{{/if}}
    {{#if coolingDays}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Số ngày nguội</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{coolingDays}}</td></tr>{{/if}}
    {{#if lastInteractionAt}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Tương tác gần nhất</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{lastInteractionAt}}</td></tr>{{/if}}
    {{#if winRate}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Xác suất chốt (AI)</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{winRate}}</td></tr>{{/if}}
    {{#if level}}<tr><td style="padding:6px 12px;color:#6b7280;white-space:nowrap">Mức độ (AI)</td><td style="padding:6px 12px;color:#111827;font-weight:500">{{level}}</td></tr>{{/if}}
  </table>
  {{#if nextAction}}<div style="margin-top:16px;padding:12px 16px;background:#fff7ed;border-left:3px solid #f59e0b;border-radius:4px"><div style="font-size:12px;color:#b45309;font-weight:600;margin-bottom:4px">Gợi ý hành động</div><div style="color:#92400e">{{nextAction}}</div></div>{{/if}}
  <p style="margin-top:20px;font-size:12px;color:#9ca3af">Email tự động từ hệ thống TourKit · Tự động hoá &gt; Cảnh báo deal nguội</p>
</div>
""",
        },
    };
}

/// Read/write-model 1 template mail. Code = PK global.
public class MailTemplate
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string? Description { get; set; }
    public string? SampleParams { get; set; }
    public bool Enabled { get; set; } = true;
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
