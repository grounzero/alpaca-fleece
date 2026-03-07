using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Reads and writes the bot's appsettings.json configuration file.
/// Atomic write pattern: write to temp file then rename. Maintains up to 5 backups.
/// </summary>
public sealed class ConfigService(
    IOptions<AdminOptions> options,
    ILogger<ConfigService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask<ConfigDraft> ReadDraftAsync(CancellationToken ct = default)
    {
        var path = options.Value.BotSettingsPath;
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

                if (trading.TryGetProperty("VolatilityRegime", out var vr))
                {
                    draft.VolatilityRegimeEnabled = GetBool(vr, "Enabled");
                    draft.VolatilityLookbackBars = GetInt(vr, "LookbackBars", draft.VolatilityLookbackBars);
                    draft.VolatilityTransitionConfirmationBars = GetInt(vr, "TransitionConfirmationBars", draft.VolatilityTransitionConfirmationBars);
                    draft.VolatilityHysteresisBuffer = GetDecimal(vr, "HysteresisBuffer", draft.VolatilityHysteresisBuffer);
                    draft.VolatilityLowMaxVolatility = GetDecimal(vr, "LowMaxVolatility", draft.VolatilityLowMaxVolatility);
                    draft.VolatilityNormalMaxVolatility = GetDecimal(vr, "NormalMaxVolatility", draft.VolatilityNormalMaxVolatility);
                    draft.VolatilityHighMaxVolatility = GetDecimal(vr, "HighMaxVolatility", draft.VolatilityHighMaxVolatility);
                    draft.VolatilityLowPositionMultiplier = GetDecimal(vr, "LowPositionMultiplier", draft.VolatilityLowPositionMultiplier);
                    draft.VolatilityNormalPositionMultiplier = GetDecimal(vr, "NormalPositionMultiplier", draft.VolatilityNormalPositionMultiplier);
                    draft.VolatilityHighPositionMultiplier = GetDecimal(vr, "HighPositionMultiplier", draft.VolatilityHighPositionMultiplier);
                    draft.VolatilityExtremePositionMultiplier = GetDecimal(vr, "ExtremePositionMultiplier", draft.VolatilityExtremePositionMultiplier);
                    draft.VolatilityLowStopMultiplier = GetDecimal(vr, "LowStopMultiplier", draft.VolatilityLowStopMultiplier);
                    draft.VolatilityNormalStopMultiplier = GetDecimal(vr, "NormalStopMultiplier", draft.VolatilityNormalStopMultiplier);
                    draft.VolatilityHighStopMultiplier = GetDecimal(vr, "HighStopMultiplier", draft.VolatilityHighStopMultiplier);
                    draft.VolatilityExtremeStopMultiplier = GetDecimal(vr, "ExtremeStopMultiplier", draft.VolatilityExtremeStopMultiplier);

                    if (vr.TryGetProperty("Equity", out var equity))
                    {
                        draft.UseEquityVolatilityOverrides = true;
                        draft.EquityVolatilityLookbackBars = GetInt(equity, "LookbackBars", draft.EquityVolatilityLookbackBars);
                        draft.EquityVolatilityTransitionConfirmationBars = GetInt(equity, "TransitionConfirmationBars", draft.EquityVolatilityTransitionConfirmationBars);
                        draft.EquityVolatilityHysteresisBuffer = GetDecimal(equity, "HysteresisBuffer", draft.EquityVolatilityHysteresisBuffer);
                        draft.EquityVolatilityLowMaxVolatility = GetDecimal(equity, "LowMaxVolatility", draft.EquityVolatilityLowMaxVolatility);
                        draft.EquityVolatilityNormalMaxVolatility = GetDecimal(equity, "NormalMaxVolatility", draft.EquityVolatilityNormalMaxVolatility);
                        draft.EquityVolatilityHighMaxVolatility = GetDecimal(equity, "HighMaxVolatility", draft.EquityVolatilityHighMaxVolatility);
                        draft.EquityVolatilityLowPositionMultiplier = GetDecimal(equity, "LowPositionMultiplier", draft.EquityVolatilityLowPositionMultiplier);
                        draft.EquityVolatilityNormalPositionMultiplier = GetDecimal(equity, "NormalPositionMultiplier", draft.EquityVolatilityNormalPositionMultiplier);
                        draft.EquityVolatilityHighPositionMultiplier = GetDecimal(equity, "HighPositionMultiplier", draft.EquityVolatilityHighPositionMultiplier);
                        draft.EquityVolatilityExtremePositionMultiplier = GetDecimal(equity, "ExtremePositionMultiplier", draft.EquityVolatilityExtremePositionMultiplier);
                        draft.EquityVolatilityLowStopMultiplier = GetDecimal(equity, "LowStopMultiplier", draft.EquityVolatilityLowStopMultiplier);
                        draft.EquityVolatilityNormalStopMultiplier = GetDecimal(equity, "NormalStopMultiplier", draft.EquityVolatilityNormalStopMultiplier);
                        draft.EquityVolatilityHighStopMultiplier = GetDecimal(equity, "HighStopMultiplier", draft.EquityVolatilityHighStopMultiplier);
                        draft.EquityVolatilityExtremeStopMultiplier = GetDecimal(equity, "ExtremeStopMultiplier", draft.EquityVolatilityExtremeStopMultiplier);
                    }

                    if (vr.TryGetProperty("Crypto", out var crypto))
                    {
                        draft.UseCryptoVolatilityOverrides = true;
                        draft.CryptoVolatilityLookbackBars = GetInt(crypto, "LookbackBars", draft.CryptoVolatilityLookbackBars);
                        draft.CryptoVolatilityTransitionConfirmationBars = GetInt(crypto, "TransitionConfirmationBars", draft.CryptoVolatilityTransitionConfirmationBars);
                        draft.CryptoVolatilityHysteresisBuffer = GetDecimal(crypto, "HysteresisBuffer", draft.CryptoVolatilityHysteresisBuffer);
                        draft.CryptoVolatilityLowMaxVolatility = GetDecimal(crypto, "LowMaxVolatility", draft.CryptoVolatilityLowMaxVolatility);
                        draft.CryptoVolatilityNormalMaxVolatility = GetDecimal(crypto, "NormalMaxVolatility", draft.CryptoVolatilityNormalMaxVolatility);
                        draft.CryptoVolatilityHighMaxVolatility = GetDecimal(crypto, "HighMaxVolatility", draft.CryptoVolatilityHighMaxVolatility);
                        draft.CryptoVolatilityLowPositionMultiplier = GetDecimal(crypto, "LowPositionMultiplier", draft.CryptoVolatilityLowPositionMultiplier);
                        draft.CryptoVolatilityNormalPositionMultiplier = GetDecimal(crypto, "NormalPositionMultiplier", draft.CryptoVolatilityNormalPositionMultiplier);
                        draft.CryptoVolatilityHighPositionMultiplier = GetDecimal(crypto, "HighPositionMultiplier", draft.CryptoVolatilityHighPositionMultiplier);
                        draft.CryptoVolatilityExtremePositionMultiplier = GetDecimal(crypto, "ExtremePositionMultiplier", draft.CryptoVolatilityExtremePositionMultiplier);
                        draft.CryptoVolatilityLowStopMultiplier = GetDecimal(crypto, "LowStopMultiplier", draft.CryptoVolatilityLowStopMultiplier);
                        draft.CryptoVolatilityNormalStopMultiplier = GetDecimal(crypto, "NormalStopMultiplier", draft.CryptoVolatilityNormalStopMultiplier);
                        draft.CryptoVolatilityHighStopMultiplier = GetDecimal(crypto, "HighStopMultiplier", draft.CryptoVolatilityHighStopMultiplier);
                        draft.CryptoVolatilityExtremeStopMultiplier = GetDecimal(crypto, "ExtremeStopMultiplier", draft.CryptoVolatilityExtremeStopMultiplier);
                    }
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
        var path = options.Value.BotSettingsPath;
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

        // Read existing JSON to preserve credentials if draft has blanks
        JsonNode? existingNode = null;
        if (File.Exists(path))
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(path, ct);
                existingNode = JsonNode.Parse(existingJson);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read existing config for credential preservation");
            }
        }

        var node = BuildJsonNode(draft, existingNode);
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

        ValidateVolatilityProfile(
            errors,
            "VolatilityRegime",
            draft.VolatilityLookbackBars,
            draft.VolatilityTransitionConfirmationBars,
            draft.VolatilityLowMaxVolatility,
            draft.VolatilityNormalMaxVolatility,
            draft.VolatilityHighMaxVolatility,
            draft.VolatilityLowPositionMultiplier,
            draft.VolatilityNormalPositionMultiplier,
            draft.VolatilityHighPositionMultiplier,
            draft.VolatilityExtremePositionMultiplier,
            draft.VolatilityLowStopMultiplier,
            draft.VolatilityNormalStopMultiplier,
            draft.VolatilityHighStopMultiplier,
            draft.VolatilityExtremeStopMultiplier);

        if (draft.UseEquityVolatilityOverrides)
        {
            ValidateVolatilityProfile(
                errors,
                "VolatilityRegime.Equity",
                draft.EquityVolatilityLookbackBars,
                draft.EquityVolatilityTransitionConfirmationBars,
                draft.EquityVolatilityLowMaxVolatility,
                draft.EquityVolatilityNormalMaxVolatility,
                draft.EquityVolatilityHighMaxVolatility,
                draft.EquityVolatilityLowPositionMultiplier,
                draft.EquityVolatilityNormalPositionMultiplier,
                draft.EquityVolatilityHighPositionMultiplier,
                draft.EquityVolatilityExtremePositionMultiplier,
                draft.EquityVolatilityLowStopMultiplier,
                draft.EquityVolatilityNormalStopMultiplier,
                draft.EquityVolatilityHighStopMultiplier,
                draft.EquityVolatilityExtremeStopMultiplier);
        }

        if (draft.UseCryptoVolatilityOverrides)
        {
            ValidateVolatilityProfile(
                errors,
                "VolatilityRegime.Crypto",
                draft.CryptoVolatilityLookbackBars,
                draft.CryptoVolatilityTransitionConfirmationBars,
                draft.CryptoVolatilityLowMaxVolatility,
                draft.CryptoVolatilityNormalMaxVolatility,
                draft.CryptoVolatilityHighMaxVolatility,
                draft.CryptoVolatilityLowPositionMultiplier,
                draft.CryptoVolatilityNormalPositionMultiplier,
                draft.CryptoVolatilityHighPositionMultiplier,
                draft.CryptoVolatilityExtremePositionMultiplier,
                draft.CryptoVolatilityLowStopMultiplier,
                draft.CryptoVolatilityNormalStopMultiplier,
                draft.CryptoVolatilityHighStopMultiplier,
                draft.CryptoVolatilityExtremeStopMultiplier);
        }

        return errors;
    }

    public ValueTask<IReadOnlyList<string>> GetBackupsAsync()
    {
        var path = options.Value.BotSettingsPath;
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
        var dest = options.Value.BotSettingsPath;
        var json = await File.ReadAllTextAsync(backupPath, ct);
        await File.WriteAllTextAsync(dest, json, ct);
        logger.LogInformation("Restored backup {Backup} to {Dest}", backupPath, dest);
    }

    public async ValueTask<string> GetRawJsonAsync(CancellationToken ct = default)
    {
        var path = options.Value.BotSettingsPath;
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

    private static JsonNode BuildJsonNode(ConfigDraft d, JsonNode? existingNode = null)
    {
        // Preserve existing credentials if draft values are blank (blank means unchanged)
        var apiKey = string.IsNullOrWhiteSpace(d.ApiKey)
            ? GetExistingValue(existingNode, "Broker", "ApiKey", "")
            : d.ApiKey;
        var secretKey = string.IsNullOrWhiteSpace(d.SecretKey)
            ? GetExistingValue(existingNode, "Broker", "SecretKey", "")
            : d.SecretKey;

        return new JsonObject
        {
            ["Broker"] = new JsonObject
            {
                ["ApiKey"] = apiKey,
                ["SecretKey"] = secretKey,
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
                },
                ["VolatilityRegime"] = BuildVolatilityRegimeNode(d)
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

    private static string GetExistingValue(JsonNode? node, string section, string key, string fallback)
    {
        if (node?[section]?[key]?.GetValue<string>() is { } value && !string.IsNullOrEmpty(value))
            return value;
        return fallback;
    }

    private static JsonObject BuildVolatilityRegimeNode(ConfigDraft d)
    {
        var node = new JsonObject
        {
            ["Enabled"] = d.VolatilityRegimeEnabled,
            ["LookbackBars"] = d.VolatilityLookbackBars,
            ["TransitionConfirmationBars"] = d.VolatilityTransitionConfirmationBars,
            ["HysteresisBuffer"] = d.VolatilityHysteresisBuffer,
            ["LowMaxVolatility"] = d.VolatilityLowMaxVolatility,
            ["NormalMaxVolatility"] = d.VolatilityNormalMaxVolatility,
            ["HighMaxVolatility"] = d.VolatilityHighMaxVolatility,
            ["LowPositionMultiplier"] = d.VolatilityLowPositionMultiplier,
            ["NormalPositionMultiplier"] = d.VolatilityNormalPositionMultiplier,
            ["HighPositionMultiplier"] = d.VolatilityHighPositionMultiplier,
            ["ExtremePositionMultiplier"] = d.VolatilityExtremePositionMultiplier,
            ["LowStopMultiplier"] = d.VolatilityLowStopMultiplier,
            ["NormalStopMultiplier"] = d.VolatilityNormalStopMultiplier,
            ["HighStopMultiplier"] = d.VolatilityHighStopMultiplier,
            ["ExtremeStopMultiplier"] = d.VolatilityExtremeStopMultiplier
        };

        if (d.UseEquityVolatilityOverrides)
        {
            node["Equity"] = new JsonObject
            {
                ["LookbackBars"] = d.EquityVolatilityLookbackBars,
                ["TransitionConfirmationBars"] = d.EquityVolatilityTransitionConfirmationBars,
                ["HysteresisBuffer"] = d.EquityVolatilityHysteresisBuffer,
                ["LowMaxVolatility"] = d.EquityVolatilityLowMaxVolatility,
                ["NormalMaxVolatility"] = d.EquityVolatilityNormalMaxVolatility,
                ["HighMaxVolatility"] = d.EquityVolatilityHighMaxVolatility,
                ["LowPositionMultiplier"] = d.EquityVolatilityLowPositionMultiplier,
                ["NormalPositionMultiplier"] = d.EquityVolatilityNormalPositionMultiplier,
                ["HighPositionMultiplier"] = d.EquityVolatilityHighPositionMultiplier,
                ["ExtremePositionMultiplier"] = d.EquityVolatilityExtremePositionMultiplier,
                ["LowStopMultiplier"] = d.EquityVolatilityLowStopMultiplier,
                ["NormalStopMultiplier"] = d.EquityVolatilityNormalStopMultiplier,
                ["HighStopMultiplier"] = d.EquityVolatilityHighStopMultiplier,
                ["ExtremeStopMultiplier"] = d.EquityVolatilityExtremeStopMultiplier
            };
        }

        if (d.UseCryptoVolatilityOverrides)
        {
            node["Crypto"] = new JsonObject
            {
                ["LookbackBars"] = d.CryptoVolatilityLookbackBars,
                ["TransitionConfirmationBars"] = d.CryptoVolatilityTransitionConfirmationBars,
                ["HysteresisBuffer"] = d.CryptoVolatilityHysteresisBuffer,
                ["LowMaxVolatility"] = d.CryptoVolatilityLowMaxVolatility,
                ["NormalMaxVolatility"] = d.CryptoVolatilityNormalMaxVolatility,
                ["HighMaxVolatility"] = d.CryptoVolatilityHighMaxVolatility,
                ["LowPositionMultiplier"] = d.CryptoVolatilityLowPositionMultiplier,
                ["NormalPositionMultiplier"] = d.CryptoVolatilityNormalPositionMultiplier,
                ["HighPositionMultiplier"] = d.CryptoVolatilityHighPositionMultiplier,
                ["ExtremePositionMultiplier"] = d.CryptoVolatilityExtremePositionMultiplier,
                ["LowStopMultiplier"] = d.CryptoVolatilityLowStopMultiplier,
                ["NormalStopMultiplier"] = d.CryptoVolatilityNormalStopMultiplier,
                ["HighStopMultiplier"] = d.CryptoVolatilityHighStopMultiplier,
                ["ExtremeStopMultiplier"] = d.CryptoVolatilityExtremeStopMultiplier
            };
        }

        return node;
    }

    private static void ValidateVolatilityProfile(
        List<string> errors,
        string profileName,
        int lookbackBars,
        int transitionConfirmationBars,
        decimal lowMaxVolatility,
        decimal normalMaxVolatility,
        decimal highMaxVolatility,
        decimal lowPositionMultiplier,
        decimal normalPositionMultiplier,
        decimal highPositionMultiplier,
        decimal extremePositionMultiplier,
        decimal lowStopMultiplier,
        decimal normalStopMultiplier,
        decimal highStopMultiplier,
        decimal extremeStopMultiplier)
    {
        if (lookbackBars < 5)
            errors.Add($"{profileName}: LookbackBars must be >= 5.");
        if (transitionConfirmationBars < 1)
            errors.Add($"{profileName}: TransitionConfirmationBars must be >= 1.");
        if (!(lowMaxVolatility < normalMaxVolatility && normalMaxVolatility < highMaxVolatility))
            errors.Add($"{profileName}: volatility thresholds must satisfy Low < Normal < High.");

        if (lowPositionMultiplier <= 0 || normalPositionMultiplier <= 0 ||
            highPositionMultiplier <= 0 || extremePositionMultiplier <= 0)
            errors.Add($"{profileName}: all position multipliers must be > 0.");

        if (lowStopMultiplier <= 0 || normalStopMultiplier <= 0 ||
            highStopMultiplier <= 0 || extremeStopMultiplier <= 0)
            errors.Add($"{profileName}: all stop multipliers must be > 0.");
    }
}
