using System.Text.RegularExpressions;

namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Singleton background service that watches the bot's log file for new lines
/// and broadcasts them to connected SignalR clients via a pub/sub channel.
/// </summary>
public sealed partial class LogStreamService(
    IOptions<AdminOptions> options,
    ILogger<LogStreamService> logger) : IHostedService, IDisposable
{
    private static readonly Regex LineRegex = LogStreamLineRegex();

    [GeneratedRegex(
        @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(DBG|INF|WRN|ERR|FTL)\] (.+)$",
        RegexOptions.Compiled)]
    private static partial Regex LogStreamLineRegex();

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _tailTask;
    private long _lastPosition;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private int _lineCounter;

    /// <summary>
    /// Raised on the background thread each time a new log line is parsed.
    /// Blazor Server components subscribe here directly — no HubConnection needed.
    /// Use InvokeAsync(StateHasChanged) inside the handler.
    /// </summary>
    public event Action<LogLine>? NewLine;

    /// <summary>
    /// Resolves the actual log file, handling Serilog's rolling-file date suffix.
    /// e.g. alpaca-fleece.log → alpaca-fleece20260304.log
    /// </summary>
    private string ResolveLogPath()
    {
        var configured = options.Value.LogPath;
        if (File.Exists(configured)) return configured;

        var dir  = Path.GetDirectoryName(configured) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(configured);
        var ext  = Path.GetExtension(configured);

        if (!Directory.Exists(dir)) return configured;

        return Directory.GetFiles(dir, $"{stem}*{ext}")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault() ?? configured;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();

        // Watch the log directory (not a specific file) so day-rollover creates new watcher events
        var dir = Path.GetDirectoryName(options.Value.LogPath) ?? ".";
        if (Directory.Exists(dir))
        {
            var resolved = ResolveLogPath();
            if (File.Exists(resolved))
                _lastPosition = new FileInfo(resolved).Length;

            _watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }

        _tailTask = PollAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _watcher?.Dispose();
        if (_cts != null) await _cts.CancelAsync();
        if (_tailTask != null) await _tailTask.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _ = ReadNewLinesAsync(_cts?.Token ?? CancellationToken.None);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            await ReadNewLinesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ReadNewLinesAsync(CancellationToken ct)
    {
        var path = ResolveLogPath();
        if (!File.Exists(path)) return;

        if (!await _readLock.WaitAsync(200, ct)) return;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _lastPosition) _lastPosition = 0; // log rotation
            fs.Seek(_lastPosition, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var m = LineRegex.Match(line);
                if (m.Success)
                {
                    DateTimeOffset.TryParse(m.Groups[1].Value, out var ts);
                    var logLine = new LogLine(ts, m.Groups[2].Value, m.Groups[3].Value, null,
                        Interlocked.Increment(ref _lineCounter));
                    NewLine?.Invoke(logLine);
                }
            }
            _lastPosition = fs.Position;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error tailing log file");
        }
        finally
        {
            _readLock.Release();
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _cts?.Dispose();
        _readLock.Dispose();
    }
}
