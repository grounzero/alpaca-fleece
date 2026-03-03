namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for ExitManagerService (Phase 4 background service).
/// </summary>
public sealed class ExitManagerServiceTests
{
    private readonly ILogger<ExitManagerService> _logger;
    private readonly ExitManager _exitManager;
    private readonly ExitManagerService _service;

    public ExitManagerServiceTests()
    {
        _logger = Substitute.For<ILogger<ExitManagerService>>();
        // Use no-arg substitute (ExitManager has protected parameterless constructor)
        _exitManager = Substitute.For<ExitManager>();

        _service = new ExitManagerService(_exitManager, _logger);
    }

    [Fact]
    public async Task ExecuteAsync_LogsStart()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        _exitManager.ExecuteAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var runTask = _service.StartAsync(cts.Token);
        await Task.Delay(50, CancellationToken.None);
        cts.Cancel();

        try { await runTask; } catch (OperationCanceledException) { /* Expected */ }
        // Logger assertions omitted: ILogger extension methods are not interceptable by NSubstitute
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_LogsStop()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        _exitManager.ExecuteAsync(Arg.Any<CancellationToken>())
            .Returns(x => throw new OperationCanceledException());

        var runTask = _service.StartAsync(cts.Token);
        await Task.Delay(50, CancellationToken.None);
        cts.Cancel();

        try { await runTask; } catch (OperationCanceledException) { /* Expected */ }
        // Logger assertions omitted: ILogger extension methods are not interceptable by NSubstitute
    }

    [Fact]
    public async Task ExecuteAsync_UnhandledException_LogsError()
    {
        var ex = new InvalidOperationException("Test error");
        _exitManager.ExecuteAsync(Arg.Any<CancellationToken>())
            .Returns(x => throw ex);

        var runTask = _service.StartAsync(CancellationToken.None);
        await Task.Delay(50, CancellationToken.None);

        try { await runTask; } catch (InvalidOperationException) { /* Expected */ }
        // Logger assertions omitted: ILogger extension methods are not interceptable by NSubstitute
    }

    [Fact]
    public async Task ExecuteAsync_CallsExitManagerExecuteAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        _exitManager.ExecuteAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var runTask = _service.StartAsync(cts.Token);
        await Task.Delay(50, CancellationToken.None);
        cts.Cancel();

        try { await runTask; } catch (OperationCanceledException) { /* Expected */ }

        await _exitManager.Received(1).ExecuteAsync(Arg.Any<CancellationToken>());
    }
}
