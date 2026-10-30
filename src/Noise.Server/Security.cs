using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noise.Server;

public sealed class TeacherAccess
{
    private readonly byte[] salt, hash;
    public TeacherAccess(string directory, string? password)
    {
        var file = Path.Combine(directory, "teacher-auth.json");
        if (File.Exists(file))
        {
            var stored = JsonSerializer.Deserialize<AuthFile>(File.ReadAllText(file))!;
            salt = Convert.FromBase64String(stored.Salt);
            hash = Convert.FromBase64String(stored.Hash);
        }
        else
        {
            password = string.IsNullOrEmpty(password) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(10)) : password;
            if (password.Length < 10) throw new InvalidOperationException("Teacher password must contain at least 10 characters.");
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
    public bool Verify(string password) => password.Length <= 256 &&
        CryptographicOperations.FixedTimeEquals(hash, Derive(password, salt));
    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 150_000, HashAlgorithmName.SHA256, 32);
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static void Restrict(string file)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private sealed record AuthFile(string Salt, string Hash);
}
