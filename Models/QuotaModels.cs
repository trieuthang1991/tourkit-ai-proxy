namespace TourkitAiProxy.Models;

/// State quota AI 1 tenant (persisted). Limit có thể tăng qua API topup (admin).
/// Used = số lượt AI đã dùng. Reset: KHÔNG tự động — lĩnh 1 lần, hết phải topup.
public record QuotaState(int Limit, int Used, DateTime UpdatedAt);

/// Snapshot trả ra FE/API: kèm % + flag cảnh báo (>=90%) + hết (>=100%).
public record QuotaSnapshot(
    string Tenant,
    int Limit,
    int Used,
    int Remaining,
    int UsedPct,
    bool Warn,
    bool Exhausted,
    DateTime UpdatedAt);
