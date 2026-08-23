using IPDocketing.Core.Ai;
using Xunit;

namespace IPDocketing.Core.Tests;

public class AiCredentialStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly AiCredentialStore _store;

    public AiCredentialStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ipd_ai_{Guid.NewGuid():N}");
        _store = new AiCredentialStore(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AKeySurvivesARoundTrip()
    {
        _store.SetKey(AiProviderKind.Anthropic, "sk-ant-example-0123456789");
        Assert.Equal("sk-ant-example-0123456789", _store.GetKey(AiProviderKind.Anthropic));
    }

    [Fact]
    public void KeysAreNotReadableInTheFile()
    {
        // The whole point of DPAPI here. If this ever fails, keys are sitting in
        // plain text in the user's AppData and in every backup of it.
        const string secret = "sk-ant-plaintext-canary-value";
        _store.SetKey(AiProviderKind.Anthropic, secret);

        var onDisk = Directory.GetFiles(_dir).Single(f => f.EndsWith(".dat"));
        var bytes = File.ReadAllBytes(onDisk);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(secret, asText);
    }

    [Fact]
    public void ProvidersAreStoredIndependently()
    {
        _store.SetKey(AiProviderKind.Anthropic, "key-a");
        _store.SetKey(AiProviderKind.OpenAi, "key-o");
        _store.SetKey(AiProviderKind.Gemini, "key-g");

        Assert.Equal("key-a", _store.GetKey(AiProviderKind.Anthropic));
        Assert.Equal("key-o", _store.GetKey(AiProviderKind.OpenAi));
        Assert.Equal("key-g", _store.GetKey(AiProviderKind.Gemini));
    }

    [Fact]
    public void ClearingOneKeyLeavesTheOthers()
    {
        _store.SetKey(AiProviderKind.Anthropic, "key-a");
        _store.SetKey(AiProviderKind.OpenAi, "key-o");

        _store.SetKey(AiProviderKind.Anthropic, null);

        Assert.False(_store.HasKey(AiProviderKind.Anthropic));
        Assert.True(_store.HasKey(AiProviderKind.OpenAi));
    }

    [Fact]
    public void WhitespaceIsTrimmedBecausePastedKeysCarryIt()
    {
        _store.SetKey(AiProviderKind.OpenAi, "  key-with-padding\n");
        Assert.Equal("key-with-padding", _store.GetKey(AiProviderKind.OpenAi));
    }

    [Fact]
    public void AMissingStoreReadsAsNoKeysRatherThanThrowing()
    {
        var fresh = new AiCredentialStore(Path.Combine(_dir, "never-written"));

        Assert.Null(fresh.GetKey(AiProviderKind.Gemini));
        Assert.False(fresh.HasKey(AiProviderKind.Gemini));
    }

    [Fact]
    public void MaskingShowsShapeButNotTheKey()
    {
        var masked = AiCredentialStore.Mask("sk-ant-api03-abcdefghijklmnop9fQe");

        Assert.StartsWith("sk-ant-", masked);
        Assert.EndsWith("9fQe", masked);
        Assert.DoesNotContain("abcdefghijklmnop", masked);
    }

    [Fact]
    public void MaskingAShortValueRevealsNothing()
    {
        Assert.Equal("••••••", AiCredentialStore.Mask("abc123"));
        Assert.Equal("not set", AiCredentialStore.Mask(null));
    }

    [Fact]
    public void SettingsRoundTripSeparatelyFromKeys()
    {
        var settings = new AiSettings
        {
            Enabled = true,
            CloudConsentGiven = true,
            TimeoutSeconds = 45,
            ActiveProviders = { AiProviderKind.Anthropic, AiProviderKind.Gemini },
        };
        settings.Models["OpenAi"] = "some-other-model";

        _store.SaveSettings(settings);
        var loaded = _store.LoadSettings();

        Assert.True(loaded.Enabled);
        Assert.True(loaded.CloudConsentGiven);
        Assert.Equal(45, loaded.TimeoutSeconds);
        Assert.Contains(AiProviderKind.Anthropic, loaded.ActiveProviders);
        Assert.DoesNotContain(AiProviderKind.OpenAi, loaded.ActiveProviders);
        Assert.Equal("some-other-model", loaded.ModelFor(AiProviderKind.OpenAi));
    }

    [Fact]
    public void DefaultsAreOffUntilTheUserTurnsThemOn()
    {
        var fresh = _store.LoadSettings();

        // Adding a key must never be enough on its own to start sending client
        // documents to a third party.
        Assert.False(fresh.Enabled);
        Assert.False(fresh.CloudConsentGiven);
        Assert.Empty(fresh.ActiveProviders);
    }

    [Fact]
    public void AnUnsetModelFallsBackToTheProviderDefault()
    {
        var settings = _store.LoadSettings();

        Assert.Equal(
            AiSettings.DefaultModel(AiProviderKind.Anthropic),
            settings.ModelFor(AiProviderKind.Anthropic));
    }
}
