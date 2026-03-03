namespace AlpacaFleece.Core.Models;

/// <summary>
/// Market clock information (open/close times, session status).
/// </summary>
public sealed record ClockInfo(
    bool IsOpen,
    DateTimeOffset NextOpen,
    DateTimeOffset NextClose,
    DateTimeOffset FetchedAt);
