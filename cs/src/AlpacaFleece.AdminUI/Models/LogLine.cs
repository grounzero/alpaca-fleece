namespace AlpacaFleece.AdminUI.Models;

public sealed record LogLine(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception,
    int LineNumber);
