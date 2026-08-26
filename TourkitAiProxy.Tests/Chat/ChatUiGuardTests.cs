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
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

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

    /// <summary>
    /// Không component React nào được khai báo BÊN TRONG một component khác.
    ///
    /// <para><b>Lỗi này đã xảy ra thật</b> (26/08): <c>ONhap</c> — ô nhập của form khai kênh — nằm
    /// trong thân <c>KhaiKenh</c>. Hàm khai bên trong một component là một <b>kiểu component MỚI ở
    /// mỗi lần vẽ lại</b>: React thấy kiểu khác thì tháo cả nhánh cũ rồi dựng nhánh mới, thẻ
    /// <c>input</c> thành một nút DOM khác hẳn, con trỏ nhảy ra ngoài. Gõ một ký tự → đổi state →
    /// vẽ lại → mất focus, người dùng phải bấm lại vào ô sau <b>mỗi chữ cái</b>.</para>
    ///
    /// <para>Triệu chứng là "trang bị đơ, nhập không được", không ai nghĩ tới React — nên phải canh
    /// bằng test chứ không bằng lời dặn.</para>
    /// </summary>
    [Fact]
    public void Khong_khai_component_long_trong_component_khac()
    {
        // Component ở tầng module thụt 2 dấu cách (nằm trong IIFE) — thụt sâu hơn là đang nằm
        // trong thân một component khác.
        //
        // Hai điều kiện, PHẢI có cả hai, vì mỗi cái một mình đều báo oan:
        //   1. thật sự ĐỊNH NGHĨA hàm (không tính `const Page = current.component` — đó là gán lại
        //      một component đã có, kiểu không đổi theo mỗi lần vẽ);
        //   2. có được DÙNG NHƯ THẺ JSX `<Ten` — hàm trợ giúp trả về JSX rồi gọi thẳng `F(...)`
        //      thì không tạo ranh giới component nào, React không tháo dựng lại gì cả.
        var dinhNghia = new Regex(
            @"^\s{4,}(function\s+(?<ten>[A-Z][A-Za-z0-9]*)\s*\("
            + @"|const\s+(?<ten>[A-Z][A-Za-z0-9]*)\s*=\s*(\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>"
            + @"|const\s+(?<ten>[A-Z][A-Za-z0-9]*)\s*=\s*function\b)");

        var pham = new List<string>();
        foreach (var f in DocJsx())
        {
            var noiDung = File.ReadAllText(f);
            var dong = File.ReadAllLines(f);
            for (var i = 0; i < dong.Length; i++)
            {
                var m = dinhNghia.Match(dong[i]);
                if (!m.Success) continue;
                if (!Regex.IsMatch(noiDung, @"<" + Regex.Escape(m.Groups["ten"].Value) + @"[\s/>]")) continue;
                pham.Add($"{Path.GetFileName(f)}:{i + 1} — {dong[i].Trim()}");
            }
        }

        Assert.True(pham.Count == 0,
            "Component React khai bên trong component khác → mỗi lần vẽ lại là một kiểu mới, "
            + "React tháo/dựng lại cả nhánh và ô nhập MẤT FOCUS sau mỗi ký tự. Đưa ra tầng module, "
            + "truyền giá trị qua props:\n  " + string.Join("\n  ", pham));
    }

    private static IEnumerable<string> DocJsx()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);

        var goc = Path.Combine(d!.FullName, "wwwroot");
        // Bỏ dist/ (bản gộp, đã bị minify) và lib/ (thư viện bên thứ ba, không phải mã của mình).
        return Directory.EnumerateFiles(goc, "*.jsx", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}")
                     && !x.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}"));
    }
}
