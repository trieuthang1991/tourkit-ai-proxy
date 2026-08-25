using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh vài lỗi giao diện đã bị người dùng chỉ ra, ở mức mã nguồn. Không thay được việc nhìn
/// bằng mắt, nhưng chặn được việc lặp lại đúng lỗi cũ.
/// </summary>
public class ChatUiGuardTests
{
    [Fact]
    public void O_soan_khong_bi_vien_long_vien()
    {
        // .ci-soan-o đã có viền riêng và đổi màu khi focus. Nếu textarea bên trong lại nhận
        // outline nữa thì thành hai viền lồng nhau.
        var css = ChatSchemaGuardTests.DocFile("wwwroot/styles.css");
        Assert.Contains(".ci-soan-o textarea:focus-visible { outline: none; }", css);
    }

    [Fact]
    public void Moi_o_nhap_khai_kenh_deu_co_placeholder()
    {
        // Ô trống không gợi ý gì thì người khai phải đoán định dạng — nhất là các ô token dài.
        //
        // Đếm token "Hint:" thì HỎNG: gán theo vị trí (đối số thứ 4) cũng là khai placeholder mà
        // không có chữ "Hint:" nào. Phải soi TỪNG dòng khai báo và đếm đối số.
        var src = ChatSchemaGuardTests.DocFile("Endpoints/ChatInboxEndpoints.cs");

        var thieu = new List<string>();
        foreach (Match m in Regex.Matches(src, @"new ONhap\((?<args>[^;]*?)\),?\s*$",
                                          RegexOptions.Multiline))
        {
            var args = m.Groups["args"].Value;
            if (args.Contains("\"note\"")) continue;          // ghi chú, không phải ô nhập

            // Tách đối số ở dấu phẩy NGOÀI chuỗi — nhãn tiếng Việt có dấu phẩy bên trong.
            var soDoiSo = 1; var trongChuoi = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == '"' && (i == 0 || args[i - 1] != '\\')) trongChuoi = !trongChuoi;
                else if (args[i] == ',' && !trongChuoi) soDoiSo++;
            }
            if (soDoiSo < 4) thieu.Add(args.Trim());
        }

        Assert.True(thieu.Count == 0,
            "Các ô sau chưa có placeholder (thiếu đối số thứ 4):\n  " + string.Join("\n  ", thieu));
    }

    [Fact]
    public void Khai_kenh_dung_TAB_chu_khong_do_het_ra_mot_man_hinh()
    {
        // Ba kênh cạnh nhau, mỗi kênh lại bọc thêm một thẻ, trong thẻ lại lồng khối tài khoản →
        // ba lớp viền. Với tab thì mỗi lúc chỉ một kênh trên màn hình, thẻ bao quanh thành thừa.
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.Contains("ci-tab-nut", jsx);
        Assert.DoesNotContain("ci-khai-luoi", jsx);
    }

    [Fact]
    public void Mot_he_nut_duy_nhat_cho_ca_trang_chat()
    {
        // Trước đó bốn cỡ, ba kiểu: .ci-nut-nhom button (viền xám), .btn-primary (cam đặc),
        // .ci-nut-xoa (viền đỏ), nút "Thêm" nhỏ hơn hẳn.
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.DoesNotContain("ci-nut-xoa", jsx);
        Assert.DoesNotContain("btn-primary", jsx);
    }
}
