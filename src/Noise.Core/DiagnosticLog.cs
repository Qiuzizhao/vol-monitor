using System.Text;
using System.Text.RegularExpressions;

namespace Noise.Core;

public static partial class DiagnosticLog
{
    public static string PathFor(string directory) => Path.Combine(directory, "运行错误.log");
    public static void Write(string directory, string context, Exception? error, string? detail = null)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(directory);
            lock (typeof(DiagnosticLog))
            {
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                var message = SecretPattern().Replace($"{DateTimeOffset.Now:O} {context}\n{detail}\n{error}\n", "$1=[已隐藏]");
                File.AppendAllText(path, message, new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    [GeneratedRegex("""(?i)(["']?(?:password|token|authorization|protectedtoken)["']?)\s*[:=]\s*(?:"[^"]*"|'[^']*'|(?:Bearer|Enrollment)\s+[^\s,;]+|[^\s,;]+)""")]
    private static partial Regex SecretPattern();
}
