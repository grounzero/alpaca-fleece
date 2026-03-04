using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Reads and writes the bot's appsettings.json configuration file.
/// Atomic write pattern: write to temp file then rename. Maintains up to 5 backups.
/// </summary>
public sealed class ConfigService(
    IOptions<AdminOptions> opts,
    ILogger<ConfigService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask<ConfigDraft> ReadDraftAsync(CancellationToken ct = default)
    {
        var path = opts.Value.BotSettingsPath;
        if (!File.Exists(path)) return new ConfigDraft();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var draft = new ConfigDraft();

            if (root.TryGetProperty("Broker", out var broker))
            {
                draft.ApiKey = broker.TryGetProperty("ApiKey", out var v) ? v.GetString() ?? "" : "";
                draft.SecretKey = broker.TryGetProperty("SecretKey", out v) ? v.GetString() ?? "" : "";
                draft.IsPaperTrading = broker.TryGetProperty("IsPaperTrading", out v) && v.GetBoolean();
                draft.AllowLiveTrading = broker.TryGetProperty("AllowLiveTrading", out v) && v.GetBoolean();
                draft.KillSwitch = broker.TryGetProperty("KillSwitch", out v) && v.GetBoolean();
                draft.DryRun = broker.TryGetProperty("DryRun", out v) && v.GetBoolean();
            }

            if (root.TryGetProperty("Trading", out var trading))
            {
                if (trading.TryGetProperty("Symbols", out var syms))
                {
                    draft.CryptoSymbols = syms.TryGetProperty("CryptoSymbols", out var cs)
                        ? cs.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
                    draft.EquitySymbols = syms.TryGetProperty("EquitySymbols", out var es)
                        ? es.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
                    if (syms.TryGetProperty("MinVolume", out var mv)) draft.MinVolume = mv.GetInt64();
                }

                if (trading.TryGetProperty("Session", out var session))
                {
                    draft.TimeZone = session.TryGetProperty("TimeZone", out var v) ? v.GetString() ?? "" : "";
                    draft.MarketOpenTime = session.TryGetProperty("MarketOpenTime", out v) ? v.GetString() ?? "" : "";
                    draft.MarketCloseTime = session.TryGetProperty("MarketCloseTime", out v) ? v.GetString() ?? "" : "";
                }

                if (trading.TryGetProperty("RiskLimits", out var risk))
                {
                    draft.MaxDailyLoss = GetDecimal(risk, "MaxDailyLoss", draft.MaxDailyLoss);
                    draft.MaxTradeRisk = GetDecimal(risk, "MaxTradeRisk", draft.MaxTradeRisk);
                    draft.MaxTradesPerDay = GetInt(risk, "MaxTradesPerDay", draft.MaxTradesPerDay);
                    draft.MaxConcurrentPositions = GetInt(risk, "MaxConcurrentPositions", draft.MaxConcurrentPositions);
                    draft.MaxPositionSizePct = GetDecimal(risk, "MaxPositionSizePct", draft.MaxPositionSizePct);
                    draft.MaxRiskPerTradePct = GetDecimal(risk, "MaxRiskPerTradePct", draft.MaxRiskPerTradePct);
                    draft.StopLossPct = GetDecimal(risk, "StopLossPct", draft.StopLossPct);
                    draft.MinSignalConfidence = GetDecimal(risk, "MinSignalConfidence", draft.MinSignalConfidence);
                }

                if (trading.TryGetProperty("Exit", out var exit))
                {
                    draft.AtrStopLossMultiplier = GetDecimal(exit, "AtrStopLossMultiplier", draft.AtrStopLossMultiplier);
                    draft.AtrProfitTargetMultiplier = GetDecimal(exit, "AtrProfitTargetMultiplier", draft.AtrProfitTargetMultiplier);
                    draft.TrailingStopPercent = GetDecimal(exit, "TrailingStopPercent", draft.TrailingStopPercent);
                    draft.ExitCheckIntervalSeconds = GetInt(exit, "CheckIntervalSeconds", draft.ExitCheckIntervalSeconds);
                    draft.MaxPriceAgeSeconds = GetInt(exit, "MaxPriceAgeSeconds", draft.MaxPriceAgeSeconds);
                }

                if (trading.TryGetProperty("Execution", out var exec))
                {
                    draft.BarHistoryDepth = GetInt(exec, "BarHistoryDepth", draft.BarHistoryDepth);
                    draft.MaxBarAgeMinutes = GetInt(exec, "MaxBarAgeMinutes", draft.MaxBarAgeMinutes);
                    draft.AllowFractionalOrders = GetBool(exec, "AllowFractionalOrders");
                    draft.EntryOrderType = exec.TryGetProperty("EntryOrderType", out var v) ? v.GetString() ?? "Market" : "Market";
                }

                if (trading.TryGetProperty("Filters", out var filters))
                {
                    draft.MaxBidAskSpreadPercent = GetDecimal(filters, "MaxBidAskSpreadPercent", draft.MaxBidAskSpreadPercent);
                    draft.MinBarVolume = filters.TryGetProperty("MinBarVolume", out var v) ? v.GetInt64() : draft.MinBarVolume;
                    draft.MinMinutesAfterOpen = GetInt(filters, "MinMinutesAfterOpen", draft.MinMinutesAfterOpen);
                    draft.MinMinutesBeforeClose = GetInt(filters, "MinMinutesBeforeClose", draft.MinMinutesBeforeClose);
                }

                if (trading.TryGetProperty("Drawdown", out var dd))
                {
                    draft.DrawdownEnabled = GetBool(dd, "Enabled", true);
                    draft.LookbackDays = GetInt(dd, "LookbackDays", draft.LookbackDays);
                    draft.EnableAutoRecovery = GetBool(dd, "EnableAutoRecovery", true);
                    draft.WarningThresholdPct = GetDecimal(dd, "WarningThresholdPct", draft.WarningThresholdPct);
                    draft.HaltThresholdPct = GetDecimal(dd, "HaltThresholdPct", draft.HaltThresholdPct);
                    draft.EmergencyThresholdPct = GetDecimal(dd, "EmergencyThresholdPct", draft.EmergencyThresholdPct);
                    draft.WarningPositionMultiplier = GetDecimal(dd, "WarningPositionMultiplier", draft.WarningPositionMultiplier);
                }

                if (trading.TryGetProperty("SignalFilters", out var sf))
                {
                    draft.EnableDailyTrendFilter = GetBool(sf, "EnableDailyTrendFilter");
                    draft.DailySmaPeriod = GetInt(sf, "DailySmaPeriod", draft.DailySmaPeriod);
                    draft.EnableVolumeFilter = GetBool(sf, "EnableVolumeFilter");
                    draft.VolumeLookbackPeriod = GetInt(sf, "VolumeLookbackPeriod", draft.VolumeLookbackPeriod);
                    draft.VolumeMultiplier = GetDecimal(sf, "VolumeMultiplier", draft.VolumeMultiplier);
                }

                if (trading.TryGetProperty("CorrelationLimits", out var corr))
                {
                    draft.CorrelationEnabled = GetBool(corr, "Enabled", true);
                    draft.MaxCorrelation = GetDecimal(corr, "MaxCorrelation", draft.MaxCorrelation);
                    draft.MaxSectorPct = GetDecimal(corr, "MaxSectorPct", draft.MaxSectorPct);
                    draft.MaxAssetClassPct = GetDecimal(corr, "MaxAssetClassPct", draft.MaxAssetClassPct);
                }
            }

            draft.IsDirty = false;
            return draft;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse bot settings from {Path}", path);
            return new ConfigDraft();
        }
    }

    public async ValueTask WriteAsync(ConfigDraft draft, CancellationToken ct = default)
    {
        var path = opts.Value.BotSettingsPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Backup existing file (keep last 5)
        if (File.Exists(path))
        {
            var backupPath = path + $".bak.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Copy(path, backupPath, overwrite: true);
            CleanOldBackups(path);
        }

        var node = BuildJsonNode(draft);
        var json = node.ToJsonString(JsonOpts);
        var tmp = path + ".tmp";

        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
        logger.LogInformation("Bot settings written to {Path}", path);
    }

    public IReadOnlyList<string> ValidateDraft(ConfigDraft draft)
    {
        var errors = new List<string>();
        if (draft.MaxDailyLoss <= 0) errors.Add("MaxDailyLoss must be positive.");
        if (draft.MaxRiskPerTradePct is <= 0 or > 0.2m) errors.Add("MaxRiskPerTradePct must be 0.001–0.20.");
        if (draft.WarningThresholdPct >= draft.HaltThresholdPct)
            errors.Add("Warning drawdown threshold must be less than Halt threshold.");
        if (draft.HaltThresholdPct >= draft.EmergencyThresholdPct)
            errors.Add("Halt drawdown threshold must be less than Emergency threshold.");
        if (draft.AtrStopLossMultiplier <= 0) errors.Add("AtrStopLossMultiplier must be positive.");
        if (draft.AtrProfitTargetMultiplier <= draft.AtrStopLossMultiplier)
            errors.Add("AtrProfitTargetMultiplier must exceed AtrStopLossMultiplier.");
        if (!draft.IsPaperTrading && draft.AllowLiveTrading)
            errors.Add("Warning: Live trading is enabled. Ensure this is intentional.");
        return errors;
    }

    public ValueTask<IReadOnlyList<string>> GetBackupsAsync()
    {
        var path = opts.Value.BotSettingsPath;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileName(path);
        if (!Directory.Exists(dir)) return ValueTask.FromResult<IReadOnlyList<string>>([]);

        var backups = Directory.GetFiles(dir, name + ".bak.*")
            .OrderByDescending(f => f)
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<string>>(backups);
    }

    public async ValueTask RestoreBackupAsync(string backupPath, CancellationToken ct = default)
    {
        var dest = opts.Value.BotSettingsPath;
        var json = await File.ReadAllTextAsync(backupPath, ct);
        await File.WriteAllTextAsync(dest, json, ct);
        logger.LogInformation("Restored backup {Backup} to {Dest}", backupPath, dest);
    }

    public async ValueTask<string> GetRawJsonAsync(CancellationToken ct = default)
    {
        var path = opts.Value.BotSettingsPath;
        if (!File.Exists(path)) return "{}";
        return await File.ReadAllTextAsync(path, ct);
    }

    private void CleanOldBackups(string basePath)
    {
        var dir = Path.GetDirectoryName(basePath) ?? ".";
        var name = Path.GetFileName(basePath);
        var old = Directory.GetFiles(dir, name + ".bak.*")
            .OrderByDescending(f => f)
            .Skip(5)
            .ToList();
        foreach (var f in old)
        {
            try { File.Delete(f); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not delete old backup {F}", f); }
        }
    }

    private static JsonNode BuildJsonNode(ConfigDraft d)
    {
        return new JsonObject
        {
            ["Broker"] = new JsonObject
            {
                ["ApiKey"] = d.ApiKey,
                ["SecretKey"] = d.SecretKey,
                ["IsPaperTrading"] = d.IsPaperTrading,
                ["AllowLiveTrading"] = d.AllowLiveTrading,
                ["KillSwitch"] = d.KillSwitch,
                ["DryRun"] = d.DryRun
            },
            ["Trading"] = new JsonObject
            {
                ["Symbols"] = new JsonObject
                {
                    ["CryptoSymbols"] = new JsonArray([.. d.CryptoSymbols.Select(s => JsonValue.Create(s)!)]),
                    ["EquitySymbols"] = new JsonArray([.. d.EquitySymbols.Select(s => JsonValue.Create(s)!)]),
                    ["MinVolume"] = d.MinVolume
                },
                ["Session"] = new JsonObject
                {
                    ["TimeZone"] = d.TimeZone,
                    ["MarketOpenTime"] = d.MarketOpenTime,
                    ["MarketCloseTime"] = d.MarketCloseTime
                },
                ["RiskLimits"] = new JsonObject
                {
                    ["MaxDailyLoss"] = d.MaxDailyLoss,
                    ["MaxTradeRisk"] = d.MaxTradeRisk,
                    ["MaxTradesPerDay"] = d.MaxTradesPerDay,
                    ["MaxConcurrentPositions"] = d.MaxConcurrentPositions,
                    ["MaxPositionSizePct"] = d.MaxPositionSizePct,
                    ["MaxRiskPerTradePct"] = d.MaxRiskPerTradePct,
                    ["StopLossPct"] = d.StopLossPct,
                    ["MinSignalConfidence"] = d.MinSignalConfidence
                },
                ["Exit"] = new JsonObject
                {
                    ["AtrStopLossMultiplier"] = d.AtrStopLossMultiplier,
                    ["AtrProfitTargetMultiplier"] = d.AtrProfitTargetMultiplier,
                    ["TrailingStopPercent"] = d.TrailingStopPercent,
                    ["CheckIntervalSeconds"] = d.ExitCheckIntervalSeconds,
                    ["MaxPriceAgeSeconds"] = d.MaxPriceAgeSeconds
                },
                ["Execution"] = new JsonObject
                {
                    ["BarHistoryDepth"] = d.BarHistoryDepth,
                    ["MaxBarAgeMinutes"] = d.MaxBarAgeMinutes,
                    ["AllowFractionalOrders"] = d.AllowFractionalOrders,
                    ["EntryOrderType"] = d.EntryOrderType
                },
                ["Filters"] = new JsonObject
                {
                    ["MaxBidAskSpreadPercent"] = d.MaxBidAskSpreadPercent,
                    ["MinBarVolume"] = d.MinBarVolume,
                    ["MinMinutesAfterOpen"] = d.MinMinutesAfterOpen,
                    ["MinMinutesBeforeClose"] = d.MinMinutesBeforeClose
                },
                ["Drawdown"] = new JsonObject
                {
                    ["Enabled"] = d.DrawdownEnabled,
                    ["LookbackDays"] = d.LookbackDays,
                    ["EnableAutoRecovery"] = d.EnableAutoRecovery,
                    ["WarningThresholdPct"] = d.WarningThresholdPct,
                    ["HaltThresholdPct"] = d.HaltThresholdPct,
                    ["EmergencyThresholdPct"] = d.EmergencyThresholdPct,
                    ["WarningPositionMultiplier"] = d.WarningPositionMultiplier
                },
                ["SignalFilters"] = new JsonObject
                {
                    ["EnableDailyTrendFilter"] = d.EnableDailyTrendFilter,
                    ["DailySmaPeriod"] = d.DailySmaPeriod,
                    ["EnableVolumeFilter"] = d.EnableVolumeFilter,
                    ["VolumeLookbackPeriod"] = d.VolumeLookbackPeriod,
                    ["VolumeMultiplier"] = d.VolumeMultiplier
                },
                ["CorrelationLimits"] = new JsonObject
                {
                    ["Enabled"] = d.CorrelationEnabled,
                    ["MaxCorrelation"] = d.MaxCorrelation,
                    ["MaxSectorPct"] = d.MaxSectorPct,
                    ["MaxAssetClassPct"] = d.MaxAssetClassPct,
                    ["StaticCorrelations"] = new JsonObject()
                }
            }
        };
    }

    private static decimal GetDecimal(JsonElement el, string key, decimal fallback)
    {
        if (el.TryGetProperty(key, out var v) && v.TryGetDecimal(out var d)) return d;
        return fallback;
    }

    private static int GetInt(JsonElement el, string key, int fallback)
    {
        if (el.TryGetProperty(key, out var v) && v.TryGetInt32(out var i)) return i;
        return fallback;
    }

    private static bool GetBool(JsonElement el, string key, bool fallback = false)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True) return true;
        if (el.TryGetProperty(key, out v) && v.ValueKind == JsonValueKind.False) return false;
        return fallback;
    }
}
