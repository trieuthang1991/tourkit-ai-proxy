using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Quota;

/// <summary>
/// Tingee VietQR client — tạo QR động + verify webhook signature.
///
/// 2 impl:
///   • <see cref="MockTingeeClient"/>     — dev/staging, KHÔNG gọi Tingee, tạo VietQR động qua public
///                                          API `img.vietqr.io` (đường tắt phổ biến VN). Dùng cấu hình
///                                          Tingee:Account* trong appsettings — anh test scan được bằng app
///                                          banking thật. Dev endpoint /quota/dev/simulate-paid để simulate
///                                          IPN không cần chuyển tiền thật.
///   • <see cref="TingeeHttpClient"/>     — production, gọi API Tingee thật để tạo QR + verify HMAC webhook.
///                                          STUBBED đến khi có ApiKey thật + đọc xong docs Tingee — TODO sửa lại.
///
/// Switch: cờ `Tingee:Mock=true` trong appsettings → Program.cs register Mock; ngược lại register Http.
/// </summary>
public interface ITingeeClient
{
    /// <summary>Có phải chế độ mock không (FE hiện banner "DEV mode" khi true).</summary>
    bool IsMock { get; }

    /// <summary>Cấu hình account để FE hiện STK + tên người nhận khi user chuyển khoản tay (fallback).</summary>
    TingeeAccountInfo Account { get; }

    /// <summary>
    /// Tạo VietQR động cho 1 order. Trả URL ảnh QR (img.vietqr.io) — FE dùng &lt;img src&gt; render.
    /// </summary>
    Task<TingeeQrResult> CreateQrAsync(string orderId, long amountVnd, string memo, CancellationToken ct = default);

    /// <summary>
    /// Verify webhook signature (HMAC-SHA256 thường thấy). Mock: trả true.
    /// Real: tính HMAC body với secret, so với header X-Tingee-Signature.
    /// </summary>
    bool VerifyWebhookSignature(string rawBody, string? signatureHeader);
}

public record TingeeAccountInfo(string BankBin, string AccountNumber, string AccountName);

public record TingeeQrResult(string QrPayload, string Memo);

/// <summary>Mock impl — đủ test full flow không cần Tingee real.</summary>
public class MockTingeeClient : ITingeeClient
{
    private readonly ILogger<MockTingeeClient> _log;
    public TingeeAccountInfo Account { get; }
    public bool IsMock => true;

    public MockTingeeClient(IConfiguration cfg, ILogger<MockTingeeClient> log)
    {
        _log = log;
        var bin   = cfg["Tingee:BankBin"]       ?? "970422";        // MB Bank
        var acct  = cfg["Tingee:AccountNumber"] ?? "0123456789";    // STK demo
        var name  = cfg["Tingee:AccountName"]   ?? "TOURKIT AI";
        Account = new TingeeAccountInfo(bin, acct, name);
        _log.LogInformation("Tingee MOCK mode — bank {Bin} stk {Acct} ({Name})", bin, acct, name);
    }

    public Task<TingeeQrResult> CreateQrAsync(string orderId, long amountVnd, string memo, CancellationToken ct = default)
    {
        // VietQR qua img.vietqr.io — public, miễn phí, không cần API key. Format chuẩn NAPAS.
        // template `qr_only` = chỉ ảnh QR (FE tự dệt frame). `compact2` = có thông tin bên cạnh.
        // Memo dùng `addInfo` (nội dung CK) → webhook Tingee sẽ match.
        var url = "https://img.vietqr.io/image/" +
                  $"{Account.BankBin}-{Account.AccountNumber}-qr_only.png" +
                  $"?amount={amountVnd}" +
                  $"&addInfo={Uri.EscapeDataString(memo)}" +
                  $"&accountName={Uri.EscapeDataString(Account.AccountName)}";
        return Task.FromResult(new TingeeQrResult(url, memo));
    }

    public bool VerifyWebhookSignature(string rawBody, string? signatureHeader) => true;     // dev mode
}

/// <summary>
/// HTTP client gọi API Tingee thật. STUBBED — paste ApiKey vào appsettings + đọc doc Tingee chính thức
/// (https://developers.tingee.vn/docs/banking/) để fill endpoint thật.
/// Khi switch sang chế độ này, set `Tingee:Mock=false` + `Tingee:ApiKey=...`.
/// </summary>
public class TingeeHttpClient : ITingeeClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TingeeHttpClient> _log;
    public TingeeAccountInfo Account { get; }
    public bool IsMock => false;

    public TingeeHttpClient(IHttpClientFactory http, IConfiguration cfg, ILogger<TingeeHttpClient> log)
    {
        _http = http; _cfg = cfg; _log = log;
        Account = new TingeeAccountInfo(
            cfg["Tingee:BankBin"]       ?? "970422",
            cfg["Tingee:AccountNumber"] ?? "",
            cfg["Tingee:AccountName"]   ?? "TOURKIT AI");
    }

    public async Task<TingeeQrResult> CreateQrAsync(string orderId, long amountVnd, string memo, CancellationToken ct = default)
    {
        // TODO: implement Tingee CreateQR API thật khi có docs + key.
        // Tham khảo skeleton:
        //   var c = _http.CreateClient("tingee");
        //   c.DefaultRequestHeaders.Authorization = new("Bearer", _cfg["Tingee:ApiKey"]);
        //   var body = new { amount = amountVnd, description = memo, account = Account.AccountNumber };
        //   var resp = await c.PostAsJsonAsync("/qr/dynamic", body, ct);
        //   resp.EnsureSuccessStatusCode();
        //   var data = await resp.Content.ReadFromJsonAsync<TingeeCreateQrResp>(ct);
        //   return new TingeeQrResult(data.QrUrl ?? data.QrString, memo);
        _log.LogWarning("TingeeHttpClient.CreateQrAsync chưa implement — fallback vietqr.io");
        await Task.CompletedTask;
        var url = "https://img.vietqr.io/image/" +
                  $"{Account.BankBin}-{Account.AccountNumber}-qr_only.png" +
                  $"?amount={amountVnd}&addInfo={Uri.EscapeDataString(memo)}";
        return new TingeeQrResult(url, memo);
    }

    public bool VerifyWebhookSignature(string rawBody, string? signatureHeader)
    {
        var secret = _cfg["Tingee:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            _log.LogWarning("Tingee:WebhookSecret trống — verify pass mặc định (KHÔNG AN TOÀN cho prod)");
            return true;
        }
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;

        // HMAC-SHA256 hex (chuẩn phổ biến). Sửa nếu Tingee dùng format khác (base64, prefix sha256=).
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawBody));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        var got = signatureHeader.Trim().Replace("sha256=", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(computed),
            System.Text.Encoding.ASCII.GetBytes(got));
    }
}
