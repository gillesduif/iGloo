using System.Text.Json;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent;

/// <summary>At most 100 immutable sanitized result files; fail closed instead of dropping evidence at capacity.</summary>
public sealed class ResultSpool(string directory)
{
    public void Save(ReadOnlyWorkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
        if (bytes.Length > 32768) throw new InvalidDataException("Result exceeds spool limit.");
        var target = Path.Join(directory, result.WorkItemId + ".json");
        if (File.Exists(target))
        {
            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Conflicting result already exists.");
            return;
        }
        if (Directory.EnumerateFiles(directory).Count() >= 100)
            throw new IOException("Fleet spool is full; review retained evidence.");
        var temporary = target + ".pending";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        File.Move(temporary, target);
    }

    public IReadOnlyList<ReadOnlyWorkResult> Pending()
    {
        if (!Directory.Exists(directory)) return [];
        // Complete a crash-interrupted atomic publish. Invalid files remain for operator recovery.
        foreach (var pending in Directory.EnumerateFiles(directory, "*.pending"))
        {
            var final = pending[..^8];
            if (!File.Exists(final)) File.Move(pending, final);
        }
        return Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).Select(path =>
        {
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Invalid spool file.");
            return JsonSerializer.Deserialize<ReadOnlyWorkResult>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Invalid spool result.");
        }).ToArray();
    }

    public void Acknowledge(Guid workId) => File.Delete(Path.Join(directory, workId + ".json"));
}
