using System.Diagnostics;

namespace TourkitAiProxy.Services.Http;

/// <summary>
/// DelegatingHandler log mọi outbound HTTP call: URL, status, duration, full exception chain.
/// Apply vào mọi named HttpClient ("opencode", "anthropic", "openai", "deepseek", "tourkit", ...).
/// Khi gặp lỗi SSL/network → log ra ngay biết upstream nào fail + root cause.
///
/// Output mẫu:
///   [HTTP opencode] POST https://opencode.ai/zen/go/v1/chat/completions → 200 (842ms)
///   [HTTP anthropic] POST https://api.anthropic.com/v1/messages → FAIL (1230ms)
///     ↳ HttpRequestException: The SSL connection could not be established, see inner exception.
///     ↳ root cause [AuthenticationException]: The remote certificate is invalid according to the validation procedure.
/// </summary>
public class HttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<HttpLoggingHandler> _log;
    private readonly string _clientName;

    public HttpLoggingHandler(ILogger<HttpLoggingHandler> log, string clientName)
    {
        _log = log;
        _clientName = clientName;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var url = request.RequestUri?.ToString() ?? "(no uri)";
        var method = request.Method.Method;

        try
        {
            var resp = await base.SendAsync(request, cancellationToken);
            sw.Stop();
            // OK case: log Information cho 2xx, Warning cho 4xx/5xx (vẫn có response, không phải lỗi connection)
            var status = (int)resp.StatusCode;
            if (status >= 400)
                _log.LogWarning("[HTTP {Client}] {Method} {Url} → {Status} ({Ms}ms)",
                    _clientName, method, url, status, sw.ElapsedMilliseconds);
            else
                _log.LogInformation("[HTTP {Client}] {Method} {Url} → {Status} ({Ms}ms)",
                    _clientName, method, url, status, sw.ElapsedMilliseconds);
            return resp;
        }
        catch (OperationCanceledException) { throw; }   // client cancel, không phải lỗi
        catch (Exception ex)
        {
            sw.Stop();
            var root = ex.GetBaseException();
            // Fail case: log ra URL + outer exception + root cause (loại + message)
            _log.LogError(
                "[HTTP {Client}] {Method} {Url} → FAIL ({Ms}ms)\n  ↳ {OuterType}: {OuterMsg}\n  ↳ root [{RootType}]: {RootMsg}",
                _clientName, method, url, sw.ElapsedMilliseconds,
                ex.GetType().Name, ex.Message,
                root.GetType().Name, root.Message);
            throw;
        }
    }
}
