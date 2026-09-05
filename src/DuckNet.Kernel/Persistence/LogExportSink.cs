using DuckNet.Contracts;
using DuckNet.EventBus;

namespace DuckNet.Kernel.Persistence;

/// <summary>
/// Telemetry-owned analytics offramp. Other Centers never read this file.
/// </summary>
public sealed class LogExportSink
{
    private readonly object _lock = new();
    private readonly string _path;

    public LogExportSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string FilePath => _path;

    public void Append(EventEnvelope envelope)
    {
        var line = EnvelopeJson.Serialize(envelope);
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    public IReadOnlyList<EventEnvelope> ReadFrom(long fromOffset, int limit = 1000)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        string[] lines;
        lock (_lock)
        {
            lines = File.ReadAllLines(_path);
        }

        var rows = new List<EventEnvelope>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var envelope = EnvelopeJson.Deserialize(line);
            if (envelope.LogOffset < fromOffset)
            {
                continue;
            }

            rows.Add(envelope);
            if (rows.Count >= limit)
            {
                break;
            }
        }

        return rows;
    }
}
