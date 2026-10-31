#if DEBUG

using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Screenbox.Helpers;

/// <summary>
/// Debug-only logger that appends to <c>LocalState\debug.log</c>.
/// </summary>
/// <remarks>
/// The built-in Debug provider only writes while a debugger is attached, and a sideloaded debug
/// build usually runs without one. Many failure paths in this app are deliberately caught and
/// logged rather than rethrown - an exception escaping an <c>async void</c> handler terminates the
/// process with no crash record - so without this, those failures leave no trace at all. The file
/// is truncated once it passes <see cref="MaxBytes"/> so it cannot grow without bound.
/// </remarks>
internal sealed class DebugFileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 1_000_000;

    private readonly object _lock = new();
    private string? _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string text)
    {
        lock (_lock)
        {
            try
            {
                _path ??= Path.Combine(ApplicationData.Current.LocalFolder.Path, "debug.log");
                FileInfo info = new(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    info.Delete();
                }

                File.AppendAllText(_path, text);
            }
            catch (Exception)
            {
                // A diagnostic aid must never become a new failure mode.
            }
        }
    }

    private sealed class FileLogger(DebugFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line + Environment.NewLine);
        }
    }
}
#endif
