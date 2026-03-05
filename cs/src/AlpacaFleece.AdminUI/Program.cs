using MudBlazor.Services;
using Serilog;
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

    // Enable detailed circuit errors for debugging (development only)
    if (builder.Environment.IsDevelopment())
    {
        builder.WebHost.UseSetting("circuitOptions:DetailedErrors", "true");
    }

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
    builder.Services.AddScoped<EfGridDataService>();
    builder.Services.AddScoped<IGridStateStore, LocalStorageGridStateStore>();

    var app = builder.Build();

    app.UseSerilogRequestLogging(opts => opts.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms");

    // Serve static files from wwwroot
    app.UseStaticFiles();

    // Fallback handler to serve component library assets (MudBlazor, etc.)
    // This workaround handles the _content route by mapping to NuGet staticwebassets
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/_content"))
        {
            var requestPath = context.Request.Path.Value?.Substring("/_content/".Length) ?? "";
            var nugetPackagesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget/packages");

            // For MudBlazor: _content/MudBlazor/MudBlazor.min.css
            // Maps to: mudblazor/{version}/staticwebassets/MudBlazor.min.css
            if (requestPath.StartsWith("MudBlazor/", StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = requestPath.Substring("MudBlazor/".Length);
                var mudblazorDir = Path.Combine(nugetPackagesPath, "mudblazor");

                if (Directory.Exists(mudblazorDir))
                {
                    // Find the latest version directory
                    var versionDirs = Directory.GetDirectories(mudblazorDir)
                        .OrderByDescending(d =>
                        {
                            if (Version.TryParse(Path.GetFileName(d), out var v)) return v;
                            return new Version(0, 0);
                        })
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(versionDirs))
                    {
                        var filePath = Path.Combine(versionDirs, "staticwebassets", relativePath);
                        if (File.Exists(filePath))
                        {
                            context.Response.ContentType = GetContentType(filePath);
                            await context.Response.SendFileAsync(filePath);
                            return;
                        }
                    }
                }
            }
        }

        await next();
    });

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

// Helper function to determine content type
static string GetContentType(string filePath)
{
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    return ext switch
    {
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".eot" => "application/vnd.ms-fontobject",
        _ => "application/octet-stream"
    };
}
