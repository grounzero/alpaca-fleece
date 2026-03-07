namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Read-only queries against the bot's SQLite database via EF Core.
/// Uses IDbContextFactory to create short-lived contexts per operation.
///
/// ORDER BY note: EF Core's SQLite provider cannot translate DateTimeOffset
/// in SQL ORDER BY clauses. All sorting by timestamp is done either by Id
/// (integer PK, chronologically ordered) or client-side via AsEnumerable().
/// </summary>
public sealed class AdminDbService(
    IDbContextFactory<TradingDbContext> dbFactory,
    ILogger<AdminDbService> logger)
{
    public async ValueTask<DashboardViewModel> GetDashboardAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var state = await db.BotState
                .AsNoTracking()
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

            var drawdown = await db.DrawdownState
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            var cb = await db.CircuitBreakerState
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            var openPositions = await db.PositionTracking
                .AsNoTracking()
                .CountAsync(ct);

            // Use Id (integer PK) for ordering — avoids DateTimeOffset ORDER BY limitation
            var latestEquity = await db.EquityCurve
                .AsNoTracking()
                .OrderByDescending(e => e.Id)
                .FirstOrDefaultAsync(ct);

            var cutoff30 = DateTimeOffset.UtcNow.AddDays(-30);

            // Fetch all equity rows then filter client-side.
            // EF Core SQLite provider cannot translate DateTimeOffset in WHERE or ORDER BY.
            var allEquityRows = await db.EquityCurve.AsNoTracking().ToListAsync(ct);
            var equityRows = allEquityRows.Where(e => e.Timestamp >= cutoff30).ToList();

            var equityCurve = equityRows
                .OrderBy(e => e.Timestamp)
                .Select(e => new EquityPoint(e.Timestamp, e.PortfolioValue))
                .ToList();

            var drawdownHistory = equityRows
                .OrderBy(e => e.Timestamp)
                .Select(e => new DrawdownPoint(e.Timestamp, e.DailyPnl))
                .ToList();

            // Group by date client-side
            var dailyPnlPoints = equityRows
                .GroupBy(e => e.Timestamp.UtcDateTime.Date)
                .OrderBy(g => g.Key)
                .Select(g => new DailyPnlPoint(g.Key.ToString("MM/dd"), g.Sum(e => e.DailyPnl)))
                .ToList();

            state.TryGetValue("trading_ready", out var tradingReady);
            state.TryGetValue("market_data_degraded", out var marketDegraded);
            state.TryGetValue("kill_switch", out var killSwitch);
            state.TryGetValue("dry_run", out var dryRun);
            state.TryGetValue("daily_realized_pnl", out var dailyPnlStr);
            state.TryGetValue("daily_trade_count", out var dailyTradeStr);

            decimal.TryParse(dailyPnlStr, out var dailyPnl);
            int.TryParse(dailyTradeStr, out var dailyTradeCount);

            return new DashboardViewModel(
                TradingReady: tradingReady == "true",
                MarketDataDegraded: marketDegraded == "true",
                KillSwitchActive: killSwitch == "true",
                DryRun: dryRun == "true",
                IsPaperTrading: true,
                DrawdownLevel: drawdown?.Level.ToString() ?? "Normal",
                CurrentDrawdownPct: drawdown?.CurrentDrawdownPct ?? 0m,
                CircuitBreakerCount: cb?.Count ?? 0,
                DailyPnl: dailyPnl,
                DailyTradeCount: dailyTradeCount,
                EquityValue: latestEquity?.PortfolioValue ?? 0m,
                CashBalance: latestEquity?.CashBalance ?? 0m,
                OpenPositionCount: openPositions,
                LastUpdated: DateTimeOffset.UtcNow,
                DatabaseConnected: true,
                EquityCurve: equityCurve,
                DrawdownHistory: drawdownHistory,
                DailyPnl30: dailyPnlPoints);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load dashboard data");
            return new DashboardViewModel(
                false, false, false, false, true, "Normal", 0, 0, 0, 0, 0, 0, 0,
                DateTimeOffset.UtcNow, false, [], [], []);
        }
    }

    public async ValueTask<string?> GetBotStateAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.BotState.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct);
        return entity?.Value;
    }

    public async ValueTask<Dictionary<string, string>> GetAllBotStateAsync(
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BotState.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
    }

    public async ValueTask<IReadOnlyList<PositionTrackingEntity>> GetOpenPositionsAsync(
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PositionTracking.AsNoTracking()
            .OrderBy(p => p.Symbol)
            .ToListAsync(ct);
    }

    public async ValueTask<IReadOnlyList<PositionSnapshotEntity>> GetPositionSnapshotsAsync(
        string symbol, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PositionSnapshots.AsNoTracking()
            .Where(p => p.Symbol == symbol)
            .OrderByDescending(p => p.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async ValueTask<(IReadOnlyList<OrderIntentEntity> Rows, int Total)> GetOrderIntentsAsync(
        string? symbol, string? status, DateTimeOffset? from, DateTimeOffset? to,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.OrderIntents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(symbol))
            q = q.Where(o => o.Symbol == symbol.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(status) && status != "All")
            q = q.Where(o => o.Status == status.ToLowerInvariant());
        if (from.HasValue) q = q.Where(o => o.CreatedAt >= from.Value);
        if (to.HasValue)   q = q.Where(o => o.CreatedAt <= to.Value);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (rows, total);
    }

    public async ValueTask<(IReadOnlyList<TradeEntity> Rows, int Total)> GetTradesAsync(
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var total = await db.Trades.AsNoTracking().CountAsync(ct);
        var rows = await db.Trades.AsNoTracking()
            .OrderByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (rows, total);
    }

    public async ValueTask<TradeStatsViewModel> GetTradeStatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var trades = await db.Trades.AsNoTracking()
            .Where(t => t.ExitedAt != null)
            .Select(t => t.RealizedPnl)
            .ToListAsync(ct);

        if (trades.Count == 0)
            return new TradeStatsViewModel(0, 0, 0, 0, 0, 0, 0, 0);

        var wins   = trades.Where(p => p > 0).ToList();
        var losses = trades.Where(p => p < 0).ToList();
        var winRate = (decimal)wins.Count / trades.Count;
        var avgWin  = wins.Count   > 0 ? wins.Average()               : 0;
        var avgLoss = losses.Count > 0 ? Math.Abs(losses.Average())   : 0;
        var grossProfit = wins.Count   > 0 ? wins.Sum()               : 0;
        var grossLoss   = losses.Count > 0 ? Math.Abs(losses.Sum())   : 0;
        var profitFactor = grossLoss > 0
            ? grossProfit / grossLoss
            : grossProfit > 0 ? 999m : 0m;

        return new TradeStatsViewModel(
            TotalTrades:    trades.Count,
            WinningTrades:  wins.Count,
            LosingTrades:   losses.Count,
            WinRate:        winRate,
            AvgWin:         avgWin,
            AvgLoss:        avgLoss,
            ProfitFactor:   profitFactor,
            TotalPnl:       trades.Sum());
    }

    public async ValueTask<IReadOnlyList<EquityCurveEntity>> GetEquityCurveAsync(
        int days = 30, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Fetch all rows first — EF Core SQLite cannot translate DateTimeOffset in WHERE/ORDER BY.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var all = await db.EquityCurve.AsNoTracking().ToListAsync(ct);
        return all.Where(e => e.Timestamp >= cutoff).OrderBy(e => e.Timestamp).ToList();
    }

    public async ValueTask<IReadOnlyList<TableInfo>> GetTableInfoAsync(
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return
            [
                new("OrderIntents",          "Order Intents",          await db.OrderIntents.CountAsync(ct)),
                new("Trades",                "Trades",                 await db.Trades.CountAsync(ct)),
                new("EquityCurve",           "Equity Curve",           await db.EquityCurve.CountAsync(ct)),
                new("BotState",              "Bot State",              await db.BotState.CountAsync(ct)),
                new("Bars",                  "Bars",                   await db.Bars.CountAsync(ct)),
                new("PositionSnapshots",     "Position Snapshots",     await db.PositionSnapshots.CountAsync(ct)),
                new("SignalGates",           "Signal Gates",           await db.SignalGates.CountAsync(ct)),
                new("Fills",                 "Fills",                  await db.Fills.CountAsync(ct)),
                new("PositionTracking",      "Position Tracking",      await db.PositionTracking.CountAsync(ct)),
                new("ExitAttempts",          "Exit Attempts",          await db.ExitAttempts.CountAsync(ct)),
                new("ReconciliationReports", "Reconciliation Reports", await db.ReconciliationReports.CountAsync(ct)),
                new("SchemaMeta",            "Schema Meta",            await db.SchemaMeta.CountAsync(ct)),
                new("CircuitBreakerState",   "Circuit Breaker",        await db.CircuitBreakerState.CountAsync(ct)),
                new("DrawdownState",         "Drawdown State",         await db.DrawdownState.CountAsync(ct)),
            ];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load table info");
            return [];
        }
    }

    public async ValueTask<(IReadOnlyList<object> Rows, int Total, IReadOnlyList<string> Columns)>
        GetTableDataAsync(string tableName, int page = 1, int pageSize = 25,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var skip = (page - 1) * pageSize;

        // All tables sorted by Id (integer PK) — avoids DateTimeOffset ORDER BY limitation
        return tableName switch
        {
            "OrderIntents" => await PagedAsync(
                db.OrderIntents.AsNoTracking().OrderByDescending(x => x.Id),
                skip, pageSize,
                ["Id", "Symbol", "Side", "Quantity", "LimitPrice", "Status", "CreatedAt", "ClientOrderId"],
                ct),
            "Trades" => await PagedAsync(
                db.Trades.AsNoTracking().OrderByDescending(x => x.Id),
                skip, pageSize,
                ["Id", "Symbol", "Side", "FilledQuantity", "AverageEntryPrice", "RealizedPnl", "EnteredAt", "ExitedAt"],
                ct),
            "EquityCurve" => await PagedAsync(
                db.EquityCurve.AsNoTracking().OrderByDescending(x => x.Id),
                skip, pageSize,
                ["Id", "Timestamp", "PortfolioValue", "CashBalance", "DailyPnl", "CumulativePnl"],
                ct),
            "BotState" => await PagedAsync(
                db.BotState.AsNoTracking().OrderBy(x => x.Key),
                skip, pageSize,
                ["Id", "Key", "Value", "UpdatedAt"],
                ct),
            "Bars" => await PagedAsync(
                db.Bars.AsNoTracking().OrderByDescending(x => x.Id),
                skip, pageSize,
                ["Id", "Symbol", "Timeframe", "Timestamp", "Open", "High", "Low", "Close", "Volume"],
                ct),
            "PositionSnapshots" => await PagedAsync(
                db.PositionSnapshots.AsNoTracking().OrderByDescending(x => x.Id),
                skip, pageSize,
                ["Id", "Symbol", "Quantity", "AverageEntryPrice", "CurrentPrice", "UnrealizedPnl", "SnapshotAt"],
                ct),
            "SignalGates" => await PagedAsync(
                db.SignalGates.AsNoTracking().OrderBy(x => x.GateName),
                skip, pageSize,
                ["Id", "GateName", "LastAcceptedBarTs", "LastAcceptedTs", "UpdatedAt"],
                ct),
            "Fills" => await PagedAsync(
                db.Fills.AsNoTracking().OrderByDescending(x => x.Id),
                skip, pageSize,
                ["Id", "AlpacaOrderId", "ClientOrderId", "FilledQuantity", "FilledPrice", "FilledAt"],
                ct),
            "PositionTracking" => await PagedAsync(
                db.PositionTracking.AsNoTracking().OrderBy(x => x.Symbol),
                skip, pageSize,
                ["Id", "Symbol", "CurrentQuantity", "EntryPrice", "AtrValue", "TrailingStopPrice", "LastUpdateAt"],
                ct),
            "ExitAttempts" => await PagedAsync(
                db.ExitAttempts.AsNoTracking().OrderBy(x => x.Symbol),
                skip, pageSize,
                ["Id", "Symbol", "AttemptCount", "LastAttemptAt", "NextRetryAt"],
                ct),
            "ReconciliationReports" => await PagedAsync(
                db.ReconciliationReports.AsNoTracking().OrderByDescending(x => x.Id),
                skip, pageSize,
                ["Id", "ReportDate", "OrdersProcessed", "TradesCompleted", "TotalPnl", "Status"],
                ct),
            "SchemaMeta" => await PagedAsync(
                db.SchemaMeta.AsNoTracking().OrderByDescending(x => x.Version),
                skip, pageSize,
                ["Id", "Version", "AppliedAt", "Description"],
                ct),
            "CircuitBreakerState" => await PagedAsync(
                db.CircuitBreakerState.AsNoTracking().OrderBy(x => x.Id),
                skip, pageSize,
                ["Id", "Count", "LastResetAt"],
                ct),
            "DrawdownState" => await PagedAsync(
                db.DrawdownState.AsNoTracking().OrderBy(x => x.Id),
                skip, pageSize,
                ["Id", "Level", "PeakEquity", "CurrentDrawdownPct", "LastUpdated", "ManualRecoveryRequested"],
                ct),
            _ => ([], 0, [])
        };
    }

    private static async ValueTask<(IReadOnlyList<object> Rows, int Total, IReadOnlyList<string> Columns)>
        PagedAsync<T>(IQueryable<T> query, int skip, int take, IReadOnlyList<string> columns,
        CancellationToken ct) where T : class
    {
        var total = await query.CountAsync(ct);
        var rows  = await query.Skip(skip).Take(take).ToListAsync(ct);
        return (rows.Cast<object>().ToList(), total, columns);
    }

    public async ValueTask<IReadOnlyList<FillEntity>> GetFillsAsync(
        string clientOrderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Fills.AsNoTracking()
            .Where(f => f.ClientOrderId == clientOrderId)
            .OrderByDescending(f => f.Id)
            .ToListAsync(ct);
    }

    public async ValueTask<IReadOnlyList<BarEntity>> GetRecentBarsAsync(
        string symbol, string timeframe, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bars = await db.Bars.AsNoTracking()
            .Where(b => b.Symbol == symbol && b.Timeframe == timeframe)
            .OrderByDescending(b => b.Id)
            .Take(limit)
            .ToListAsync(ct);
        // Reverse to chronological order
        return bars.AsEnumerable().Reverse().ToList();
    }

    public async ValueTask<(IReadOnlyList<string> Symbols, IReadOnlyList<string> Timeframes)>
        GetBarsFiltersAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var symbols = await db.Bars.Select(b => b.Symbol).Distinct().OrderBy(s => s).ToListAsync(ct);
            var timeframes = await db.Bars.Select(b => b.Timeframe).Distinct().OrderBy(t => t).ToListAsync(ct);
            return (symbols, timeframes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load bars filters");
            return ([], []);
        }
    }
}
