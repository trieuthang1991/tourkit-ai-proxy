// Services/Chat/Inbox/ChatMediaMirror.cs
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Security.Cryptography;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Storage;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Soi ảnh/tệp khách gửi về <b>kho riêng của mình</b> ngay lúc nhận, rồi dùng URL của mình.
///
/// <para><b>Vì sao bắt buộc.</b> Không kênh nào cho giữ tệp lâu dài:</para>
/// <list type="bullet">
///   <item><b>Meta</b> ký hạn thẳng vào URL — tham số <c>oe=</c> là mốc hết hạn. Đo trên dữ liệu
///     thật trong hộp thư staging (27/08/2026): ảnh khách vừa gửi hôm đó hết hạn <b>01/09/2026</b>,
///     tức sống đúng 5 ngày.</item>
///   <item><b>Telegram</b> còn ngặt hơn: không cho URL, chỉ cho <c>file_id</c> phải đổi lấy đường
///     tải sống khoảng một giờ. Bot bị gỡ là mất sạch, kể cả <c>file_id</c> cũng vô dụng.</item>
///   <item><b>WhatsApp</b> đòi kèm khoá khi tải, và mã tệp cũng hết hạn.</item>
/// </list>
///
/// <para>Lưu URL của nền tảng nghĩa là hộp thư <b>tự rỗng dần theo thời gian</b> mà không ai làm gì
/// sai — và khi phát hiện thì đã quá muộn để tải về.</para>
///
/// <para><b>Chống lặp — hai khoá cho hai loại, cố ý khác nhau:</b></para>
/// <list type="table">
///   <listheader><term>Loại</term><description>Khoá &amp; lý do</description></listheader>
///   <item>
///     <term>Nhãn dán / icon</term>
///     <description><c>sticker/{kênh}/{sticker_id}</c>. Nền tảng cấp mã CỐ ĐỊNH: cái like luôn là
///     <c>369239263222822</c> với mọi khách, mọi công ty, mãi mãi. Hỏi kho TRƯỚC khi tải, nên cái
///     like đầu tiên tải một lần rồi mọi lượt sau <b>không tải, không ghi, không tốn chỗ</b> — đây
///     là ca lặp nhiều nhất nên cũng là chỗ tiết kiệm nhất.</description>
///   </item>
///   <item>
///     <term>Ảnh/tệp khách gửi</term>
///     <description><c>chat/{công ty}/{sha256}</c>. Phải tải mới băm được nên không tiết kiệm băng
///     thông, nhưng hai khách gửi cùng một tấm chỉ tốn một chỗ.</description>
///   </item>
/// </list>
///
/// <para>⚠️ <b>Nhãn dán dùng chung mọi công ty, ảnh khách thì KHÔNG.</b> Nhãn dán là tài sản công
/// khai của nền tảng, giống hệt nhau ở mọi nơi. Ảnh khách gửi là dữ liệu riêng của công ty đó —
/// khoá theo <c>tenant</c> để hai công ty không bao giờ dùng chung một đối tượng.</para>
/// </summary>
public class ChatMediaMirror
{
    /// <summary>
    /// Chặn cỡ tệp. Vượt thì GIỮ NGUYÊN url gốc thay vì bỏ tin: ảnh hết hạn sau vài ngày vẫn hơn
    /// là không có gì, và tải một tệp khổng lồ sẽ chặn cả hàng đợi tin.
    /// </summary>
    private const long MaxBytes = 25 * 1024 * 1024;

    /// <summary>Hạn giờ cho MỘT lượt tải tệp. Xem lý do ở chỗ dùng.</summary>
    private const int HanTaiGiay = 30;

    private readonly IHttpClientFactory _http;
    private readonly IChatFileStorage _kho;
    private readonly ILogger<ChatMediaMirror> _log;

    public ChatMediaMirror(IHttpClientFactory http, IChatFileStorage kho, ILogger<ChatMediaMirror> log)
    { _http = http; _kho = kho; _log = log; }

    public bool Configured => _kho.Configured;

    /// <summary>Đoạn đầu url của kho mình — để nhận ra ảnh nào đã soi rồi. Xem
    /// <see cref="IChatFileStorage.PublicBase"/>.</summary>
    public string? KhoCuaMinh => _kho.PublicBase;

    /// <param name="StickerId">Mã nhãn dán do nền tảng cấp, nếu tệp này là nhãn dán.</param>
    /// <param name="Auth">Khoá kèm theo khi tải (WhatsApp bắt buộc; Meta thường không cần).</param>
    public record NguonTep(string Url, string? StickerId = null, string? Auth = null);

    /// <summary>
    /// Kết quả một lượt soi.
    ///
    /// <para><b>Vì sao phải phân biệt hỏng vĩnh viễn với hỏng tạm.</b> Cả hai đều trả
    /// <c>Url = null</c>, nhưng đáng đối xử ngược nhau: url đã hết hạn thì thử lại một trăm lần
    /// vẫn hỏng, còn CDN treo một phút thì lần sau là được. Gộp chung lại nghĩa là hoặc bỏ sớm
    /// những tấm còn cứu được, hoặc tải lại mãi những tấm đã chết — mà số tấm đã chết chỉ tăng
    /// theo thời gian, nên vế sau là kiểu hỏng lớn dần cùng dữ liệu.</para>
    /// </summary>
    /// <param name="Url">Url trong kho của mình; <c>null</c> khi không soi được.</param>
    /// <param name="HetCuu">Thử lại cũng vô ích: url hết hạn, tệp bị gỡ, tệp quá khổ.</param>
    public record KetQuaSoi(string? Url, bool HetCuu = false);

    /// <summary>
    /// Soi một tệp về kho. Trả URL của mình, hoặc <c>null</c> khi không soi được —
    /// <b>chỗ gọi phải giữ nguyên url gốc</b> lúc đó, đừng bỏ tệp.
    /// </summary>
    public async Task<KetQuaSoi> MirrorAsync(string tenantId, ChatChannel kenh, NguonTep nguon,
        CancellationToken ct)
    {
        // Chưa khai kho là chuyện của cấu hình, khai xong thì soi được — hỏng TẠM.
        if (!_kho.Configured) return new(null);
        // Không có url thì mai sau cũng không tự mọc ra — hỏng VĨNH VIỄN.
        if (string.IsNullOrWhiteSpace(nguon.Url)) return new(null, true);

        // ĐƯỜNG NHANH cho nhãn dán: biết khoá TRƯỚC khi tải, nên nếu đã có thì không chạm mạng lần nào.
        var khoaNhanDan = string.IsNullOrWhiteSpace(nguon.StickerId)
            ? null
            : $"sticker/{kenh.ToString().ToLowerInvariant()}/{AnToan(nguon.StickerId!)}";

        if (khoaNhanDan is not null &&
            await _kho.ExistingUrlAsync(khoaNhanDan, ct) is { } daCo)
            return new(daCo);

        byte[] bytes;
        string kieuNoiDung;
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, nguon.Url);
            if (!string.IsNullOrWhiteSpace(nguon.Auth))
                req.Headers.Add("Authorization", "Bearer " + nguon.Auth);
            // Vài CDN từ chối khi thiếu User-Agent.
            req.Headers.Add("User-Agent", "TourkitAiProxy");

            // Hạn giờ RIÊNG cho lượt tải này. Mặc định của HttpClient là 100 giây, mà hàm này
            // chạy NGAY trong luồng nhận tin: một CDN treo là cả hàng đợi tin đứng im hơn một
            // phút cho MỘT tấm ảnh. Quá hạn thì giữ url gốc — hệt như mọi lỗi tải khác.
            using var hanGio = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hanGio.CancelAfter(HanTaiGiay * 1000);
            using var res = await http.SendAsync(req, hanGio.Token);
            if (!res.IsSuccessStatusCode)
            {
                var het = HetCuu(res.StatusCode);
                _log.LogWarning("[chat/soi-tep] tải hỏng {Ma}{Them} — {Url}",
                    (int)res.StatusCode, het ? " (thôi không thử lại)" : "", Cat(nguon.Url));
                return new(null, het);
            }

            // Chặn TRƯỚC khi đọc hết vào bộ nhớ khi máy chủ có nói cỡ.
            if (res.Content.Headers.ContentLength is > MaxBytes)
            {
                _log.LogWarning("[chat/soi-tep] tệp quá lớn ({Co} byte), giữ url gốc",
                    res.Content.Headers.ContentLength);
                return new(null, true);   // lần sau nó cũng không nhỏ đi
            }

            bytes = await res.Content.ReadAsByteArrayAsync(hanGio.Token);
            if (bytes.LongLength > MaxBytes) return new(null, true);

            kieuNoiDung = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        }
        catch (Exception ex)
        {
            // Mạng chập, CDN treo, quá hạn giờ — đều đáng thử lại.
            _log.LogWarning(ex, "[chat/soi-tep] không tải được {Url}", Cat(nguon.Url));
            return new(null);
        }

        // Nén ảnh TRƯỚC khi băm: băm trước rồi nén thì hai lần nhận cùng một ảnh gốc vẫn ra
        // cùng khoá, nhưng nội dung đã đổi — chống lặp mất tác dụng ngay ở ca nó cần nhất.
        //
        // KHÔNG nén nhãn dán: chúng vốn đã nhỏ (dưới 100KB), và nén lại thì mất nền trong suốt.
        if (khoaNhanDan is null)
            (bytes, kieuNoiDung) = NenAnh(bytes, kieuNoiDung, _log);

        // Nhãn dán đi theo mã của nền tảng; tệp thường băm nội dung và khoá theo TỪNG công ty.
        var khoa = khoaNhanDan ?? $"chat/{AnToan(tenantId)}/{Bam(bytes)}{DuoiTep(kieuNoiDung)}";

        // Tệp thường: hỏi lại sau khi băm — hai khách gửi cùng một tấm thì lượt sau khỏi ghi.
        if (khoaNhanDan is null && await _kho.ExistingUrlAsync(khoa, ct) is { } trung)
            return new(trung);

        try
        {
            using var ms = new MemoryStream(bytes);
            return new(await _kho.UploadAsync(khoa, ms, kieuNoiDung, ct));
        }
        catch (Exception ex)
        {
            // Kho hỏng là chuyện của mình, không phải của tệp — thử lại đáng giá, và tới lúc đó
            // tệp đã tải về được một lần rồi nên nhiều khả năng vẫn còn tải được.
            _log.LogWarning(ex, "[chat/soi-tep] không ghi được vào kho: {Khoa}", khoa);
            return new(null);
        }
    }

    /// <summary>
    /// Mã lỗi HTTP này có nghĩa là "thôi, đừng thử nữa" hay không.
    ///
    /// <para>Phía khách (4xx) là url đã hết hạn / tệp đã bị gỡ / không còn quyền đọc — thử lại
    /// bao nhiêu lần cũng đúng chừng ấy kết quả. HAI ngoại lệ: <b>408</b> (máy chủ chờ quá lâu)
    /// và <b>429</b> (mình hỏi quá dày) là lỗi của LÚC NÀY, không phải của tệp.</para>
    ///
    /// <para>5xx thì để dành thử lại: máy chủ của kênh hỏng một lúc là chuyện thường ngày.</para>
    /// </summary>
    private static bool HetCuu(System.Net.HttpStatusCode ma)
        => (int)ma is >= 400 and < 500
           && ma is not System.Net.HttpStatusCode.RequestTimeout
                 and not System.Net.HttpStatusCode.TooManyRequests;

    /// <summary>Cạnh dài nhất sau khi nén. Đủ để soi hoá đơn, hộ chiếu; vẫn tải nhanh.</summary>
    private const int CanhToiDa = 1600;

    /// <summary>Dưới ngưỡng này thì đụng vào chỉ tổ làm ảnh xấu đi mà chẳng nhẹ thêm bao nhiêu.</summary>
    private const int BoQuaDuoi = 300 * 1024;

    /// <summary>
    /// Trần số điểm ảnh được phép GIẢI NÉN. Vượt thì giữ nguyên bản, không nén.
    ///
    /// <para><b>Đây là chốt chặn chống sập, không phải chốt tối ưu.</b> Cỡ TỆP không nói được cỡ
    /// bộ nhớ cần để mở nó: một ảnh PNG 30.000×30.000 toàn nền trắng chỉ nặng vài MB — lọt qua
    /// mọi chặn cỡ tệp — nhưng giải nén ra là hơn 3GB, đủ để giết cả tiến trình. Mà tiến trình
    /// này CŨNG LÀ trang web đang phục vụ khách, nên một tấm ảnh như vậy làm sập cả hộp thư.</para>
    ///
    /// <para>50 triệu điểm ảnh (~200MB lúc mở) cao hơn mọi máy ảnh điện thoại thật, kể cả loại
    /// 108MP vốn xuất ra khoảng 12MP. Vượt trần thì vẫn LƯU tệp gốc — chỉ bỏ bước nén.</para>
    /// </summary>
    private const long DiemAnhToiDa = 50_000_000;

    /// <summary>
    /// Thu nhỏ ảnh quá khổ về <see cref="CanhToiDa"/> và ghi lại thành JPEG.
    ///
    /// <para><b>Vì sao cần.</b> Điện thoại bây giờ chụp 4000×3000, một tấm 6–8MB. Khách gửi vài
    /// tấm là hộp thư tải ì ạch, mà nhân viên chỉ cần nhìn đủ rõ để đọc chữ trên hoá đơn — không
    /// ai phóng to 400%.</para>
    ///
    /// <para><b>Hỏng thì trả lại NGUYÊN BẢN, không ném.</b> Ảnh lạ, ảnh hỏng một phần, định dạng
    /// chưa hỗ trợ… — thà lưu tấm nặng còn hơn mất tệp của khách vì một bước tối ưu.</para>
    ///
    /// <para>⚠️ Giữ nguyên <b>ảnh động</b> (GIF/WebP động): ghi lại thành JPEG là mất hết khung,
    /// còn đúng một khung đứng im — khách gửi ảnh động mà hộp thư hiện ảnh tĩnh thì trông như lỗi.</para>
    /// </summary>
    internal static (byte[] Bytes, string Mime) NenAnh(byte[] bytes, string mime,
        ILogger? log = null)
    {
        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return (bytes, mime);
        if (mime.Contains("gif", StringComparison.OrdinalIgnoreCase)) return (bytes, mime);
        if (bytes.LongLength <= BoQuaDuoi) return (bytes, mime);

        try
        {
            // ĐỌC PHẦN ĐẦU TỆP TRƯỚC, đừng mở cả ảnh: Identify chỉ đọc mấy chục byte đầu để lấy
            // kích thước, còn Load thì cấp bộ nhớ cho TOÀN BỘ điểm ảnh ngay. Hỏi trước rồi mới
            // mở là cách duy nhất chặn được "bom giải nén" — xem DiemAnhToiDa.
            var co = SixLabors.ImageSharp.Image.Identify(bytes);
            if ((long)co.Width * co.Height > DiemAnhToiDa)
            {
                log?.LogWarning("[chat/soi-tep] ảnh {W}×{H} quá lớn để mở, giữ nguyên bản",
                    co.Width, co.Height);
                return (bytes, mime);
            }

            using var anh = SixLabors.ImageSharp.Image.Load(bytes);
            if (anh.Frames.Count > 1) return (bytes, mime);   // ảnh động — xem ghi chú ở trên

            var canh = Math.Max(anh.Width, anh.Height);
            if (canh > CanhToiDa)
            {
                var ti = (double)CanhToiDa / canh;
                anh.Mutate(x => x.Resize(
                    (int)Math.Round(anh.Width * ti), (int)Math.Round(anh.Height * ti)));
            }

            using var ra = new MemoryStream();
            // Chất lượng 82: mắt thường gần như không phân biệt được với 100, mà nhẹ hơn nhiều lần.
            anh.SaveAsJpeg(ra, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 82 });

            // Nén xong mà KHÔNG nhẹ hơn thì giữ bản gốc — ảnh đã tối ưu sẵn (PNG phẳng, ảnh nhỏ)
            // đem ghi lại thành JPEG chỉ làm nó vừa nặng hơn vừa mờ đi.
            var moi = ra.ToArray();
            if (moi.LongLength >= bytes.LongLength) return (bytes, mime);

            log?.LogDebug("[chat/soi-tep] nén ảnh {Cu} → {Moi} byte", bytes.LongLength, moi.LongLength);
            return (moi, "image/jpeg");
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "[chat/soi-tep] không nén được, giữ nguyên bản");
            return (bytes, mime);
        }
    }

    private static string Bam(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Chỉ giữ ký tự an toàn cho đường dẫn — mã nền tảng và tên công ty đều là dữ liệu ngoài.</summary>
    private static string AnToan(string s)
        => new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(80).ToArray());

    private static string DuoiTep(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        "video/mp4" => ".mp4",
        "application/pdf" => ".pdf",
        _ => "",
    };

    private static string Cat(string s) => s.Length <= 120 ? s : s[..120];
}
