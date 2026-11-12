using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace RdpProxy;

internal sealed class Credentials : IDisposable
{
    public string Username { get; }
    public string Password { get; }
    public string Path { get; }
    public bool IsCustom { get; }
    private readonly byte[] usernameBytes;
    private readonly byte[] passwordBytes;

    public Credentials(int port, string side, ProxyMode mode, string? authFile = null)
    {
        string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpProxy");
        string prefix = side == "Client" ? "client-session" : "session";
        Path = System.IO.Path.Combine(directory, $"{prefix}-{Process.GetCurrentProcess().SessionId}.json");
        if (authFile != null)
        {
            // Reject direct reuse of the generated session export as an auth source.
            if (string.Equals(System.IO.Path.GetFullPath(authFile), System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Auth file must be separate from the generated session credential file.");
            (Username, Password) = ReadAuthFile(authFile);
            IsCustom = true;
        }
        else
        {
            Username = "rdp";
            Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        usernameBytes = Protocol.Utf8.GetBytes(Username);
        passwordBytes = Protocol.Utf8.GetBytes(Password);
        var info = Directory.CreateDirectory(directory);
        var sid = WindowsIdentity.GetCurrent().User!;
        var acl = new DirectorySecurity();
        acl.SetOwner(sid);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
        File.Delete(Path);
        File.WriteAllText(Path, JsonSerializer.Serialize(new
        {
            Host = "127.0.0.1", Port = port, Username, Password, Side = side, Mode = mode.ToString(),
            Authentication = IsCustom ? "AuthFile" : "Generated",
            SessionId = Process.GetCurrentProcess().SessionId, ProcessId = Environment.ProcessId
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (string Username, string Password) ReadAuthFile(string path)
    {
        byte[] bytes;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > 16384) throw new IOException("Auth file must be 1..16384 bytes.");
            bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Cannot read auth file. Check its path, size and access permissions.", ex);
        }
        try
        {
            // Windows PowerShell 5.1 commonly writes UTF-8 with a BOM.
            ReadOnlyMemory<byte> json = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) json = bytes.AsMemory(3);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            string? username = null;
            string? password = null;
            bool hasUser = false, hasPassword = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("Username", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasUser || property.Value.ValueKind != JsonValueKind.String) throw new JsonException();
                    hasUser = true;
                    username = property.Value.GetString();
                }
                else if (property.Name.Equals("Password", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasPassword || property.Value.ValueKind != JsonValueKind.String) throw new JsonException();
                    hasPassword = true;
                    password = property.Value.GetString();
                }
            }
            if (!ValidField(username) || !ValidField(password))
                throw new IOException("Auth Username and Password must each be 1..255 UTF-8 bytes without control characters.");
            return (username!, password!);
        }
        catch (JsonException)
        {
            // Parser exception messages can include fragments of the input secret.
            throw new IOException("Invalid auth JSON. Supply one string Username and one string Password.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool ValidField(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(char.IsControl)) return false;
        try { return Protocol.Utf8.GetByteCount(text) is >= 1 and <= 255; }
        catch (System.Text.EncoderFallbackException) { return false; }
    }

    public bool Validate(byte[] username, byte[] password) =>
        CryptographicOperations.FixedTimeEquals(username, usernameBytes) &
        CryptographicOperations.FixedTimeEquals(password, passwordBytes);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(usernameBytes);
        CryptographicOperations.ZeroMemory(passwordBytes);
        try { File.Delete(Path); }
        catch (IOException) { Console.Error.WriteLine("Could not delete credential file."); }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("Could not delete credential file."); }
    }
}
