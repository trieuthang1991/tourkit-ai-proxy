using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// Counts cho sidebar: tổng + chưa đọc + theo trạng thái + theo nhóm.
/// </summary>
public record MailCounts(int Total, int Unread, Dictionary<string, int> ByStatus, Dictionary<string, int> ByCategory);

/// <summary>
/// SQL Server-backed store: (TenantId, mailId) → MailItem. Persist dbo.Mails.
/// Mọi method nhận tenantId — query luôn filter scoped theo tenant. Cross-tenant access trả null.
/// Không fallback file — DB lỗi → throw, endpoint trả 503.
/// </summary>
public class MailRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<MailRepository> _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MailRepository(TourkitAiDb db, ILogger<MailRepository> log)
    {
        _db = db; _log = log;
    }

    public MailItem? Get(string tenantId, string id)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(id)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<MailRow>(
            @"SELECT * FROM dbo.Mails WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id });
        return row == null ? null : Hydrate(row);
    }

    public bool Has(string tenantId, string id)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(id)) return false;
        using var c = _db.Open();
        return c.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM dbo.Mails WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id }) > 0;
    }

    public void Upsert(string tenantId, MailItem item)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId rỗng", nameof(tenantId));
        using var c = _db.Open();
        c.Execute(@"
MERGE dbo.Mails AS T
USING (SELECT @t AS TenantId, @id AS Id) AS S
   ON T.TenantId = S.TenantId AND T.Id = S.Id
WHEN MATCHED THEN UPDATE SET
    FromName=@fn, FromEmail=@fe, Subject=@sub, Body=@body, BodyHtml=@html,
    ReceivedAt=@recv, IsRead=@read, Category=@cat, Status=@stat,
    AiSummary=@sum, DraftJson=@draft
WHEN NOT MATCHED THEN INSERT
    (TenantId, Id, FromName, FromEmail, Subject, Body, BodyHtml, ReceivedAt, IsRead, Category, Status, AiSummary, DraftJson)
VALUES
    (@t, @id, @fn, @fe, @sub, @body, @html, @recv, @read, @cat, @stat, @sum, @draft);",
            new
            {
                t = tenantId, id = item.Id,
                fn = item.From.Name, fe = item.From.Email,
                sub = item.Subject, body = item.Body, html = item.BodyHtml,
                recv = DateTime.TryParse(item.ReceivedAt, out var dt) ? dt : DateTime.UtcNow,
                read = item.IsRead, cat = item.Category, stat = item.Status, sum = item.AiSummary,
                draft = item.Draft == null ? null : JsonSerializer.Serialize(item.Draft, _jsonOpts)
            });
    }

    public bool SetStatus(string tenantId, string id, string status)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        return c.Execute(
            "UPDATE dbo.Mails SET Status=@s WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id, s = status }) > 0;
    }

    public bool SetRead(string tenantId, string id, bool isRead = true)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        return c.Execute(
            "UPDATE dbo.Mails SET IsRead=@r WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id, r = isRead }) > 0;
    }

    public bool SetDraft(string tenantId, string id, MailDraft draft, string status)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        var draftJson = JsonSerializer.Serialize(draft, _jsonOpts);
        return c.Execute(
            "UPDATE dbo.Mails SET DraftJson=@d, Status=@s WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id, d = draftJson, s = status }) > 0;
    }

    /// Đánh dấu lỗi auto-reply (soạn/gửi tự động thất bại). error=null → xoá cờ (auto-reply thành công).
    public bool SetAutoReplyError(string tenantId, string id, string? error)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        var msg = error?.Length > 490 ? error[..490] + "…" : error;
        return c.Execute(
            "UPDATE dbo.Mails SET AutoReplyError=@e WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id, e = msg }) > 0;
    }

    /// Lọc theo status/category/search (search bỏ dấu, không phân biệt hoa). Mới nhất trước.
    public IReadOnlyList<MailItem> Filter(string tenantId, string? status, string? category, string? search)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return Array.Empty<MailItem>();
        using var c = _db.Open();
        var rows = c.Query<MailRow>(
            "SELECT * FROM dbo.Mails WHERE TenantId=@t ORDER BY ReceivedAt DESC",
            new { t = tenantId }).ToList();

        IEnumerable<MailItem> q = rows.Select(Hydrate).Where(m => m != null)!;
        if (!string.IsNullOrWhiteSpace(status))   q = q.Where(m => m!.Status == status);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(m => m!.Category == category);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = Norm(search);
            q = q.Where(m => Norm($"{m!.Subject} {m.From.Name} {m.From.Email} {m.Body}").Contains(s));
        }
        return q.ToList()!;
    }

    /// Lọc + PHÂN TRANG (cho infinite-scroll). Trả (items của trang, TỔNG sau lọc) → FE biết còn nữa không.
    ///
    /// FAST PATH (không search): phân trang Ở SQL bằng OFFSET/FETCH → CHỈ kéo `limit` row, KHÔNG kéo cả
    /// bảng (trước đây `Filter` SELECT * toàn bộ kèm Body/BodyHtml nặng → mỗi lần loadMore ~vài chục MB ~28s).
    /// SEARCH PATH (hiếm): cần Body cho tìm bỏ-dấu → giữ in-memory như cũ.
    public (IReadOnlyList<MailItem> Items, int Total) FilterPaged(
        string tenantId, string? status, string? category, string? search, int limit, int offset)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return (Array.Empty<MailItem>(), 0);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var all = Filter(tenantId, status, category, search);
            var pg = all.Skip(Math.Max(0, offset)).Take(limit <= 0 ? all.Count : limit).ToList();
            return (pg, all.Count);
        }

        using var c = _db.Open();
        var where = "WHERE TenantId=@t"
                  + (string.IsNullOrWhiteSpace(status)   ? "" : " AND Status=@st")
                  + (string.IsNullOrWhiteSpace(category) ? "" : " AND Category=@cat");
        var prm = new { t = tenantId, st = status, cat = category,
                        off = Math.Max(0, offset), lim = limit <= 0 ? int.MaxValue : limit };
        var total = c.ExecuteScalar<int>($"SELECT COUNT(1) FROM dbo.Mails {where}", prm);
        // CHỈ lấy metadata cho list — KHÔNG kéo Body/BodyHtml/AiSummary/DraftJson (nặng).
        // Body... lấy khi mở từng email qua GET /mail/{id}. → list rất nhẹ, scroll mượt.
        var rows = c.Query<MailRow>(
            $@"SELECT Id, FromName, FromEmail, Subject, ReceivedAt, IsRead, Category, Status, AutoReplyError
               FROM dbo.Mails {where}
               ORDER BY ReceivedAt DESC
               OFFSET @off ROWS FETCH NEXT @lim ROWS ONLY", prm).ToList();
        var items = rows.Select(Hydrate).Where(m => m != null).ToList()!;
        return (items, total);
    }

    /// Xoá TOÀN BỘ mail của tenant. Dùng khi user disconnect + chọn xoá lịch sử.
    /// Trả số dòng bị xoá. Idempotent.
    public int ClearTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return 0;
        using var c = _db.Open();
        var n = c.Execute(@"DELETE FROM dbo.Mails WHERE TenantId = @t", new { t = tenantId });
        if (n > 0) _log.LogInformation("[MailRepo] ClearTenant tenant={Tenant} rows={N}", tenantId, n);
        return n;
    }

    public MailCounts Counts(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new MailCounts(0, 0, new(), new());
        using var c = _db.Open();
        var rows = c.Query<CountsRow>(
            "SELECT Status, Category, IsRead FROM dbo.Mails WHERE TenantId=@t",
            new { t = tenantId }).ToList();

        var byStatus = new Dictionary<string, int>();
        var byCat = new Dictionary<string, int>();
        int unread = 0;
        foreach (var r in rows)
        {
            byStatus[r.Status] = byStatus.GetValueOrDefault(r.Status) + 1;
            var cat = r.Category ?? "khac";
            byCat[cat] = byCat.GetValueOrDefault(cat) + 1;
            if (!r.IsRead) unread++;
        }
        return new MailCounts(rows.Count, unread, byStatus, byCat);
    }

    // ─── Hydration ────────────────────────────────────────────────────────
    private MailItem? Hydrate(MailRow row)
    {
        try
        {
            MailDraft? draft = string.IsNullOrEmpty(row.DraftJson)
                ? null
                : JsonSerializer.Deserialize<MailDraft>(row.DraftJson, _jsonOpts);
            return new MailItem(
                Id: row.Id,
                From: new MailContact(row.FromName ?? "", row.FromEmail ?? ""),
                Subject: row.Subject ?? "",
                Body: row.Body ?? "",
                ReceivedAt: row.ReceivedAt.ToString("o"),
                IsRead: row.IsRead,
                Category: row.Category,
                Status: row.Status,
                AiSummary: row.AiSummary,
                Draft: draft,
                BodyHtml: row.BodyHtml,
                AutoReplyError: row.AutoReplyError);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[MailRepo] Hydrate row {Id} fail", row.Id);
            return null;
        }
    }

    /// <summary>Dapper row mapper cho dbo.Mails — bind theo NAME, không phải ordinal.</summary>
    private sealed class MailRow
    {
        public string TenantId { get; set; } = "";
        public string Id { get; set; } = "";
        public string? FromName { get; set; }
        public string? FromEmail { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? BodyHtml { get; set; }
        public DateTime ReceivedAt { get; set; }
        public bool IsRead { get; set; }
        public string? Category { get; set; }
        public string Status { get; set; } = "moi";
        public string? AiSummary { get; set; }
        public string? DraftJson { get; set; }
        public string? AutoReplyError { get; set; }
    }

    /// <summary>Compact row mapper cho Counts — chỉ 3 column cần.</summary>
    private sealed class CountsRow
    {
        public string Status { get; set; } = "moi";
        public string? Category { get; set; }
        public bool IsRead { get; set; }
    }

    /// Chuẩn hóa search: lowercase + bỏ dấu tiếng Việt + đ→d.
    private static string Norm(string s)
    {
        s = (s ?? "").ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
