using AlpacaFleece.AdminUI.Config;
using AlpacaFleece.AdminUI.Models;
using AlpacaFleece.AdminUI.Services;

namespace AlpacaFleece.Tests;

public sealed class ConfigServiceTests
{
    private static ConfigService CreateService()
    {
        var options = Options.Create(new AdminOptions
        {
            BotSettingsPath = "/tmp/alpaca-fleece-config-service-tests.json"
        });
        return new ConfigService(options, Substitute.For<ILogger<ConfigService>>());
    }

    [Fact]
    public void ValidateDraft_GlobalVolatilityDisabled_DoesNotValidateGlobalProfile()
    {
        var service = CreateService();
        var draft = new ConfigDraft
        {
            VolatilityRegimeEnabled = false,
            VolatilityLowMaxVolatility = 0.010m,
            VolatilityNormalMaxVolatility = 0.005m, // invalid ordering if validated
            VolatilityHighMaxVolatility = 0.020m
        };

        var errors = service.ValidateDraft(draft);

        Assert.DoesNotContain(errors, e => e.Contains("volatility thresholds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateDraft_GlobalVolatilityEnabled_ValidatesGlobalProfile()
    {
        var service = CreateService();
        var draft = new ConfigDraft
        {
            VolatilityRegimeEnabled = true,
            VolatilityLowMaxVolatility = 0.010m,
            VolatilityNormalMaxVolatility = 0.005m, // invalid ordering
            VolatilityHighMaxVolatility = 0.020m
        };

        var errors = service.ValidateDraft(draft);

        Assert.Contains(errors, e => e.Contains("volatility thresholds", StringComparison.OrdinalIgnoreCase));
    }
}
