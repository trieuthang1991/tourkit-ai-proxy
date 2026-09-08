using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Mọi endpoint đụng MỘT hội thoại cụ thể đều phải đi qua cửa chung.
///
/// <para>Trước 07/09/2026 ChatInboxEndpoints dài 2.527 dòng và KHÔNG có một dòng kiểm quyền
/// nào: ai đăng nhập được là đọc được hội thoại của mọi đồng nghiệp. Lọc ở danh sách thôi thì
/// vẫn gõ thẳng /conversations/123 là đọc được — nên cửa phải nằm ở nơi lấy hội thoại ra.</para>
/// </summary>
public class ChatScopeGuardTests
{
    private static string Endpoint() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
    private static string SessionAuthSrc() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/SessionAuth.cs");

    [Fact]
    public void Cua_chung_bat_buoc_nhan_nguoi_xem()
    {
        // Tham số BẮT BUỘC, không phải tuỳ chọn có mặc định: có mặc định thì quên truyền cũng
        // biên dịch được, và lỗ hổng đi thẳng lên bản chạy thật. Đòi nguyên văn "NguoiXem xem,"
        // (dấu phẩy ngay sau, không dấu "=") — pattern cũ chỉ đòi "NguoiXem " nên vẫn khớp cả
        // "NguoiXem xem = default!," tức không khoá được đúng điều chú thích này tuyên bố.
        Assert.Matches(@"GetConversationAsync\(\s*string tenant,\s*long id,\s*NguoiXem xem,", Repo());
    }

    [Fact]
    public void Ca_ba_ham_kep_luat_xem_bang_dung_menh_de_SQL()
    {
        // Đòi NGUYÊN VĂN mệnh đề, không chỉ đòi chữ "assigned_user_id" xuất hiện đâu đó — chữ đó
        // cũng nằm sẵn trong SELECT v.* của GetConversationAsync/ListConversationsAsync, nên một
        // assert lỏng sẽ xanh giả kể cả khi một trong ba hàm không hề kẹp luật xem.
        //
        // Phủ CẢ BA hàm đọc hội thoại theo id/tenant — thiếu CountAsync là chip đếm (tổng, chưa
        // đọc, theo kênh) lộ đúng con số mà luật 404 ở hai hàm kia đang giấu.
        var repo = Repo();
        foreach (var ten in new[] { "GetConversationAsync", "ListConversationsAsync", "CountAsync" })
        {
            var m = Regex.Match(repo, ten + @"(.{0,3000})", RegexOptions.Singleline);
            Assert.True(m.Success, $"Không thấy {ten}");
            Assert.Contains("@xemTatCa OR v.assigned_user_id = @maNguoi", m.Groups[1].Value);
        }
    }

    [Fact]
    public void Moi_route_mot_hoi_thoai_deu_doc_nguoi_xem_tu_phien()
    {
        var src = Endpoint();

        // Điểm bắt đầu của MỌI route (không riêng /conversations/{id...}) — dùng làm biên cắt
        // thân từng route. Cắt theo "route kế tiếp bất kỳ" thay vì so hai tổng số: một canary kiểu
        // "tổng route <= tổng lượt đọc người xem" chỉ kêu từ route bỏ sót THỨ HAI trở đi (route đầu
        // bị bỏ sót vẫn giữ đẳng thức tổng số bằng nhau) — cắt từng thân thì route đầu bị bỏ sót
        // cũng lộ ngay, kèm đúng tên route.
        var moiRoute = Regex.Matches(src, @"g\.Map(?:Get|Post|Put|Patch|Delete)\(""[^""]*""")
            .Select(m => m.Index).OrderBy(i => i).ToList();
        Assert.True(moiRoute.Count > 0, "Không thấy route nào trong ChatInboxEndpoints");

        var diemHoiThoai = Regex.Matches(src, @"g\.Map(?:Get|Post|Put|Patch|Delete)\(""(/conversations/\{id[^""]*)""");
        Assert.True(diemHoiThoai.Count >= 20,
            $"Chỉ thấy {diemHoiThoai.Count} route — biểu thức đã lạc khỏi cách viết thật");

        var boQua = new List<string>();
        foreach (Match m in diemHoiThoai)
        {
            var batDau = m.Index;
            var ketThuc = moiRoute.FirstOrDefault(i => i > batDau);
            // Route cuối cùng của cả file: không có route kế tiếp để cắt, lấy hết phần còn lại.
            var than = ketThuc > batDau ? src.Substring(batDau, ketThuc - batDau) : src.Substring(batDau);
            if (!than.Contains("ReadNguoiXemAsync"))
                boQua.Add(m.Groups[1].Value);
        }

        Assert.True(boQua.Count == 0,
            "Route sau đụng một hội thoại nhưng KHÔNG đọc người xem qua cửa chung: " +
            string.Join(", ", boQua));
    }

    [Fact]
    public void Hoi_thoai_CHUA_GAN_khong_hien_voi_nguoi_khong_phai_admin()
    {
        // ĐÂY LÀ QUYẾT ĐỊNH, KHÔNG PHẢI SƠ SUẤT (chủ dự án, 07/09/2026). Bản thiết kế đầu có
        // thêm vế "OR assigned_user_id IS NULL" để nhân viên nhìn thấy hàng chờ mà tự nhận;
        // quyết định cuối là BỎ vế đó. Test này khoá lại để đợt sau không ai "sửa" nó.
        var m = Regex.Match(Repo(), "GetConversationAsync(.{0,1500})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy GetConversationAsync");
        var than = m.Groups[1].Value;
        Assert.Contains("@xemTatCa OR v.assigned_user_id = @maNguoi", than);
        Assert.DoesNotContain("assigned_user_id IS NULL", than);
    }

    [Fact]
    public void Duong_tai_tep_cung_phai_qua_luat_xem_chu_khong_chi_kep_theo_cong_ty()
    {
        // ⚠️ LỖ THẬT, hoãn từ review việc 3 rồi vá 08/09/2026. Đường tải tệp đính kèm chỉ kiểm
        // "tin có thuộc công ty này không". Kịch bản: nhân viên từng phụ trách một hội thoại, sau
        // đó bị chuyển giao. Cửa đã đóng ở màn chi tiết, nhưng còn giữ mã tin thì vẫn tải lại được
        // ảnh và tệp khách đã gửi. Đóng cửa trước mà để ngỏ cửa sau thì luật xem chỉ là hình thức.
        //
        // Chốt neo vào BA điểm, vì bỏ bất kỳ điểm nào là lỗ mở lại mà hai điểm kia vẫn xanh:
        //   (a) hàm kho NHẬN NguoiXem, và nhận BẮT BUỘC — để nó có giá trị mặc định thì chỗ gọi
        //       quên truyền vẫn biên dịch được, tức lỗ quay lại trong im lặng;
        //   (b) câu SQL mang ĐÚNG mệnh đề của GetConversationAsync — hai cách viết cho cùng một
        //       luật thì sớm muộn lệch, mà lệch ở đây là lệch quyền;
        //   (c) endpoint đọc NguoiXem và TRUYỀN nó xuống, không dừng ở SessionAuth.Read.
        var repo = Repo();
        var m = Regex.Match(repo, @"GetConversationByMessageAsync\(string tenant, long messageId,\s*([^)]*)\)");
        Assert.True(m.Success, "Không thấy GetConversationByMessageAsync");
        Assert.Contains("NguoiXem xem", m.Groups[1].Value);
        Assert.DoesNotContain("NguoiXem? xem", m.Groups[1].Value);
        Assert.DoesNotContain("NguoiXem xem = ", m.Groups[1].Value);

        var than = Regex.Match(repo, @"GetConversationByMessageAsync(.{0,900})", RegexOptions.Singleline);
        Assert.Contains("@xemTatCa OR v.assigned_user_id = @maNguoi", than.Groups[1].Value);

        // Cắt đúng thân handler tải tệp — ranh giới cú pháp, không đếm ký tự.
        var src = Endpoint();
        var i = src.IndexOf("MapGet(\"/messages/{msgId:long}/file\"", System.StringComparison.Ordinal);
        var j = src.IndexOf("MapGet(\"/avatars/{accountId}/{fid}\"", System.StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "Không thấy handler tải tệp trong ChatInboxEndpoints.cs");
        var thanTep = src[i..j];
        Assert.Contains("SessionAuth.ReadNguoiXemAsync(ctx, sessions, assign, ct)", thanTep);
        Assert.Contains("GetConversationByMessageAsync(a.TenantId, msgId, xem, ct)", thanTep);
        // Đọc phiên trần ở đây là dấu hiệu ai đó lùi lại bản cũ.
        Assert.DoesNotContain("SessionAuth.Read(ctx, sessions)", thanTep);
    }

    [Fact]
    public void Khong_duoc_xem_thi_tra_404_chu_khong_403()
    {
        // 403 nghĩa là "có hội thoại này nhưng anh không được xem" — tức xác nhận đúng cái
        // đang giấu. Dò tuần tự theo id là biết công ty có bao nhiêu khách.
        //
        // ⚠️ Bản trước chỉ cấm ĐÚNG MỘT CÂU CHỮ tiếng Việt. Đổi cách viết là lọt: thay
        // `return Results.NotFound();` ở route chi tiết bằng một thân lỗi 403 chữ khác thì chốt
        // VẪN XANH (đã đo). Chốt canh phải bám vào ĐƯỜNG TỪ CHỐI, không bám vào lời văn.
        //
        // Không cấm trần số 403 trong cả file: xác thực webhook của Meta/Zalo/Telegram và quyền
        // cấu hình hệ thống dùng 403 HỢP LỆ — cấm trần là đỏ oan.
        var src = Endpoint();
        Assert.DoesNotContain("Bạn không được xem hội thoại này", src);

        // Mọi lần tra hội thoại mà KHÔNG được xem đều phải rẽ về đúng một chỗ: 404.
        var traVe = Regex.Matches(src, @"GetConversationAsync\([^)]*\) is null\) return ([^;]+);")
            .Select(m => m.Groups[1].Value.Trim())
            .Concat(Regex.Matches(src, @"[ (]v is null\) return ([^;]+);")
                .Select(m => m.Groups[1].Value.Trim()))
            .Concat(Regex.Matches(src, @"is not \{ \} v\) return ([^;]+);")
                .Select(m => m.Groups[1].Value.Trim()))
            .ToList();
        Assert.True(traVe.Count >= 24,
            $"Chỉ thấy {traVe.Count} đường từ chối — biểu thức cắt đã lạc, chốt sẽ xanh giả");
        foreach (var r in traVe)
            Assert.Equal("Results.NotFound()", r);
    }

    /// Thân route <c>GET /conversations</c> (danh sách) — KHÔNG lấy nhầm sang
    /// <c>GET /conversations/{id:long}</c> (chi tiết) đứng ngay sau nó.
    private static string ThanDanhSach()
    {
        var src = Endpoint();
        var i = src.IndexOf("g.MapGet(\"/conversations\",", System.StringComparison.Ordinal);
        var j = src.IndexOf("g.MapGet(\"/conversations/{id:long}\",", System.StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "Không thấy route GET /conversations trong ChatInboxEndpoints.cs");
        return src[i..j];
    }

    [Fact]
    public void Nhan_vien_thuong_khong_tra_ra_ma_thi_bi_DONG_khong_duoc_MO()
    {
        // Luật xem CŨ (dựa quyền CH_HT_XEM, xemHet) tách riêng khỏi luật xem MỚI (tham số
        // `xem`) — xem chú thích "Luật cũ CANH RIÊNG" ngay tại chỗ. `chiCuaToi` là biến quyết
        // định luật cũ này khi đổ xuống ListConversationsAsync/CountAsync qua mệnh đề
        // "@chiCuaToi IS NULL OR ...": null nghĩa là KHÔNG LỌC GÌ CẢ.
        //
        // EnsureCrmUserIdAsync trả null ở BA đường (không phiên, JWT thiếu claim, ngoại lệ bị
        // nuốt) — trước bản sửa phân công theo mã, giá trị gốc là TÊN ĐĂNG NHẬP nên không bao
        // giờ null; nay có thể null, và bản `chiCuaToi = xemHet ? null : maToi;` cho null đó lọt
        // thẳng xuống SQL, mở toang hội thoại cả công ty cho nhân viên thường ngay lúc tra mã
        // lỗi. Khoá lại: mã không tra ra được phải đổi thành sentinel không mã người thật nào
        // khớp (mã CRM luôn > 0), không được lọt thành null.
        // ⚠️ Neo vào ĐÚNG CÂU GÁN của từng biến. Bản trước rải hai khẳng định ra cả thân hàm —
        // "không chứa nguyên văn dòng cũ" + "ở đâu đó có sentinel" — mà hai vế không buộc vào cùng
        // một biến. Hệ quả: viết lại y nguyên lỗ hổng bằng chữ khác thì chốt VẪN XANH. Đã đo:
        //     var chiCuaToi = xemHet ? (int?)null : maToi;      // fail-open, chốt cũ xanh 7/7
        //     var giaoChoLoc = mine == true ? maToi : (int?)null; // fail-open, chốt cũ xanh
        // Vế DoesNotContain cho giaoCho còn tệ hơn: biểu thức đã tách ra biến riêng nên chuỗi bị
        // cấm KHÔNG THỂ xuất hiện nữa — nó vĩnh viễn đúng, tức vĩnh viễn vô nghĩa.
        //
        // Chốt canh phải bắt LUẬT ("mã tra không ra thì đóng"), không bắt CHÍNH TẢ của một bản viết.
        var than = ThanDanhSach();
        foreach (var bien in new[] { "chiCuaToi", "giaoChoLoc" })
        {
            var cau = CauGan(than, bien);
            Assert.False(string.IsNullOrWhiteSpace(cau), $"Không cắt được câu gán {bien}");
            Assert.Contains("KHONG_XAC_DINH_DUOC_MA", cau);
        }
    }

    [Fact]
    public void Quyen_xem_het_chat_chot_tam_theo_TEN_dang_nhap_khong_doc_IsAdmin()
    {
        // Yêu cầu chủ dự án 07/09/2026: chat TẠM chốt quản trị viên theo tên đăng nhập, không
        // đọc claim/cột IsAdmin nữa — "đừng xoá tránh lỗi, cứ để tạm đấy". Cột
        // dbo.TkSessions.IsAdmin, TkSession.IsAdmin, JwtClaims.TryGetIsAdmin PHẢI còn nguyên
        // (test riêng của chúng ở JwtClaimsTests không đụng tới) — guard này chỉ khoá đúng CHỖ
        // ĐỌC, không khoá phần hạ tầng ghi/nạp.
        //
        // ⚠️ Bản trước đòi NGUYÊN VĂN câu Equals(...) nằm trong HAI cửa sổ (thân
        // ReadNguoiXemAsync, route GET /assign-settings). Ngày 08/09/2026 câu đó được gom vào
        // một hàm dùng chung (SessionAuth.LaQuanTriChat) — đúng việc phải làm khi bản chép thứ
        // BA sắp xuất hiện — và chốt ĐỎ dù luật không đổi lấy một chữ. Đó là cơ chế mục ruỗng
        // số 1 đã ghi sổ trên nhánh này: chốt bám CHÍNH TẢ, mã dời sang hàm gói.
        //
        // Nay chốt bám LUẬT, ba vế:
        //   (a) có ĐÚNG MỘT nơi trả lời "ai là quản trị chat", và nó so theo tên đăng nhập;
        //   (b) mọi cửa quyết định của chat đều HỎI qua nơi đó;
        //   (c) không cửa nào đọc IsAdmin.
        // Gom lại lần nữa (đổi tên hàm, dời sang lớp khác) thì vế (b) đỏ và người sửa buộc phải
        // đọc chốt này — khác hẳn bản cũ, đỏ mà không nói được vì sao.
        var neo = "string.Equals(a.Username, \"admin\", StringComparison.OrdinalIgnoreCase)";
        var sessionAuth = SessionAuthSrc();

        // (a) MỘT nguồn duy nhất. Đếm chứ không chỉ Contains: bản chép THỨ HAI làm chốt đỏ ngay
        // tại lúc nó ra đời, không đợi tới bản thứ ba như lần trước.
        Assert.Contains("public static bool LaQuanTriChat(Ctx a)", sessionAuth);
        Assert.Equal(1, Regex.Matches(sessionAuth, Regex.Escape(neo)).Count);
        var thanLaQuanTri = Regex.Match(sessionAuth,
            @"public static bool LaQuanTriChat\(Ctx a\)(.{0,200})", RegexOptions.Singleline);
        Assert.True(thanLaQuanTri.Success, "Không thấy thân LaQuanTriChat");
        Assert.Contains(neo, thanLaQuanTri.Groups[1].Value);

        // (b)+(c) Hai cửa quyết định. Cắt tới THÀNH VIÊN KẾ TIẾP, không đếm ký tự — cửa sổ cố
        // định 1200 từng chỉ dư 76 ký tự và làm bộ test này đỏ oan ba lần.
        var thanRead = ThanReadNguoiXem();
        Assert.False(string.IsNullOrWhiteSpace(thanRead), "Không cắt được thân ReadNguoiXemAsync");
        Assert.Contains("LaQuanTriChat(a)", thanRead);
        Assert.DoesNotContain("s?.IsAdmin", thanRead);

        var src = Endpoint();
        var i = src.IndexOf("MapGet(\"/assign-settings\"", System.StringComparison.Ordinal);
        var j = src.IndexOf("MapPut(\"/assign-settings\"", System.StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "Không thấy route GET /assign-settings trong ChatInboxEndpoints.cs");
        var than = src[i..j];
        Assert.Contains("SessionAuth.LaQuanTriChat(a)", than);
        Assert.DoesNotContain("s?.IsAdmin", than);

        // Không bản chép tay nào trong ChatInboxEndpoints. Soi CẢ FILE, không cửa sổ hẹp: cửa sổ
        // hẹp bị vô hiệu bằng cách dời mã ra ngoài — đã đo hai lần trên chính nhánh này.
        Assert.DoesNotContain("a.Username, \"admin\"", src);
    }
    /// <summary>Cắt đúng CÂU LỆNH gán <c>var &lt;bien&gt; = …;</c> — trả rỗng nếu không thấy.</summary>
    private static string CauGan(string than, string bien)
    {
        var i = than.IndexOf($"var {bien} = ", StringComparison.Ordinal);
        if (i < 0) return "";
        var j = than.IndexOf(';', i);
        return j < 0 ? "" : than[i..j];
    }

    /// <summary>
    /// Thân <c>ReadNguoiXemAsync</c>, cắt tới thành viên kế tiếp của lớp.
    /// Ranh giới cú pháp, không phải số ký tự — xem lý do ở chỗ gọi.
    /// </summary>
    private static string ThanReadNguoiXem()
    {
        var src = SessionAuthSrc();
        var i = src.IndexOf("ReadNguoiXemAsync", StringComparison.Ordinal);
        if (i < 0) return "";
        // Dừng ở thành viên kế tiếp HOẶC ở chú thích tài liệu của nó — cái nào tới trước. Chỉ
        // bắt "\n    public" thì cửa sổ ôm luôn phần /// <summary> của hàm sau, mà chú thích
        // đó nhắc tên các thứ hàm này KHÔNG được đọc (IsAdmin…) — đủ để một vế DoesNotContain
        // đỏ oan vì lời văn chứ không vì mã.
        var j = new[] { src.IndexOf("\n    public", i + 20, StringComparison.Ordinal),
                        src.IndexOf("\n    /// <summary>", i + 20, StringComparison.Ordinal) }
                .Where(x => x > 0).DefaultIfEmpty(-1).Min();
        return j < 0 ? src[i..] : src[i..j];
    }
}
