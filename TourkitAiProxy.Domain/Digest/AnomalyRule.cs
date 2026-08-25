using System.Globalization;
using TourkitAiProxy.Domain.Speech;

namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// Phát hiện tuần bất thường (C2): doanh thu tuần vừa rồi lệch quá xa mức bình thường của mấy tuần
/// trước → dựng cảnh báo.
///
/// <para><b>Vì sao so theo TUẦN chứ không theo ngày:</b> doanh thu tour giật cục — một hợp đồng
/// đoàn về đúng thứ Ba là ngày đó gấp mười ngày thường. So từng ngày sẽ báo động gần như mỗi ngày.
/// Gộp tuần làm phẳng chuyện đó mà vẫn đủ sớm để xoay.</para>
///
/// <para><b>Nền tính bằng TRUNG VỊ, không phải trung bình.</b> Một tuần có hợp đồng lớn gấp mười sẽ
/// kéo trung bình lên cao, và mọi tuần bình thường sau đó đều trông như "sụt giảm bất thường" —
/// tức là một tuần đẹp lại sinh ra hàng loạt báo động giả.</para>
///
/// <para><b>Ba chỗ im lặng có chủ ý:</b> chưa đủ tuần nền (so với tiếng ồn), nền bằng 0 (phần trăm
/// vô nghĩa), và lệch trong ngưỡng (ngành theo mùa, lên xuống là chuyện thường). Báo động mỗi tuần
/// thì người ta tắt tính năng sau ba lần — lúc đó cảnh báo thật cũng mất theo.</para>
/// </summary>
public static class AnomalyRule
{
    /// Dưới 2 tuần nền thì "bình thường" chỉ là ngẫu nhiên của đúng tuần đó.
    public const int MinBaselineWeeks = 2;

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => TourkitAiProxy.Shared.Text.Money.So(v);

    /// <param name="Severity">0 = tin vui (tăng) · 1 = cần để ý · 2 = gấp.</param>
    public record Result(decimal Current, decimal Baseline, int DeviationPercent, int Severity, string Text);

    /// <summary>Trả <c>null</c> = KHÔNG dựng cảnh báo nào.</summary>
    /// <param name="baseline">Doanh thu của các tuần TRƯỚC tuần đang xét.</param>
    public static Result? Detect(decimal current, IReadOnlyList<decimal> baseline, int thresholdPercent)
    {
        if (baseline.Count < MinBaselineWeeks) return null;

        var median = Median(baseline);
        if (median <= 0) return null;   // chia cho 0 — và "tăng vô hạn %" thì không ai đọc được

        var deviation = (int)Math.Round((current - median) / median * 100, MidpointRounding.AwayFromZero);
        if (Math.Abs(deviation) < thresholdPercent) return null;

        var up = deviation > 0;
        // Tăng vọt là TIN VUI — tô mức cảnh báo ở đây làm người đọc hoảng nhầm, và lần sau họ đọc
        // mọi cảnh báo bằng con mắt nghi ngờ.
        var severity = up ? 0 : (deviation <= -50 ? 2 : 1);

        var text = $"Doanh thu tuần vừa rồi {Vnd(current)}đ, "
                 + $"{(up ? "tăng" : "giảm")} {Math.Abs(deviation)}% so với mức thường "
                 + $"{Vnd(median)}đ/tuần của {baseline.Count} tuần trước.";

        return new Result(current, median, deviation, severity, text);
    }

    /// Trung vị. Số chẵn phần tử thì lấy trung bình hai giá trị giữa.
    private static decimal Median(IReadOnlyList<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
