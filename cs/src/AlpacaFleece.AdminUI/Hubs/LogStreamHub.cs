using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace AlpacaFleece.AdminUI.Hubs;

/// <summary>
/// SignalR hub that streams new log lines to connected clients.
/// Kept for potential external / non-Blazor clients.
/// Blazor Server components subscribe to LogStreamService.NewLine directly instead.
/// </summary>
[Authorize]
public sealed class LogStreamHub(LogStreamService logStreamService) : Hub
{
    /// <summary>
    /// Called by the client to begin receiving live log lines.
    /// Creates a per-connection channel and subscribes to the service event.
    /// </summary>
    public async Task StartStreaming(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        void Handler(LogLine line) => channel.Writer.TryWrite(line);

        logStreamService.NewLine += Handler;
        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(ct))
            {
                await Clients.Caller.SendAsync("ReceiveLog", line, ct);
            }
        }
        finally
        {
            logStreamService.NewLine -= Handler;
            channel.Writer.TryComplete();
        }
    }
}
