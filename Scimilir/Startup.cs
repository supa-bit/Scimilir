using System.Diagnostics;
using System.Text.Json;

namespace Scimilir;

public static class Browser
{
    /// <summary>Opens a web address in the default browser (Windows only; elsewhere does nothing).</summary>
    public static void Open(string url)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* No browser to open; nothing else to do. */ }
    }
}

public static class Startup
{
    /// <summary>
    /// The folder for settings, chats and models. On Windows this is
    /// C:\Users\you\AppData\Local\Scimilir, which updates and reinstalls leave alone.
    /// </summary>
    public static string DefaultDataFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scimilir");

    /// <summary>True if Scimilir is already running on this port (started twice, for example).</summary>
    public static bool AlreadyRunning(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var json = http.GetStringAsync($"http://localhost:{port}/api/status").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("setupNeeded", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Writes the app's messages to scimilir.log in the data folder, for finding out
/// what went wrong. Each start replaces the last start's log.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    readonly StreamWriter _writer;
    readonly Lock _lock = new();

    public FileLoggerProvider(string file)
    {
        _writer = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string category) => new FileLogger(this, category);

    void Write(string line)
    {
        lock (_lock) _writer.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }

    sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level)) return;
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-11} {category}: {format(state, error)}";
            if (error is not null) text += Environment.NewLine + error;
            try { owner.Write(text); }
            catch (ObjectDisposedException) { }
        }
    }
}
