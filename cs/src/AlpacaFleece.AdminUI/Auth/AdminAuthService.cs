namespace AlpacaFleece.AdminUI.Auth;

/// <summary>
/// Verifies the admin password against the stored BCrypt hash.
/// Password hash is supplied via ADMIN_PASSWORD_HASH environment variable.
/// </summary>
public sealed class AdminAuthService(IOptions<AdminOptions> options)
{
    public bool Verify(string password)
    {
        var hash = options.Value.AdminPasswordHash;
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        return BCrypt.Net.BCrypt.Verify(password, hash);
    }
}
