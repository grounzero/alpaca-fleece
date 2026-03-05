namespace AlpacaFleece.AdminUI.Models;

public sealed record DashboardViewModel(
    bool TradingReady,
    bool MarketDataDegraded,
    bool KillSwitchActive,
    bool DryRun,
    bool IsPaperTrading,
    string DrawdownLevel,
    decimal CurrentDrawdownPct,
    int CircuitBreakerCount,
    decimal DailyPnl,
    int DailyTradeCount,
    decimal EquityValue,
    decimal CashBalance,
    int OpenPositionCount,
    DateTimeOffset LastUpdated,
    bool DatabaseConnected,
    IReadOnlyList<EquityPoint> EquityCurve,
    IReadOnlyList<DrawdownPoint> DrawdownHistory,
    IReadOnlyList<DailyPnlPoint> DailyPnl30);

public sealed record EquityPoint(DateTimeOffset Timestamp, decimal Value);
public sealed record DrawdownPoint(DateTimeOffset Timestamp, decimal DrawdownPct);
public sealed record DailyPnlPoint(string Date, decimal Pnl);
