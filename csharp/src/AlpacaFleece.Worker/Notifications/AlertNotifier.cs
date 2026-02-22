namespace AlpacaFleece.Worker.Notifications;

/// <summary>
/// Alert notifier: sends alerts via Slack, email, or silent (logger-only).
/// Retry logic: 3 attempts with exponential backoff (1s, 2s, 4s).
/// Named alerts for common events.
/// </summary>
public sealed class AlertNotifier(
    ILogger<AlertNotifier> logger,
    IOptions<NotificationOptions> options)
{
    private readonly NotificationOptions _options = options.Value;
    private const int MaxRetries = 3;

    /// <summary>
    /// Sends an alert with retry logic and backoff.
    /// </summary>
    public async ValueTask SendAlertAsync(
        string title,
        string message,
        AlertSeverity severity = AlertSeverity.Info,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("Alert disabled: {title} - {message}", title, message);
            return;
        }

        var delayMs = 1000; // Start with 1s

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                switch (_options.NotificationChannel)
                {
                    case NotificationChannel.Slack:
                        await SendSlackAsync(title, message, severity, ct);
                        return;

                    case NotificationChannel.Email:
                        await SendEmailAsync(title, message, severity, ct);
                        return;

                    case NotificationChannel.Silent:
                        LogAlert(title, message, severity);
                        return;

                    default:
                        LogAlert(title, message, severity);
                        return;
                }
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                logger.LogWarning(ex, "Alert send failed (attempt {attempt}), retrying in {delay}ms",
                    attempt + 1, delayMs);
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Alert send failed after {attempts} attempts", MaxRetries);
                return;
            }
        }
    }

    /// <summary>
    /// Circuit breaker tripped alert.
    /// </summary>
    public async ValueTask CircuitBreakerTrippedAsync(
        int failureCount,
        CancellationToken ct = default)
    {
        var message = $"Circuit breaker tripped after {failureCount} failures. Trading halted.";
        await SendAlertAsync("⚠️ Circuit Breaker Tripped", message,
            AlertSeverity.Critical, ct);
    }

    /// <summary>
    /// Daily loss limit exceeded alert.
    /// </summary>
    public async ValueTask DailyLossLimitExceededAsync(
        decimal pnl,
        decimal limit,
        CancellationToken ct = default)
    {
        var message = $"Daily P&L {pnl:C} exceeds limit {limit:C}. Trading halted.";
        await SendAlertAsync("💔 Daily Loss Limit Exceeded", message,
            AlertSeverity.Critical, ct);
    }

    /// <summary>
    /// Position size limit exceeded alert.
    /// </summary>
    public async ValueTask PositionSizeLimitExceededAsync(
        decimal current,
        decimal limit,
        CancellationToken ct = default)
    {
        var message = $"Position size {current:C} exceeds limit {limit:C}.";
        await SendAlertAsync("📊 Position Size Limit Exceeded", message,
            AlertSeverity.Warning, ct);
    }

    /// <summary>
    /// Order submission failed alert.
    /// </summary>
    public async ValueTask OrderSubmissionFailedAsync(
        string symbol,
        string error,
        CancellationToken ct = default)
    {
        var message = $"Failed to submit order for {symbol}: {error}";
        await SendAlertAsync("❌ Order Submission Failed", message,
            AlertSeverity.Error, ct);
    }

    /// <summary>
    /// Ghost position detected alert.
    /// </summary>
    public async ValueTask GhostPositionDetectedAsync(
        string symbol,
        CancellationToken ct = default)
    {
        var message = $"Ghost position detected for {symbol} and auto-cleared.";
        await SendAlertAsync("👻 Ghost Position Cleared", message,
            AlertSeverity.Warning, ct);
    }

    /// <summary>
    /// Reconciliation failed alert.
    /// </summary>
    public async ValueTask ReconciliationFailedAsync(
        IReadOnlyList<string> discrepancies,
        CancellationToken ct = default)
    {
        var message = $"Reconciliation failed with {discrepancies.Count} discrepancies.";
        await SendAlertAsync("🔄 Reconciliation Failed", message,
            AlertSeverity.Critical, ct);
    }

    /// <summary>
    /// Sends Slack webhook alert.
    /// </summary>
    private async ValueTask SendSlackAsync(
        string title,
        string message,
        AlertSeverity severity,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.SlackWebhookUrl))
        {
            throw new InvalidOperationException("Slack webhook URL not configured");
        }

        var color = severity switch
        {
            AlertSeverity.Info => "#36a64f",
            AlertSeverity.Warning => "#ff9900",
            AlertSeverity.Error => "#ff6600",
            AlertSeverity.Critical => "#ff0000",
            _ => "#808080"
        };

        var payload = new
        {
            attachments = new[]
            {
                new
                {
                    color = color,
                    title = title,
                    text = message,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            }
        };

        using var client = new HttpClient();
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync(_options.SlackWebhookUrl, content, ct);
        response.EnsureSuccessStatusCode();

        logger.LogInformation("Slack alert sent: {title}", title);
    }

    /// <summary>
    /// Sends email alert via SMTP (using MailKit).
    /// </summary>
    private async ValueTask SendEmailAsync(
        string title,
        string message,
        AlertSeverity severity,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.SmtpServer) ||
            string.IsNullOrEmpty(_options.EmailFrom) ||
            string.IsNullOrEmpty(_options.EmailTo))
        {
            throw new InvalidOperationException("SMTP configuration incomplete");
        }

        var body = $@"
<h2>{title}</h2>
<p>{message}</p>
<p>
    <strong>Severity:</strong> {severity}<br/>
    <strong>Time:</strong> {DateTimeOffset.UtcNow:O}
</p>
";

        var mailMessage = new MimeMessage();
        mailMessage.From.Add(MailboxAddress.Parse(_options.EmailFrom));
        mailMessage.To.Add(MailboxAddress.Parse(_options.EmailTo));
        mailMessage.Subject = $"[AlpacaFleece] {title}";

        var bodyBuilder = new BodyBuilder { HtmlBody = body };
        mailMessage.Body = bodyBuilder.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(
            _options.SmtpServer,
            _options.SmtpPort,
            _options.SmtpUseSsl ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.None,
            ct);

        if (!string.IsNullOrEmpty(_options.SmtpUsername) && !string.IsNullOrEmpty(_options.SmtpPassword))
        {
            await client.AuthenticateAsync(
                _options.SmtpUsername,
                _options.SmtpPassword,
                ct);
        }

        await client.SendAsync(mailMessage, ct);
        await client.DisconnectAsync(true, ct);

        logger.LogInformation("Email alert sent to {to}: {title}", _options.EmailTo, title);
    }

    /// <summary>
    /// Logs alert without sending external notification.
    /// </summary>
    private void LogAlert(string title, string message, AlertSeverity severity)
    {
        var level = severity switch
        {
            AlertSeverity.Info => LogLevel.Information,
            AlertSeverity.Warning => LogLevel.Warning,
            AlertSeverity.Error => LogLevel.Error,
            AlertSeverity.Critical => LogLevel.Critical,
            _ => LogLevel.Information
        };

        logger.Log(level, "Alert [{severity}]: {title} - {message}",
            severity, title, message);
    }
}

/// <summary>
/// Alert severity levels.
/// </summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Notification channel options.
/// </summary>
public enum NotificationChannel
{
    Silent,
    Slack,
    Email
}

/// <summary>
/// Configuration options for notifications.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>
    /// Enable/disable notifications (default: false).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Notification channel (default: Silent).
    /// </summary>
    public NotificationChannel NotificationChannel { get; set; } = NotificationChannel.Silent;

    /// <summary>
    /// Slack webhook URL (for Slack channel).
    /// </summary>
    public string? SlackWebhookUrl { get; set; }

    /// <summary>
    /// SMTP server hostname.
    /// </summary>
    public string? SmtpServer { get; set; }

    /// <summary>
    /// SMTP server port (default: 587).
    /// </summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// SMTP username.
    /// </summary>
    public string? SmtpUsername { get; set; }

    /// <summary>
    /// SMTP password (never logged).
    /// </summary>
    public string? SmtpPassword { get; set; }

    /// <summary>
    /// Use SSL for SMTP (default: true).
    /// </summary>
    public bool SmtpUseSsl { get; set; } = true;

    /// <summary>
    /// Email from address.
    /// </summary>
    public string? EmailFrom { get; set; }

    /// <summary>
    /// Email to address.
    /// </summary>
    public string? EmailTo { get; set; }
}
