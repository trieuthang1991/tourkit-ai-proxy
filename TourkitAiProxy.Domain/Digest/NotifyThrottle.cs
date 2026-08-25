namespace TourkitAiProxy.Domain.Digest;

/// Đã nhắc về một đối tượng bao nhiêu lần, lần cuối khi nào, lúc đó trạng thái ra sao.
public record NotifyMark(int Times, DateTime FirstUtc, DateTime LastUtc, string? StateStamp);

/// <summary>
/// Quyết định "có nên nhắc lại không" — HÀM THUẦN, tách khỏi DB để test được mọi ca biên.
///
/// <para>Tách ra vì đây đúng loại logic sai trong im lặng: nhắc thừa thì người ta tắt tính năng,
/// nhắc thiếu thì việc rơi mất — cả hai đều không có lỗi nào hiện ra để ai đó phát hiện.</para>
/// </summary>
public static class NotifyThrottle
{
    /// <param name="mark">Lần nhắc trước, null = chưa nhắc bao giờ.</param>
    /// <param name="stateNow">Dấu vết trạng thái HIỆN TẠI của đối tượng (vd ngày chăm sóc gần
    /// nhất). Khác với lúc nhắc trước = đã có người xử lý thật → coi như chưa nhắc, đếm lại từ đầu.</param>
    /// <param name="minGapDays">Nhắc rồi thì im bấy nhiêu ngày. 0 = không giới hạn khoảng cách.</param>
    /// <param name="maxTimes">Nhắc tối đa bấy nhiêu lần rồi thôi. 0 = không giới hạn số lần.</param>
    /// <returns>Skip=true kèm <c>Reason</c> để tóm tắt lần chạy nói được VÌ SAO bỏ, thay vì chỉ
    /// đưa ra một con số.</returns>
    public static (bool Skip, string? Reason) Decide(
        NotifyMark? mark, string? stateNow, DateTime nowUtc, int minGapDays, int maxTimes)
    {
        if (mark == null) return (false, null);

        // Trạng thái đổi = đã có người động vào thật → vòng đời mới, quên hết lần nhắc cũ.
        // Kiểm TRƯỚC hai luật kia: khách vừa được chăm mà vẫn tới hạn lần nữa thì phải nhắc được,
        // không thì "đã đủ 3 lần" trở thành án chung thân.
        if (!string.Equals(mark.StateStamp ?? "", stateNow ?? "", StringComparison.Ordinal))
            return (false, null);

        if (maxTimes > 0 && mark.Times >= maxTimes)
            return (true, $"đã nhắc đủ {maxTimes} lần");

        if (minGapDays > 0 && (nowUtc - mark.LastUtc).TotalDays < minGapDays)
            return (true, $"vừa nhắc {(int)(nowUtc - mark.LastUtc).TotalDays} ngày trước");

        return (false, null);
    }
}
