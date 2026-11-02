using System.IO.Pipes;
using System.Text;

namespace Noise.Agent;

internal static class GuardClient
{
    internal const string ServiceName = "RoomScopeStudentGuard";
    internal const string PipeName = "RoomScope.StudentGuard.Exit";
    internal sealed record Request(string Action, string? Password = null);
    internal static Task<bool> RequestExit(string password) => Send(new("exit", password));
    internal static Task<bool> RequestStart() => Send(new("start"));
    private static async Task<bool> Send(Request request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        GuardServiceIdentity.Verify(pipe);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
        return await reader.ReadLineAsync(timeout.Token) == "OK";
    }
    internal static async Task<string> ReadPassword(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[1];
        while (await reader.ReadAsync(buffer, ct) > 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(buffer[0]);
            if (result.Length > 16384) throw new InvalidDataException("维护请求过大。");
        }
        throw new IOException("维护请求未完成。");
    }
}
