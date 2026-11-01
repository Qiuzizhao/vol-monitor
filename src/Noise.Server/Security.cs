using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noise.Server;

public sealed class TeacherAccess
{
    private readonly object gate = new();
    private readonly string directory;
    private byte[] salt, hash;
    private bool configured;
    public bool Configured { get { lock (gate) return configured; } }
    public TeacherAccess(string directory, string? password)
    {
        this.directory = directory;
        var file = Path.Combine(directory, "teacher-auth.json");
        if (File.Exists(file))
        {
            var stored = JsonSerializer.Deserialize<AuthFile>(File.ReadAllText(file))!;
            salt = Convert.FromBase64String(stored.Salt);
            hash = Convert.FromBase64String(stored.Hash);
            configured = stored.Configured;
        }
        else
        {
            password = string.IsNullOrEmpty(password) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(10)) : password;
            if (password.Length < 10) throw new InvalidOperationException("维护密码至少需要 10 位。");
            salt = RandomNumberGenerator.GetBytes(16);
            hash = Derive(password, salt);
            File.WriteAllText(file, JsonSerializer.Serialize(new AuthFile(Convert.ToBase64String(salt), Convert.ToBase64String(hash))));
            Restrict(file);
            // The generated credential is deliberately not written to application logs.
            var passwordFile = Path.Combine(directory, "initial-password.txt");
            File.WriteAllText(passwordFile, password);
            Restrict(passwordFile);
        }
    }
    public string CredentialVersion { get { lock (gate) return Convert.ToHexString(salt); } }
    public bool Verify(string password)
    {
        return VerifyAndGetVersion(password) is not null;
    }
    public string? VerifyAndGetVersion(string password)
    {
        lock (gate) return password.Length <= 256 && CryptographicOperations.FixedTimeEquals(hash, Derive(password, salt))
            ? Convert.ToHexString(salt) : null;
    }
    public void SetPassword(string? password, string? confirmation)
    {
        ValidatePassword(password, confirmation);
        lock (gate)
        {
            var nextSalt = RandomNumberGenerator.GetBytes(16);
            var nextHash = Derive(password!, nextSalt);
            var file = Path.Combine(directory, "teacher-auth.json");
            var temporary = Path.Combine(directory, "teacher-auth-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new AuthFile(Convert.ToBase64String(nextSalt), Convert.ToBase64String(nextHash), true)));
                Restrict(temporary);
                // Once a teacher chooses a password, no plaintext copy is retained.
                File.Delete(Path.Combine(directory, "initial-password.txt"));
                File.Move(temporary, file, true);
                salt = nextSalt; hash = nextHash; configured = true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public static void ValidatePassword(string? password, string? confirmation)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 10 or > 256 || password.Any(char.IsControl))
            throw new ArgumentException("维护密码需要 10～256 位，不能包含换行等控制字符。");
        if (password != confirmation) throw new ArgumentException("两次输入的密码不一致。");
    }
    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 150_000, HashAlgorithmName.SHA256, 32);
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static void Restrict(string file)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private sealed record AuthFile(string Salt, string Hash, bool Configured = false);
}
