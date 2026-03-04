using MudBlazor.Services;
using Serilog;

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
    // SQLite WAL mode allows concurrent readers alongside the bot writer.
    var roConnString = $"Data Source={adminOptions.DatabasePath};Mode=ReadOnly;Cache=Shared";
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

    // MudBlazor services
    builder.Services.AddMudServices(config =>
    {
        config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
        config.SnackbarConfiguration.ShowCloseIcon = true;
        config.SnackbarConfiguration.VisibleStateDuration = 4000;
    });

    // Admin services
    builder.Services.AddSingleton<AdminAuthService>();

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
