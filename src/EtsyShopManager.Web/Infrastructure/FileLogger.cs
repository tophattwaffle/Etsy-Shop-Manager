using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace EtsyShopManager.Web.Infrastructure;

/// <summary>
/// Minimal daily rolling file logger (DataRoot\logs\esm-yyyyMMdd.log), so there is something to read when the
/// service misbehaves. Old files beyond <see cref="RetainDays"/> are removed.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;

    public int RetainDays { get; init; } = 14;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minLevel;

    internal void Enqueue(string line) => _channel.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        string? currentDay = null;
        StreamWriter? writer = null;
        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    var day = DateTime.Now.ToString("yyyyMMdd");
                    if (day != currentDay)
                    {
                        if (writer is not null) await writer.DisposeAsync();
                        writer = null;
                        var stream = new FileStream(Path.Combine(_directory, $"esm-{day}.log"), FileMode.Append,
                            FileAccess.Write, FileShare.ReadWrite);
                        writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                        currentDay = day;
                        PruneOldFiles();
                    }
                    await writer!.WriteLineAsync(line);
                }
                catch
                {
                    // A failed write (the file locked by another program, a full disk) loses this line only: the file is
                    // opened again for the next one, instead of file logging stopping until a restart.
                    try { if (writer is not null) await writer.DisposeAsync(); } catch { }
                    writer = null;
                    currentDay = null;
                }
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetainDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "esm-*.log"))
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            provider.IsEnabled(logLevel)
            // Keep EF/ASP.NET chatter out unless it is a warning.
            && (logLevel >= LogLevel.Warning || !(category.StartsWith("Microsoft.") || category.StartsWith("System.")));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                .Append(Short(logLevel)).Append(' ')
                .Append(category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null) sb.AppendLine().Append(exception);
            provider.Enqueue(sb.ToString());
        }

        private static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
            LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
        };
    }
}
