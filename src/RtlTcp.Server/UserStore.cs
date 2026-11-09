using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RtlTcp.Server;

/// <summary>
/// users.json: web admins (PBKDF2 hash). Stream clients do not log in. Thread-safe.
/// </summary>
public sealed class UserStore
{
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

    private sealed class FileModel
    {
        public List<AdminUser> Admins { get; set; } = [];
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
    }

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

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_model, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
