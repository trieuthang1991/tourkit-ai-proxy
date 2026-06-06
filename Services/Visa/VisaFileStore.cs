namespace TourkitAiProxy.Services.Visa;

/// Lưu file hồ sơ visa gốc (ảnh/PDF) TẠM THỜI tại data/visa-files/{assessmentId}/.
/// Tự xóa thư mục cũ hơn RetentionDays (mặc định 7) — lazy purge mỗi lần ghi + lúc khởi động.
/// PII nhạy cảm (hộ chiếu, sao kê) KHÔNG giữ lâu; thư mục data/ đã gitignored.
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
        Purge();   // dọn rác cũ lúc khởi động
    }

    /// Lưu 1 file vào thư mục của assessment. Trả đường dẫn đĩa.
    public string Save(string assessmentId, int index, string fileName, byte[] bytes)
    {
        var dir = Path.Combine(_root, Safe(assessmentId));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{index:D2}_{Safe(fileName)}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// Xóa toàn bộ file của 1 assessment (khi user xóa lượt thẩm định).
    public void DeleteAssessment(string assessmentId)
    {
        var dir = Path.Combine(_root, Safe(assessmentId));
        TryDeleteDir(dir);
    }

    /// Dọn các thư mục cũ hơn RetentionDays. Gọi lazy mỗi lần upload.
    public void Purge()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(_root)) return;
                var cutoffTicks = DateTime.UtcNow.AddDays(-RetentionDays).Ticks;
                foreach (var dir in Directory.GetDirectories(_root))
                {
                    // dùng mtime thư mục; cũ hơn cutoff → xóa
                    if (Directory.GetLastWriteTimeUtc(dir).Ticks < cutoffTicks)
                        TryDeleteDir(dir);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Purge visa-files lỗi"); }
        }
    }

    /// Id thư mục đã quá hạn (FilesPurged) — để đánh dấu trong repo nếu cần.
    public bool HasFiles(string assessmentId)
        => Directory.Exists(Path.Combine(_root, Safe(assessmentId)));

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
