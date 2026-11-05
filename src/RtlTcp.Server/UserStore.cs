using System.Security.Cryptography;
using System.Text.Json;
using RtlTcp.Core.Auth;

namespace RtlTcp.Server;

/// <summary>Stream users in users.json (managed by the web GUI later). Thread-safe.</summary>
public sealed class UserStore : IStreamUserStore
{
    public sealed class StreamUser
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;

        /// <summary>Serials this user may use; "*" = all sticks.</summary>
        public List<string> Devices { get; set; } = ["*"];

        public string Salt { get; set; } = "";
        public int Iterations { get; set; }
        public string StoredKey { get; set; } = "";
        public string ServerKey { get; set; } = "";
    }

    private sealed class FileModel
    {
        public List<StreamUser> StreamUsers { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly Lock _lock = new();
    private FileModel _model;

    public UserStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "users.json");
        _model = File.Exists(_path)
            ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json) ?? new FileModel()
            : new FileModel();

        var secretPath = Path.Combine(dataDir, "server.secret");
        if (!File.Exists(secretPath)) File.WriteAllBytes(secretPath, RandomNumberGenerator.GetBytes(32));
        ServerSecret = File.ReadAllBytes(secretPath);
    }

    public byte[] ServerSecret { get; }

    public ScramCredentials? Find(string user, string deviceSerial)
    {
        lock (_lock)
        {
            var u = _model.StreamUsers.FirstOrDefault(x => x.Name == user);
            if (u is null || !u.Enabled || !(u.Devices.Contains("*") || u.Devices.Contains(deviceSerial))) return null;
            return new ScramCredentials(Convert.FromBase64String(u.Salt), u.Iterations,
                Convert.FromBase64String(u.StoredKey), Convert.FromBase64String(u.ServerKey));
        }
    }

    public void SetPassword(string user, string password, IEnumerable<string>? devices = null)
    {
        var c = ScramCredentials.Create(password);
        lock (_lock)
        {
            var u = _model.StreamUsers.FirstOrDefault(x => x.Name == user);
            if (u is null) _model.StreamUsers.Add(u = new StreamUser { Name = user });
            if (devices is not null) u.Devices = [.. devices];
            u.Salt = Convert.ToBase64String(c.Salt);
            u.Iterations = c.Iterations;
            u.StoredKey = Convert.ToBase64String(c.StoredKey);
            u.ServerKey = Convert.ToBase64String(c.ServerKey);
            Save();
        }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_model, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
