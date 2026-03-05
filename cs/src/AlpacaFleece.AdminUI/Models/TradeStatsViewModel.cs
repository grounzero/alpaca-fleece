namespace AlpacaFleece.AdminUI.Models;

public sealed record TradeStatsViewModel(
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    decimal WinRate,
    decimal AvgWin,
    decimal AvgLoss,
    decimal ProfitFactor,
    decimal TotalPnl);
