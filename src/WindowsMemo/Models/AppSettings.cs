namespace WindowsMemo.Models;

public sealed record AppSettings(int Version = 1, string DefaultPaper = "Butter", double NoteWidth = 320,
    double NoteHeight = 300, bool ShowListOnLaunch = true)
{
    internal static void Validate(AppSettings settings)
    {
        if (settings.Version != 1 || !Views.NoteTheme.IsPaper(settings.DefaultPaper)
            || !double.IsFinite(settings.NoteWidth) || settings.NoteWidth is < 260 or > 1200
            || !double.IsFinite(settings.NoteHeight) || settings.NoteHeight is < 200 or > 1200)
            throw new System.IO.InvalidDataException("설정 값이 올바르지 않습니다.");
    }
}
