using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RtlTcp.Core.Auth;

namespace RtlTcp.Server;

/// <summary>
/// users.json: web admins (PBKDF2 hash) and stream users (SCRAM keys, enabled flag, permitted sticks). Thread-safe.
/// </summary>
public sealed class UserStore : IStreamUserStore
{
    public const string AllDevices = "*";
    private const int AdminIterations = 210_000;

    public sealed class AdminUser
    {
        public string Name { get; set; } = "";
        public string Salt { get; set; } = "";
        public int Iterations { get; set; }
        public string Hash { get; set; } = "";

        /// <summary>True for the default admin/admin until the password was changed.</summary>
        public bool MustChangePassword { get; set; }
    }

    public const string DefaultAdmin = "admin";
    public const string DefaultPassword = "admin";

    public sealed class StreamUser
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;

        /// <summary>Serials this user may use; "*" = all sticks.</summary>
        public List<string> Devices { get; set; } = [AllDevices];

        public string Salt { get; set; } = "";
        public int Iterations { get; set; }
        public string StoredKey { get; set; } = "";
        public string ServerKey { get; set; } = "";
    }

    private sealed class FileModel
    {
        public List<AdminUser> Admins { get; set; } = [];
        public List<StreamUser> StreamUsers { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly FileModel _model;

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

    // ---------------------------------------------------------------- validation

    public static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Name fehlt"
        : name.Length > 64 ? "Name zu lang (max. 64 Zeichen)"
        : name.Any(c => c == ',' || char.IsWhiteSpace(c) || char.IsControl(c)) ? "Name darf keine Leerzeichen oder Kommas enthalten"
        : null;

    public static string? ValidatePassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < 8 ? "Passwort muss mindestens 8 Zeichen haben"
        : password.Length > 256 ? "Passwort zu lang"
        : null;

    // ---------------------------------------------------------------- admins

    public bool HasAdmin
    {
        get { lock (_lock) return _model.Admins.Count > 0; }
    }

    /// <summary>First start: creates admin/admin, which has to be changed at the first login.</summary>
    public bool EnsureDefaultAdmin()
    {
        if (HasAdmin) return false;
        SetAdmin(DefaultAdmin, DefaultPassword, mustChangePassword: true);
        return true;
    }

    public bool MustChangePassword(string? name)
    {
        lock (_lock) return _model.Admins.FirstOrDefault(x => x.Name == name)?.MustChangePassword == true;
    }

    public void SetAdmin(string name, string password, bool mustChangePassword = false)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, AdminIterations, HashAlgorithmName.SHA256, 32);
        lock (_lock)
        {
            var a = _model.Admins.FirstOrDefault(x => x.Name == name);
            if (a is null) _model.Admins.Add(a = new AdminUser { Name = name });
            a.Salt = Convert.ToBase64String(salt);
            a.Iterations = AdminIterations;
            a.Hash = Convert.ToBase64String(hash);
            a.MustChangePassword = mustChangePassword;
            Save();
        }
    }

    /// <summary>CLI: reset to admin/admin (forgotten password).</summary>
    public void ResetAdmin()
    {
        lock (_lock) _model.Admins.Clear();
        EnsureDefaultAdmin();
    }

    public bool VerifyAdmin(string name, string password)
    {
        AdminUser? a;
        lock (_lock) a = _model.Admins.FirstOrDefault(x => x.Name == name);
        // Unknown user: compute anyway so the response time does not reveal valid names.
        var salt = a is null ? new byte[16] : Convert.FromBase64String(a.Salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, a?.Iterations ?? AdminIterations,
            HashAlgorithmName.SHA256, 32);
        return a is not null && CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(a.Hash));
    }

    // ---------------------------------------------------------------- stream users

    public ScramCredentials? Find(string user, string deviceSerial)
    {
        lock (_lock)
        {
            var u = _model.StreamUsers.FirstOrDefault(x => x.Name == user);
            if (u is null || !u.Enabled || !(u.Devices.Contains(AllDevices) || u.Devices.Contains(deviceSerial))) return null;
            return new ScramCredentials(Convert.FromBase64String(u.Salt), u.Iterations,
                Convert.FromBase64String(u.StoredKey), Convert.FromBase64String(u.ServerKey));
        }
    }

    public sealed record StreamUserInfo(string Name, bool Enabled, List<string> Devices);

    public List<StreamUserInfo> ListStreamUsers()
    {
        lock (_lock) return _model.StreamUsers.Select(u => new StreamUserInfo(u.Name, u.Enabled, [.. u.Devices])).ToList();
    }

    /// <summary>Creates or updates a stream user. <paramref name="password"/> null = keep the current one.</summary>
    public void SaveStreamUser(string name, bool enabled, IEnumerable<string> devices, string? password)
    {
        var creds = password is null ? null : ScramCredentials.Create(password);
        lock (_lock)
        {
            var u = _model.StreamUsers.FirstOrDefault(x => x.Name == name);
            if (u is null)
            {
                if (creds is null) throw new InvalidOperationException("Neuer Benutzer braucht ein Passwort");
                _model.StreamUsers.Add(u = new StreamUser { Name = name });
            }
            u.Enabled = enabled;
            u.Devices = devices.Distinct().ToList();
            if (u.Devices.Count == 0) u.Devices = [];
            if (creds is not null)
            {
                u.Salt = Convert.ToBase64String(creds.Salt);
                u.Iterations = creds.Iterations;
                u.StoredKey = Convert.ToBase64String(creds.StoredKey);
                u.ServerKey = Convert.ToBase64String(creds.ServerKey);
            }
            Save();
        }
    }

    public bool StreamUserExists(string name)
    {
        lock (_lock) return _model.StreamUsers.Any(x => x.Name == name);
    }

    public bool DeleteStreamUser(string name)
    {
        lock (_lock)
        {
            var removed = _model.StreamUsers.RemoveAll(x => x.Name == name) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>CLI helper: create/update with access to all sticks.</summary>
    public void SetPassword(string user, string password) =>
        SaveStreamUser(user, enabled: true, StreamUserExists(user) ? ListStreamUsers().First(u => u.Name == user).Devices : [AllDevices], password);

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_model, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
