using Noise.Core;
using Xunit;

namespace Noise.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public void ErrorRecordsKeepTheCauseAndHideCredentials()
    {
        var path=Path.Combine(Path.GetTempPath(), "RoomScope-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            DiagnosticLog.Write(path, "启动失败", new IOException("端口冲突", new InvalidOperationException("详细原因")),
                "password=secret-A Authorization: Bearer secret-B {\"password\":\"secret C\"}");
            var text=File.ReadAllText(DiagnosticLog.PathFor(path));
            Assert.Contains("端口冲突",text); Assert.Contains("详细原因",text); Assert.Contains("IOException",text);
            Assert.DoesNotContain("secret-A",text); Assert.DoesNotContain("secret-B",text); Assert.DoesNotContain("secret C",text);
        }
        finally { if(Directory.Exists(path) && Path.GetFullPath(path).StartsWith(Path.GetTempPath(),StringComparison.OrdinalIgnoreCase)) Directory.Delete(path,true); }
    }
}
