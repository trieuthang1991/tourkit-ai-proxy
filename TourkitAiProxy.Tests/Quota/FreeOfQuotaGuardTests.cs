using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Quota;

/// <summary>
/// Canh giữ một ràng buộc về TIỀN: cờ <c>freeOfQuota</c> chỉ được bật ở ĐÚNG MỘT chỗ.
///
/// <para>Cờ này cho phép gọi AI mà không trừ lượt của công ty. Nó tồn tại cho đúng một việc: hệ
/// thống tự đọc tên trạng thái để dựng cấu hình mặc định — chặn việc đó lại chỉ đổi lấy một bộ lọc
/// vô tác dụng. MỌI tính năng khác (chat, chấm điểm cơ hội, review khách, soạn mail, bản tin…) vẫn
/// phải tính phí bình thường.</para>
///
/// <para>Vì sao cần test chứ không chỉ ghi chú: thêm <c>freeOfQuota: true</c> ở một service khác là
/// việc một dòng, đọc qua code review trông vô hại, và hậu quả — công ty dùng AI miễn phí không giới
/// hạn — không lộ ra ở bất kỳ test chức năng nào. Chỉ hoá đơn cuối tháng mới biết.</para>
///
/// <para>Nếu bạn CỐ Ý mở thêm một chỗ: sửa danh sách <see cref="ChoDuocPhep"/> và ghi rõ lý do vào
/// commit. Test này chặn việc mở rộng NGẦM, không chặn quyết định có cân nhắc.</para>
/// </summary>
public class FreeOfQuotaGuardTests
{
    /// File được phép bật cờ (đường dẫn tương đối tính từ gốc repo, phân cách bằng '/').
    private static readonly string[] ChoDuocPhep =
    {
        "Services/Workflows/StatusSemanticsService.cs",
    };

    /// Nơi ĐỊNH NGHĨA cờ — có chữ "freeOfQuota" nhưng là khai báo tham số, không phải bật.
    private const string NoiDinhNghia = "Services/AiCallContext.cs";

    [Fact]
    public void Chi_MOT_cho_duoc_mien_quota()
    {
        var root = TimGocRepo();
        // Không tìm thấy source (vd chạy từ gói publish) → không kết luận được, nhưng cũng KHÔNG
        // được báo xanh: một test canh giữ tiền mà im lặng bỏ qua thì vô dụng đúng lúc cần nhất.
        Assert.True(root != null, "Không tìm thấy gốc repo — test này cần đọc source.");

        var viPham = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "Services"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root!, file).Replace('\\', '/');
            if (rel == NoiDinhNghia || ChoDuocPhep.Contains(rel)) continue;

            // Bắt cả "freeOfQuota: true" lẫn "freeOfQuota:true" và biến thể khoảng trắng.
            if (Regex.IsMatch(File.ReadAllText(file), @"freeOfQuota\s*:\s*true"))
                viPham.Add(rel);
        }

        Assert.True(viPham.Count == 0,
            "Có file mới bật miễn quota ngoài danh sách cho phép: " + string.Join(", ", viPham) +
            ". Miễn quota nghĩa là công ty dùng AI không trừ lượt — nếu cố ý thì thêm vào ChoDuocPhep kèm lý do.");
    }

    /// Chỗ được phép cũng phải giữ đúng chốt chặn: chỉ miễn cho lần hỏi TỰ ĐỘNG, còn người dùng
    /// bấm "Phân loại lại" thì tính phí. Bỏ điều kiện này đi là mở cửa bấm liên tục.
    [Fact]
    public void Cho_duoc_phep_van_tinh_phi_khi_nguoi_dung_chu_dong_chay_lai()
    {
        var root = TimGocRepo();
        Assert.True(root != null, "Không tìm thấy gốc repo — test này cần đọc source.");

        var src = File.ReadAllText(Path.Combine(root!, "Services/Workflows/StatusSemanticsService.cs"));
        Assert.Contains("freeOfQuota: !forceRefresh", src);
    }

    /// Đi ngược từ thư mục chạy test lên tới thư mục có TourkitAiProxy.csproj.
    private static string? TimGocRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TourkitAiProxy.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
