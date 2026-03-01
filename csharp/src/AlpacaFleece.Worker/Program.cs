// Note: Serilog.Formatting.Compact requires additional NuGet package
// For now, using standard JSON formatting
// using Serilog.Formatting.Compact;

using AlpacaFleece.Infrastructure.Symbols;

var hostBuilder = Host.CreateDefaultBuilder(args)
    // Configure scope validation based on environment:
    // enable in Development to catch DI lifetime issues early.
    .UseDefaultServiceProvider((context, options) =>
    {
        options.ValidateScopes = (context.HostingEnvironment.EnvironmentName ?? string.Empty) == "Development";
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        // Add environment variables with ALPACA_ prefix mapped to config sections
        // ALPACA_API_KEY -> Broker:ApiKey
        // ALPACA_SECRET_KEY -> Broker:SecretKey
        config.AddEnvironmentVariables(prefix: "ALPACA_");
        
        // Also add standard env vars for Docker compatibility (double underscore = section separator)
        // Broker__ApiKey, Broker__SecretKey
        config.AddEnvironmentVariables();

        // Load JSON configuration files, including overrides 
        // Ensure JSON files are resolved relative to the host content root (project folder).
        config.SetBasePath(context.HostingEnvironment.ContentRootPath);

        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        var environmentName = context.HostingEnvironment.EnvironmentName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            config.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true);
        }
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

        // Fallback: if binding failed (e.g. file not found in the host config pipeline),
        // try to load the environment-specific appsettings file directly from the
        // content root and re-bind the Broker section. This helps when running
        // `dotnet run` from different working directories during development.
        if (string.IsNullOrWhiteSpace(brokerOptions.ApiKey))
        {
            try
            {
                var env = context.HostingEnvironment.EnvironmentName ?? string.Empty;
                var envFile = Path.Combine(context.HostingEnvironment.ContentRootPath, $"appsettings.{env}.json");
                var baseFile = Path.Combine(context.HostingEnvironment.ContentRootPath, "appsettings.json");
                var cb = new ConfigurationBuilder();
                if (File.Exists(baseFile)) cb.AddJsonFile(baseFile, optional: true, reloadOnChange: true);
                if (!string.IsNullOrWhiteSpace(env) && File.Exists(envFile)) cb.AddJsonFile(envFile, optional: true, reloadOnChange: true);
                var directCfg = cb.Build();
                directCfg.GetSection("Broker").Bind(brokerOptions);
            }
            catch (Exception)
            {
                // ignore and let validation throw the original error below
            }
        }

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
        services.AddMarketDataServices(brokerOptions);

        // Trading (Phase 3: Risk Management + Order Submission)
        services.AddSingleton(tradingOptions);

        // Symbol classifier (centralised symbol type detection using TradingOptions lists)
        services.AddSingleton<ISymbolClassifier>(sp =>
            new SymbolClassifier(tradingOptions.Symbols.CryptoSymbols, tradingOptions.Symbols.EquitySymbols));
        services.AddScoped<IStrategy>(sp => new SmaCrossoverStrategy(
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ILogger<SmaCrossoverStrategy>>(),
            tradingOptions.Symbols.CryptoSymbols));
        services.AddSingleton<PositionTracker>();

        // Phase 2: Data Handling
        services.AddSingleton<IDataHandler, DataHandler>();

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

        // Drawdown monitor service
        services.AddHostedService<DrawdownMonitorService>();

        // Notifications
        services.AddSingleton<AlertNotifier>();
        services.Configure<NotificationOptions>(context.Configuration.GetSection("Notifications"));

        // Metrics
        services.AddSingleton<BotMetrics>();
    })
    .Build();

await hostBuilder.RunAsync();
