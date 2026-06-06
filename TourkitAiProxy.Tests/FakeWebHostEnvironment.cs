using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace TourkitAiProxy.Tests;

/// IWebHostEnvironment giả: ContentRootPath trỏ vào 1 thư mục tạm để test file-backed repo.
public sealed class FakeWebHostEnvironment : IWebHostEnvironment, IDisposable
{
    public FakeWebHostEnvironment()
    {
        ContentRootPath = Path.Combine(Path.GetTempPath(), "tkai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ContentRootPath);
    }

    public string ApplicationName { get; set; } = "TourkitAiProxy.Tests";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
    public string EnvironmentName { get; set; } = "Test";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = null!;

    public void Dispose()
    {
        try { if (Directory.Exists(ContentRootPath)) Directory.Delete(ContentRootPath, true); } catch { }
    }
}
