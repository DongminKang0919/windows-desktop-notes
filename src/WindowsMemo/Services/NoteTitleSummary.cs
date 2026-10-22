using System.Windows.Documents;

namespace WindowsMemo.Services;

// A bounded display value in memory, never a plaintext index on disk.
internal static class NoteTitleSummary
{
    internal static string FromDocument(FlowDocument document) =>
        FromText(new TextRange(document.ContentStart, document.ContentEnd).Text);

    internal static string FromText(string text)
    {
        var content = text.AsSpan().Trim();
        int newline = content.IndexOfAny('\r', '\n');
        var heading = (newline < 0 ? content : content[..newline]).TrimStart("☐☑ ");
        int length = Math.Min(80, heading.Length);
        if (length > 0 && length < heading.Length && char.IsHighSurrogate(heading[length - 1]) && char.IsLowSurrogate(heading[length])) length--;
        return length == 0 ? "메모" : heading[..length].ToString();
    }
}
