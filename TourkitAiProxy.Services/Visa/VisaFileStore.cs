namespace TourkitAiProxy.Services.Visa;

/// <summary>
/// Lưu file hồ sơ visa gốc (ảnh/PDF) TẠM THỜI tại data/visa-files/{tenantId}/{assessmentId}/.
/// Tự xóa thư mục cũ hơn RetentionDays (mặc định 7) — lazy purge mỗi lần ghi + lúc khởi động.
/// PII nhạy cảm KHÔNG giữ lâu; thư mục data/ đã gitignored.
///
/// Multi-tenant: path scoped theo tenantId → cross-tenant không đọc được file nhau.
/// </summary>
public class VisaFileStore
{
    private const int RetentionDays = 7;
    private readonly string _root;
    private readonly ILogger<VisaFileStore> _log;
    private readonly object _lock = new();

    public VisaFileStore(IWebHostEnvironment env, ILogger<VisaFileStore> log)
    {
        _log = log;
        _root = Path.Combine(env.ContentRootPath, "data", "visa-files");
        Directory.CreateDirectory(_root);
        Purge();
    }

    /// Lưu file vào data/visa-files/{tenantId}/{assessmentId}/. Trả đường dẫn đĩa.
    public string Save(string tenantId, string assessmentId, int index, string fileName, byte[] bytes)
    {
        var dir = Path.Combine(_root, Safe(tenantId), Safe(assessmentId));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{index:D2}_{Safe(fileName)}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// Xóa toàn bộ file của 1 assessment (khi user xóa lượt thẩm định).
    public void DeleteAssessment(string tenantId, string assessmentId)
    {
        var dir = Path.Combine(_root, Safe(tenantId), Safe(assessmentId));
        TryDeleteDir(dir);
    }

    /// Có file của assessment này không (cross-tenant = false).
    public bool HasFiles(string tenantId, string assessmentId)
        => Directory.Exists(Path.Combine(_root, Safe(tenantId), Safe(assessmentId)));

    /// Dọn file cũ hơn RetentionDays. Global (mọi tenant), gọi lazy mỗi lần upload + startup.
    public void Purge()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(_root)) return;
                var cutoffTicks = DateTime.UtcNow.AddDays(-RetentionDays).Ticks;
                // 2 levels: data/visa-files/{tenant}/{assessment}/
                foreach (var tenantDir in Directory.GetDirectories(_root))
                {
                    foreach (var assDir in Directory.GetDirectories(tenantDir))
                    {
                        if (Directory.GetLastWriteTimeUtc(assDir).Ticks < cutoffTicks)
                            TryDeleteDir(assDir);
                    }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Purge visa-files lỗi"); }
        }
    }

    private void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Xóa thư mục {Dir} lỗi", dir); }
    }

    private static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 80 ? s[..80] : s;
    }
}
