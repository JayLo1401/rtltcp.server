using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace RtlTcp.Server.Logging;

/// <summary>
/// Writes one log file per day (rtltcp-YYYY-MM-DD.log) and deletes files older than the retention period.
/// Logging never blocks the caller: lines go through a channel to a background writer.
/// </summary>
public sealed partial class FileLoggerProvider : ILoggerProvider
{
    public const string FilePrefix = "rtltcp-";
    private const int MaxQueuedLines = 10_000;

    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxQueuedLines) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Func<int> _retentionDays;
    private readonly Func<bool> _debug;
    private readonly Task _writer;

    public FileLoggerProvider(string directory, Func<int> retentionDays, Func<bool> debug)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        _retentionDays = retentionDays;
        _debug = debug;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Directory { get; }

    [GeneratedRegex(@"^rtltcp-\d{4}-\d{2}-\d{2}\.log$")]
    public static partial Regex FileNamePattern();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool DebugEnabled => _debug();

    internal void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? file = null;
        var day = DateOnly.MinValue;
        try
        {
            while (await _queue.Reader.WaitToReadAsync())
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (today != day || file is null)
                {
                    if (file is not null) await file.DisposeAsync();
                    day = today;
                    file = Open(today);
                    DeleteOldFiles();
                }
                while (_queue.Reader.TryRead(out var line)) await file.WriteLineAsync(line);
                await file.FlushAsync();
            }
        }
        catch (IOException)
        {
            // disk full / file locked: logging must never take the server down
        }
        finally
        {
            if (file is not null) await file.DisposeAsync();
        }
    }

    private StreamWriter Open(DateOnly day)
    {
        var path = Path.Combine(Directory, $"{FilePrefix}{day:yyyy-MM-dd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    /// <summary>Deletes log files older than the retention period (also called after the setting changed).</summary>
    public void DeleteOldFiles()
    {
        var keepFrom = DateOnly.FromDateTime(DateTime.Now).AddDays(-Math.Clamp(_retentionDays(), 1, 365) + 1);
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*.log"))
        {
            var name = Path.GetFileName(f);
            if (!FileNamePattern().IsMatch(name)) continue;
            if (DateOnly.TryParseExact(name[FilePrefix.Length..^4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && d < keepFrom)
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && (logLevel >= LogLevel.Information || provider.DebugEnabled);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder(160)
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ').Append(Level(logLevel)).Append(' ')
                .Append(category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null) sb.AppendLine().Append(exception);
            provider.Enqueue(sb.ToString());
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "CRIT ",
        };
    }
}
