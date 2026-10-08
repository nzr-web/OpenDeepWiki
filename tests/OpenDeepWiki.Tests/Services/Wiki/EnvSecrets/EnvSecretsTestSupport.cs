using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenDeepWiki.Tests.Services.Wiki.EnvSecrets;

/// <summary>
/// Temporary working copy; files are written with LF line endings.
/// </summary>
internal sealed class TempRepository : IDisposable
{
    public TempRepository()
    {
        Root = Path.Combine(Path.GetTempPath(), "envscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public TempRepository Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return this;
    }

    public TempRepository WriteBytes(string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Logger that keeps every formatted message and exception text.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Text)> Entries { get; } = [];

    public string AllText => string.Join("\n", Entries.Select(e => e.Text));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            text += " " + string.Join(" ", values.Select(v => $"{v.Key}={v.Value}"));
        }

        if (exception is not null)
        {
            text += " " + exception;
        }

        lock (Entries)
        {
            Entries.Add((logLevel, text));
        }
    }
}
