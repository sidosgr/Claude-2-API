using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ClaudeLocalBridge;

public sealed record BridgeSettings(int Port = 8081, bool OpenAi = true, string Model = "claude-sonnet-5-5", int TimeoutSeconds = 120, string? OrganizationId = null);
public sealed record LocalKey(string Id, string Name, string Prefix, string Hash) { public string Label => $"{Name} · {Prefix}…"; }
public sealed record Organization(string Id, string Name);
public sealed record Completion(string Text, string Model);
public sealed class BridgeException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed class BridgeState
{
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalClaudeBridge");
    private readonly string path;
    private readonly object gate = new();
    public BridgeSettings Settings { get; private set; } = new();
    private List<LocalKey> keys = [];
    public IReadOnlyList<LocalKey> Keys { get { lock (gate) return keys.ToArray(); } }
    public BridgeState(string? storagePath = null)
    {
        path = storagePath ?? Path.Combine(DataFolder, "settings.json");
        if (!File.Exists(path)) return;
        var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new InvalidDataException("Settings file is empty.");
        Settings = data.Settings with { Model = NormalizeModel(data.Settings.Model) };
        Validate(Settings); keys = data.Keys;
        if (Settings.Model != data.Settings.Model) Save();
    }
    public static string NormalizeModel(string model) => model.Trim() == "claude-sonnet-5.5" ? "claude-sonnet-5-5" : model.Trim();
    public static void Validate(BridgeSettings value)
    {
        if (value.Port is < 1024 or > 65535) throw new ArgumentException("Port must be between 1024 and 65535.");
        if (value.TimeoutSeconds is < 15 or > 600) throw new ArgumentException("Timeout must be between 15 and 600 seconds.");
        if (!Regex.IsMatch(value.Model, @"^claude-[a-zA-Z0-9_-]{1,100}$")) throw new ArgumentException("Enter a machine model ID with hyphens, such as claude-sonnet-5-5. Display names such as Sonnet 5.5 are not model IDs.");
        if (value.OrganizationId is not null && !Guid.TryParse(value.OrganizationId, out _)) throw new ArgumentException("Invalid saved workspace identifier.");
    }
    public void Update(BridgeSettings value)
    {
        Validate(value);
        lock (gate) { var old = Settings; Settings = value; try { Save(); } catch { Settings = old; throw; } }
    }
    public (LocalKey Key, string Secret) CreateKey(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) throw new ArgumentException("Key name must contain 1–60 printable characters.");
        var secret = "lcb_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var key = new LocalKey(Guid.NewGuid().ToString("N"), name, secret[..12], Digest(secret));
        lock (gate) { keys.Add(key); try { Save(); } catch { keys.Remove(key); throw; } }
        return (key, secret);
    }
    public void Revoke(string id)
    {
        lock (gate) { var old = keys.ToList(); keys.RemoveAll(k => k.Id == id); try { Save(); } catch { keys = old; throw; } }
    }
    public bool Authenticate(string secret)
    {
        if (secret.Length is < 10 or > 200) return false;
        var candidate = Encoding.ASCII.GetBytes(Digest(secret));
        lock (gate) return keys.Any(k => CryptographicOperations.FixedTimeEquals(candidate, Encoding.ASCII.GetBytes(k.Hash)));
    }
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Stored(Settings, keys), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    private sealed record Stored(BridgeSettings Settings, List<LocalKey> Keys);
}
