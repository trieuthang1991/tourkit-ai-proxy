namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Singleton registry — nhận <c>IEnumerable&lt;IScheduledWorkflow&gt;</c> qua DI,
/// expose <see cref="Resolve"/> và <see cref="All"/>.
/// Thêm workflow mới = implement <see cref="IScheduledWorkflow"/> + đăng ký DI.
/// </summary>
public class WorkflowRegistry
{
    private readonly IReadOnlyDictionary<string, IScheduledWorkflow> _map;

    public WorkflowRegistry(IEnumerable<IScheduledWorkflow> workflows)
    {
        _map = workflows.ToDictionary(w => w.Type, StringComparer.OrdinalIgnoreCase);
    }

    /// Trả workflow theo type. null nếu không tìm thấy.
    public IScheduledWorkflow? Resolve(string type)
        => _map.TryGetValue(type, out var w) ? w : null;

    /// Toàn bộ workflow đã đăng ký.
    public IReadOnlyCollection<IScheduledWorkflow> All() => (IReadOnlyCollection<IScheduledWorkflow>)_map.Values;
}
