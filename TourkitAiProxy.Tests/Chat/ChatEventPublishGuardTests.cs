using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Mọi lệnh <c>bus.Publish(...)</c> / <c>_bus.Publish(...)</c> đều phải điền <c>AssignedUserId</c>.
///
/// <para><b>Không dùng <c>required</c> trên <c>ChatEvent.AssignedUserId</c></b> (quyết định review
/// Task 4): triển khai cuốn chiếu thì instance bản CŨ vẫn phát sự kiện thiếu trường đó qua Redis,
/// và <c>Deserialize</c> ở bản MỚI sẽ ném vì thiếu thành viên bắt buộc — hỏng đúng đường mà trường
/// này sinh ra để phục vụ. Guard đọc mã nguồn ở đây là lưới an toàn còn lại: một lệnh
/// <c>Publish</c> MỚI quên điền sẽ bị chặn ngay ở CI, thay vì lộ ra bằng một chuông báo không tới
/// của một nhân viên nào đó, hàng tuần sau, không ai biết vì sao.</para>
/// </summary>
public class ChatEventPublishGuardTests
{
    private static readonly string[] TapTin =
    {
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs",
        "TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs",
        "TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs",
    };

    [Fact]
    public void Moi_lenh_Publish_deu_dien_AssignedUserId()
    {
        var viPham = new List<string>();
        var tongSo = 0;

        foreach (var duongDan in TapTin)
        {
            var src = ChatSchemaGuardTests.DocFile(duongDan);
            foreach (Match m in Regex.Matches(src, @"_?bus\.Publish\("))
            {
                tongSo++;
                // Vị trí dấu "(" mở đầu là ký tự cuối của chuỗi khớp.
                var viTriMo = m.Index + m.Value.Length - 1;
                var than = CauLenhCoNgoacCanBang(src, viTriMo);
                if (!than.Contains("AssignedUserId"))
                {
                    var dong = src.Take(m.Index).Count(c => c == '\n') + 1;
                    viPham.Add($"{duongDan}:{dong}");
                }
            }
        }

        // Canary chống biểu thức lạc: nếu regex thôi khớp (đổi tên biến bus, đổi cách gọi…) thì
        // tongSo tụt về gần 0 và dòng dưới hét lên, thay vì lặng lẽ báo "0 vi phạm" giả.
        Assert.True(tongSo >= 20,
            $"Chỉ thấy {tongSo} lệnh Publish trong 3 file — biểu thức đã lạc khỏi cách viết thật");

        Assert.True(viPham.Count == 0,
            "Lệnh Publish thiếu AssignedUserId — sự kiện đó sẽ mang null và bị GIẤU với mọi " +
            "nhân viên không phải admin: " + string.Join(", ", viPham));
    }

    /// <summary>
    /// Cắt nguyên văn CẢ câu lệnh <c>Publish(...)</c>, tính cả ngoặc lồng bên trong (như
    /// <c>new(...)</c>) — dừng đúng ở dấu <c>)</c> khớp với dấu <c>(</c> ở <paramref name="viTriMo"/>,
    /// không phải dấu <c>)</c> đầu tiên gặp được. Cần vậy vì <c>AssignedUserId = ...</c> thường nằm
    /// trong object-initializer <c>{ }</c> SAU dấu đóng ngoặc của <c>new(...)</c> nhưng vẫn TRƯỚC
    /// dấu đóng ngoặc của chính <c>Publish(</c>.
    /// </summary>
    private static string CauLenhCoNgoacCanBang(string src, int viTriMo)
    {
        var doSau = 0;
        for (var i = viTriMo; i < src.Length; i++)
        {
            if (src[i] == '(') doSau++;
            else if (src[i] == ')')
            {
                doSau--;
                if (doSau == 0) return src.Substring(viTriMo, i - viTriMo + 1);
            }
        }
        throw new InvalidOperationException("Không tìm thấy dấu đóng ngoặc khớp cho Publish(");
    }
}
