using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KeyceCode.Services;

/// <summary>Stores the Gemini key encrypted with Windows DPAPI for the current user.</summary>
public sealed class GeminiCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KeyceCode/Gemini/API-key/v1");
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode", "gemini-key.dpapi");
    private readonly string _secondaryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode", "gemini-key-secondary.dpapi");

    public bool HasStoredKey => File.Exists(_path);
    public bool HasStoredSecondaryKey => File.Exists(_secondaryPath);

    public void Save(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    public string? Read()
        => ReadPath(_path, Entropy);

    public void SaveSecondary(string apiKey)
        => SavePath(_secondaryPath, apiKey, Encoding.UTF8.GetBytes("KeyceCode/Gemini/API-key-secondary/v1"));

    public string? ReadSecondary()
        => ReadPath(_secondaryPath, Encoding.UTF8.GetBytes("KeyceCode/Gemini/API-key-secondary/v1"));

    private static void SavePath(string path, string apiKey, byte[] entropy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, overwrite: true); }
        finally { CryptographicOperations.ZeroMemory(encrypted); try { File.Delete(temporary); } catch (IOException) { } }
    }

    private static string? ReadPath(string path, byte[] entropy)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var encrypted = File.ReadAllBytes(path);
            try
            {
                var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public void Delete()
        => DeletePath(_path);

    public void DeleteSecondary()
        => DeletePath(_secondaryPath);

    private static void DeletePath(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
