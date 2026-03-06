// Note: Serilog.Formatting.Compact requires additional NuGet package
// For now, using standard JSON formatting
// using Serilog.Formatting.Compact;

var hostBuilder = Host.CreateDefaultBuilder(args)
    // Configure scope validation based on environment:
    // enable in Development to catch DI lifetime issues early.
    .UseDefaultServiceProvider((context, options) =>
    {
        options.ValidateScopes = context.HostingEnvironment.IsDevelopment();
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        // Override config from the shared Docker volume (written by the Admin UI).
        // Optional so the bot starts normally when running without Docker.
        config.AddJsonFile("/app/config/appsettings.json", optional: true, reloadOnChange: false);

        // Support both old and new environment variable formats:
        // - Legacy: ALPACA_API_KEY, ALPACA_SECRET_KEY (maps to Broker:ApiKey, Broker:SecretKey)
        // - Current: Broker__ApiKey, Broker__SecretKey (standard double-underscore format)
        var alpacaApiKey = Environment.GetEnvironmentVariable("ALPACA_API_KEY");
        var alpacaSecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY");

        if (!string.IsNullOrEmpty(alpacaApiKey) || !string.IsNullOrEmpty(alpacaSecretKey))
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Broker:ApiKey", alpacaApiKey },
                { "Broker:SecretKey", alpacaSecretKey }
            }!);
        }

        // Standard env vars — double underscore is the section separator.
        // Broker__ApiKey, Broker__SecretKey (set by docker-compose from .env)
        config.AddEnvironmentVariables();
    })
    .UseSerilog((context, loggerConfig) =>
    {
        var logLevel = context.Configuration.GetValue("Serilog:MinimumLevel:Default", "Information");
        var level = Enum.TryParse<LogEventLevel>(logLevel, out var parsedLevel) 
            ? parsedLevel 
            : LogEventLevel.Information;
            
        loggerConfig
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
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

        // Database path - use /app/data for persistence across restarts
        var databasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "trading.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        // Infrastructure (Phase 1)
        services.AddBrokerServices(brokerOptions);
        services.AddStateRepository(databasePath);
        services.AddEventBus();

        // Phase 2: Market Data Client
        services.AddMarketDataServices(brokerOptions, tradingOptions.Exit.MaxPriceAgeSeconds);

        // Trading (Phase 3: Risk Management + Order Submission)
        services.AddSingleton(tradingOptions);

        // Symbol classifier (centralised symbol type detection using TradingOptions lists)
        services.AddSingleton<ISymbolClassifier>(sp =>
            new SymbolClassifier(tradingOptions.Symbols.CryptoSymbols, tradingOptions.Symbols.EquitySymbols));

        // Signal quality filters (must be registered before SmaCrossoverStrategy)
        services.AddSingleton(sp => new TrendFilter(
            sp.GetRequiredService<IMarketDataClient>(),
            tradingOptions,
            sp.GetRequiredService<ILogger<TrendFilter>>()));
        services.AddSingleton(sp => new VolumeFilter(
            tradingOptions,
            sp.GetRequiredService<ILogger<VolumeFilter>>()));

        services.AddSingleton<IStrategy>(sp => new SmaCrossoverStrategy(
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ILogger<SmaCrossoverStrategy>>(),
            trendFilter: sp.GetRequiredService<TrendFilter>(),
            volumeFilter: sp.GetRequiredService<VolumeFilter>(),
            executionOptions: tradingOptions.Execution));
        services.AddSingleton<PositionTracker>();
        services.AddSingleton<IPositionTracker>(sp => sp.GetRequiredService<PositionTracker>());

        // Phase 2: Data Handling
        services.AddSingleton<IDataHandler, DataHandler>();
        services.AddSingleton<BarsHandler>();
        services.AddHostedService(sp => sp.GetRequiredService<BarsHandler>());

        // Phase 4: Exit Manager (needs full TradingOptions for crypto symbol detection)
        services.AddExitManager(Options.Create(tradingOptions));

        // Correlation service (singleton — all checks are in-memory)
        services.AddSingleton(sp => new CorrelationService(
            tradingOptions,
            sp.GetRequiredService<PositionTracker>(),
            sp.GetRequiredService<ILogger<CorrelationService>>()));

        // Drawdown monitor (singleton — maintains in-memory level cache)
        services.AddSingleton(sp => new DrawdownMonitor(
            sp.GetRequiredService<IBrokerService>(),
            sp.GetRequiredService<IStateRepository>(),
            tradingOptions,
            sp.GetRequiredService<ILogger<DrawdownMonitor>>()));

        // RiskManager and OrderManager with explicit DrawdownMonitor and CorrelationService injection
        services.AddScoped<IRiskManager>(sp => new RiskManager(
            sp.GetRequiredService<IBrokerService>(),
            sp.GetRequiredService<IStateRepository>(),
            tradingOptions,
            sp.GetRequiredService<ILogger<RiskManager>>(),
            drawdownMonitor: sp.GetRequiredService<DrawdownMonitor>(),
            correlationService: sp.GetRequiredService<CorrelationService>(),
            positionTracker: sp.GetRequiredService<IPositionTracker>(),
            symbolClassifier: sp.GetRequiredService<ISymbolClassifier>()));
        services.AddScoped<IOrderManager>(sp => new OrderManager(
            sp.GetRequiredService<IBrokerService>(),
            sp.GetRequiredService<IRiskManager>(),
            sp.GetRequiredService<IStateRepository>(),
            sp.GetRequiredService<IEventBus>(),
            tradingOptions,
            sp.GetRequiredService<ILogger<OrderManager>>(),
            drawdownMonitor: sp.GetRequiredService<DrawdownMonitor>()));

        // Phase 5: Reconciliation Service
        services.AddScoped<IReconciliationService, ReconciliationService>();

        // Worker services
        services.AddHostedService(sp => new OrchestratorService(
            sp.GetRequiredService<ILogger<OrchestratorService>>(),
            sp,
            sp.GetRequiredService<IStateRepository>()));
        services.AddHostedService<EventDispatcherService>();
        services.AddHostedService<SchemaManagerService>();

        // Phase 2: Stream Polling Service
        services.AddHostedService<StreamPollerService>();

        // Phase 4: Exit Manager Service
        services.AddHostedService<ExitManagerService>();

        // Phase 4: Runtime Reconciliation Service
        services.AddHostedService<RuntimeReconcilerService>();

        // O-3: Register custom HealthCheckService singleton
        services.AddSingleton<HealthCheckService>();

        // Phase 6: Housekeeping Service (graceful shutdown)
        services.AddHostedService(sp => new HousekeepingService(
            sp.GetRequiredService<IBrokerService>(),
            sp.GetRequiredService<IStateRepository>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<HousekeepingService>>()));

        // Drawdown monitor service
        services.AddHostedService<DrawdownMonitorService>();

        // Notifications
        services.AddSingleton<AlertNotifier>();
        services.Configure<NotificationOptions>(context.Configuration.GetSection("Notifications"));

        // Hangfire background jobs (equity snapshots, daily resets, circuit breaker resets)
        services.AddHangfireServices();

      // Metrics
      services.AddSingleton<BotMetrics>();
    })
    .Build();

// Configure recurring Hangfire jobs
var recurringJobManager = hostBuilder.Services.GetRequiredService<IRecurringJobManager>();
HangfireBackgroundJobs.ConfigureRecurringJobs(recurringJobManager);

await hostBuilder.RunAsync();
