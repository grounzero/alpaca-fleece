using AlpacaFleece.AdminUI.Auth;
using AlpacaFleece.AdminUI.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AlpacaFleece.Tests;

/// <summary>
/// Unit tests for AdminAuthService password verification logic.
/// Tests env var precedence, missing hashes, invalid hashes, and dev mode bypass.
/// </summary>
public sealed class AdminAuthServiceTests
{
    private static readonly string ValidBcryptHash = "$2a$12$VocEPvIu.6NUsnjLxPvnUe8iY9z6gVKKYrvwYJImH6AQ.jH4opYh.";
    private const string ValidPassword = "test-password";
    private const string InvalidPassword = "wrong-password";

    [Fact]
    public void Verify_DevelopmentMode_AllowsAnyPassword()
    {
        // Arrange
        var options = Options.Create(new AdminOptions());
        var config = new ConfigurationBuilder().Build();
        var env = CreateMockEnvironment(isDevelopment: true);

        var service = new AdminAuthService(options, config, env);

        // Act & Assert
        Assert.True(service.Verify("any-password"));
        Assert.True(service.Verify(""));
        Assert.True(service.Verify("very-long-wrong-password-123456789"));
    }

    [Fact]
    public void Verify_ValidPassword_WithEnvVarHash_ReturnsTrue()
    {
        // Arrange
        var options = Options.Create(new AdminOptions());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "ADMIN_PASSWORD_HASH", ValidBcryptHash } })
            .Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(ValidPassword);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Verify_InvalidPassword_WithEnvVarHash_ReturnsFalse()
    {
        // Arrange
        var options = Options.Create(new AdminOptions());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "ADMIN_PASSWORD_HASH", ValidBcryptHash } })
            .Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(InvalidPassword);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Verify_EnvVarPrecedence_PreferredOverOptions()
    {
        // Arrange — both env var and options have hashes, env var should win
        var optionsHash = "$2a$11$invalid-hash-from-options-should-not-be-used";
        var options = Options.Create(new AdminOptions { AdminPasswordHash = optionsHash });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "ADMIN_PASSWORD_HASH", ValidBcryptHash } })
            .Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(ValidPassword);

        // Assert — valid password with correct env var hash should work
        Assert.True(result);
    }

    [Fact]
    public void Verify_FallbackToOptions_WhenEnvVarMissing()
    {
        // Arrange — only options hash, no env var
        var options = Options.Create(new AdminOptions { AdminPasswordHash = ValidBcryptHash });
        var config = new ConfigurationBuilder().Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(ValidPassword);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Verify_MissingHash_DeniesAccess()
    {
        // Arrange — no env var, no options hash
        var options = Options.Create(new AdminOptions { AdminPasswordHash = "" });
        var config = new ConfigurationBuilder().Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(ValidPassword);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Verify_WhitespaceHash_TreatedAsMissing()
    {
        // Arrange — hash is whitespace (treated as empty)
        var options = Options.Create(new AdminOptions { AdminPasswordHash = "   " });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "ADMIN_PASSWORD_HASH", "\t\n" } })
            .Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(ValidPassword);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Verify_InvalidBcryptHash_DeniesAccess()
    {
        // Arrange — hash is not a valid BCrypt hash
        var options = Options.Create(new AdminOptions());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "ADMIN_PASSWORD_HASH", "not-a-valid-bcrypt-hash" } })
            .Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(ValidPassword);

        // Assert — BCrypt.Verify throws on invalid hash, caught and returns false
        Assert.False(result);
    }

    [Fact]
    public void Verify_MalformedHash_DoesNotThrow()
    {
        // Arrange — hash is clearly malformed (BCrypt hashes are always 60 chars)
        var options = Options.Create(new AdminOptions { AdminPasswordHash = "$2a$invalid" });
        var config = new ConfigurationBuilder().Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act & Assert — should not throw, just return false
        var result = service.Verify(ValidPassword);
        Assert.False(result);
    }

    [Fact]
    public void Verify_EmptyPassword_WithValidHash_ReturnsFalse()
    {
        // Arrange
        var options = Options.Create(new AdminOptions { AdminPasswordHash = ValidBcryptHash });
        var config = new ConfigurationBuilder().Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify("");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Verify_NullPassword_DeniesAccess()
    {
        // Arrange
        var options = Options.Create(new AdminOptions { AdminPasswordHash = ValidBcryptHash });
        var config = new ConfigurationBuilder().Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act & Assert — null password should fail verification
        Assert.False(service.Verify(null!));
    }

    [Fact]
    public void Verify_CaseSensitive_CorrectCase_ReturnsTrue()
    {
        // Arrange — BCrypt is case-sensitive
        var options = Options.Create(new AdminOptions { AdminPasswordHash = ValidBcryptHash });
        var config = new ConfigurationBuilder().Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(ValidPassword);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Verify_CaseSensitive_WrongCase_ReturnsFalse()
    {
        // Arrange
        var options = Options.Create(new AdminOptions { AdminPasswordHash = ValidBcryptHash });
        var config = new ConfigurationBuilder().Build();
        var env = CreateMockEnvironment(isDevelopment: false);

        var service = new AdminAuthService(options, config, env);

        // Act
        var result = service.Verify(ValidPassword.ToUpper());

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Creates a mock IWebHostEnvironment for testing.
    /// </summary>
    private static IWebHostEnvironment CreateMockEnvironment(bool isDevelopment)
    {
        var mock = Substitute.For<IWebHostEnvironment>();
        // IsDevelopment() is an extension method that checks EnvironmentName == "Development"
        mock.EnvironmentName.Returns(isDevelopment ? "Development" : "Production");
        return mock;
    }
}
