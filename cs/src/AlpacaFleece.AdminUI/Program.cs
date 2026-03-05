using MudBlazor.Services;
using Serilog;
using Blazored.LocalStorage;
using AlpacaFleece.AdminUI.Hubs;
using AlpacaFleece.AdminUI.Services;
using AlpacaFleece.AdminUI.Services.DataGrid;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog
    builder.Host.UseSerilog((ctx, services, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .WriteTo.Console());

    // Admin options
    builder.Services.Configure<AdminOptions>(
        builder.Configuration.GetSection(AdminOptions.SectionName));

    var adminOptions = builder.Configuration
        .GetSection(AdminOptions.SectionName)
        .Get<AdminOptions>() ?? new AdminOptions();

    // Read-only EF Core DbContext factory — Admin never writes to the bot's database.
    // Use Cache=Shared for concurrent access with the bot writer (WAL mode).
    var roConnString = $"Data Source={adminOptions.DatabasePath};Cache=Shared";
    builder.Services.AddDbContextFactory<TradingDbContext>(options =>
        options.UseSqlite(roConnString)
               .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

    // Cookie authentication — single admin password, 8-hour session
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.ExpireTimeSpan = TimeSpan.FromHours(adminOptions.SessionHours);
            options.SlidingExpiration = false;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Name = "AlpacaAdmin";
        });

    builder.Services.AddAuthorization();
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddHttpContextAccessor();

    // Razor Pages — handles Login and Logout (require real HTTP context)
    builder.Services.AddRazorPages();

    // Blazor Server with SignalR
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    // SignalR (for LogStreamHub)
    builder.Services.AddSignalR();

    // MudBlazor services
    builder.Services.AddMudServices(config =>
    {
        config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
        config.SnackbarConfiguration.ShowCloseIcon = true;
        config.SnackbarConfiguration.VisibleStateDuration = 4000;
    });

    // Blazored LocalStorage for DataGrid state persistence
    builder.Services.AddBlazoredLocalStorage();

    // DataGrid services (for admin database browser)
    builder.Services.AddScoped<IGridStateStore, LocalStorageGridStateStore>();
    builder.Services.AddScoped<AdminGridDataService>();

    // HTTP client (for AlpacaAssetService)
    builder.Services.AddHttpClient();

    // Admin services
    builder.Services.AddSingleton<AdminAuthService>();
    builder.Services.AddSingleton<AdminDbService>();
    builder.Services.AddSingleton<ServiceManagerService>();
    builder.Services.AddSingleton<ConfigService>();
    builder.Services.AddSingleton<AlpacaAssetService>();
    builder.Services.AddSingleton<LogStreamService>();
    builder.Services.AddSingleton<LogService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LogStreamService>());

    // Scoped services (per Blazor circuit)
    builder.Services.AddScoped<ClipboardService>();
    builder.Services.AddScoped<LocalStorageService>();
    builder.Services.AddScoped<HighlightInterop>();

    var app = builder.Build();

    app.UseSerilogRequestLogging(opts => opts.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms");

    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapRazorPages();
    app.MapRazorComponents<AlpacaFleece.AdminUI.Components.App>()
        .AddInteractiveServerRenderMode();

    // SignalR hub endpoint
    app.MapHub<LogStreamHub>("/hubs/logs");

    await app.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "AdminUI terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
