using System.IO.Compression;
using System.Text.Json;

namespace Noise.Server;

public sealed class StudentPackageBuilder(string programDirectory, string? classroomServer)
{
    public byte[] Build()
    {
        if (!Uri.TryCreate(classroomServer, UriKind.Absolute, out var server) || server.Scheme != "https" ||
            !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) ||
            !string.IsNullOrEmpty(server.Fragment) || server.AbsolutePath != "/")
            throw new ArgumentException("请先双击「启动教师端.cmd」完成机房网络配置。");
        var template = Path.Combine(programDirectory, "student-template.zip");
        var certificate = Path.Combine(programDirectory, "roomscope-teacher.cer");
        if (!File.Exists(template)) throw new ArgumentException("缺少学生端程序，请完整解压新版教师端安装包。");
        if (!File.Exists(certificate)) throw new ArgumentException("缺少教师机证书，请重新双击「启动教师端.cmd」完成配置。");
        using var output = new MemoryStream();
        using (var source = File.OpenRead(template)) source.CopyTo(output);
        output.Position = 0;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            foreach (var entry in zip.Entries.Where(e =>
                e.FullName.Replace('\\', '/').Equals("程序/agent-deployment.json", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.Replace('\\', '/').Equals("程序/roomscope-teacher.cer", StringComparison.OrdinalIgnoreCase)).ToArray()) entry.Delete();
            using (var profile = zip.CreateEntry("程序/agent-deployment.json").Open())
                JsonSerializer.Serialize(profile, new { Server = server.GetLeftPart(UriPartial.Authority), AutoEnroll = true, ManagedMode = true, EnableLoginStartup = true });
            using (var target = zip.CreateEntry("程序/roomscope-teacher.cer").Open())
            using (var source = File.OpenRead(certificate)) source.CopyTo(target);
        }
        return output.ToArray();
    }
}
