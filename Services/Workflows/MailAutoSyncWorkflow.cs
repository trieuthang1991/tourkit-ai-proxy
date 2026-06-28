using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Workflow tự động đồng bộ Gmail: kéo email mới + AI phân loại + (tùy option) auto-reply.
/// Implement <see cref="IScheduledWorkflow"/> để scheduler tự pickup. Scope = PerUser.
///
/// Auto-reply ĐIỀU KHIỂN HOÀN TOÀN BẰNG OPTION (per-user, lưu OptionsJson):
///   { autoReply, replyMode: 'draft'|'send', replyCategories: [...], replyTone }
///   - draft: AI soạn nháp + lưu (status dang_xu_ly), người duyệt rồi gửi (AN TOÀN).
///   - send : AI soạn + GỬI THẲNG cho khách (status da_phan_hoi).
/// Chỉ áp dụng cho mail thuộc replyCategories. Soạn/gửi LỖI → đánh dấu MailItem.AutoReplyError (hiện ở UI).
/// </summary>
public class MailAutoSyncWorkflow : IScheduledWorkflow
{
    private readonly MailSyncService _sync;
    private readonly MailReplyService _reply;
    private readonly IMailSender _sender;
    private readonly MailRepository _repo;
    private readonly AiCallContext _aiCtx;
    private readonly ILogger<MailAutoSyncWorkflow> _log;

    public MailAutoSyncWorkflow(
        MailSyncService sync, MailReplyService reply, IMailSender sender,
        MailRepository repo, AiCallContext aiCtx, ILogger<MailAutoSyncWorkflow> log)
    {
        _sync = sync; _reply = reply; _sender = sender; _repo = repo; _aiCtx = aiCtx; _log = log;
    }

    public string Type => "mail-auto-sync";
    public string Label => "Tự động đồng bộ Gmail";
    public string Description => "Kéo email mới từ Gmail, AI phân loại + đặt nhãn 6 nhóm (hỏi/đặt tour, báo giá, khiếu nại...)";
    public WorkflowScope Scope => WorkflowScope.PerUser;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        var opt = MailAutoSyncOptions.Parse(optionsJson);
        // QUOTA + LOG: workflow nền KHÔNG có HttpContext → phải Push để AI classify/auto-reply trừ quota
        // tenant + log đúng feature ("mail-auto-sync"). Thiếu Push = bypass quota + log tenant=null (bug cũ).
        using var _aiScope = _aiCtx.Push("mail-auto-sync", tenantId);
        try
        {
            // max nhỏ cho mỗi run nền: kết nối nhẹ → ít bị Gmail RST; backlog tự drain dần qua các chu kỳ.
            var result = await _sync.RunAsync(tenantId, username, max: 50, ct);

            int drafted = 0, sent = 0, failed = 0;
            if (opt.AutoReply && result.NewMails is { Count: > 0 } newMails)
                (drafted, sent, failed) = await AutoReplyAsync(tenantId, username, newMails, opt, ct);

            var summary = JsonSerializer.Serialize(new
            {
                fetched = result.Fetched,
                classified = result.Classified,
                skipped = result.Skipped,
                autoReply = opt.AutoReply,
                replyMode = opt.ReplyMode,
                drafted,
                sent,
                replyFailed = failed
            });
            _log.LogInformation("[MailAutoSync] tenant={T} user={U} → fetched={F} classified={C} skipped={S} autoReply={AR} drafted={D} sent={Se} failed={Fa}",
                tenantId, username, result.Fetched, result.Classified, result.Skipped, opt.AutoReply, drafted, sent, failed);
            return new WorkflowRunResult(Ok: true, Summary: summary, Error: null);
        }
        catch (OperationCanceledException)
        {
            return new WorkflowRunResult(Ok: false, Summary: null, Error: "Vượt quá thời gian 5 phút");
        }
        catch (Exception ex)
        {
            _log.LogWarning("[MailAutoSync] tenant={T} user={U} lỗi: {Err}", tenantId, username, ex.Message);
            return new WorkflowRunResult(Ok: false, Summary: null, Error: ex.Message);
        }
    }

    /// Auto-reply cho các mail MỚI thuộc nhóm được chọn. Trả (drafted, sent, failed).
    /// Mỗi mail độc lập: lỗi 1 cái KHÔNG chặn cái khác; lỗi → đánh dấu AutoReplyError để user thấy.
    private async Task<(int drafted, int sent, int failed)> AutoReplyAsync(
        string tenantId, string username, IReadOnlyList<MailItem> newMails,
        MailAutoSyncOptions opt, CancellationToken ct)
    {
        int drafted = 0, sent = 0, failed = 0;
        foreach (var mail in newMails)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(mail.Category) || !opt.ReplyCategories.Contains(mail.Category))
                continue;   // ngoài nhóm áp dụng → bỏ qua

            try
            {
                // Soạn nháp buffered (onDelta no-op). DraftStreamAsync lưu nháp + đổi status 'dang_xu_ly'.
                var req = new DraftReplyRequest(opt.ReplyTone, null, null, null, null);
                var text = await _reply.DraftStreamAsync(tenantId, username, mail, req, _ => Task.CompletedTask, ct);
                if (string.IsNullOrWhiteSpace(text)) throw new Exception("AI trả nháp rỗng");
                drafted++;

                if (opt.ReplyMode == "send")
                {
                    await _sender.SendReplyAsync(tenantId, username, mail, text, ct);
                    _repo.SetStatus(tenantId, mail.Id, "da_phan_hoi");
                    sent++;
                }

                _repo.SetAutoReplyError(tenantId, mail.Id, null);   // thành công → xoá cờ lỗi (nếu có)
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                var prefix = opt.ReplyMode == "send" ? "Gửi tự động lỗi: " : "Soạn tự động lỗi: ";
                _repo.SetAutoReplyError(tenantId, mail.Id, prefix + ex.Message);
                _log.LogWarning("[MailAutoSync] auto-reply mail {Id} ({Mode}) lỗi: {Err}", mail.Id, opt.ReplyMode, ex.Message);
            }
        }
        return (drafted, sent, failed);
    }
}

/// Shape option ĐỘNG của mail-auto-sync (parse từ OptionsJson). Tất cả theo cấu hình per-user.
/// Thêm option mới = thêm field + đọc ở Parse.
public sealed record MailAutoSyncOptions(
    bool AutoReply, string ReplyMode, HashSet<string> ReplyCategories, string ReplyTone)
{
    // Mặc định an toàn: chỉ nhóm "lành" (loại trừ khiếu nại/spam/khác), chế độ soạn-sẵn, tone lịch sự.
    private static readonly string[] DefaultCategories = { "hoi_dat_tour", "xin_bao_gia", "xac_nhan" };

    public static MailAutoSyncOptions Parse(string? json)
    {
        var def = new MailAutoSyncOptions(false, "draft", new HashSet<string>(DefaultCategories), "lich_su");
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var root = d.RootElement;
            var autoReply = GetBool(root, "autoReply");
            var mode = GetStr(root, "replyMode") == "send" ? "send" : "draft";   // mặc định draft (an toàn)
            var tone = GetStr(root, "replyTone") is { Length: > 0 } t ? t : "lich_su";
            HashSet<string> cats;
            if (root.TryGetProperty("replyCategories", out var arr) && arr.ValueKind == JsonValueKind.Array)
                cats = arr.EnumerateArray()
                          .Where(e => e.ValueKind == JsonValueKind.String)
                          .Select(e => e.GetString()!).ToHashSet();
            else
                cats = new HashSet<string>(DefaultCategories);
            return new MailAutoSyncOptions(autoReply, mode, cats, tone);
        }
        catch { return def; }
    }

    private static bool GetBool(JsonElement r, string k)
        => r.TryGetProperty(k, out var v)
           && (v.ValueKind == JsonValueKind.True
               || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static string GetStr(JsonElement r, string k)
        => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
