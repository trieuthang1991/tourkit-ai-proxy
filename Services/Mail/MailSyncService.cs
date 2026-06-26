using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Mail;

/// <summary>Kết quả 1 lần sync. NewMails = các email MỚI vừa phân loại (cho auto-reply); null nếu caller không cần.</summary>
public record MailSyncResult(int Fetched, int Classified, int Skipped, IReadOnlyList<MailItem>? NewMails = null);

/// <summary>
/// Service tái sử dụng logic đồng bộ Gmail: IMAP fetch → classify mới → lưu DB.
/// Được dùng bởi cả <c>MailEndpoints</c> (HTTP) và <c>MailAutoSyncWorkflow</c> (scheduler).
///
/// Khi <see cref="MailAccountStore.Get"/> trả null → throw <see cref="InvalidOperationException"/>
/// với message "Chưa cấu hình tài khoản Gmail" → caller quyết định xử lý (400 hoặc fail run).
/// </summary>
public class MailSyncService
{
    private readonly IMailSource _source;
    private readonly MailRepository _repo;
    private readonly MailClassifier _classifier;
    private readonly IWorkflowTraceAccessor _trace;
    private readonly ILogger<MailSyncService> _log;

    public MailSyncService(
        IMailSource source,
        MailRepository repo,
        MailClassifier classifier,
        IWorkflowTraceAccessor trace,
        ILogger<MailSyncService> log)
    {
        _source = source; _repo = repo;
        _classifier = classifier; _trace = trace; _log = log;
    }

    /// <summary>
    /// Kéo tối đa <paramref name="max"/> email mới từ Gmail IMAP (incremental theo UID),
    /// phân loại AI cho email chưa có trong DB, lưu kết quả.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="username">Username (khớp <see cref="MailAccountStore"/>).</param>
    /// <param name="max">Số email tối đa kéo 1 lần.</param>
    /// <param name="ct">CancellationToken.</param>
    /// <returns><see cref="MailSyncResult"/> với số lượng thực tế.</returns>
    /// <exception cref="InvalidOperationException">Khi chưa cấu hình Gmail.</exception>
    public async Task<MailSyncResult> RunAsync(
        string tenantId, string username, int max, CancellationToken ct)
    {
        // FetchRecentAsync tự throw InvalidOperationException nếu chưa cấu hình creds.
        IReadOnlyList<MailItem> fetched;
        try
        {
            fetched = await _source.FetchRecentAsync(tenantId, username, max, ct);
        }
        catch (InvalidOperationException)
        {
            // Re-throw trực tiếp → message đã chuẩn "Chưa cấu hình hộp thư Gmail..."
            throw;
        }
        catch (Exception ex)
        {
            // Wrap để có context rõ hơn khi log ở caller
            throw new Exception("Không kết nối được hộp thư: " + ex.Message, ex);
        }

        int skipped = 0, bulk = 0;
        var newMails = new List<MailItem>();   // email MỚI vừa phân loại → cho auto-reply
        foreach (var mail in fetched)
        {
            ct.ThrowIfCancellationRequested();
            if (_repo.Has(tenantId, mail.Id))
            {
                skipped++;
                continue;   // đã có = đã phân loại → bỏ qua (tiết kiệm token)
            }
            string cat, sum;
            if (mail.IsBulk)
            {
                // Bulk/newsletter → gán 'khac', KHÔNG gọi AI (tiết kiệm token cho inbox nhiều rác).
                cat = "khac"; sum = ""; bulk++;
            }
            else
            {
                (cat, sum) = await _classifier.ClassifyAsync(mail, ct);
            }
            var saved = mail with { Category = cat, AiSummary = sum };
            _repo.Upsert(tenantId, saved);
            newMails.Add(saved);
        }

        _log.LogInformation("[MailSync] tenant={T} user={U} — {F} kéo, {C} lưu ({B} bulk skip-AI), {S} đã có",
            tenantId, username, fetched.Count, newMails.Count, bulk, skipped);
        return new MailSyncResult(fetched.Count, newMails.Count - bulk, skipped, newMails);
    }
}
