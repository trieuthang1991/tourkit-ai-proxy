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
        "TourkitAiProxy.Services/Workflows/StatusSemanticsService.cs",
        // Key AI riêng của công ty (BYO, chủ dự án duyệt 03/10/2026): lệnh chạy bằng key CỦA KHÁCH thì
        // khách đã tự trả tiền nhà cung cấp, trừ lượt nữa là thu hai lần. Chốt chặn riêng ở
        // Byo_chi_mien_khi_key_cua_lenh_KHOP_key_khach bên dưới.
        "TourkitAiProxy.Services/AiKeys/ByoAwareProvider.cs",
        // Kiểm key BYO trước khi lưu: một lệnh gọi thử bằng key KHÁCH VỪA NHẬP. Chốt chặn riêng ở
        // Kiem_key_chan_key_rong_TRUOC_khi_mien_luot bên dưới.
        "TourkitAiProxy.Services/AiKeys/TenantAiKeyValidator.cs",
    };

    /// Nơi ĐỊNH NGHĨA cờ — có chữ "freeOfQuota" nhưng là khai báo tham số, không phải bật.
    private const string NoiDinhNghia = "TourkitAiProxy.Services/AiCallContext.cs";

    [Fact]
    public void Chi_MOT_cho_duoc_mien_quota()
    {
        var root = TimGocRepo();
        // Không tìm thấy source (vd chạy từ gói publish) → không kết luận được, nhưng cũng KHÔNG
        // được báo xanh: một test canh giữ tiền mà im lặng bỏ qua thì vô dụng đúng lúc cần nhất.
        Assert.True(root != null, "Không tìm thấy gốc repo — test này cần đọc source.");

        var viPham = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "TourkitAiProxy.Services"), "*.cs", SearchOption.AllDirectories))
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

        var src = File.ReadAllText(Path.Combine(root!, "TourkitAiProxy.Services/Workflows/StatusSemanticsService.cs"));
        Assert.Contains("freeOfQuota: !forceRefresh", src);
    }

    /// <summary>
    /// Chỗ miễn lượt của BYO phải miễn THEO TỪNG LỆNH — chỉ khi key của lệnh KHỚP ĐÚNG key riêng
    /// của công ty — không phải "công ty có key riêng thì miễn cả công ty".
    ///
    /// <para>Cái lỗ có thật (tìm ra 03/10/2026): hai đường gọi công cụ của Anthropic (trợ lý số liệu,
    /// tác vụ tự động) lấy THẲNG key hệ thống, không qua AiModelRegistry. Miễn theo công ty thì hai
    /// đường đó gọi bằng key hệ thống mà không trừ lượt — nền tảng trả AI hộ khách, miễn phí. Bỏ phép
    /// so khớp key dưới đây là mở lại đúng cái lỗ đó.</para>
    /// </summary>
    [Fact]
    public void Byo_chi_mien_khi_key_cua_lenh_KHOP_key_khach()
    {
        var root = TimGocRepo();
        Assert.True(root != null, "Không tìm thấy gốc repo — test này cần đọc source.");

        // Phép so khớp nằm ở MỘT chỗ duy nhất…
        var store = File.ReadAllText(Path.Combine(root!, "TourkitAiProxy.Services/AiKeys/TenantAiKeyStore.cs"));
        Assert.Contains("string.Equals(apiKey, d.ApiKey", store);

        // …và lớp bọc phải dùng đúng phép đó, không tự hỏi "công ty có BYO không".
        var boc = File.ReadAllText(Path.Combine(root!, "TourkitAiProxy.Services/AiKeys/ByoAwareProvider.cs"));
        Assert.Contains("IsTenantKey(tenant, req.ApiKey)", boc);
    }

    /// <summary>
    /// Đường gọi công cụ Anthropic (trợ lý số liệu) miễn lượt KHÔNG bằng chữ <c>freeOfQuota: true</c>
    /// mà bằng điều kiện <c>!laKeyKhach</c> ngay tại chỗ trừ lượt — tức guard quét chữ ở trên KHÔNG
    /// nhìn thấy nó. Khoá riêng ở đây: miễn lượt phải xuất phát từ phép so khớp key, không gì khác.
    ///
    /// <para>Vì sao đường này phải miễn: nó gọi Anthropic trực tiếp, không qua lớp bọc. Công ty khai key
    /// Claude thì ChatAgentService truyền key khách xuống đây — không miễn là thu hai lần (lỗi có thật,
    /// tìm ra 03/10/2026).</para>
    /// </summary>
    [Fact]
    public void Duong_cong_cu_Anthropic_chi_mien_khi_KHOP_key_khach()
    {
        var root = TimGocRepo();
        Assert.True(root != null, "Không tìm thấy gốc repo — test này cần đọc source.");

        var src = File.ReadAllText(Path.Combine(root!, "TourkitAiProxy.Services/Chat/NativeToolUseAgent.cs"));
        Assert.Contains("var laKeyKhach = _byo.IsTenantKey(callCtx.Tenant, apiKey);", src);
        // Cả hai chỗ trừ lượt (lượt chính + lượt thử lại) đều phải kiểm.
        Assert.Equal(2, Regex.Matches(src, @"!laKeyKhach && !string\.IsNullOrEmpty\(callCtx\.Tenant\)\) _quota\.Consume").Count);
    }

    /// <summary>
    /// Bộ kiểm key miễn lượt cho lệnh gọi thử — đúng, vì nó chạy bằng key khách vừa nhập. Nhưng KEY RỖNG
    /// thì nhà cung cấp tự lùi về key HỆ THỐNG (ProviderKeyStore), tức lệnh miễn lượt đó chạy bằng tiền
    /// của nền tảng. Nên phải chặn key rỗng TRƯỚC chỗ bật miễn lượt — và phải tắt lớp bọc (Bypass), không
    /// thì kiểm lại đúng key đang lưu mà đã hỏng sẽ bị lùi sang key hệ thống và báo "hợp lệ".
    /// </summary>
    [Fact]
    public void Kiem_key_chan_key_rong_TRUOC_khi_mien_luot()
    {
        var root = TimGocRepo();
        Assert.True(root != null, "Không tìm thấy gốc repo — test này cần đọc source.");

        var src = File.ReadAllText(Path.Combine(root!, "TourkitAiProxy.Services/AiKeys/TenantAiKeyValidator.cs"));
        var chanRong = src.IndexOf("if (string.IsNullOrWhiteSpace(apiKey)) return", StringComparison.Ordinal);
        var mienLuot = src.IndexOf("freeOfQuota: true", StringComparison.Ordinal);
        Assert.True(chanRong > 0, "Bộ kiểm key phải chặn key rỗng.");
        Assert.True(mienLuot > chanRong, "Phải chặn key rỗng TRƯỚC chỗ bật miễn lượt.");
        Assert.Contains("ByoAwareProvider.Bypass()", src);
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
