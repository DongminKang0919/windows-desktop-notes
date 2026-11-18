using System.IO;
using System.Text.Json;
using WindowsMemo.Models;

namespace WindowsMemo.Infrastructure;

// Preferences contain no note text or custom titles. Note data remains DPAPI protected.
internal sealed class FileSettingsStore(string root)
{
    internal string FilePath { get; } = Path.Combine(root, "settings.json");
    private void Check()
    {
        FileNoteRepository.CheckRoot(Path.GetFullPath(root));
        FileNoteRepository.CheckLink(FilePath);
        FileNoteRepository.CheckLink(FilePath + ".bak");
    }
    internal AppSettings Load()
    {
        Check();
        if (!File.Exists(FilePath)) return new();
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > 4096) throw new InvalidDataException("설정 파일 크기가 올바르지 않습니다.");
        var settings = JsonSerializer.Deserialize<AppSettings>(stream) ?? throw new InvalidDataException("설정을 읽지 못했습니다.");
        AppSettings.Validate(settings);
        return settings;
    }
    internal void Save(AppSettings settings)
    {
        AppSettings.Validate(settings);
        Check();
        Directory.CreateDirectory(root);
        string temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".settings-pending");
        try
        {
            Check();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, settings); stream.Flush(true); }
            Check();
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
