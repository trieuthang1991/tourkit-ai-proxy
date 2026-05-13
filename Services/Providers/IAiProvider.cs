using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Providers;

/// Common interface cho mọi upstream AI. Thêm provider mới = implement interface này
/// + đăng ký vào DI ở Program.cs. Endpoint layer agnostic về upstream.
public interface IAiProvider
{
    /// Provider id dùng để route từ CompleteRequest.Provider. Phải unique.
    string Id { get; }

    /// Tên hiển thị cho UI.
    string Label { get; }

    /// Models list provider này hỗ trợ. ID + label cho client populate dropdown.
    IReadOnlyList<ProviderModel> Models { get; }

    /// Buffered completion. Implementations tự handle retry/budget bump.
    Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct);

    /// Streaming completion. `onDelta` được gọi cho mỗi text chunk.
    /// Return final result với usage + finishReason. Throw nếu connect fail.
    Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct);

    /// Live model list từ upstream (nếu hỗ trợ). Provider có model động (vd 9routes
    /// proxy nhiều backend) override để gọi upstream /models. Default: trả static <see cref="Models"/>.
    Task<IReadOnlyList<ProviderModel>> ListLiveModelsAsync(CancellationToken ct)
        => Task.FromResult(Models);
}

public record ProviderModel(string Id, string Label, bool Recommended = false);

public record CompleteResult(
    string Text,
    string Model,
    int InputTokens,
    int OutputTokens,
    long LatencyMs,
    string FinishReason,
    int Attempts = 1,
    string? Warning = null,
    string? RawUpstream = null
);
