using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Domain.Models;

/// <summary>
/// Nội dung chuyển khoản của đơn nạp lượt AI — DỰNG và BÓC ở cùng một chỗ.
///
/// <para>Trước đây nội dung CK <b>chính là</b> mã đơn (<c>TKAI-8E1FBB-6AAA0748-667D</c>). Khớp tiền
/// thì đúng, nhưng người chuyển khoản mở app ngân hàng lên chỉ thấy một dãy ký tự vô nghĩa — không
/// biết mình đang trả cho cái gì, và kế toán soi sổ phụ ngân hàng cũng không đọc ra công ty nào vừa
/// chuyển. Nên nội dung giờ có thêm phần người đọc được:
/// <c>"TKAI-8E1FBB-6AAA0748-667D Cong ty CP Tourkit thanh toan nap them luot AI"</c>.</para>
///
/// <para><b>MÃ ĐỨNG ĐẦU — đây là lựa chọn có chủ ý, đừng đảo lại.</b> Chuẩn NAPAS chỉ hứa 25 ký tự
/// cho trường nội dung (đúng bằng độ dài mã), ngân hàng nào cắt thì cắt từ CUỐI. Để tên công ty
/// trước thì một lần bị cắt là mất mã ⇒ tiền vào mà không cộng lượt, không màn hình nào báo lỗi.
/// Để mã trước thì bị cắt chỉ mất phần chữ trang trí, đường cộng lượt vẫn nguyên.</para>
///
/// <para><b>Mã là thứ duy nhất dùng để đối soát.</b> <see cref="ExtractOrderId"/> quét mã ở bất kỳ
/// vị trí nào trong nội dung nên phần chữ quanh nó không ảnh hưởng. Đổi câu chữ thoải mái; đừng
/// đụng vào mã và đừng đẩy nó ra sau.</para>
///
/// <para>Không có tên công ty trong nội dung vẫn tra ra được công ty: mã gắn cứng với đơn, mà đơn
/// (<c>dbo.QuotaOrders</c>) lưu sẵn TenantId + người tạo. Phần chữ chỉ để đọc bằng mắt trên sổ phụ.</para>
///
/// <para><b>KHÔNG DẤU.</b> Nội dung đi vào payload VietQR (EMVCo) rồi qua nhiều cổng ngân hàng —
/// tiếng Việt có dấu chỗ thì hiện ký tự rác, chỗ thì bị lọc mất. Bỏ dấu ngay từ lúc dựng để cái hiện
/// trên app ngân hàng đúng bằng cái ta lưu ở <c>QuotaOrders.Memo</c>.</para>
/// </summary>
public static class QuotaMemo
{
    /// Câu mô tả đứng sau tên công ty. Không dấu, không ký tự đặc biệt.
    public const string FixedText = "thanh toan nap them luot AI";

    /// <summary>
    /// Trần độ dài nội dung CK. Cột <c>QuotaOrders.Memo</c> là NVARCHAR(128); chừa biên để không bao
    /// giờ chạm tới giới hạn cột. Khi vượt trần thì cắt bớt TÊN CÔNG TY — mã (đứng đầu) và câu mô tả
    /// luôn giữ nguyên.
    /// </summary>
    public const int MaxLength = 120;

    /// <summary>
    /// Dựng nội dung CK: <c>"{orderId} {tên công ty không dấu} {FixedText}"</c>.
    /// Không có tên công ty (phiên cũ chưa lưu, hoặc tên rỗng sau khi lọc) thì bỏ luôn phần đó —
    /// nội dung ngắn hơn vẫn đối soát được, còn hơn chèn chữ giữ chỗ vô nghĩa.
    /// </summary>
    public static string Build(string orderId, string? companyName)
    {
        var cty = SanitizeCompany(companyName);
        if (cty.Length == 0) return $"{orderId} {FixedText}";

        // Phần cố định luôn được giữ; chỉ tên công ty phải co lại cho vừa trần.
        var budget = MaxLength - orderId.Length - FixedText.Length - 2;   // -2 cho hai dấu cách nối
        if (budget < 1) return $"{orderId} {FixedText}";
        if (cty.Length > budget) cty = CatTheoTu(cty, budget);

        return cty.Length == 0 ? $"{orderId} {FixedText}" : $"{orderId} {cty} {FixedText}";
    }

    /// <summary>
    /// Bỏ dấu + chỉ giữ chữ/số/dấu cách. Ký tự đặc biệt bị loại vì nhiều ngân hàng tự lọc chúng
    /// khỏi nội dung CK — để lại thì cái người dùng thấy khác cái ta lưu.
    /// </summary>
    public static string SanitizeCompany(string? ten)
    {
        var s = (ten ?? "").Trim();
        if (s.Length == 0) return "";

        // đ/Đ phải thay TRƯỚC khi bóc dấu: nó không phải nguyên âm mang dấu, FormD không tách ra.
        s = s.Replace('đ', 'd').Replace('Đ', 'D');
        s = new string(s.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).Normalize(NormalizationForm.FormC);

        s = Regex.Replace(s, @"[^a-zA-Z0-9\s]", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// Cắt về đúng trần, lùi tới ranh giới từ gần nhất (tên bị cắt giữa từ đọc như lỗi hiển thị).
    private static string CatTheoTu(string s, int max)
    {
        var cut = s[..max];
        var sp = cut.LastIndexOf(' ');
        return (sp > 0 ? cut[..sp] : cut).TrimEnd();
    }

    /// <summary>
    /// Bóc OrderId TKAI-XXXXXX-XXXXXXXX-XXXX khỏi nội dung CK (ngân hàng thường thêm chữ quanh nó,
    /// vd VCB prepend "TT CK QR"; từ 09/2026 chính ta cũng thêm tên công ty + câu mô tả ở sau).
    /// </summary>
    public static string? ExtractOrderId(string? memo)
    {
        if (string.IsNullOrWhiteSpace(memo)) return null;

        // Dấu gạch nối là TUỲ CHỌN ở đây, cố ý. Nhiều đường đi làm mất nó trước khi tới được đây:
        //   • BankHubService.GenerateQrCode bên web tự lọc `[^a-zA-Z0-9\s]` khỏi nội dung CK
        //     → "TKAI-8E1FBB-6A90F5AA-9A82" thành "TKAI8E1FBB6A90F5AA9A82";
        //   • một số ngân hàng cũng bỏ ký tự đặc biệt trong nội dung khi đẩy sang cổng trung gian.
        // Khớp cứng dấu gạch thì những ca đó rơi vào nhánh "không có mã TKAI" → tiền vào mà không
        // cộng lượt, và người dùng chẳng thấy lỗi gì.
        //
        // Ghép lại được vì cấu trúc mã là cố định: TKAI + 6 hex (băm công ty) + N hex (thời điểm)
        // + 4 hex (ngẫu nhiên) — biết 6 đầu và 4 cuối thì phần giữa là phần còn lại, không mơ hồ.
        // Thử dạng CÓ gạch nối TRƯỚC. Gộp hai dạng vào một biểu thức với gạch nối tuỳ chọn nghe gọn
        // hơn nhưng SAI: phần giữa dài bao nhiêu là mơ hồ, "TKAI-8E1FBB-6A90F5AA-9A82" bị cắt nhầm
        // thành "TKAI-8E1FBB-6A90-F5AA". Tách hai bước thì mỗi bước chỉ có một cách hiểu.
        const RegexOptions Ci = RegexOptions.IgnoreCase;

        var m = Regex.Match(memo, @"TKAI-[A-F0-9]{6}-[A-F0-9]+-[A-F0-9]{4}", Ci);
        if (m.Success) return m.Value.ToUpperInvariant();

        // Dạng đã bị lọc mất gạch nối: lấy trọn cụm hex rồi cắt lại theo cấu trúc cố định
        // (6 đầu = băm công ty · 4 cuối = số ngẫu nhiên · phần giữa = thời điểm).
        var m2 = Regex.Match(memo, @"TKAI([A-F0-9]{14,26})(?![A-F0-9])", Ci);
        if (!m2.Success) return null;

        var hex = m2.Groups[1].Value.ToUpperInvariant();

        // Cụm hex DÀI HƠN cấu trúc thật thì cắt về đúng 18. Ca này có thật kể từ khi mã chuyển lên
        // ĐẦU nội dung: chữ đứng sau mã có thể bắt đầu bằng chữ cái hex, mà ngân hàng nào xoá luôn
        // dấu cách thì nó dính liền vào đuôi mã — "…667D" + "Cong ty…" thành "…667DC", cắt theo
        // "4 ký tự cuối" sẽ ra mã sai và đơn không bao giờ khớp.
        // 18 = 6 (băm công ty) + 8 (thời điểm, dạng hex của giây Unix — 8 chữ số từ 1978 tới 2109)
        // + 4 (ngẫu nhiên). Ngắn hơn 18 thì vẫn cắt theo đầu/cuối như cũ để chịu được mã lạ.
        const int DoDaiChuan = 18;
        if (hex.Length > DoDaiChuan) hex = hex[..DoDaiChuan];

        return $"TKAI-{hex[..6]}-{hex[6..^4]}-{hex[^4..]}";
    }
}
