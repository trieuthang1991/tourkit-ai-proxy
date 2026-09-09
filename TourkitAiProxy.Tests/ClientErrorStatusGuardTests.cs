using System;
using System.Linq;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Lỗi của NGƯỜI GỌI phải trả về mã của người gọi, không được đội lốt lỗi máy chủ.
///
/// <para><b>Chuyện đã xảy ra (09/09/2026).</b> Bộ bắt lỗi cuối đường ép MỌI ngoại lệ thành 500.
/// Nhưng <c>BadHttpRequestException</c> — thân JSON hỏng, thiếu tham số bắt buộc, thân quá lớn —
/// đã mang sẵn mã 400 của nó. Gửi <c>{"userId":null}</c> vào đường giao việc nhận về
/// <c>{"error":"Internal server error"}</c>: người gọi không đoán được mình sai ở đâu, còn đội vận
/// hành thì thấy một lỗi máy chủ không có thật.</para>
///
/// <para>Hai cái giá, không phải một: nói sai với người gọi, VÀ nhét lỗi của người gọi vào nhật ký
/// lỗi máy chủ nên cảnh báo thật bị chìm giữa tiếng ồn.</para>
/// </summary>
public class ClientErrorStatusGuardTests
{
    private static string Nguon()
        => BoChuThich(Chat.ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Logging/GlobalExceptionHandler.cs"));

    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                && !d.TrimStart().StartsWith("*", StringComparison.Ordinal)
                                && !d.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    [Fact]
    public void Phai_ton_trong_ma_ma_ngoai_le_yeu_cau_hong_mang_san()
    {
        var src = Nguon();

        // Canh chính bài canh: phải thấy chỗ gán mã, không thì bài này soi nhầm tệp.
        Assert.Contains("ctx.Response.StatusCode", src);

        Assert.Contains("BadHttpRequestException", src);
        Assert.Contains("StatusCode", src);

        // Gán CỨNG 500 cho mọi ca là quay lại đúng lỗi cũ.
        Assert.DoesNotContain("ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;", src);
    }

    [Fact]
    public void Loi_cua_nguoi_goi_khong_duoc_ghi_vao_nhat_ky_loi_may_chu()
    {
        // Vế thứ hai, tách riêng vì nó hỏng độc lập: trả đúng 400 nhưng vẫn LogError thì nhật ký
        // vẫn đầy lỗi giả, và người trực vẫn bị đánh thức vì chuyện không phải của mình.
        var src = Nguon();
        Assert.Contains("LogWarning", src);

        var iCanh = src.IndexOf("LogWarning", StringComparison.Ordinal);
        var iLoi = src.IndexOf("LogError", StringComparison.Ordinal);
        Assert.True(iLoi > 0, "Vẫn phải giữ LogError cho lỗi máy chủ thật");
        Assert.True(iCanh < iLoi,
            "Nhánh lỗi-người-gọi (LogWarning) phải đứng TRƯỚC nhánh lỗi máy chủ (LogError) — "
            + "viết sau nghĩa là lỗi người gọi vẫn rơi vào LogError trước khi tới được nhánh của nó");
    }
}
