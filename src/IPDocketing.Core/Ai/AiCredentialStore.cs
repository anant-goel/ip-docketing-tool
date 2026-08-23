using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPDocketing.Core.Ai;

/// <summary>
/// Stores API keys encrypted with DPAPI under the current Windows user.
///
/// DPAPI rather than the app's own EncryptionService on purpose: DPAPI ties the
/// ciphertext to the Windows account, so a copied AppData folder, a stolen
/// backup or another user on the same machine cannot read the keys. An
/// app-managed passphrase would have to live somewhere this app can reach, which
/// means anyone who can reach this app can reach the keys.
///
/// Keys live in the user's AppData directory, never in the installation folder
/// and never in the database - the database gets backed up, exported and copied
/// between machines, and API keys should not travel with it.
///
/// Nothing in this class ever writes a key to a log, an exception message or a
/// ToString. The only way a key leaves is <see cref="GetKey"/>, which the
/// providers call immediately before a request.
/// </summary>
public sealed class AiCredentialStore
{
    private const string KeyFileName = "ai-keys.dat";
    private const string SettingsFileName = "ai-settings.json";

    // Extra entropy mixed into DPAPI. Not a secret - it is in the binary - but
    // it means ciphertext from this app cannot be decrypted by simply calling
    // DPAPI from another program running as the same user.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("IPDocketing.AiCredentials.v1");

    private readonly string _keyPath;
    private readonly string _settingsPath;

    public AiCredentialStore(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _keyPath = Path.Combine(appDataDirectory, KeyFileName);
        _settingsPath = Path.Combine(appDataDirectory, SettingsFileName);
    }

    // ---------------------------------------------------------------- keys

    /// <summary>Returns the stored key, or null. Never logged, never cached on disk in the clear.</summary>
    public string? GetKey(AiProviderKind provider)
        => ReadAll().TryGetValue(provider.ToString(), out var key) && !string.IsNullOrWhiteSpace(key)
            ? key
            : null;

    public bool HasKey(AiProviderKind provider) => GetKey(provider) is not null;

    /// <summary>
    /// Saves or clears one provider's key. Passing null or whitespace removes it,
    /// which is how Settings implements "Clear".
    /// </summary>
    public void SetKey(AiProviderKind provider, string? key)
    {
        var all = ReadAll();

        if (string.IsNullOrWhiteSpace(key)) all.Remove(provider.ToString());
        else all[provider.ToString()] = key.Trim();

        WriteAll(all);
    }

    /// <summary>
    /// A key with everything but its shape removed, for showing in the UI.
    /// "sk-ant-api03-Xk…9fQe" tells you which key is installed; the whole thing
    /// on screen is a key one screenshot away from being someone else's.
    /// </summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "not set";
        var trimmed = key.Trim();
        if (trimmed.Length <= 12) return new string('•', trimmed.Length);
        return $"{trimmed[..8]}…{trimmed[^4..]}";
    }

    private Dictionary<string, string> ReadAll()
    {
        try
        {
            if (!File.Exists(_keyPath)) return new Dictionary<string, string>();

            var protectedBytes = File.ReadAllBytes(_keyPath);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plain);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            // Unreadable means unreadable - a different Windows account, a
            // restored profile, or a corrupt file. Returning empty degrades to
            // "no keys configured", which the UI already handles. Rethrowing
            // here would take out whatever screen asked.
            return new Dictionary<string, string>();
        }
    }

    private void WriteAll(Dictionary<string, string> all)
    {
        var json = JsonSerializer.Serialize(all);
        var plain = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        // Written via a temp file and moved, so an interrupted write cannot
        // leave a half-file that loses every key at once.
        var temp = _keyPath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, _keyPath, overwrite: true);

        try { File.SetAttributes(_keyPath, FileAttributes.Hidden); } catch { }
    }

    // ------------------------------------------------------------ settings

    public AiSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AiSettings();
            return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(_settingsPath))
                   ?? new AiSettings();
        }
        catch
        {
            return new AiSettings();
        }
    }

    public void SaveSettings(AiSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var temp = _settingsPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _settingsPath, overwrite: true);
    }
}
