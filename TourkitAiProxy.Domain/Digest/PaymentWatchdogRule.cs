namespace TourkitAiProxy.Domain.Digest;

/// 1 dòng tour kèm số tiền, lấy từ /api/ai/tours.
/// <param name="SellerUserName">Tên đăng nhập NV phụ trách — dùng để ghi Bảng tin ĐÍCH DANH.
/// null = tour chưa gán người phụ trách (thường là tour ghép) → workflow bỏ qua, xem
/// <see cref="PaymentWatchdogRule.Evaluate"/>.</param>
public record TourPaymentRow(int TourId, string Title, string? CustomerName, string? SellerName,
    DateTime DepartureDate, decimal Revenue, decimal ActualRevenue, string? SellerUserName = null);

/// 1 cảnh báo "sắp đi mà chưa thu đủ".
public record PaymentAlert(int TourId, string Title, string? CustomerName, string? SellerName,
    decimal Outstanding, DateTime DepartureDate, int DaysLeft, int Severity, string AlertKey,
    string? SellerUserName = null);

/// <summary>
/// Luật thuần (không gọi AI, không đụng DB): tour khởi hành trong <c>[hôm nay, +windowDays]</c>
/// mà <c>ActualRevenue &lt; Revenue</c> → còn nợ → cảnh báo.
///
/// <para><b>Mức độ:</b> còn ≤3 ngày → 2 (gấp), còn lại → 1 (nhắc). Chọn 3 vì dưới mức đó thì
/// đòi tiền trước giờ bay gần như không kịp.</para>
///
/// <para><b>Vì sao viết thành luật thuần thay vì nhét vào workflow:</b> sai ở đây tốn tiền thật —
/// bỏ sót thì tour khởi hành mà chưa thu đủ; báo thừa thì nhân viên nhờn cảnh báo rồi bỏ qua cả
/// cái thật. Tách ra mới test được hết các ca biên.</para>
///
/// <para><c>AlertKey = "payment:{tourId}"</c> để <see cref="InsightRepository.InsertAsync"/> chống
/// nhắc lại trong 24h — workflow chạy mỗi giờ nhưng mỗi tour chỉ nhắc 1 lần/ngày.</para>
/// </summary>
public static class PaymentWatchdogRule
{
    /// Còn ≤ số ngày này thì coi là gấp.
    private const int CriticalDays = 3;

    /// <summary>
    /// Cảnh báo này ghi cho AI: trả <c>Owner</c> = tài khoản nhận (rỗng = cả công ty), hoặc
    /// <c>Skip=true</c> = không báo ai cả.
    ///
    /// <para>Ba trạng thái, đừng gộp hai cái đầu:</para>
    /// <list type="bullet">
    /// <item><b>API chưa nâng cấp</b> (<paramref name="apiHasSellerField"/>=false): chưa có căn cứ
    /// chia người → giữ hành vi cũ, ghi cho cả công ty. Gộp nhầm sang "bỏ qua" thì hôm deploy proxy
    /// trước TourKit.Api là tác vụ IM LẶNG HOÀN TOÀN — không cảnh báo nào, không lỗi nào.</item>
    /// <item><b>Tour chưa gán ai</b> (có trường nhưng rỗng): BỎ QUA. Không rơi về cả công ty —
    /// cảnh báo ai cũng thấy = không ai chịu trách nhiệm, đúng cái đang sửa. Chỗ thiếu dồn vào tour
    /// ghép (GIT thiếu 90%, LandTour 95%; Dịch vụ lẻ/Booking/Visa/Vé máy bay thiếu 0% — staging
    /// 08/2026), mà tour ghép nhiều người cùng bán nên gán ai cũng sai.</item>
    /// <item><b>Có người phụ trách</b>: ghi đích danh.</item>
    /// </list>
    /// </summary>
    public static (string Owner, bool Skip) ResolveOwner(string? sellerUserName, bool apiHasSellerField)
    {
        if (!apiHasSellerField) return ("", false);                 // API cũ → cả công ty
        if (string.IsNullOrWhiteSpace(sellerUserName)) return ("", true);  // chưa gán → bỏ qua
        return (sellerUserName.Trim(), false);
    }

    /// <param name="minOutstanding">Nợ dưới mức này thì không nhắc. 0 = nhắc mọi khoản còn thiếu.
    /// Có ngưỡng vì chênh vài nghìn do làm tròn cũng là "còn nợ" theo đúng phép trừ, mà mỗi dòng
    /// như thế lại chiếm một thẻ trong Bảng tin.</param>
    public static List<PaymentAlert> Evaluate(IEnumerable<TourPaymentRow> rows, DateTime todayLocal,
        int windowDays = 7, decimal minOutstanding = 0m)
    {
        var result = new List<PaymentAlert>();
        // .Date cả 2 vế: DepartureDate hay todayLocal lỡ kèm giờ thì số ngày còn lại vẫn đúng,
        // không bị lệch 1 ngày ở mốc gần nửa đêm.
        var today = todayLocal.Date;

        foreach (var r in rows)
        {
            // Chưa chốt giá (Revenue<=0) thì khái niệm "còn nợ" vô nghĩa — nhắc chỉ gây nhiễu.
            if (r.Revenue <= 0) continue;

            var outstanding = r.Revenue - r.ActualRevenue;
            if (outstanding <= 0) continue;   // đủ hoặc trả dư
            if (minOutstanding > 0 && outstanding < minOutstanding) continue;

            var daysLeft = (r.DepartureDate.Date - today).Days;
            if (daysLeft < 0 || daysLeft > windowDays) continue;   // đã đi rồi, hoặc còn xa

            result.Add(new PaymentAlert(
                TourId: r.TourId,
                Title: r.Title,
                CustomerName: r.CustomerName,
                SellerName: r.SellerName,
                Outstanding: outstanding,
                DepartureDate: r.DepartureDate,
                DaysLeft: daysLeft,
                Severity: daysLeft <= CriticalDays ? 2 : 1,
                AlertKey: $"payment:{r.TourId}",
                SellerUserName: r.SellerUserName));
        }

        return result;
    }

    /// <summary>Một lá thư sắp gửi: gửi tới đâu, gồm những cảnh báo nào, của ai.</summary>
    /// <param name="OwnerUsername">Rỗng = thư gộp cả công ty (API chưa nâng cấp, hoặc bản sao
    /// gửi thêm cho kế toán/quản lý).</param>
    public record PaymentMailPlan(string ToEmail, List<PaymentAlert> Alerts, string OwnerUsername);

    /// <param name="OwnersWithoutEmail">Người phụ trách có cảnh báo nhưng chưa khai email —
    /// đếm để nói ra trong tóm tắt, KHÔNG được im lặng bỏ qua.</param>
    public record PaymentMailPlanResult(List<PaymentMailPlan> Mails, List<string> OwnersWithoutEmail);

    /// <summary>
    /// Chia cảnh báo thành thư: <b>mỗi người phụ trách một thư gộp</b>, không phải một thư chung
    /// gửi tất cả.
    ///
    /// <para><b>Vì sao đổi (20/08/2026):</b> Bảng tin vốn đã ghi ĐÍCH DANH người phụ trách
    /// (<see cref="ResolveOwner"/>) nhưng email lại gửi cho MỌI người đã bật email — cùng một cảnh
    /// báo mà hai cách chọn người nhận. Đo trên staging: một lá thư chứa 3 tour của 2 người phụ
    /// trách khác nhau, gửi cho một người thứ ba. Thư liệt kê tên khách và số tiền còn thiếu, nên
    /// đó không chỉ là ồn — người này đọc được tour của người kia.</para>
    ///
    /// <para><b>Vẫn gộp theo lượt quét, không phải mỗi tour một thư</b> — lý do cũ còn nguyên:
    /// hộp thư sáng ra 20 thư cùng tiêu đề thì chỉ cái đầu được đọc.</para>
    ///
    /// <para>Ba đường, đừng gộp:</para>
    /// <list type="bullet">
    /// <item><b>Có người phụ trách + đã khai email</b> → thư riêng, chỉ tour của họ.</item>
    /// <item><b>Có người phụ trách + chưa khai email</b> → KHÔNG gửi cho ai khác thay, chỉ đếm.
    /// Đẩy sang người khác là quay lại đúng cái vừa sửa.</item>
    /// <item><b>Owner rỗng</b> (API chưa nâng cấp — xem <see cref="ResolveOwner"/>) → giữ hành vi
    /// cũ: thư gộp cho mọi người bật email. Lúc đó chưa có căn cứ chia, im lặng còn tệ hơn.</item>
    /// </list>
    ///
    /// <para><paramref name="extraEmails"/> là địa chỉ người dùng TỰ GÕ ở ô "Gửi thêm tới" — chủ ý
    /// muốn một bản đầy đủ (kế toán, quản lý) nên nhận trọn danh sách. Khác hẳn việc vô tình gửi
    /// tất cả chỉ vì ai đó bật email để nhận bản tin sáng.</para>
    /// </summary>
    /// <param name="emailOfUser">tên đăng nhập → email, CHỈ gồm người đã bật kênh email.</param>
    public static PaymentMailPlanResult PlanMails(
        IReadOnlyList<PaymentAlert> alerts,
        bool apiHasSellerField,
        IReadOnlyDictionary<string, string> emailOfUser,
        IEnumerable<string>? extraEmails = null)
    {
        // Gộp theo địa chỉ: một người vừa là phụ trách vừa nằm trong ô "Gửi thêm tới" thì nhận
        // MỘT thư, không phải hai.
        var byAddr = new Dictionary<string, PaymentMailPlan>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        void Add(string addr, string owner, IEnumerable<PaymentAlert> items)
        {
            addr = addr.Trim();
            if (addr.Length == 0) return;
            if (!byAddr.TryGetValue(addr, out var plan))
                byAddr[addr] = plan = new PaymentMailPlan(addr, new List<PaymentAlert>(), owner);
            foreach (var a in items)
                if (plan.Alerts.All(x => x.TourId != a.TourId)) plan.Alerts.Add(a);
        }

        var companyWide = new List<PaymentAlert>();
        foreach (var g in alerts.GroupBy(a => ResolveOwner(a.SellerUserName, apiHasSellerField).Owner,
                                         StringComparer.OrdinalIgnoreCase))
        {
            var owner = g.Key ?? "";
            if (owner.Length == 0) { companyWide.AddRange(g); continue; }

            if (emailOfUser.TryGetValue(owner, out var addr) && !string.IsNullOrWhiteSpace(addr))
                Add(addr, owner, g);
            else
                missing.Add(owner);
        }

        // Owner rỗng: chưa có căn cứ chia → gửi cho mọi người bật email (hành vi cũ).
        if (companyWide.Count > 0)
            foreach (var addr in emailOfUser.Values) Add(addr, "", companyWide);

        // Bản sao đầy đủ cho địa chỉ tự gõ.
        foreach (var addr in extraEmails ?? Enumerable.Empty<string>()) Add(addr, "", alerts);

        return new PaymentMailPlanResult(
            byAddr.Values.ToList(),
            missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
