using System;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Đường <c>POST /channels/pancake/refresh-source</c> — nhận lại <b>nguồn</b> cho những Trang nối
/// từ trước, khi nguồn chưa từng được ghi.
///
/// <para>Nó tồn tại vì cách chữa hiển nhiên — nối lại Trang — <b>nguy hiểm</b>: luồng nối có thể
/// sinh khoá mới cho Trang, mà sinh khoá mới là HUỶ khoá cũ, giết luôn mọi tích hợp khác đang
/// dùng khoá đó. Đường này chỉ ĐỌC qua khoá đã lưu rồi ghi đúng một trường.</para>
///
/// <para>Ba thứ dưới đây là lý do nó an toàn. Mất thứ nào thì nút "Nhận lại nguồn" từ chỗ chữa
/// thành chỗ phá, nên chốt bằng mã chứ không bằng lời hứa trong chú thích.</para>
/// </summary>
public class PancakeRefreshSourceGuardTests
{
    /// <summary>Cắt đúng thân route, không trèo sang route kế.</summary>
    private static string Than()
    {
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var i = src.IndexOf("g.MapPost(\"/channels/pancake/refresh-source\"", StringComparison.Ordinal);
        Assert.True(i >= 0, "Không thấy đường /channels/pancake/refresh-source");
        var j = src.IndexOf("g.MapPost(\"/channels/pancake/connect\"", i, StringComparison.Ordinal);
        Assert.True(j > i, "Không thấy mốc kết thúc — route kế đã đổi tên?");
        return src[i..j];
    }

    /// <summary>
    /// <b>KHÔNG được sinh khoá mới.</b> Sinh khoá là huỷ khoá cũ của Trang đó — tích hợp nào khác
    /// đang dùng nó sẽ chết ngay, mà người bấm nút chỉ định xem Trang này thuộc Facebook hay Zalo.
    /// </summary>
    [Fact]
    public void Khong_duoc_sinh_khoa_moi()
        => Assert.DoesNotContain("GeneratePageTokenAsync", Than());

    /// <summary>
    /// <b>KHÔNG được đè tên gợi nhớ.</b> Tên là chữ người khai tự đặt; đè lên là họ phải gõ lại
    /// toàn bộ, và tệ hơn: không có gì trên màn hình nói là vừa bị đè.
    /// </summary>
    [Fact]
    public void Khong_duoc_de_ten_goi_nho()
    {
        var than = Than();
        Assert.DoesNotContain("[\"label\"]", than);
        Assert.DoesNotContain("o[\"label\"]", than);
        // Chỉ đúng MỘT trường được ghi.
        Assert.Contains("o[\"platform\"] = nenTang;", than);
    }

    /// <summary>
    /// <b>Hỏi hụt thì GIỮ NGUYÊN nguồn đang có.</b> Một lượt mạng chập chờn mà xoá sạch thì cả hộp
    /// thư quay về "Pancake" — tệ hơn hẳn lúc chưa bấm.
    /// </summary>
    [Fact]
    public void Hoi_hut_thi_giu_nguyen_chu_khong_xoa()
    {
        var than = Than();
        var iHut = than.IndexOf("string.IsNullOrWhiteSpace(nenTang)", StringComparison.Ordinal);
        var iGhi = than.IndexOf("SaveAsync", StringComparison.Ordinal);
        Assert.True(iHut >= 0, "Không thấy vế chặn khi Pancake không trả nguồn");
        Assert.True(iHut < iGhi, "Vế chặn phải đứng TRƯỚC lượt ghi, không thì hụt vẫn ghi đè");
        Assert.Contains("continue;", than[iHut..iGhi]);
    }

    /// <summary>
    /// Ghi phải chép lại MỌI khoá cũ: <c>SaveAsync</c> thay cả khối, nên dựng một từ điển chỉ có
    /// <c>platform</c> là Trang mất khoá của chính nó và mất cả chuỗi bí mật webhook.
    /// </summary>
    [Fact]
    public void Phai_chep_lai_moi_khoa_cu_truoc_khi_ghi()
        => Assert.Contains("t.GiaTri.ToDictionary(", Than());

    /// <summary>Đổi cấu hình kênh là việc của quản trị, không phải của nhân viên trực.</summary>
    [Fact]
    public void Phai_doi_quyen_cau_hinh_he_thong()
        => Assert.Contains("CanConfigSystemAsync", Than());
}
