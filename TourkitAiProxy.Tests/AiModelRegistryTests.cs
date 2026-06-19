using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using Xunit;

namespace TourkitAiProxy.Tests;

public class AiModelRegistryTests
{
    private static AiModelRegistry MakeRegistry(Dictionary<string, string?> config)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var providers = new List<IAiProvider> { new FakeProvider("anthropic"), new FakeProvider("deepseek") };
        var pReg = new ProviderRegistry(providers, cfg);
        return new AiModelRegistry(cfg, pReg);
    }

    [Fact]
    public void Throws_at_construct_when_Primary_Provider_missing()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var pReg = new ProviderRegistry(new[] { new FakeProvider("anthropic") }, cfg);
        var ex = Assert.Throws<InvalidOperationException>(() => new AiModelRegistry(cfg, pReg));
        Assert.Contains("Models:Primary:Provider", ex.Message);
    }

    [Fact]
    public void Feature_with_null_section_inherits_Primary()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"] = "anthropic",
            ["Models:Primary:Model"]    = "claude-haiku-4-5",
            ["Models:Primary:ApiKey"]   = "sk-primary",
        });

        var rm = r.Resolve(AiFeature.Wizard);
        Assert.Equal("anthropic", rm.Provider);
        Assert.Equal("claude-haiku-4-5", rm.Model);
        Assert.Equal("sk-primary", rm.ApiKey);
    }

    [Fact]
    public void Feature_with_explicit_section_overrides_Primary()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"]        = "anthropic",
            ["Models:Primary:Model"]           = "claude-haiku-4-5",
            ["Models:Primary:ApiKey"]          = "sk-primary",
            ["Models:CustomerReview:Provider"] = "deepseek",
            ["Models:CustomerReview:Model"]    = "deepseek-chat",
            ["Models:CustomerReview:ApiKey"]   = "sk-deepseek-review",
        });

        var rm = r.Resolve(AiFeature.CustomerReview);
        Assert.Equal("deepseek", rm.Provider);
        Assert.Equal("deepseek-chat", rm.Model);
        Assert.Equal("sk-deepseek-review", rm.ApiKey);
    }

    [Fact]
    public void Partial_section_inherits_missing_fields_from_Primary()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"]     = "anthropic",
            ["Models:Primary:Model"]        = "claude-haiku-4-5",
            ["Models:Primary:ApiKey"]       = "sk-primary",
            ["Models:VisaScoring:Model"]    = "claude-sonnet-4-5",
        });

        var rm = r.Resolve(AiFeature.VisaScoring);
        Assert.Equal("anthropic", rm.Provider);
        Assert.Equal("claude-sonnet-4-5", rm.Model);
        Assert.Equal("sk-primary", rm.ApiKey);
    }

    [Fact]
    public void ApiKey_falls_back_to_Providers_section_when_feature_uses_different_provider()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"]        = "anthropic",
            ["Models:Primary:Model"]           = "claude-haiku-4-5",
            ["Models:Primary:ApiKey"]          = "sk-primary-anthropic",
            ["Models:MailClassify:Provider"]   = "deepseek",
            ["Models:MailClassify:Model"]      = "deepseek-chat",
            ["Providers:DeepSeek:ApiKey"]      = "sk-shared-deepseek",
        });

        var rm = r.Resolve(AiFeature.MailClassify);
        Assert.Equal("deepseek", rm.Provider);
        Assert.Equal("sk-shared-deepseek", rm.ApiKey);
    }

    [Fact]
    public void Per_request_override_takes_highest_priority()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"] = "anthropic",
            ["Models:Primary:Model"]    = "claude-haiku-4-5",
        });

        var rm = r.Resolve(AiFeature.Wizard, overrideProvider: "deepseek", overrideModel: "deepseek-reasoner");
        Assert.Equal("deepseek", rm.Provider);
        Assert.Equal("deepseek-reasoner", rm.Model);
    }

    [Fact]
    public void Throws_when_resolved_provider_not_registered_in_DI()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"] = "openai",
            ["Models:Primary:Model"]    = "gpt-4o",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => r.Resolve(AiFeature.Wizard));
        Assert.Contains("openai", ex.Message);
        Assert.Contains("chưa đăng ký", ex.Message);
    }

    [Fact]
    public void Model_falls_back_to_provider_recommended_when_Primary_Model_missing()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"] = "anthropic",
        });

        var rm = r.Resolve(AiFeature.Wizard);
        Assert.Equal("anthropic", rm.Provider);
        Assert.Equal("fake-recommended", rm.Model);
    }

    private class FakeProvider : IAiProvider
    {
        public FakeProvider(string id) => Id = id;
        public string Id { get; }
        public string Label => Id;
        public IReadOnlyList<ProviderModel> Models => new[]
        {
            new ProviderModel("fake-default", "Fake Default"),
            new ProviderModel("fake-recommended", "Fake Recommended", Recommended: true),
        };
        public Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
            => throw new NotImplementedException();
    }
}
