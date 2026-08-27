using System.Text.RegularExpressions;
using TourkitAiProxy.Endpoints;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Chốt chặn cho cờ <c>Features:Chat</c>.
///
/// <para><b>Lỗi này đã xảy ra thật</b> (24/08): thêm <c>/channels</c> và <c>/messages/{id}/file</c>
/// vào hộp thư chat nhưng quên cập nhật nhánh TẮT cờ trong <c>Program.cs</c>. Hậu quả không phải
/// "endpoint không chạy" mà tệ hơn: không map ≠ 404 — <c>app.MapFallback</c> (deep link SPA) nuốt
/// mọi đường không khớp kể cả <c>/api/**</c> và trả <c>index.html</c> kèm status <b>200</b>. Client
/// gọi API nhận về HTML, và lỗi kiểu đó rất khó lần ra.</para>
///
/// <para>⚠️ <b>Từ 26/08/2026 cờ không còn chặn route nữa</b> — quyết định của chủ dự án: chặn cả
/// cụm thì không setup và kiểm thử được trên bản chạy thật. Cờ nay chỉ ẩn mục menu.</para>
///
/// <para>Test này vẫn giữ vì <see cref="ChatInboxEndpoints.OwnedPaths"/> vẫn là bản kiểm kê bề mặt
/// API của cụm chat, và cờ theo-từng-công-ty (nếu làm) sẽ cần đúng danh sách đó. Để nó mục ruỗng
/// rồi mới đi dùng lại là lặp lại y hệt lỗi cũ.</para>
/// </summary>
public class ChatFeatureFlagCoverageTests
{
    private const string Goc = "/api/v1/chat";

    [Fact]
    public void Moi_route_cua_hop_thu_chat_deu_nam_trong_DuongRieng()
    {
        var src = DocMaNguon();

        // Route nhóm: g.MapGet("/conversations", …) → /api/v1/chat/conversations
        var trongNhom = Regex.Matches(src, @"g\.Map(?:Get|Post|Put|Patch|Delete)\(""(/[^""]*)""")
            .Select(m => Goc + m.Groups[1].Value);

        // Route tuyệt đối: routes.MapPost("/api/v1/chat/webhook/…", …)
        var tuyetDoi = Regex.Matches(src, @"routes\.Map(?:Get|Post|Put|Patch|Delete)\(""(/api/v1/chat[^""]*)""")
            .Select(m => m.Groups[1].Value);

        var tatCa = trongNhom.Concat(tuyetDoi).Distinct().ToList();

        // Không có route nào = biểu thức chính quy đã lạc khỏi cách viết thật của file. Im lặng bỏ
        // qua thì test này thành vô dụng mà vẫn xanh — tệ hơn là không có test.
        Assert.NotEmpty(tatCa);

        var thieu = tatCa
            .Where(r => !ChatInboxEndpoints.OwnedPaths.Any(
                p => r.Equals(p, StringComparison.Ordinal) || r.StartsWith(p + "/", StringComparison.Ordinal)))
            .ToList();

        Assert.True(thieu.Count == 0,
            "Các route sau không được ChatInboxEndpoints.OwnedPaths phủ, nên khi tắt Features:Chat "
            + "chúng sẽ rơi vào MapFallback và trả index.html kèm 200 thay vì 404:\n  "
            + string.Join("\n  ", thieu));
    }

    [Fact]
    public void DuongRieng_khong_duoc_chan_nham_Tro_ly_so_lieu()
    {
        // POST /api/v1/chat và /api/v1/chat/stream là Chat-Analytics — tính năng KHÁC, không nằm
        // sau cờ Features:Chat. Nếu ai đó "gọn hoá" danh sách thành tiền tố "/api/v1/chat" trần
        // thì tắt cờ chat sẽ giết luôn Trợ lý số liệu đang chạy thật.
        Assert.DoesNotContain(Goc, ChatInboxEndpoints.OwnedPaths);

        foreach (var duong in new[] { Goc, Goc + "/stream", Goc + "/unresolved" })
        {
            var bịChan = ChatInboxEndpoints.OwnedPaths.Any(
                p => duong.Equals(p, StringComparison.Ordinal) || duong.StartsWith(p + "/", StringComparison.Ordinal));
            Assert.False(bịChan, $"'{duong}' thuộc Trợ lý số liệu nhưng lại bị cờ chat chặn.");
        }
    }

    [Fact]
    public void Co_tat_thi_route_VAN_duoc_map()
    {
        // Chốt cho quyết định 26/08/2026: cờ chỉ ẩn menu. Ai đó "sửa lại cho đúng" bằng cách chặn
        // route trở lại là chặn luôn đường setup trên bản chạy thật — đúng cái đã phải gỡ ra.
        //
        // ⚠️ Cái giá đi kèm, cố ý nhận: tắt cờ giờ chỉ là GIẤU. Webhook vẫn sống nên tin của khách
        // vẫn chảy vào hệ thống dù menu đang ẩn. Muốn tắt THẬT thì bỏ ConnectionStrings:Chat.
        var src = DocFileGoc("TourkitAiProxy.Endpoints/EndpointRegistration.cs");
        var i = src.IndexOf("MapHopThuChat(WebApplication", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy MapHopThuChat");

        // Cắt tới DÒNG TRỐNG đầu tiên, không lấy một cửa sổ ký tự cố định: cửa sổ trùm sang hàm
        // ChanTuongMinh nằm ngay dưới (hàm đó vẫn dùng cho cụm bản tin) rồi báo đỏ oan.
        var het = src.IndexOf("\r\n\r\n", i, System.StringComparison.Ordinal);
        var than = het > i ? src[i..het] : src[i..];

        Assert.Contains("MapChatInboxEndpoints", than);
        Assert.DoesNotContain("ChanTuongMinh", than);
        Assert.DoesNotContain("FeatureFlags.Chat", than);
    }

    private static string DocFileGoc(string duongDanTuongDoi)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        var f = Path.Combine(d!.FullName, duongDanTuongDoi.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(f), $"Không thấy {f}");
        return File.ReadAllText(f);
    }

    private static string DocMaNguon()
    {
        // Đi ngược từ thư mục chạy test lên tới gốc repo — không hardcode đường dẫn tuyệt đối.
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;

        Assert.NotNull(d);
        var f = Path.Combine(d!.FullName, "TourkitAiProxy.Endpoints", "ChatInboxEndpoints.cs");
        Assert.True(File.Exists(f), $"Không thấy {f}");
        return File.ReadAllText(f);
    }
}
