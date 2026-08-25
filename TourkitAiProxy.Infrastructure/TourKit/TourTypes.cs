using System.Text.Json;

namespace TourkitAiProxy.Infrastructure.TourKit;

/// <summary>
/// Mã loại tour của TourKit. Danh sách CỐ ĐỊNH của hệ thống (mirror <c>AiFormat.TourTypeName</c>
/// bên TourKit.Api) — KHÔNG phải mã mỗi công ty tự đặt.
///
/// <para><b>Vì sao phải có chỗ này:</b> <c>GET /api/ai/tours</c> mà không truyền <c>TourType</c> thì
/// upstream mặc định <b>2 = FIT</b> (xem <c>TourSearchRequest.TourType</c>) — nó KHÔNG trả "tất cả
/// loại". Đo thật trên staging 15/08: gọi không truyền loại ra 100/100 dòng FIT, trong khi công ty
/// đó có 100 tour GIT và 81 hồ sơ Visa. Tác vụ nào quét tour mà quên truyền loại thì mù đúng những
/// phần mình định canh, lại còn im lặng: log vẫn ghi "quét N tour", chỉ là N đó thiếu.</para>
/// </summary>
public static class TourTypes
{
    public const int LandTour = 1;
    public const int Fit = 2;
    public const int Git = 3;
    public const int Booking = 100;
    public const int DichVuLe = 101;
    public const int Visa = 102;
    public const int VeMayBay = 104;

    /// <summary>
    /// Mặc định quét: FIT + GIT — hai loại có khách thật sự lên đường, tức có chuyện "thiếu tiền",
    /// "thiếu khách", "sắp đầy chỗ". Cố ý KHÔNG quét sẵn Visa/Vé bay/Dịch vụ lẻ: chúng là dịch vụ
    /// bán rời, đưa hết vào thì số cảnh báo tăng vọt ngay lần đầu bật mà phần lớn không phải việc
    /// của điều hành tour. Công ty nào cần thì tick thêm.
    /// </summary>
    public static readonly IReadOnlyList<int> DefaultScan = new[] { Fit, Git };

    public static string Name(int t) => t switch
    {
        LandTour => "LandTour",
        Fit => "FIT",
        Git => "GIT",
        Booking => "Booking",
        DichVuLe => "Dịch vụ lẻ",
        Visa => "Visa",
        VeMayBay => "Vé máy bay",
        _ => $"Loại {t}",
    };

    /// <summary>
    /// Kéo tour theo khoảng ngày khởi hành, MỘT LƯỢT GỌI CHO MỖI LOẠI (upstream chỉ lọc được 1
    /// loại/lần). Trả về các phần tử <c>items</c> đã gộp; dòng nào lỗi thì bỏ qua loại đó chứ không
    /// làm hỏng cả lượt quét — mất một loại vẫn hơn mất tất cả.
    /// </summary>
    /// <param name="failed">Những loại gọi lỗi, để tác vụ nói ra trong tóm tắt thay vì im lặng.</param>
    /// <param name="extraQuery">Tham số lọc thêm, đã ở dạng <c>a=1&amp;b=2</c> (không có dấu ? đầu).</param>
    public static async Task<List<JsonElement>> FetchByTypesAsync(
        TourKitApiClient api, string jwt, IEnumerable<int> types,
        DateTime fromLocal, DateTime toLocal, int pageSize,
        List<string> failed, CancellationToken ct, string? extraQuery = null)
    {
        var rows = new List<JsonElement>();
        var extra = string.IsNullOrWhiteSpace(extraQuery) ? "" : "&" + extraQuery.TrimStart('&');
        foreach (var t in types.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await api.GetAsync(jwt,
                    $"/api/ai/tours?TourType={t}&StartDate={fromLocal:yyyy-MM-dd}&EndDate={toLocal:yyyy-MM-dd}&PageSize={pageSize}{extra}",
                    ct);
                if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    rows.AddRange(items.EnumerateArray());
            }
            catch (Exception) { failed.Add(Name(t)); }
        }
        return rows;
    }
}
