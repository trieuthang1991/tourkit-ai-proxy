using System.Text.Json;
using TourkitAiProxy.Services.Digest.Channels;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Gửi các dòng hàng đợi kênh NGOÀI-EMAIL (telegram/zalo) đã đến hạn. Email là việc của worker
/// bên toutkit-app (nó poll <c>Channel=0</c>) — hai bên không giẫm nhau nhờ cột Channel.
///
/// <para>ĐỢT NÀY KHÔNG THỬ LẠI (quyết định 13/08): gửi hỏng → <c>Status=2</c> + ErrorMessage +
/// log ERROR, dòng nằm lại để còn tra. Chính sách thử lại thiết kế riêng sau — khi đó chỉ cần
/// thêm chỗ lật 2→0, phần còn lại giữ nguyên.</para>
///
/// <para>Nội dung đọc lại từ <c>dbo.AgentInsights</c> qua <c>SourceId</c> (một nguồn duy nhất,
/// không nhân bản nội dung vào từng dòng hàng đợi); token OA/bot resolve NGAY LÚC GỬI.</para>
/// </summary>
public class OutboundChannelDrainer : BackgroundService
{
    private readonly MailQueueRepository _queue;
    private readonly InsightRepository _insights;
    private readonly TelegramChannel _telegram;
    private readonly ZaloOaChannel _zalo;
    private readonly ILogger<OutboundChannelDrainer> _log;

    public OutboundChannelDrainer(MailQueueRepository queue, InsightRepository insights,
        TelegramChannel telegram, ZaloOaChannel zalo, ILogger<OutboundChannelDrainer> log)
    { _queue = queue; _insights = insights; _telegram = telegram; _zalo = zalo; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken st)
    {
        _log.LogInformation("[digest/drainer] khởi động — nhịp 60s, chỉ kênh ngoài-email");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(st).ConfigureAwait(false))
        {
            try { await DrainOnceAsync(st); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[digest/drainer] một nhịp lỗi (không thoát vòng lặp)"); }
        }
    }

    internal async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        var due = await _queue.ListDueNonEmailAsync(100, ct);
        if (due.Count == 0) return 0;

        var sent = 0;
        foreach (var row in due)
        {
            ct.ThrowIfCancellationRequested();
            bool ok = false; string? err = null;
            try
            {
                var (title, body) = await ResolveContentAsync(row, ct);
                ok = (OutboundChannel)row.Channel switch
                {
                    OutboundChannel.Telegram => await _telegram.SendToChatAsync(
                        Addr(row.Data, "chatId"), title, body, row.TenantId, row.Username ?? "", ct),
                    OutboundChannel.Zalo => await _zalo.SendToUserAsync(
                        row.TenantId, Addr(row.Data, "zaloUserId"), title, body, row.Username ?? "", ct),
                    // Kênh lạ (enum hai bên lệch phiên bản?) → cho hỏng ra mặt, không nuốt im.
                    _ => false,
                };
                if (!ok) err ??= "kênh trả false — xem log Warning cùng thời điểm";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { err = ex.Message; }

            await _queue.MarkProcessedAsync(row.Id, ok, err, ct);
            if (ok) sent++;
            else
                _log.LogError("[digest/drainer] GỬI HỎNG dòng={Id} kênh={Ch} tenant={T} user={U}: {Err}",
                    row.Id, (OutboundChannel)row.Channel, row.TenantId, row.Username, err);
        }

        _log.LogInformation("[digest/drainer] xử {Total} dòng đến hạn — gửi được {Sent}, hỏng {Failed}",
            due.Count, sent, due.Count - sent);
        return sent;
    }

    /// Nội dung: ưu tiên bản tin trong AgentInsights qua SourceId (một nguồn); thiếu thì rơi về
    /// Subject của chính dòng hàng đợi để vẫn gửi được cái gì đó thay vì im lặng.
    private async Task<(string Title, string Body)> ResolveContentAsync(OutboundMail row, CancellationToken ct)
    {
        if (long.TryParse(row.SourceId, out var insightId))
        {
            var ins = await _insights.GetAsync(row.TenantId, insightId, ct);
            if (ins != null) return (ins.Title, ins.Body);
        }
        return (row.Subject ?? "(bản tin)", "");
    }

    private static string Addr(string? dataJson, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson);
            return doc.RootElement.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }
}
