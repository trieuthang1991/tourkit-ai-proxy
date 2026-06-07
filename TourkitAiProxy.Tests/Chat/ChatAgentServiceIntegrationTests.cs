using TourkitAiProxy.Services.Chat;

namespace TourkitAiProxy.Tests.Chat;

/// Integration smoke cho ChatAgentService. Sẽ wire mock đầy đủ ở Phase 2 (multi-step agent).
/// Phase 1 dùng manual verify qua /assistant + log [chat] L1/L2 cache hit.
public class ChatAgentServiceIntegrationTests
{
    [Fact(Skip = "Cần mock ChatCache + ProviderRegistry, sẽ wire ở Phase 2 khi refactor IAgentRuntime")]
    public void L1_cache_hit_skips_planner_and_dispatch()
    {
        // TODO Phase 2: dispatch mock provider that throws if called, expect L1 hit returns cached result.
    }

    [Fact(Skip = "Cần mock ChatCache + ProviderRegistry, sẽ wire ở Phase 2 khi refactor IAgentRuntime")]
    public void L2_cache_hit_skips_dispatch_and_analysis()
    {
        // TODO Phase 2: planner runs, then L2 hits, dispatch + analysis NOT called.
    }
}
