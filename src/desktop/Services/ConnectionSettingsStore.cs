using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace desktop.Services;

public sealed record SavedConnectionSettings(string ServerUrl, string CreationKey);

public sealed class ConnectionSettingsStore
{
    public const string FileName = "lmt.settings.json";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LMT.ConnectionSettings.v1");
    private readonly string path;
    private sealed record Document(int Version, string ServerUrl, string ProtectedCreationKey);

    public ConnectionSettingsStore(string? directory = null) => path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);

    public SavedConnectionSettings? Load()
    {
        // File.Exists also hides permission failures. Opening explicitly keeps those errors visible to the UI.
        byte[] content;
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 128 * 1024) throw new InvalidDataException("保存文件过大。");
            using var buffer = new MemoryStream();
            file.CopyTo(buffer); content = buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var document = JsonSerializer.Deserialize<Document>(content);
        if (document is not { Version: 1, ServerUrl: not null, ProtectedCreationKey: not null })
            throw new InvalidDataException("不支持的保存文件格式。");
        byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(document.ProtectedCreationKey), Entropy, DataProtectionScope.CurrentUser);
        try { return new(document.ServerUrl, Encoding.UTF8.GetString(clear)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save(string serverUrl, string creationKey)
    {
        if (serverUrl.Length > 8192 || creationKey.Length > 16384) throw new InvalidDataException("地址或密钥过长。");
        byte[] clear = Encoding.UTF8.GetBytes(creationKey);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        var document = new Document(1, serverUrl, Convert.ToBase64String(encrypted));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, document, new JsonSerializerOptions { WriteIndented = true });
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete() => File.Delete(path);
}
