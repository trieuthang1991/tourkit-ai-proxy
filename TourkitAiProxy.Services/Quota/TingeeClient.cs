using TourkitAiProxy.Domain.Models;

namespace TourkitAiProxy.Services.Quota;

/// <summary>
/// Tingee client cho luồng mua quota. Tingee KHÔNG tạo QR — nó chỉ giám sát tài khoản ngân hàng + bắn
/// webhook IPN (về tourkit-web). QR ở đây là VietQR ĐỘNG sinh qua public API img.vietqr.io (chuẩn NAPAS):
/// số TK + số tiền + nội dung CK (= OrderId). Khách quét trả tiền vào TK Tingee giám sát → Tingee bắn
/// webhook → tourkit-web ghi nhận → cộng lượt. Account* đọc từ appsettings (Tingee:BankBin/AccountNumber/AccountName).
/// KHÔNG có mock — chỉ chạy dữ liệu thật.
/// </summary>
public interface ITingeeClient
{
    /// <summary>Cấu hình account để FE render QR + hiện STK/tên người nhận khi chuyển khoản tay.</summary>
    TingeeAccountInfo Account { get; }

    /// <summary>Tạo VietQR động cho 1 order. Trả URL ảnh QR (img.vietqr.io) — FE dùng &lt;img src&gt; render.</summary>
    Task<TingeeQrResult> CreateQrAsync(string orderId, long amountVnd, string memo, CancellationToken ct = default);

    /// <summary>
    /// Verify chữ ký webhook Tingee (HMAC). Chỉ dùng nếu webhook Tingee được relay tới proxy —
    /// luồng thật webhook về tourkit-web. Secret = Tingee:ApiKey.
    /// </summary>
    bool VerifyWebhookSignature(string rawBody, string? signatureHeader);
}

public record TingeeAccountInfo(string BankBin, string AccountNumber, string AccountName);

public record TingeeQrResult(string QrPayload, string Memo);

/// <summary>Client thật — sinh VietQR động + verify webhook HMAC. Không có mock.</summary>
public class TingeeClient : ITingeeClient
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<TingeeClient> _log;
    public TingeeAccountInfo Account { get; }

    public TingeeClient(IConfiguration cfg, ILogger<TingeeClient> log)
    {
        _cfg = cfg; _log = log;
        Account = new TingeeAccountInfo(
            cfg["Tingee:BankBin"]       ?? "970432",
            cfg["Tingee:AccountNumber"] ?? "",
            cfg["Tingee:AccountName"]   ?? "TOURKIT AI");
        _log.LogInformation("Tingee client — bank {Bin} stk {Acct} ({Name})",
            Account.BankBin, Account.AccountNumber, Account.AccountName);
    }

    public Task<TingeeQrResult> CreateQrAsync(string orderId, long amountVnd, string memo, CancellationToken ct = default)
    {
        // VietQR qua img.vietqr.io — public, chuẩn NAPAS. template qr_only = chỉ ảnh QR (FE tự dệt frame).
        // addInfo = nội dung CK (= OrderId) → webhook Tingee match theo nội dung này.
        var url = "https://img.vietqr.io/image/" +
                  $"{Account.BankBin}-{Account.AccountNumber}-qr_only.png" +
                  $"?amount={amountVnd}" +
                  $"&addInfo={Uri.EscapeDataString(memo)}" +
                  $"&accountName={Uri.EscapeDataString(Account.AccountName)}";
        return Task.FromResult(new TingeeQrResult(url, memo));
    }

    public bool VerifyWebhookSignature(string rawBody, string? signatureHeader)
    {
        var secret = _cfg["Tingee:ApiKey"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            _log.LogWarning("Tingee:ApiKey trống — verify pass mặc định (KHÔNG AN TOÀN cho prod)");
            return true;
        }
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;

        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawBody));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        var got = signatureHeader.Trim().Replace("sha256=", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(computed),
            System.Text.Encoding.ASCII.GetBytes(got));
    }
}
