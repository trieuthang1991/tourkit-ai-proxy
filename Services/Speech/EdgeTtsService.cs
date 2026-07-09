using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace TourkitAiProxy.Services.Speech;

/// <summary>
/// Text-to-Speech qua Microsoft edge-tts — MIỄN PHÍ, giọng vi-VN NEURAL BẢN ĐỊA
/// (HoaiMy nữ / NamMinh nam) → phát âm tiếng Việt CHUẨN (khác Piper=espeak-ng sai thanh điệu).
///
/// Cơ chế: WebSocket tới speech.platform.bing.com với token DRM (Sec-MS-GEC) + SSML → nhận mp3.
/// Không key. Cần MẠNG (endpoint không chính thức của Edge). Version string PHẢI mới (MS check → 403 nếu cũ).
///
/// LƯU Ý: Microsoft có thể chặn theo IP (403 từ 1 số datacenter/VPS). Nếu server bị 403 → tự fallback Piper/OpenAI.
/// Cập nhật khi hỏng: đọc Sec-MS-GEC-Version + version Edge mới nhất từ github.com/rany2/edge-tts (constants.py).
///
/// Config: "Speech": { "Edge": { "Enabled": true, "Voice": "vi-VN-HoaiMyNeural" } }  (mặc định bật + HoaiMy).
/// </summary>
public class EdgeTtsService
{
    private const string TrustedToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string GecVersion = "1-143.0.3650.75";   // ⚠️ phải khớp Edge mới nhất (rany2/edge-tts constants.py)
    private const string EdgeVer = "143.0.0.0";
    private const long WinEpoch = 11644473600L;
    private const int MAX_CHARS = 2000;   // đủ cho reply dài (top-10 list + phân tích)
    private const int CACHE_CAP = 200;
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();

    private readonly IConfiguration _cfg;
    private readonly ILogger<EdgeTtsService> _log;
    public EdgeTtsService(IConfiguration cfg, ILogger<EdgeTtsService> log) { _cfg = cfg; _log = log; }

    public bool Enabled => _cfg.GetValue("Speech:Edge:Enabled", true);

    public async Task<(byte[] Mp3, bool Cached)> SynthesizeAsync(string text, string? voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Thiếu text để đọc.");
        text = SanitizeSsml(text.Trim());
        if (text.Length > MAX_CHARS) text = text.Substring(0, MAX_CHARS);

        var v = string.IsNullOrWhiteSpace(voice) ? (_cfg["Speech:Edge:Voice"] ?? "vi-VN-HoaiMyNeural") : voice;
        var cacheKey = Hash($"edge|{v}|{text}");
        if (_cache.TryGetValue(cacheKey, out var hit)) return (hit, true);

        var mp3 = await SynthesizeWsAsync(text, v, ct);
        if (mp3.Length < 200) throw new InvalidOperationException("edge-tts trả audio rỗng.");
        if (_cache.Count < CACHE_CAP) _cache.TryAdd(cacheKey, mp3);
        _log.LogInformation("edge-tts OK: {Chars}ch → {Kb}KB, voice={Voice}", text.Length, mp3.Length / 1024, v);
        return (mp3, false);
    }

    private async Task<byte[]> SynthesizeWsAsync(string text, string voice, CancellationToken ct)
    {
        var sec = GenerateSecMsGec();
        var url = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1"
                + $"?TrustedClientToken={TrustedToken}&Sec-MS-GEC={sec}&Sec-MS-GEC-Version={GecVersion}";

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{EdgeVer} Safari/537.36 Edg/{EdgeVer}");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpstbhdldidmgoebmjlaolfo");
        ws.Options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(new Uri(url), connectCts.Token);

        var reqId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);

        var config = $"X-Timestamp:{now}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n"
            + "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
        await SendTextAsync(ws, config, ct);

        var ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='vi-VN'>"
                 + $"<voice name='{voice}'><prosody pitch='+0Hz' rate='+0%' volume='+0%'>{text}</prosody></voice></speak>";
        var ssmlMsg = $"X-RequestId:{reqId}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{now}\r\nPath:ssml\r\n\r\n{ssml}";
        await SendTextAsync(ws, ssmlMsg, ct);

        var audio = new MemoryStream();
        var buf = new byte[16384];
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        recvCts.CancelAfter(TimeSpan.FromSeconds(30));
        while (ws.State == WebSocketState.Open)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), recvCts.Token);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                    return audio.ToArray();
                }
                frame.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            var bytes = frame.ToArray();
            if (res.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(bytes).Contains("Path:turn.end"))
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                    return audio.ToArray();
                }
            }
            else if (res.MessageType == WebSocketMessageType.Binary && bytes.Length > 2)
            {
                int headerLen = (bytes[0] << 8) | bytes[1];   // 2-byte big-endian độ dài header
                int start = 2 + headerLen;
                if (start < bytes.Length) audio.Write(bytes, start, bytes.Length - start);
            }
        }
        return audio.ToArray();
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string msg, CancellationToken ct)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);

    // Token DRM: khớp bit-for-bit với edge-tts python (float64 → làm tròn ~17 chữ số).
    private static string GenerateSecMsGec()
    {
        double ticks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        ticks += WinEpoch;
        ticks -= ticks % 300;
        ticks *= 1e7;   // S_TO_NS / 100
        var toHash = ticks.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + TrustedToken;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(toHash)));
    }

    private static string SanitizeSsml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0, 12);
}
