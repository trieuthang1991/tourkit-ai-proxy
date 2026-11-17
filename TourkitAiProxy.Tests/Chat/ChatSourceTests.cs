using System;
using System.Collections.Generic;
using System.Linq;
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// <b>Nguồn</b> — nơi khách thật sự nhắn từ đó — khác với <b>kênh</b> là đường tin đi vào.
///
/// <para>Triệu chứng chủ dự án báo 10/11/2026: hộp thư nhận hộ qua Pancake nên mọi hội thoại đổ
/// về một tab duy nhất tên "Pancake", và người trực <b>không biết Trang đó lấy dữ liệu từ đâu</b>
/// — Facebook hay Zalo. Pancake trên web của họ chia sẵn "Tất cả · Facebook · Zalo"; hộp thư này
/// phải nói được đúng như vậy.</para>
/// </summary>
public class ChatSourceTests
{
    // ── Chuẩn hoá tên nền tảng ───────────────────────────────────────────────

    [Theory]
    [InlineData("facebook")]
    [InlineData("Facebook")]
    [InlineData("  FACEBOOK  ")]
    [InlineData("messenger")]   // webhook của Meta gọi thế
    [InlineData("fb")]
    [InlineData("facebook_page")]
    public void Moi_cach_viet_cua_Facebook_ve_cung_mot_nguon(string raw)
        => Assert.Equal("facebook", ChatSource.Chuan(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Khong_co_nen_tang_thi_khong_doan_bua(string? raw)
        => Assert.Null(ChatSource.Chuan(raw));

    /// <summary>
    /// Nền tảng LẠ không được nuốt. Pancake thêm kênh mới thì tab hiện tên lạ — xấu nhưng đúng;
    /// nuốt đi thì hội thoại rơi vào tab "chưa rõ" mà không ai biết vì sao.
    /// </summary>
    [Fact]
    public void Nen_tang_la_di_nguyen_xuong_chu_khong_bi_nuot()
        => Assert.Equal("lazada", ChatSource.Chuan("Lazada"));

    // ── Kênh → nguồn ─────────────────────────────────────────────────────────

    /// <summary>
    /// Kênh tên "Messenger" nhưng nguồn là Facebook — và đó là chủ ý: cùng một Trang Facebook có
    /// thể vào bằng hai đường (nối thẳng và qua Pancake). Hai đường phải gộp chung MỘT tab, không
    /// thì cùng một Trang hiện ở hai chỗ với hai cái tên.
    /// </summary>
    [Fact]
    public void Kenh_Messenger_co_nguon_la_Facebook()
        => Assert.Equal("facebook", ChatSource.CuaKenh(ChatChannel.Messenger));

    [Fact]
    public void Kenh_noi_thang_thi_nguon_chinh_la_no()
    {
        Assert.Equal("zalo", ChatSource.CuaKenh(ChatChannel.Zalo));
        Assert.Equal("telegram", ChatSource.CuaKenh(ChatChannel.Telegram));
        Assert.Equal("instagram", ChatSource.CuaKenh(ChatChannel.Instagram));
    }

    /// <summary>Cổng gom KHÔNG có nguồn của riêng nó — thiếu nền tảng thì để nguyên tên cổng.</summary>
    [Fact]
    public void Cong_gom_thieu_nen_tang_thi_la_chua_ro()
    {
        Assert.Equal(ChatSource.ChuaRo, ChatSource.CuaKenh(ChatChannel.Pancake));
        Assert.Equal("pancake", ChatSource.Cua(ChatChannel.Pancake, null));
        Assert.Equal("pancake", ChatSource.Cua(ChatChannel.Pancake, "   "));
    }

    [Fact]
    public void Co_nen_tang_thi_nen_tang_thang()
    {
        Assert.Equal("facebook", ChatSource.Cua(ChatChannel.Pancake, "facebook"));
        Assert.Equal("zalo", ChatSource.Cua(ChatChannel.Pancake, "zalo"));
    }

    [Fact]
    public void Chi_Pancake_la_cong_gom()
    {
        Assert.True(ChatSource.LaCongGom(ChatChannel.Pancake));
        foreach (var k in Enum.GetValues<ChatChannel>().Where(k => k != ChatChannel.Pancake))
            Assert.False(ChatSource.LaCongGom(k));
    }

    /// <summary>MỌI kênh phải có nguồn — thiếu một cái là hội thoại của nó không vào tab nào.</summary>
    [Fact]
    public void Moi_kenh_deu_ra_mot_nguon_khong_rong()
    {
        foreach (var k in Enum.GetValues<ChatChannel>())
            Assert.False(string.IsNullOrWhiteSpace(ChatSource.CuaKenh(k)), $"kênh {k} không có nguồn");
    }

    // ── Khoá "{kênh}:{mã}" ───────────────────────────────────────────────────

    [Fact]
    public void Tach_khoa_cat_o_dau_hai_cham_DAU_TIEN()
    {
        Assert.Equal(((short)8, "page_1"), ChatSourceMap.TachKhoa("8:page_1"));
        // Mã tài khoản của vài kênh có chứa dấu hai chấm — cắt ở dấu cuối là mất một khúc mã.
        Assert.Equal(((short)0, "oa:123"), ChatSourceMap.TachKhoa("0:oa:123"));
    }

    [Theory]
    [InlineData("khong-co-dau-hai-cham")]
    [InlineData(":thieu-kenh")]
    [InlineData("8:")]
    [InlineData("abc:xyz")]
    public void Khoa_hong_thi_bo_qua_chu_khong_nem(string khoa)
        => Assert.Null(ChatSourceMap.TachKhoa(khoa));

    // ── Đếm theo nguồn ───────────────────────────────────────────────────────

    private static Dictionary<(short, string), MoTaTaiKhoan> MoTa(
        params (short Kenh, string Ma, string? NenTang)[] ds)
        => ds.ToDictionary(x => (x.Kenh, x.Ma), x => new MoTaTaiKhoan("Trang " + x.Ma, x.NenTang));

    /// <summary>
    /// Đúng ca chủ dự án báo: hai Trang Facebook và một Trang Zalo, tất cả vào qua Pancake. Dải
    /// tab phải tách được thành "Facebook 2 · Zalo 1", không gộp thành "Pancake 3".
    /// </summary>
    [Fact]
    public void Ba_trang_Pancake_tach_dung_thanh_Facebook_va_Zalo()
    {
        var moTa = MoTa(((short)8, "fb1", "facebook"), ((short)8, "fb2", "facebook"),
                        ((short)8, "za1", "zalo"));
        var theoKenh = new Dictionary<short, int> { [8] = 12 };
        var theoTk = new Dictionary<string, int> { ["8:fb1"] = 7, ["8:fb2"] = 3, ["8:za1"] = 2 };

        var dem = ChatSourceMap.Dem(theoKenh, theoTk, moTa);

        Assert.Equal(10, dem["facebook"]);
        Assert.Equal(2, dem["zalo"]);
        Assert.False(dem.ContainsKey("pancake"));
    }

    /// <summary>
    /// Phần dôi ra giữa "tổng của kênh" và "tổng của các Trang" phải về nguồn mặc định của kênh.
    /// Dôi ra là chuyện thật: hội thoại tạo trước 24/08/2026 không ghi mã tài khoản. Bỏ nó đi thì
    /// cộng các tab lại nhỏ hơn số ở tab "Tất cả" mà không ai giải thích được.
    /// </summary>
    [Fact]
    public void Hoi_thoai_khong_co_ma_trang_van_duoc_dem()
    {
        var moTa = MoTa(((short)8, "fb1", "facebook"));
        var dem = ChatSourceMap.Dem(new Dictionary<short, int> { [8] = 10 },
                                    new Dictionary<string, int> { ["8:fb1"] = 4 }, moTa);

        Assert.Equal(4, dem["facebook"]);
        Assert.Equal(6, dem["pancake"]);
        Assert.Equal(10, dem.Values.Sum());
    }

    /// <summary>
    /// Trang đã gỡ khai báo mà hội thoại cũ còn: không tra ra nền tảng nữa thì về "chưa rõ", chứ
    /// không biến mất khỏi mọi tab.
    /// </summary>
    [Fact]
    public void Trang_da_go_khai_bao_van_duoc_dem_vao_chua_ro()
    {
        var dem = ChatSourceMap.Dem(new Dictionary<short, int> { [8] = 5 },
                                    new Dictionary<string, int> { ["8:dago"] = 5 },
                                    new Dictionary<(short, string), MoTaTaiKhoan>());

        Assert.Equal(5, dem["pancake"]);
    }

    /// <summary>
    /// Zalo nối thẳng và Trang Zalo qua Pancake là CÙNG một nguồn — người trực hỏi "khách nhắn từ
    /// đâu", không hỏi "tin đi đường nào".
    /// </summary>
    [Fact]
    public void Noi_thang_va_qua_cong_gom_cung_nguon_thi_cong_don()
    {
        var moTa = MoTa(((short)8, "za1", "zalo"), ((short)0, "oa1", null));
        var dem = ChatSourceMap.Dem(new Dictionary<short, int> { [8] = 3, [0] = 4 },
                                    new Dictionary<string, int> { ["8:za1"] = 3, ["0:oa1"] = 4 }, moTa);

        Assert.Equal(7, dem["zalo"]);
        Assert.Single(dem);
    }

    [Fact]
    public void Khong_co_hoi_thoai_nao_thi_dem_rong_chu_khong_nem()
        => Assert.Empty(ChatSourceMap.Dem(new Dictionary<short, int>(), new Dictionary<string, int>(),
                                          new Dictionary<(short, string), MoTaTaiKhoan>()));

    // ── Nguồn đã khai ────────────────────────────────────────────────────────

    [Fact]
    public void Nguon_da_khai_lay_tu_tai_khoan_du_chua_co_hoi_thoai_nao()
    {
        var ds = ChatSourceMap.DaKhai(MoTa(((short)8, "fb1", "facebook"), ((short)0, "oa1", null)),
                                      new HashSet<ChatChannel>());
        Assert.Equal(new[] { "facebook", "zalo" }, ds.OrderBy(x => x).ToArray());
    }

    /// <summary>Kênh đang tắt không được dựng sẵn một tab trống — tắt nghĩa là không mời nối.</summary>
    [Fact]
    public void Kenh_dang_tat_khong_dung_san_tab_trong()
    {
        var ds = ChatSourceMap.DaKhai(MoTa(((short)1, "page1", null), ((short)0, "oa1", null)),
                                      new HashSet<ChatChannel> { ChatChannel.Messenger });
        Assert.Equal(new[] { "zalo" }, ds);
    }

    /// <summary>
    /// Nhưng hội thoại CŨ của kênh tắt thì vẫn đếm — tab vẫn mọc nếu thật sự có tin. Thôi không
    /// mời nối nữa là một chuyện; chôn tin nhắn thật của khách là chuyện khác.
    /// </summary>
    [Fact]
    public void Hoi_thoai_cu_cua_kenh_tat_VAN_duoc_dem()
    {
        var dem = ChatSourceMap.Dem(new Dictionary<short, int> { [1] = 6 },
                                    new Dictionary<string, int>(),
                                    new Dictionary<(short, string), MoTaTaiKhoan>());
        Assert.Equal(6, dem["facebook"]);
    }

    [Fact]
    public void Phong_chat_noi_bo_khong_phai_mot_nguon()
    {
        var ds = ChatSourceMap.DaKhai(MoTa(((short)7, "noibo", null)), new HashSet<ChatChannel>());
        Assert.Empty(ds);
    }

    // ── Nguồn của từng tài khoản ─────────────────────────────────────────────

    [Fact]
    public void Nguon_theo_tai_khoan_giu_nguyen_khoa_cua_bo_dem()
    {
        var ra = ChatSourceMap.TheoTaiKhoan(MoTa(((short)8, "fb1", "facebook")));
        Assert.Equal("facebook", ra["8:fb1"]);
    }

    /// <summary>
    /// Trang chỉ còn trong bộ đếm (đã gỡ khai báo) vẫn phải có nguồn, không thì hàng chip Trang bỏ
    /// sót đúng những Trang đang có tin, và tab đếm ra một con số không chip nào cộng lại thành.
    /// </summary>
    [Fact]
    public void Trang_chi_con_trong_bo_dem_van_co_nguon()
    {
        var ra = ChatSourceMap.TheoTaiKhoan(MoTa(((short)8, "fb1", "facebook")),
                                            new[] { "8:fb1", "8:dago", "0:oa1" });
        Assert.Equal("facebook", ra["8:fb1"]);   // đã khai thì giữ nguyên, không bị đè
        Assert.Equal("pancake", ra["8:dago"]);
        Assert.Equal("zalo", ra["0:oa1"]);
    }

    // ── Nở nguồn thành bộ lọc ────────────────────────────────────────────────

    /// <summary>
    /// Tab "Facebook" phải lấy CẢ hai đường: kênh Messenger nối thẳng (trọn kênh) và các Trang
    /// Facebook của cổng gom (theo từng mã).
    /// </summary>
    [Fact]
    public void No_Facebook_lay_ca_kenh_noi_thang_lan_Trang_cong_gom()
    {
        var moTa = MoTa(((short)8, "fb1", "facebook"), ((short)8, "za1", "zalo"));
        var loc = ChatSourceMap.No("facebook", moTa);

        Assert.NotNull(loc);
        Assert.Contains((short)ChatChannel.Messenger, loc!.Value.Kenh);
        Assert.DoesNotContain((short)ChatChannel.Pancake, loc.Value.Kenh);
        Assert.Equal(new[] { "fb1" }, loc.Value.TaiKhoan);
    }

    /// <summary>
    /// Cổng gom KHÔNG bao giờ vào theo trọn kênh — vào thế thì bấm tab "Facebook" đổ ra cả Trang
    /// Zalo nằm cùng kênh.
    /// </summary>
    [Fact]
    public void No_khong_bao_gio_lay_tron_kenh_cong_gom()
    {
        foreach (var n in new[] { "facebook", "zalo", "pancake", "tiktok" })
        {
            var loc = ChatSourceMap.No(n, MoTa(((short)8, "fb1", "facebook")));
            Assert.DoesNotContain((short)ChatChannel.Pancake, loc!.Value.Kenh);
        }
    }

    /// <summary>Tab "chưa rõ" gom đúng những Trang không khai được nền tảng.</summary>
    [Fact]
    public void No_chua_ro_gom_dung_Trang_thieu_nen_tang()
    {
        var moTa = MoTa(((short)8, "fb1", "facebook"), ((short)8, "la", null));
        var loc = ChatSourceMap.No("pancake", moTa);

        Assert.Equal(new[] { "la" }, loc!.Value.TaiKhoan);
        Assert.Empty(loc.Value.Kenh);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Khong_truyen_nguon_thi_KHONG_loc(string? nguon)
        => Assert.Null(ChatSourceMap.No(nguon, MoTa(((short)8, "fb1", "facebook"))));

    /// <summary>
    /// Nguồn lạ nở ra hai tập RỖNG, không phải null: danh sách phải TRỐNG. Về null là lờ bộ lọc đi
    /// rồi đổ ra cả hộp thư dưới một cái tab mà người dùng tưởng đang lọc.
    /// </summary>
    [Fact]
    public void Nguon_la_cho_danh_sach_trong_chu_khong_mo_toang()
    {
        var loc = ChatSourceMap.No("khong-ton-tai", MoTa(((short)8, "fb1", "facebook")));

        Assert.NotNull(loc);
        Assert.Empty(loc!.Value.Kenh);
        Assert.Empty(loc.Value.TaiKhoan);
    }

    /// <summary>
    /// Mọi nguồn đang hiện trên dải tab đều phải nở ra được bộ lọc không rỗng — tab bấm vào ra
    /// danh sách trống là lỗi nặng hơn không có tab.
    /// </summary>
    [Fact]
    public void Moi_nguon_tren_dai_tab_deu_loc_duoc()
    {
        var moTa = MoTa(((short)8, "fb1", "facebook"), ((short)8, "za1", "zalo"),
                        ((short)0, "oa1", null), ((short)3, "bot", null));

        foreach (var n in ChatSourceMap.DaKhai(moTa, new HashSet<ChatChannel>()))
        {
            var loc = ChatSourceMap.No(n, moTa);
            Assert.True(loc!.Value.Kenh.Length + loc.Value.TaiKhoan.Length > 0,
                        $"nguồn {n} nở ra bộ lọc rỗng");
        }
    }
}
