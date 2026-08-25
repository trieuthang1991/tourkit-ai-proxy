// Services/Db/MultiTenantMigration.cs
namespace TourkitAiProxy.Services.Db;

/// <summary>
/// Backup legacy single-tenant data files lúc deploy chuyển sang multi-tenant DB.
/// Move data/{mails,mail-account,mail-sync,visa-assessments}.json + data/visa-files/
/// vào data/legacy-backup/{yyyy-MM-dd-HHmmss}/ (UTC). Gọi 1 lần từ Program.cs sau app.Build().
///
/// Idempotent trên clean runs — gọi nhiều lần OK (lần sau không có legacy → noop).
/// Partial-failure: nếu Move throw giữa chừng → log + re-throw (fail-loud), startup abort.
/// Admin xử lý partial state ở backup folder rồi restart, tránh split-brain across multiple backups.
///
/// Sync (chỉ move file metadata) — không cần fire-and-forget.
/// </summary>
public static class MultiTenantMigration
{
    private static readonly string[] LegacyFiles =
    {
        "mails.json", "mail-account.json", "mail-sync.json", "visa-assessments.json"
    };
    private static readonly string[] LegacyFolders = { "visa-files" };

    public static void Run(string dataDir, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir)) return;

        bool hasLegacy = LegacyFiles.Any(f => File.Exists(Path.Combine(dataDir, f)))
            || LegacyFolders.Any(d =>
                Directory.Exists(Path.Combine(dataDir, d))
                && Directory.EnumerateFileSystemEntries(Path.Combine(dataDir, d)).Any());
        if (!hasLegacy) return;

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
        var backupRoot = Path.Combine(dataDir, "legacy-backup", ts);
        Directory.CreateDirectory(backupRoot);

        try
        {
            foreach (var f in LegacyFiles)
            {
                var src = Path.Combine(dataDir, f);
                if (File.Exists(src)) File.Move(src, Path.Combine(backupRoot, f));
            }
            foreach (var d in LegacyFolders)
            {
                var src = Path.Combine(dataDir, d);
                if (Directory.Exists(src)) Directory.Move(src, Path.Combine(backupRoot, d));
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "[multi-tenant migration] Move thất bại giữa chừng. Một số file đã ở {Backup}, " +
                "phần còn lại vẫn ở {Data}. Admin cần xử lý thủ công trước khi restart " +
                "(move file còn lại vào backup hoặc revert tất cả).",
                backupRoot, dataDir);
            throw;
        }

        log.LogWarning("[multi-tenant migration] Backed up legacy single-tenant data to {Path}. " +
                       "Mail/Visa now require login + per-tenant setup. " +
                       "Rollback: stop proxy, move files back, revert deploy.", backupRoot);
    }
}
