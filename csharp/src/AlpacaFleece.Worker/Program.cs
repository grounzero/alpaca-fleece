// Note: Serilog.Formatting.Compact requires additional NuGet package
// For now, using standard JSON formatting
// using Serilog.Formatting.Compact;

var hostBuilder = Host.CreateDefaultBuilder(args)
    .UseSerilog((context, loggerConfig) =>
    {
        loggerConfig
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                "logs/alpaca-fleece.log",
                rollingInterval: RollingInterval.Day,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 30);
    })
    .ConfigureServices((context, services) =>
    {
        // Configuration
        var tradingOptions = new TradingOptions();
        context.Configuration.GetSection("Trading").Bind(tradingOptions);
        services.Configure<TradingOptions>(context.Configuration.GetSection("Trading"));

        var brokerOptions = new BrokerOptions();
        context.Configuration.GetSection("Broker").Bind(brokerOptions);

        var exitOptions = new ExitOptions();
        context.Configuration.GetSection("Exit").Bind(exitOptions);
        services.Configure<ExitOptions>(context.Configuration.GetSection("Exit"));

        var runtimeReconciliationOptions = new RuntimeReconciliationOptions();
        context.Configuration.GetSection("RuntimeReconciliation").Bind(runtimeReconciliationOptions);
        services.Configure<RuntimeReconciliationOptions>(context.Configuration.GetSection("RuntimeReconciliation"));

        // Database path
        var databasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trading.db");

        // Infrastructure (Phase 1)
        services.AddBrokerServices(brokerOptions);
        services.AddStateRepository(databasePath);
        services.AddEventBus();

        // Phase 2: Market Data Client
        services.AddMarketDataServices();

        // Trading (Phase 3: Risk Management + Order Submission)
        services.AddSingleton(tradingOptions);
        services.AddScoped<IStrategy, SmaCrossoverStrategy>();
        services.AddScoped<IRiskManager, RiskManager>();
        services.AddScoped<IOrderManager, OrderManager>();
        services.AddScoped<PositionTracker>();

        // Phase 2: Data Handling
        services.AddSingleton<IDataHandler, DataHandler>();

        // Phase 4: Exit Manager
        services.AddExitManager(Options.Create(exitOptions));

        // Phase 5: Reconciliation Service
        services.AddScoped<IReconciliationService, ReconciliationService>();

        // Worker services
        services.AddHostedService<OrchestratorService>();
        services.AddHostedService<EventDispatcherService>();
        services.AddHostedService<SchemaManagerService>();

        // Phase 2: Stream Polling Service
        services.AddHostedService<StreamPollerService>();

        // Phase 2: Bars Handler
        services.AddHostedService<BarsHandler>();

        // Phase 4: Exit Manager Service
        services.AddHostedService<ExitManagerService>();

        // Phase 4: Runtime Reconciliation Service
        services.AddHostedService<RuntimeReconcilerService>();

        // Phase 6: Housekeeping Service
        services.AddHostedService<HousekeepingService>();

        // Notifications
        services.AddSingleton<AlertNotifier>();
        services.Configure<NotificationOptions>(context.Configuration.GetSection("Notifications"));

        // Metrics
        services.AddSingleton<BotMetrics>();
    })
    .Build();

await hostBuilder.RunAsync();
