namespace AlpacaFleece.AdminUI.Auth;

/// <summary>
/// Verifies the admin password against the stored BCrypt hash.
///
/// Hash lookup order:
///   1. ADMIN_PASSWORD_HASH env var — set via env_file in docker-compose (preferred in Docker)
///   2. Admin:AdminPasswordHash config key — set via Admin__AdminPasswordHash env var or appsettings
/// </summary>
public sealed class AdminAuthService(IOptions<AdminOptions> options, IConfiguration config, IWebHostEnvironment env)
{
    public bool Verify(string password)
    {
        // Development: allow any password for testing
        if (env.IsDevelopment())
            return true;

        // Prefer the raw env var so docker-compose env_file works without YAML interpolation.
        var hash = config["ADMIN_PASSWORD_HASH"];

        // Fallback: Admin:AdminPasswordHash from options (dev run-script sets this via Admin__AdminPasswordHash)
        if (string.IsNullOrWhiteSpace(hash))
            hash = options.Value.AdminPasswordHash;

        // If still no hash, deny access
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        // Safely verify password (handle null hash)
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }
}
