using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CLIo.Services;

[SupportedOSPlatform("windows")]
public sealed class DataProtectionService
{
    private readonly byte[] masterKey;

    public DataProtectionService()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIo");
        Directory.CreateDirectory(appData);
        var keyPath = Path.Combine(appData, "master.key");

        if (File.Exists(keyPath))
        {
            var protectedKey = File.ReadAllBytes(keyPath);
            masterKey = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        }
        else
        {
            masterKey = RandomNumberGenerator.GetBytes(32);
            var protectedKey = ProtectedData.Protect(masterKey, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(keyPath, protectedKey);
        }
    }

    public string MasterKeyHex => Convert.ToHexString(masterKey);

    public string ProtectString(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedData);
    }

    public string UnprotectString(string protectedText)
    {
        var protectedData = Convert.FromBase64String(protectedText);
        var data = ProtectedData.Unprotect(protectedData, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}
