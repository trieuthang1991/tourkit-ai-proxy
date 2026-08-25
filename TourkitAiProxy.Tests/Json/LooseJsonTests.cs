using System.Text.Json;
using TourkitAiProxy.Shared.Json;
using Xunit;

namespace TourkitAiProxy.Tests.Json;

/// <summary>
/// <see cref="LooseJson"/> là chỗ DUY NHẤT bóc JSON ra khỏi văn bản AI, nên mọi tính năng dùng AI
/// đều đi qua đây. Nó không có test nào cho tới 25/08/2026 — và đúng ngày đó một lỗi thật lộ ra
/// trên staging vì cái nó bỏ sót.
/// </summary>
public class LooseJsonTests
{
    // ── Ca cơ bản ────────────────────────────────────────────────────────────

    [Fact]
    public void Boc_duoc_json_tran()
    {
        Assert.Equal("""{"a":1}""", LooseJson.ExtractFirstObject("""{"a":1}"""));
    }

    [Fact]
    public void Boc_duoc_json_trong_rao_markdown()
    {
        var raw = "```json\n{\"a\":1}\n```";
        Assert.Equal("""{"a":1}""", LooseJson.ExtractFirstObject(raw));
    }

    [Fact]
    public void Boc_duoc_json_co_prose_bao_quanh()
    {
        var raw = "Đây là kết quả:\n{\"open\":[1,2]}\nHy vọng giúp được bạn.";
        Assert.Equal("""{"open":[1,2]}""", LooseJson.ExtractFirstObject(raw));
    }

    [Fact]
    public void Ngoac_nhon_trong_chuoi_khong_lam_lech_do_sau()
    {
        var raw = """{"ghiChu":"dùng { và } trong tên","ma":7}""";
        var json = LooseJson.ExtractFirstObject(raw);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal(7, doc.RootElement.GetProperty("ma").GetInt32());
    }

    [Fact]
    public void Khong_co_gi_thi_tra_null()
    {
        Assert.Null(LooseJson.ExtractFirstObject("không có json ở đây"));
        Assert.Null(LooseJson.ExtractFirstObject(""));
        Assert.Null(LooseJson.ExtractFirstObject("   "));
    }

    // ── Ca đã hỏng thật trên staging 25/08/2026 ──────────────────────────────

    /// <summary>
    /// <b>Lỗi thật.</b> Model suy luận (DeepSeek/Kimi…) chảy nội tâm ra trước câu trả lời, và đoạn
    /// nghĩ đó có chứa dấu <c>{</c>. Bản cũ lấy dấu <c>{</c> ĐẦU TIÊN nên cắt trúng đoạn nghĩ, trả
    /// về rác, rồi <c>JsonDocument.Parse</c> ném ở tận nơi gọi.
    ///
    /// <para>Nguyên văn bắt được trên staging (gợi ý trạng thái deal):
    /// <c>{'. Need decide each.\n\nWe have statuses: 1 Tạo mới, …</c></para>
    ///
    /// <para>Hậu quả không phải màn hình lỗi mà là <b>tính năng lặng lẽ biến mất</b>: người dùng mở
    /// form cấu hình bản tin thì không thấy gợi ý nào, bấm lại vẫn không có, và log chỉ nói "AI lỗi".
    /// Dự án ĐÃ biết kiểu hỏng này — có hẳn ca E2E <c>feat-chat-07-khong-ro-ri-suy-nghi</c> cho
    /// luồng chat — nhưng chỗ bóc JSON dùng chung thì chưa chống.</para>
    /// </summary>
    [Fact]
    public void Bo_qua_ngoac_nam_trong_doan_AI_tu_nghi()
    {
        var raw = "{'. Need decide each.\n\nWe have statuses: 1 Tạo mới, 2 Chờ xử lý.\n"
                + "Vậy kết quả là:\n{\"open\":[1,2],\"closed\":[5,6]}";

        var json = LooseJson.ExtractFirstObject(raw);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);   // KHÔNG được ném
        Assert.Equal(2, doc.RootElement.GetProperty("open").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("closed").GetArrayLength());
    }

    /// <summary>Cùng bẫy, cho hàm bóc mảng (dùng ở nhập giá NCC, bóc danh sách khách…).</summary>
    [Fact]
    public void Bo_qua_ngoac_rac_khi_boc_mang()
    {
        var raw = "Hmm, [ chưa chắc. Để tôi nghĩ lại.\nKết quả:\n[{\"ten\":\"Hà Nội\"}]";

        var json = LooseJson.ExtractFirstArrayOrObject(raw);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);   // KHÔNG được ném
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    /// <summary>
    /// Không tìm được khối nào parse nổi thì vẫn trả về ứng viên đầu — giữ nguyên hành vi cũ để
    /// nơi gọi tự quyết. Đổi thành <c>null</c> ở đây là âm thầm đổi ý nghĩa của mọi nơi đang dùng.
    /// </summary>
    [Fact]
    public void Khong_khoi_nao_hop_le_thi_van_tra_ung_vien_dau()
    {
        var raw = "{ hỏng bét";
        Assert.NotNull(LooseJson.ExtractFirstObject(raw));
    }

    /// <summary>Object lồng nhau: phải lấy TRỌN khối ngoài, không dừng ở dấu đóng đầu tiên.</summary>
    [Fact]
    public void Lay_tron_object_long_nhau()
    {
        var raw = """{"ngoai":{"trong":1},"sau":2}""";
        var json = LooseJson.ExtractFirstObject(raw);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal(2, doc.RootElement.GetProperty("sau").GetInt32());
    }
}
