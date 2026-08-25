using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TourkitAiProxy.Services.Speech;

/// <summary>
/// Text-to-Speech qua Piper (https://github.com/rhasspy/piper) — OPEN-SOURCE, OFFLINE, MIỄN PHÍ.
/// Không key, không mạng, không phụ thuộc bên thứ ba. Giọng vi_VN neural (VITS). Chạy mọi trình duyệt.
///
/// Cách chạy: spawn piper.exe --model &lt;vi.onnx&gt; --output_file &lt;temp.wav&gt;, đẩy text vào stdin,
/// đợi xong, đọc file WAV, xóa. Trả WAV bytes (browser Audio phát WAV trực tiếp).
///
/// SETUP (1 lần, trên máy/server chạy proxy):
///   1. Tải piper Windows: https://github.com/rhasspy/piper/releases → piper_windows_amd64.zip → giải nén.
///   2. Tải giọng Việt: https://huggingface.co/rhasspy/piper-voices/tree/main/vi/vi_VN/vais1000/medium
///      → 2 file: vi_VN-vais1000-medium.onnx + vi_VN-vais1000-medium.onnx.json (để CẠNH nhau).
///   3. appsettings.json:
///        "Speech": { "Piper": { "ExePath": "C:\\piper\\piper.exe",
///                               "ModelPath": "C:\\piper\\vi_VN-vais1000-medium.onnx" } }
///      (hoặc env PIPER_EXE / PIPER_MODEL).
///
/// Cache theo hash(text|model) → câu lặp không synth lại (đỡ CPU).
/// </summary>
public class PiperTtsService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<PiperTtsService> _log;

    private const int MAX_CHARS = 2000;   // đủ cho reply dài (top-10 list + phân tích)
    private const int CACHE_CAP = 200;
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();

    public PiperTtsService(IConfiguration cfg, ILogger<PiperTtsService> log) { _cfg = cfg; _log = log; }

    private string? ExePath => _cfg["Speech:Piper:ExePath"] ?? Environment.GetEnvironmentVariable("PIPER_EXE");
    private string? ModelPath => _cfg["Speech:Piper:ModelPath"] ?? Environment.GetEnvironmentVariable("PIPER_MODEL");

    public bool Configured =>
        !string.IsNullOrWhiteSpace(ExePath) && File.Exists(ExePath)
        && !string.IsNullOrWhiteSpace(ModelPath) && File.Exists(ModelPath);

    public async Task<(byte[] Wav, bool Cached)> SynthesizeAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Thiếu text để đọc.");
        text = text.Trim();
        if (text.Length > MAX_CHARS) text = text.Substring(0, MAX_CHARS);

        var exe = ExePath; var model = ModelPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidOperationException("Piper chưa cấu hình (Speech:Piper:ExePath không tồn tại).");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            throw new InvalidOperationException("Piper thiếu model giọng (Speech:Piper:ModelPath không tồn tại).");

        var cacheKey = Hash($"piper|{Path.GetFileName(model)}|{text}");
        if (_cache.TryGetValue(cacheKey, out var hit)) return (hit, true);

        var tmp = Path.Combine(Path.GetTempPath(), $"piper-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(tmp);

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Không khởi động được piper.exe.");
            await p.StandardInput.WriteAsync(text.AsMemory(), ct);
            p.StandardInput.Close();

            using (var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                var err = await p.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"Piper exit {p.ExitCode}: {Trunc(err)}");
            }
            if (!File.Exists(tmp)) throw new InvalidOperationException("Piper không tạo file audio.");

            var wav = await File.ReadAllBytesAsync(tmp, ct);
            if (wav.Length < 100) throw new InvalidOperationException("Piper trả audio rỗng.");

            if (_cache.Count < CACHE_CAP) _cache.TryAdd(cacheKey, wav);
            _log.LogInformation("Piper TTS OK: {Chars}ch → {Kb}KB, model={Model}", text.Length, wav.Length / 1024, Path.GetFileName(model));
            return (wav, false);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0, 12);
}
