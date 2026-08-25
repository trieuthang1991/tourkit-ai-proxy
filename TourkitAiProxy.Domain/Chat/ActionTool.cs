namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Kiểu dữ liệu của một hành động trợ lý — thuần, không biết cấu hình.
///
/// <para>Tách khỏi <c>ActionTools</c> (danh mục + cổng bật/tắt theo cờ tính năng) vì lớp đó cần
/// <c>IConfiguration</c>, tức thuộc tầng ngoài. Bản thân hình dạng dữ liệu thì không.</para>
/// </summary>
public enum ActionKind { Mail, Internal, CrmQueue }

/// 1 "action" = 1 hành động GHI/nghiệp vụ trợ lý có thể đề xuất. Song song ChatTools (read).
public record ActionTool(
    string Name, string Description, string[] Params,
    ActionKind Kind, bool NeedsConfirm, string Title);
