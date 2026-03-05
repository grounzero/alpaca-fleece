using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Reads and parses structured Serilog log lines from the bot's rolling log file.
/// Gracefully returns an empty list if the file does not exist.
/// </summary>
public sealed partial class LogService(
    IOptions<AdminOptions> options,
    ILogger<LogService> logger)
{
    private static readonly Regex LineRegex = LogLineRegex();

    [GeneratedRegex(
        @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(DBG|INF|WRN|ERR|FTL)\] (.+)$",
        RegexOptions.Compiled)]
    private static partial Regex LogLineRegex();

    /// <summary>
    /// Resolves the actual log file path, handling Serilog's rolling-file date suffix.
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

    public async ValueTask<IReadOnlyList<LogLine>> GetRecentLinesAsync(
        int count = 200, CancellationToken ct = default)
    {
        var path = ResolveLogPath();
        if (!File.Exists(path)) return [];

        try
        {
            var lines = await ReadLastLinesAsync(path, count * 3, ct); // over-read for multi-line entries
            return ParseLines(lines).TakeLast(count).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read log file {Path}", path);
            return [];
        }
    }

    public async ValueTask<(IReadOnlyList<LogLine> Lines, int Total)> GetPageAsync(
        int page = 1, int pageSize = 100, string? level = null, string? search = null,
        CancellationToken ct = default)
    {
        var path = ResolveLogPath();
        if (!File.Exists(path)) return ([], 0);

        try
        {
            // Read a bounded amount of lines (estimate: pageSize * requested page + buffer for filtering)
            var estimatedReadSize = Math.Max(pageSize * 10, 10000);
            var rawLines = await ReadLastLinesAsync(path, estimatedReadSize, ct);
            var all = ParseLines(rawLines);

            if (!string.IsNullOrWhiteSpace(level) && level != "All")
                all = all.Where(l => l.Level.Equals(level, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrWhiteSpace(search))
                all = all.Where(l => l.Message.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

            var total = all.Count;
            var rows = all
                .OrderByDescending(l => l.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return (rows, total);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to page log file {Path}", path);
            return ([], 0);
        }
    }

    private static List<LogLine> ParseLines(IEnumerable<string> rawLines)
    {
        var result = new List<LogLine>();
        var lineNum = 0;
        string? pendingException = null;
        LogLine? pending = null;

        foreach (var raw in rawLines)
        {
            lineNum++;
            var m = LineRegex.Match(raw);
            if (m.Success)
            {
                if (pending != null)
                    result.Add(pending with { Exception = pendingException });

                pendingException = null;
                DateTimeOffset.TryParse(m.Groups[1].Value, out var ts);
                pending = new LogLine(ts, m.Groups[2].Value, m.Groups[3].Value, null, lineNum);
            }
            else if (pending != null)
            {
                pendingException = (pendingException == null) ? raw : pendingException + "\n" + raw;
            }
        }

        if (pending != null)
            result.Add(pending with { Exception = pendingException });

        return result;
    }

    private static async Task<string[]> ReadLastLinesAsync(string path, int count, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var all = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
            all.Add(line);

        return all.Count <= count ? [.. all] : [.. all.TakeLast(count)];
    }
}
