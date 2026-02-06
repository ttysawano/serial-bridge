using System;
using System.IO;
using System.Text.Json;

namespace SerialBridge;

public sealed class HealthLog : IDisposable
{
    private readonly object _lockObj = new();
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _jsonOpt = new()
    {
        WriteIndented = false
    };

    public HealthLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public void Info(string ev, object? data = null) => Write("info", ev, data);
    public void Warn(string ev, object? data = null) => Write("warn", ev, data);
    public void Error(string ev, object? data = null) => Write("error", ev, data);

    private void Write(string level, string ev, object? data)
    {
        var entry = new
        {
            ts = DateTimeOffset.UtcNow.ToString("o"),
            level,
            ev,
            data
        };
        var line = JsonSerializer.Serialize(entry, _jsonOpt);

        lock (_lockObj)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lockObj)
        {
            _writer.Dispose();
        }
    }
}
