namespace AlpacaFleece.AdminUI.Config;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>BCrypt hash of the admin password. Set via ADMIN_PASSWORD_HASH env var.</summary>
    public string AdminPasswordHash { get; init; } = "";

    /// <summary>Docker container name of the trading bot.</summary>
    public string BotContainerName { get; init; } = "alpaca-fleece-bot";

    /// <summary>Path to the bot's SQLite database file (shared volume).</summary>
    public string DatabasePath { get; init; } = "/app/data/trading.db";

    /// <summary>Path to the bot's Serilog rolling log file (shared volume).</summary>
    public string LogPath { get; init; } = "/app/logs/alpaca-fleece.log";

    /// <summary>Path to the bot's appsettings.json (shared volume, editable).</summary>
    public string BotSettingsPath { get; init; } = "/app/config/appsettings.json";

    /// <summary>Path to the bot's metrics.json snapshot.</summary>
    public string MetricsPath { get; init; } = "/app/data/metrics.json";

    /// <summary>Path to the bot's health.json snapshot.</summary>
    public string HealthPath { get; init; } = "/app/data/health.json";

    /// <summary>Admin session duration in hours.</summary>
    public int SessionHours { get; init; } = 8;

    /// <summary>Dashboard auto-refresh interval in seconds.</summary>
    public int DashboardRefreshSeconds { get; init; } = 30;
}
