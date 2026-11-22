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

    public bool HasStoredKey => File.Exists(_path);

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
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var encrypted = File.ReadAllBytes(_path);
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
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
