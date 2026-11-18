using System.Windows.Documents;

namespace WindowsMemo.Services;

// A bounded display value in memory, never a plaintext index on disk.
internal static class NoteTitleSummary
{
    internal const int MaxLength = 80;
    internal static bool IsValidCustomTitle(string? title)
    {
        if (title is null || title.Length > MaxLength || title != title.Trim()) return false;
        for (int i = 0; i < title.Length; i++)
        {
            char ch = title[i];
            if (char.IsControl(ch) || ch is '\u2028' or '\u2029') return false;
            if (char.IsHighSurrogate(ch)) { if (++i >= title.Length || !char.IsLowSurrogate(title[i])) return false; }
            else if (char.IsLowSurrogate(ch)) return false;
        }
        return true;
    }
    internal static string FromRecord(Models.NoteRecord record)
    {
        var document = RtfDocumentCodec.Load(record.Rtf); // Always validate the content, even with a custom title.
        return record.Title.Length > 0 ? record.Title : FromDocument(document);
    }
    internal static string FromDocument(FlowDocument document) =>
        FromText(new TextRange(document.ContentStart, document.ContentEnd).Text);

    internal static string FromText(string text)
    {
        var content = text.AsSpan().Trim();
        int newline = content.IndexOfAny('\r', '\n');
        var heading = (newline < 0 ? content : content[..newline]).TrimStart("☐☑ ");
        int length = Math.Min(MaxLength, heading.Length);
        if (length > 0 && length < heading.Length && char.IsHighSurrogate(heading[length - 1]) && char.IsLowSurrogate(heading[length])) length--;
        return length == 0 ? "메모" : heading[..length].ToString();
    }
}
