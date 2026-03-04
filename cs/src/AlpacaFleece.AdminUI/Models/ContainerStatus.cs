namespace AlpacaFleece.AdminUI.Models;

public sealed record ContainerStatus(
    string State,
    string Status,
    bool IsRunning,
    DateTimeOffset? StartedAt,
    double CpuPercent,
    long MemoryUsageBytes,
    long MemoryLimitBytes);
