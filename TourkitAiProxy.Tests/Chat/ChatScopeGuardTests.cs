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
        //
        // ⚠️ Cắt theo RANH GIỚI CÚ PHÁP, không đếm ký tự. Bản trước lấy cửa sổ 1.500 ký tự và
        // đã dùng hết 1.058 — thêm sáu dòng chú thích vào hàm là chốt đỏ oan (bộ này đã đỏ oan
        // ba lần vì đúng cơ chế đó). Với vế DoesNotContain thì cửa sổ đếm ký tự còn nguy theo
        // chiều NGƯỢC LẠI: nới ra một chút là ôm sang ClaimConversationAsync — hàm đó dùng
        // "assigned_user_id IS NULL" hoàn toàn hợp lệ — và chốt đỏ vì mã của hàm KHÁC.
        var than = ThanHamKho("GetConversationAsync");
        Assert.False(string.IsNullOrWhiteSpace(than), "Không cắt được thân GetConversationAsync");
        Assert.Contains("@xemTatCa OR v.assigned_user_id = @maNguoi", than);
        Assert.DoesNotContain("assigned_user_id IS NULL", than);
    }

    /// <summary>
    /// Thân một hàm trong <c>ChatRepository</c>, cắt tới THÀNH VIÊN KẾ TIẾP.
    ///
    /// <para>Dừng ở chú thích tài liệu của thành viên sau nếu nó tới trước — chú thích đó hay nhắc
    /// tên những thứ hàm này KHÔNG được dùng, nên ôm vào là một vế DoesNotContain đỏ vì LỜI VĂN
    /// chứ không vì mã.</para>
    /// </summary>
    private static string ThanHamKho(string ten)
    {
        var src = Repo();
        var i = src.IndexOf(" " + ten + "(", StringComparison.Ordinal);
        if (i < 0) return "";
        var j = new[] { src.IndexOf("\n    public", i + 20, StringComparison.Ordinal),
                        src.IndexOf("\n    /// <summary>", i + 20, StringComparison.Ordinal),
                        src.IndexOf("\n    private", i + 20, StringComparison.Ordinal) }
                .Where(x => x > 0).DefaultIfEmpty(-1).Min();
        return j < 0 ? src[i..] : src[i..j];
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
        Assert.Contains("SessionAuth.ReadNguoiXemAsync(ctx, sessions, ct)", thanTep);
        Assert.Contains("GetConversationByMessageAsync(a.TenantId, msgId, xem, ct)", thanTep);
        // Đọc phiên trần ở đây là dấu hiệu ai đó lùi lại bản cũ.
        Assert.DoesNotContain("SessionAuth.Read(ctx, sessions)", thanTep);
    }

    [Fact]
    public void HeThong_chi_duoc_dung_de_DOC_LAI_roi_PHAT_SU_KIEN()
    {
        // NguoiXem.HeThong bỏ qua luật xem — đó là cả mục đích của nó, và cũng là lý do nó nguy.
        // Nó tồn tại cho ĐÚNG MỘT việc: sau khi đổi người phụ trách, đọc lại hội thoại để biết
        // giá trị MỚI mà bắn lên bus. Phải bỏ qua luật xem vì người vừa nhận việc có thể không
        // còn thấy hội thoại đó bằng phạm vi cũ.
        //
        // Nếu giá trị đọc bằng HeThong đi vào THÂN PHẢN HỒI thì luật xem thủng ngay tại đó: người
        // gọi nhận nội dung hội thoại mà lẽ ra họ không được xem. Đợt review trước đã soi tay ba
        // chỗ này và kết luận không phải lỗ — nhưng ghi vào sổ nợ rằng KHÔNG có chốt nào giữ, nên
        // lần sau ai thêm chỗ thứ tư thì không gì cản.
        //
        // Cách phát biểu: bám vào BIẾN. Mỗi lượt đọc bằng HeThong gán vào một biến; biến đó chỉ
        // được xuất hiện ở ba dạng câu — chính câu gán, câu kiểm null, và câu bus.Publish. Xuất
        // hiện ở bất kỳ đâu khác (nhất là trong Results.*) là đỏ.
        var src = Endpoint();
        var gan = Regex.Matches(src,
            @"var (\w+) = await repo\.GetConversationAsync\([^)]*NguoiXem\.HeThong[^)]*\);");

        // Không khớp chỗ nào = biểu thức đã lạc khỏi cách viết thật. Im lặng bỏ qua thì chốt thành
        // vô dụng mà vẫn xanh — tệ hơn là không có chốt.
        Assert.True(gan.Count >= 3,
            $"Chỉ thấy {gan.Count} lượt đọc bằng NguoiXem.HeThong — biểu thức đã lạc, chốt sẽ xanh giả");

        // Mọi lượt dùng HeThong đều phải đi qua một câu gán như trên. Dùng thẳng trong biểu thức
        // (không gán ra biến) là cách vòng qua chính chốt này.
        Assert.Equal(gan.Count + 1, Regex.Matches(src, @"NguoiXem\.HeThong").Count);
        //                     ^ +1: một lượt nhắc trong chú thích giải thích, không phải mã.

        var dong = src.Split('\n');
        foreach (Match m in gan)
        {
            var bien = m.Groups[1].Value;
            var pham = dong
                .Where(d => Regex.IsMatch(d, @"\b" + Regex.Escape(bien) + @"\b"))
                .Where(d => !d.Contains("GetConversationAsync")            // chính câu gán
                         && !Regex.IsMatch(d, @"if \(" + Regex.Escape(bien) + @" is null\)")
                         && !d.Contains("bus.Publish("))
                .Select(d => d.Trim())
                .ToList();

            Assert.True(pham.Count == 0,
                $"Giá trị đọc bằng NguoiXem.HeThong ({bien}) đang đi ra ngoài đường đọc-lại-rồi-phát:\n  "
                + string.Join("\n  ", pham)
                + "\n\nHeThong bỏ qua luật xem. Cho nó chạm vào thân phản hồi là thủng luật xem "
                + "ngay tại chỗ đó.");
        }
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
    public void Quyen_xem_het_chat_doc_QUYEN_CRM_chu_khong_theo_ten_dang_nhap()
    {
        // 09/09/2026: chốt tạm "tên đăng nhập là admin" ĐÃ BỎ. CRM vốn đã có sẵn hai mã quyền
        // CHAT_XEM và CHAT_XEM_ALL — đo trên staging cùng ngày: AdminThoa có CHAT_XEM_ALL mà bị
        // kẹp oan, trang01 không có mã nào mà vẫn vào được hộp thư. Chốt tạm ấy sai theo CẢ HAI
        // chiều, nên nay quyền của CRM là nguồn duy nhất.
        //
        // Cột dbo.TkSessions.IsAdmin, TkSession.IsAdmin, JwtClaims.TryGetIsAdmin PHẢI còn nguyên
        // (test riêng của chúng ở JwtClaimsTests không đụng tới) — guard này chỉ khoá đúng CHỖ
        // ĐỌC, không khoá phần hạ tầng ghi/nạp.
        //
        // ⚠️ Bản trước đòi NGUYÊN VĂN câu Equals(...) nằm trong HAI cửa sổ (thân
        // ReadNguoiXemAsync, route GET /assign-settings). Ngày 08/09/2026 câu đó được gom vào
        // một hàm dùng chung (SessionAuth.LaQuanTriChat) — đúng việc phải làm khi bản chép thứ
        // BA sắp xuất hiện — và chốt ĐỎ dù luật không đổi lấy một chữ. Đó là cơ chế mục ruỗng
        // số 1 đã ghi sổ trên nhánh này: chốt bám CHÍNH TẢ, mã dời sang hàm gói.
        //
        // Nay chốt bám LUẬT, bốn vế:
        //   (a) có ĐÚNG MỘT nơi trả lời "ai là quản trị chat", và nó đọc quyền CHAT_XEM_ALL;
        //   (b) mọi cửa quyết định của chat đều HỎI qua nơi đó;
        //   (c) không cửa nào đọc IsAdmin;
        //   (d) KHÔNG chỗ nào quay lại so theo tên đăng nhập.
        // Gom lại lần nữa (đổi tên hàm, dời sang lớp khác) thì vế (b) đỏ và người sửa buộc phải
        // đọc chốt này — khác hẳn bản cũ, đỏ mà không nói được vì sao.
        var neo = "TkPermissionCodes.ChatXemTatCa";
        // BỎ CHÚ THÍCH trước khi đếm. Chú thích tài liệu của chính hàm này có <see cref=...> trỏ
        // tới hằng số đó, nên bản đầu của chốt đếm ra 2 và đỏ oan — đúng cơ chế mục ruỗng số 1
        // đã ghi sổ trên nhánh này, chỉ khác chiều: lần trước chú thích che mã, lần này chú
        // thích ĐÓNG VAI mã.
        var sessionAuth = BoChuThich(SessionAuthSrc());

        // (a) MỘT nguồn duy nhất. Đếm chứ không chỉ Contains: bản chép THỨ HAI làm chốt đỏ ngay
        // tại lúc nó ra đời, không đợi tới bản thứ ba như lần trước.
        Assert.Contains("public static async Task<bool> IsQuanTriChatAsync(", sessionAuth);
        Assert.Equal(1, Regex.Matches(sessionAuth, Regex.Escape(neo)).Count);
        var thanQuanTri = Regex.Match(sessionAuth,
            @"public static async Task<bool> IsQuanTriChatAsync\((.{0,400})", RegexOptions.Singleline);
        Assert.True(thanQuanTri.Success, "Không thấy thân IsQuanTriChatAsync");
        Assert.Contains(neo, thanQuanTri.Groups[1].Value);
        // (d) Chốt tạm cũ không được quay lại — kể cả ở SessionAuth, nơi nó từng sống.
        Assert.DoesNotContain("a.Username, \"admin\"", sessionAuth);

        // (b)+(c) Hai cửa quyết định. Cắt tới THÀNH VIÊN KẾ TIẾP, không đếm ký tự — cửa sổ cố
        // định 1200 từng chỉ dư 76 ký tự và làm bộ test này đỏ oan ba lần.
        var thanRead = ThanReadNguoiXem();
        Assert.False(string.IsNullOrWhiteSpace(thanRead), "Không cắt được thân ReadNguoiXemAsync");
        Assert.Contains("IsQuanTriChatAsync(a.SessionId, sessions, ct)", thanRead);
        Assert.DoesNotContain("s?.IsAdmin", thanRead);

        var src = Endpoint();
        var i = src.IndexOf("MapGet(\"/assign-settings\"", System.StringComparison.Ordinal);
        var j = src.IndexOf("MapPut(\"/assign-settings\"", System.StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "Không thấy route GET /assign-settings trong ChatInboxEndpoints.cs");
        var than = src[i..j];
        Assert.Contains("SessionAuth.IsQuanTriChatAsync(a.SessionId, sessions, ct)", than);
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

    /// Bỏ mọi dòng chú thích trước khi soi. Hai chốt dưới đòi những chuỗi (CHAT_XEM,
    /// /api/auth/permissions) mà chú thích ở chính hai tệp đó có nhắc tới — tính chú thích là mã
    /// thì gỡ sạch phần thân mà chốt vẫn xanh.
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void Ca_nhom_hop_thu_chat_phai_gac_bang_QUYEN_o_ca_hai_dau()
    {
        // Máy chủ và giao diện phải nói CÙNG một câu. Chỉ gác một đầu thì:
        //   · chỉ giao diện  → giấu mục menu, nhưng gõ tay đường dẫn hoặc gọi thẳng API vẫn vào;
        //   · chỉ máy chủ    → mục menu vẫn bày ra, bấm vào nhận 403 không rõ vì sao.
        // Đo thật 09/09/2026 trước khi sửa: trang01 không có mã quyền chat nào mà vẫn mở được
        // hộp thư, vì lúc đó KHÔNG đầu nào gác cả.
        var src = BoChuThich(Endpoint());

        // Gác ở NHÓM, không rải vào từng handler: nhóm này hơn ba mươi đường, thêm đường mới mà
        // quên kiểm là thủng mà không có triệu chứng — nó chạy đúng, chỉ là chạy cho người
        // không được phép.
        var m = Regex.Match(src, @"MapGroup\(""/api/v1/chat""\)(.{0,400})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy nhóm /api/v1/chat");
        var khaiNhom = m.Groups[1].Value;
        Assert.Contains("RequirePermissionFilter", khaiNhom);
        Assert.Contains("TkPermissionCodes.ChatXemTatCa", khaiNhom);
        Assert.Contains("TkPermissionCodes.ChatXem", khaiNhom);

        // Giao diện: mục menu khai ĐỦ HAI mã (đủ một là hiện — hasPerm dùng .some cho mảng), và
        // route cũng phải qua gatePerm. Thiếu vế route thì bookmark cũ vẫn mở được trang.
        var app = BoChuThich(ChatSchemaGuardTests.DocFile("wwwroot/app.jsx"));
        var muc = Regex.Match(app, @"to: '/chat-inbox'(.{0,300}?)\},", RegexOptions.Singleline);
        Assert.True(muc.Success, "Không thấy mục menu /chat-inbox");
        Assert.Contains("requirePerm", muc.Groups[1].Value);
        Assert.Contains("CHAT_XEM'", muc.Groups[1].Value);
        Assert.Contains("CHAT_XEM_ALL'", muc.Groups[1].Value);
        Assert.Contains("gatePerm('/chat-inbox'", app);
    }

    [Fact]
    public void Duong_doc_quyen_phai_lay_tu_PHIEN_chu_khong_goi_thang_CRM()
    {
        // MỘT nguồn sự thật. Máy chủ gác cửa bằng bản quyền lưu trong phiên; nếu giao diện lấy
        // bản đi thẳng CRM thì hai bản khác thời điểm, và menu với máy chủ nói ngược nhau —
        // thấy mục menu mà bấm vào nhận 403, hoặc mất mục menu cho thứ vẫn gọi được.
        //
        // Kèm lý do đo được (09/09/2026): 17 lượt gọi thẳng CRM trong một buổi, trung bình
        // 559ms, cá biệt 3,35 giây, ngay trên đường đăng nhập.
        var src = BoChuThich(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/TourEndpoints.cs"));
        var m = Regex.Match(src, @"MapGet\(""/permissions""(.{0,1800}?)\n        \}\);",
            RegexOptions.Singleline);
        Assert.True(m.Success, "Không cắt được handler /permissions");
        var than = m.Groups[1].Value;

        Assert.Contains("EnsurePermissionsAsync", than);
        Assert.DoesNotContain("/api/auth/permissions", than);

        // Nạp hụt PHẢI thành lỗi, không thành danh sách rỗng: rỗng là câu trả lời hợp lệ nên
        // giao diện sẽ tin và cất vào localStorage — mất sạch menu, không lỗi nào hiện ra, và
        // F5 cũng không cứu vì bản rỗng đã nằm trong bộ nhớ trình duyệt.
        Assert.Contains("!s.PermissionsLoaded", than);
        Assert.Contains("statusCode: 502", than);
    }
}
