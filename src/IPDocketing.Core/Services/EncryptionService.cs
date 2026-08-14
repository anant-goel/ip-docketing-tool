using System.Security.Cryptography;
using System.Text;

namespace IPDocketing.Core.Services;

/// <summary>
/// File encryption bound to the current Windows user via DPAPI.
/// Only the same Windows account on this machine can decrypt the data.
/// Used for API key storage and encrypted database backups.
/// </summary>
public static class EncryptionService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPDocketing.v1.Professional");

    /// <summary>Encrypts plaintext bytes; result can only be decrypted by the same Windows user.</summary>
    public static byte[] Protect(byte[] plain)
    {
        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>Decrypts data previously protected with <see cref="Protect"/>.</summary>
    public static byte[] Unprotect(byte[] cipher)
    {
        return ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
    }

    public static void EncryptFileTo(string sourcePath, string destinationPath)
    {
        var plain = File.ReadAllBytes(sourcePath);
        var cipher = Protect(plain);
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(destinationPath, cipher);
    }

    public static void DecryptFileTo(string encryptedPath, string destinationPath)
    {
        var cipher = File.ReadAllBytes(encryptedPath);
        var plain = Unprotect(cipher);
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(destinationPath, plain);
    }

    public static void EncryptStringToFile(string text, string destinationPath)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        var cipher = Protect(plain);
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(destinationPath, cipher);
    }

    public static string? DecryptStringFromFile(string encryptedPath)
    {
        if (!File.Exists(encryptedPath)) return null;
        var cipher = File.ReadAllBytes(encryptedPath);
        var plain = Unprotect(cipher);
        return Encoding.UTF8.GetString(plain);
    }
}
