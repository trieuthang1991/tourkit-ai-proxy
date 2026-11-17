using System;
using System.Linq;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Lọc theo <b>nguồn</b> phải có mặt ở <b>CẢ HAI</b> câu: liệt kê VÀ đếm — đúng luật đã chốt cho
/// lọc theo kênh (28/08/2026) rồi cho lọc theo nhãn (xem <see cref="ChatTagFilterGuardTests"/>).
///
/// <para>Chip trạng thái đứng ngay trên danh sách. Lọc mà chỉ sửa câu liệt kê thì chip đếm cả
/// công ty trong khi danh sách chỉ hiện một nguồn: hai con số mâu thuẫn nhau, cạnh nhau, trên
/// cùng một màn hình, và không có gì nói là đang sai.</para>
/// </summary>
public class ChatSourceFilterGuardTests
{
    private static string Kho()
        => BoChuThich(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs"));

    private static string DiemCuoi()
        => BoChuThich(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs"));

    /// <summary>Bỏ dòng chú thích để chốt soi vào mã thật, không soi vào lời hứa trong chú thích.</summary>
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                && !d.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [Theory]
    [InlineData("public async Task<List<ChatConversation>> ListConversationsAsync(")]
    [InlineData("public async Task<ChatInboxCounts> CountAsync(")]
    public void Ca_liet_ke_lan_dem_deu_nhan_bo_loc_nguon(string ham)
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Kho(), ham);
        Assert.Contains("NguonLoc? nguon", than);
    }

    /// <summary>
    /// Câu liệt kê phải kẹp bằng cờ RIÊNG, không bằng "mảng rỗng thì thôi": mảng rỗng ở đây có
    /// nghĩa thật — nguồn không khớp Trang nào thì danh sách phải TRỐNG. Suy từ mảng rỗng là bấm
    /// tab rồi thấy nguyên cả hộp thư.
    /// </summary>
    [Fact]
    public void Cau_liet_ke_kep_bang_co_rieng_chu_khong_suy_tu_mang_rong()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Kho(),
            "public async Task<List<ChatConversation>> ListConversationsAsync(");

        Assert.Contains("NOT @locNguon", than);
        Assert.Contains("v.channel = ANY(@nguonKenh::smallint[])", than);
        Assert.Contains("v.account_id = ANY(@nguonTaiKhoan::text[])", than);
        Assert.Contains("locNguon = nguon is not null", than);
    }

    /// <summary>
    /// Điểm cuối phải truyền bộ lọc nguồn xuống <b>cả hai</b> lượt gọi. Đây chính là chỗ hỏng của
    /// lọc nhãn ngày trước: kho đã nhận tham số, điểm cuối quên truyền cho câu đếm.
    /// </summary>
    [Fact]
    public void Diem_cuoi_truyen_nguon_cho_ca_hai_luot_goi()
    {
        var src = DiemCuoi();

        Assert.Contains("nhan: nhanLoc, taiKhoan: accountId, nguon: nguonLoc", src);
        Assert.Contains("nhan: nhanLoc, nguon: nguonLoc", src);
    }

    /// <summary>
    /// Dải tab đọc nguồn từ máy chủ, không tự quy đổi: giao diện giữ thêm một bảng kênh → nguồn là
    /// có hai bản sự thật, rồi tab đếm một đằng danh sách lọc ra một nẻo.
    /// </summary>
    [Fact]
    public void May_chu_tra_san_bo_dem_theo_nguon_va_danh_sach_nguon()
    {
        var src = DiemCuoi();

        Assert.Contains("sourceCounts = ChatSourceMap.Dem(", src);
        Assert.Contains("sources = ChatSourceMap.DaKhai(", src);
        Assert.Contains("accountSources = ChatSourceMap.TheoTaiKhoan(", src);
    }

    /// <summary>
    /// Bản đồ tài khoản đọc ĐÚNG MỘT lượt cho cả trang danh sách. Trước 10/11/2026 chỗ này đọc hai
    /// lượt (tên Trang, rồi nền tảng) trên cùng những dòng đó — mà hộp thư hỏi lại 4 giây một lần.
    /// </summary>
    [Fact]
    public void Chi_doc_ban_do_tai_khoan_mot_luot_moi_request()
    {
        var src = DiemCuoi();

        Assert.DoesNotContain("LabelMapAsync", src);
        Assert.DoesNotContain("PlatformMapAsync", src);
        Assert.Equal(2, src.Split("MoTaTaiKhoanAsync").Length - 1);   // danh sách + chi tiết
    }
}
