using System.Globalization;
using System.Text;

namespace TourkitAiProxy.Services.Digest;

public record CeoNumbers(decimal Revenue, decimal Expense, decimal Profit);

/// <param name="TodayAppointments">Lịch hẹn CSKH hôm nay của cả công ty.</param>
/// <param name="OverdueAppointments">Lịch hẹn đã quá hạn mà chưa xử lý — con số này mới là
/// thứ giám đốc cần thấy: hẹn nhiều mà quá hạn nhiều nghĩa là đội đang không theo kịp.</param>
/// <param name="ShowCompare">Có in phần trăm so kỳ trước không. Tắt khi công ty chọn "không so sánh"
/// — kỳ trước lúc đó không được lấy, in "n/a" khắp nơi thì đọc như hệ thống hỏng.</param>
/// <param name="CompareLabel">Gọi tên kỳ so sánh cho đúng ("so cùng kỳ tháng trước" / "so cùng kỳ
/// năm trước"). Nhãn cứng sẽ NÓI SAI ngay khi công ty đổi kỳ so sánh.</param>
/// <param name="ShowNumbers">Đính bảng số dưới bài AI viết. Tắt thì bản tin chỉ còn lời văn —
/// gọn hơn nhưng mất chỗ đối chiếu, nên mặc định bật.</param>
public record CeoBriefData(CeoNumbers ThisMtd, CeoNumbers PrevMtd, List<string> TopSellers,
    int NewDealsYesterday, int OpenPaymentAlerts,
    int TodayAppointments = 0, int OverdueAppointments = 0,
    bool ShowSellers = true, bool ShowNewDeals = true, bool ShowAppointments = true,
    bool ShowAlerts = true, bool ShowNumbers = true,
    bool ShowCompare = true, string CompareLabel = "so cùng kỳ tháng trước");

/// <summary>
/// Dựng bản tin điều hành cho giám đốc.
///
/// <para><b>Số do máy chủ tính, AI CHỈ viết lời.</b> Đây là ranh giới cố ý: AI mà tự tính thì
/// sai số là chuyện sớm muộn, mà sai số trong báo cáo cho giám đốc thì tai hại hơn hẳn diễn đạt
/// kém. Prompt đưa số ĐÃ tính sẵn kèm lệnh cấm bịa thêm số nào ngoài danh sách.</para>
///
/// <para><b>AI lỗi hoặc hết lượt thì bản tin KHÔNG mất</b> — rơi về <see cref="RenderFallback"/>
/// in thẳng bảng số. Thà đọc bảng số khô còn hơn sáng ra không có gì.</para>
///
/// <para>Bảng số luôn được đính kèm DƯỚI phần AI viết, để người đọc đối chiếu được ngay —
/// không phải tin AI suông.</para>
/// </summary>
public static class CeoBriefBuilder
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    /// Public để nơi lấy số (CeoBriefWorkflow) định dạng CÙNG một kiểu — bản tin trộn hai kiểu số
    /// đọc như ghép từ hai nguồn khác nhau.
    public static string Vnd(decimal v) => v.ToString("N0", Vi) + "đ";

    /// So sánh kỳ này với kỳ trước. Kỳ trước = 0 → "n/a" chứ không phải "+∞%" hay chia cho 0.
    ///
    /// <para><b>Đổi DẤU thì không in phần trăm.</b> Lãi 200tr thành lỗ 644tr ra "-101%" — con số đó
    /// vô nghĩa (giảm 101% là giảm quá cả số ban đầu) và đọc lên còn nhẹ hơn thực tế, trong khi đây
    /// mới là điều đáng báo động nhất của bản tin. Nói thẳng "chuyển từ lãi sang lỗ".</para>
    public static string PctChange(decimal cur, decimal prev)
    {
        if (prev == 0) return "n/a";
        if (prev > 0 && cur < 0) return "chuyển từ lãi sang lỗ";
        if (prev < 0 && cur > 0) return "chuyển từ lỗ sang lãi";

        // Cả hai kỳ đều ÂM: công thức phần trăm cho dấu NGƯỢC với ý nghĩa. Lỗ 100tr thành lỗ 150tr
        // ra "+50%" — đọc như đang khá lên, trong khi thực tế lỗ nặng thêm. Nói bằng chữ.
        if (prev < 0 && cur < 0)
        {
            var m = Math.Round(Math.Abs((cur - prev) / prev * 100));
            if (m == 0) return "lỗ gần như không đổi";
            return cur < prev ? $"lỗ nặng thêm {m}%" : $"lỗ giảm {m}%";
        }

        var pct = Math.Round((cur - prev) / prev * 100);
        return (pct >= 0 ? "+" : "") + pct + "%";
    }

    /// Số âm gọi thẳng là LỖ. "Lợi nhuận: -644.211.149đ" bắt người đọc tự nhận ra dấu trừ —
    /// mà dấu trừ là thứ dễ lướt qua nhất trong một dòng toàn chữ số.
    private static string ProfitLine(decimal v)
        => v < 0 ? $"Lỗ: {Vnd(-v)}" : $"Lợi nhuận: {Vnd(v)}";

    /// <summary>
    /// Cách đọc gọn ("4,53 tỷ đồng", "644 triệu đồng") — MÁY CHỦ tính, không để AI tự quy đổi.
    ///
    /// <para>Vì sao phải làm: đọc "4.531.460.000đ" giữa câu văn thì nặng, nên AI tự rút gọn cho
    /// tự nhiên. Nhưng quy đổi đơn vị CŨNG LÀ MỘT PHÉP TÍNH, mà đây là chỗ đã có luật "số do máy
    /// chủ tính, AI chỉ viết lời". Lần chạy thật: AI viết "lỗ 644 tỷ đồng" trong khi số thật là
    /// 644.211.149đ — sai đúng một bậc, mà trong báo cáo cho giám đốc thì sai một bậc là hỏng cả
    /// bản tin. Nay đưa sẵn chuỗi rút gọn vào prompt để AI chỉ việc chép.</para>
    /// </summary>
    public static string Short(decimal v)
    {
        var a = Math.Abs(v);
        var (num, unit) = a switch
        {
            >= 1_000_000_000m => (v / 1_000_000_000m, "tỷ"),
            >= 1_000_000m     => (v / 1_000_000m,     "triệu"),
            >= 1_000m         => (v / 1_000m,         "nghìn"),
            _                 => (v,                  ""),
        };
        // 1 chữ số thập phân là đủ để giữ ý nghĩa mà không dài dòng; bỏ ",0" cho tròn số.
        var s = Math.Round(num, 1).ToString("0.#", Vi);
        return unit.Length == 0 ? $"{s} đồng" : $"{s} {unit} đồng";
    }

    /// <summary>
    /// Không có hẹn nào thì nói thẳng "không có cuộc nào" — để trống dễ bị hiểu là lỗi lấy dữ liệu.
    ///
    /// <para><b>Quá hạn gọi đúng tên là "tồn đọng", không phải "cần xử lý ngay".</b> Nguồn
    /// (<c>/api/ai/appointments?dateFilter=3</c>) trả MỌI cuộc quá hạn chưa đóng từ trước tới nay,
    /// nên ở CRM dùng lâu con số này lên hàng nghìn. Lần chạy thật đầu tiên ra 2.338 cuộc và AI viết
    /// thành "cần xử lý ngay lập tức" — đọc như công ty đang cháy, mà thực chất là nợ dữ liệu tích
    /// luỹ nhiều năm. Số vẫn hiện đủ (che đi là giấu vấn đề), chỉ gọi tên cho đúng.</para>
    /// </summary>
    private static string AppointmentLine(CeoBriefData d)
    {
        if (d.TodayAppointments == 0 && d.OverdueAppointments == 0) return "không có cuộc nào";
        var s = $"{Num(d.TodayAppointments)} cuộc";
        if (d.OverdueAppointments > 0) s += $" · tồn đọng {Num(d.OverdueAppointments)} cuộc quá hạn (tích luỹ từ trước)";
        return s;
    }

    /// Số đếm cũng cần dấu phân cách nghìn: "2345 cuộc" đọc phải dừng lại đếm chữ số, trong khi
    /// mọi số tiền quanh nó đều đã có dấu chấm — nhìn như hai nguồn khác nhau.
    private static string Num(int v) => v.ToString("N0", Vi);

    /// Bảng số — chỉ in những mục công ty chọn đưa vào bản tin. Mục bị tắt thì KHÔNG lấy số nên
    /// cũng không được in: in "0" cho một mục không lấy là nói dối, người đọc tưởng thật sự bằng 0.
    ///
    /// <param name="forPrompt">Bản đưa cho AI thì kèm cách đọc gọn của từng số (xem <see cref="Short"/>).
    /// Bản in cho người đọc thì không — đọc "4.531.460.000đ (4,53 tỷ đồng)" là thừa.</param>
    private static string NumbersBlock(CeoBriefData d, bool forPrompt = false)
    {
        var sb = new StringBuilder();
        string Cmp(decimal cur, decimal prev, bool withLabel = false)
            => d.ShowCompare ? $" ({PctChange(cur, prev)}{(withLabel ? " " + d.CompareLabel : "")})" : "";
        string Say(decimal v) => forPrompt ? $" [đọc gọn: {Short(v)}]" : "";

        sb.AppendLine($"- Doanh thu: {Vnd(d.ThisMtd.Revenue)}{Say(d.ThisMtd.Revenue)}{Cmp(d.ThisMtd.Revenue, d.PrevMtd.Revenue, true)}");

        // Chi phí 0đ ở một CRM đang chạy nghĩa là CHƯA GHI NHẬN, không phải "không tốn đồng nào"
        // (đã gặp thật ở erp.tourkit.vn). In "0đ (n/a)" thì vừa trông như lỗi hệ thống, vừa kéo theo
        // dòng lợi nhuận bằng đúng doanh thu — đọc lướt là tưởng lãi trọn, mà đó là kết luận tài
        // chính sai. Nói thẳng ra và gắn nhãn cho dòng lợi nhuận.
        var noExpense = d.ThisMtd.Expense == 0;
        if (noExpense)
        {
            sb.AppendLine("- Chi phí: chưa ghi nhận trong hệ thống");
            var p = d.ThisMtd.Profit;
            sb.AppendLine($"- Lợi nhuận (CHƯA trừ chi phí): {Vnd(p)}{Say(Math.Abs(p))}");
        }
        else
        {
            sb.AppendLine($"- Chi phí: {Vnd(d.ThisMtd.Expense)}{Say(d.ThisMtd.Expense)}{Cmp(d.ThisMtd.Expense, d.PrevMtd.Expense)}");
            sb.AppendLine($"- {ProfitLine(d.ThisMtd.Profit)}{Say(Math.Abs(d.ThisMtd.Profit))}{Cmp(d.ThisMtd.Profit, d.PrevMtd.Profit)}");
        }

        // Hai số này trước nằm chung một dòng; tách ra để tắt riêng từng cái mà dòng không bị cụt
        // kiểu "Cơ hội mới hôm qua: 3 · " .
        if (d.ShowNewDeals) sb.AppendLine($"- Cơ hội mới hôm qua: {d.NewDealsYesterday}");
        if (d.ShowAlerts) sb.AppendLine($"- Cảnh báo thanh toán đang mở: {d.OpenPaymentAlerts}");
        if (d.ShowAppointments) sb.AppendLine($"- Lịch hẹn hôm nay: {AppointmentLine(d)}");
        if (d.ShowSellers)
            sb.AppendLine($"- Top nhân viên bán hàng từ đầu tháng: {(d.TopSellers.Count > 0 ? string.Join("; ", d.TopSellers) : "n/a")}");

        return sb.ToString().TrimEnd();
    }

    public static string BuildPrompt(CeoBriefData d, DateTime todayLocal) =>
        $"Bạn là giám đốc tài chính đang tóm tắt tình hình cho giám đốc công ty du lịch. " +
        $"Hôm nay {todayLocal:dd/MM/yyyy}.\n" +
        // MỐC THỜI GIAN: số là LUỸ KẾ từ đầu tháng, không phải của riêng hôm nay. Thiếu dòng này
        // AI mở bài bằng "Tình hình kinh doanh HÔM NAY khá khó khăn" trong khi đang nói về 14 ngày.
        $"Các số tài chính dưới đây là LUỸ KẾ từ 01/{todayLocal:MM} đến hết {todayLocal:dd/MM}, " +
        "KHÔNG phải số của riêng hôm nay — đừng viết \"hôm nay doanh thu…\".\n" +
        "Viết 5-8 câu tiếng Việt tổng kết tình hình từ CHÍNH XÁC các số dưới đây. " +
        "TUYỆT ĐỐI không bịa thêm số nào ngoài input, không dùng tiêu đề markdown, " +
        "giọng tự nhiên, đi thẳng vào ý chính, nêu rõ điều đáng lưu ý nhất.\n" +
        // Đây là phần khiến bản tin đáng đọc: một dòng "nên làm gì". Không dặn thì AI chỉ đọc lại
        // bảng số bằng lời — giám đốc đã nhìn thấy bảng số ngay bên dưới rồi.
        "Kết bằng 1-2 câu KHUYẾN NGHỊ cụ thể nên làm gì tiếp, bám đúng số đang có.\n" +
        // Doanh số nhân viên đến từ nguồn khác (giá trị phiếu đặt) nên KHÔNG cộng khớp doanh thu ghi
        // nhận. Lần chạy thật: top 3 cộng lại 9,1 tỷ trong khi doanh thu toàn công ty 4,5 tỷ — AI đặt
        // hai số cạnh nhau tỉnh bơ. Cấm so chéo là cách duy nhất tránh kết luận sai.
        "Doanh số của nhân viên đo theo giá trị phiếu đặt, KHÔNG cùng thước đo với doanh thu ghi " +
        "nhận — tuyệt đối không cộng chúng lại, không so hai nhóm số này với nhau, không suy ra " +
        "mâu thuẫn từ chúng.\n" +
        // Biến động cỡ này ở CRM thường là dữ liệu chưa nhập/nhập sót, không phải kinh doanh sụp đổ.
        // Kết luận "khó khăn" từ một con số chưa kiểm là kiểu sai mất lòng tin ngay lần đọc đầu.
        "Nếu một chỉ số biến động quá 80%, coi đó là dấu hiệu cần KIỂM TRA LẠI SỐ LIỆU trước khi " +
        "kết luận về kinh doanh — nói rõ điều đó thay vì khẳng định công ty đang tốt hay xấu.\n" +
        // Quy đổi đơn vị cũng là một PHÉP TÍNH. Lần chạy thật: AI viết "lỗ 644 tỷ đồng" cho số
        // 644.211.149đ — lệch đúng một bậc. Đưa sẵn cách đọc gọn, cấm tự tính lại.
        "Mỗi số tiền đã kèm sẵn cách đọc gọn trong ngoặc vuông. Khi nhắc trong câu văn, dùng ĐÚNG " +
        "cách đọc gọn đó (hoặc chép nguyên số đầy đủ). TUYỆT ĐỐI không tự quy đổi sang tỷ/triệu/" +
        "nghìn theo cách của bạn, không làm tròn khác đi.\n" +
        // Không có dòng này thì AI lấy con số tồn đọng lớn nhất làm tiêu điểm và viết "cần xử lý
        // ngay lập tức" — báo động sai về việc đã nằm đó nhiều năm (xem AppointmentLine).
        "Lưu ý: số 'tồn đọng ... quá hạn' là tích luỹ từ trước, KHÔNG phải việc phát sinh hôm nay — " +
        "nếu có nói thì nói như việc cần dọn dần, đừng gọi là khẩn cấp trong ngày.\n" +
        // Chi phí 0đ là chuyện thật ở công ty chưa ghi nhận chi phí vào CRM (đã gặp lần chạy thật) —
        // AI mà tự suy "không phát sinh chi phí" thì thành kết luận sai về hoạt động kinh doanh.
        "Nếu chi phí bằng 0, hiểu là CHƯA GHI NHẬN trong hệ thống, không được kết luận là công ty " +
        "không phát sinh chi phí hay lãi trọn doanh thu.\n" +
        // Nền vài chục triệu thì "+21%" chỉ là vài triệu — gọi đó là "xu hướng tích cực" nghe như
        // công ty đang bứt tốc. Đã gặp thật ở erp: 25tr → 31tr.
        "Nếu doanh thu kỳ này dưới 100 triệu đồng, đây là quy mô nhỏ nên phần trăm biến động ít ý " +
        "nghĩa — nói con số tuyệt đối, đừng kết luận xu hướng mạnh chỉ dựa vào phần trăm.\n" +
        // Công ty tắt so sánh kỳ trước → prompt không có phần trăm nào. Không dặn thì AI vẫn viết
        // "tăng so với tháng trước" theo thói quen, tức là bịa ra một so sánh không có số.
        (d.ShowCompare ? "" : "Bộ số này KHÔNG có kỳ so sánh — tuyệt đối không viết tăng/giảm so với kỳ trước.\n") +
        "\n" + NumbersBlock(d, forPrompt: true);

    public static DigestMessage RenderFallback(CeoBriefData d, DateTime todayLocal)
        => Wrap(NumbersBlock(d), todayLocal);

    public static DigestMessage WrapAiReply(string aiProse, CeoBriefData d, DateTime todayLocal)
        => Wrap(d.ShowNumbers
                    ? aiProse.Trim() + "\n\n**Số liệu:**\n" + NumbersBlock(d)
                    : aiProse.Trim(),
                todayLocal);

    private static DigestMessage Wrap(string bodyMd, DateTime todayLocal)
        => new($"Bản tin điều hành {todayLocal:dd/MM}", bodyMd,
               SaleBriefBuilder.ToHtml(bodyMd),   // dùng chung 1 bộ đổi HTML → không lệch cách escape
               BriefTypes.Ceo);
}
