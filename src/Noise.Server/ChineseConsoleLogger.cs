namespace Noise.Server;

// Framework diagnostics are translated at the console boundary. Never print an
// exception object, request body or credentials to the operator's launch window.
public sealed class ChineseConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ChineseConsoleLogger(categoryName);
    public void Dispose() { }

    private sealed class ChineseConsoleLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var severity = level >= LogLevel.Error ? "错误" : "提示";
            var message = category.Contains(nameof(RoomTicker), StringComparison.Ordinal)
                ? "区域数据统计未完成，请检查数据目录权限和磁盘空间。"
                : "服务组件报告异常，请检查网络连接、端口、证书和数据目录权限。";
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {severity}：{message}（事件编号 {eventId.Id}）");
        }
    }
}
