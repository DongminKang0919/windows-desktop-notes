using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using WindowsMemo.Views;

namespace WindowsMemo.Services;

public static class RtfDocumentCodec
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static byte[] Save(FlowDocument document)
    {
        ValidateDocument(document);
        using var stream = new MemoryStream();
        new TextRange(document.ContentStart, document.ContentEnd).Save(stream, DataFormats.Rtf);
        if (stream.Length > MaxBytes) throw new InvalidDataException("서식 데이터가 저장 한도를 초과했습니다.");
        return stream.ToArray();
    }

    public static FlowDocument Load(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes) throw new InvalidDataException("잘못된 서식 데이터 크기입니다.");
        string text = Encoding.ASCII.GetString(bytes);
        if (!text.StartsWith(@"{\rtf1", StringComparison.Ordinal))
            throw new InvalidDataException("지원하지 않는 서식 데이터입니다.");
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                int start = ++i;
                while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
                string word = text[start..i].ToLowerInvariant();
                if (word is "object" or "objdata" or "pict" or "field" or "filetbl" or "datafield" or "bin")
                    throw new InvalidDataException("개체가 포함된 서식 데이터는 지원하지 않습니다.");
                if (i > start) i--;
                continue;
            }
            if (text[i] == '{' && ++depth > 64) throw new InvalidDataException("서식 중첩 한도를 초과했습니다.");
            if (text[i] == '}' && --depth < 0) throw new InvalidDataException("서식 데이터가 손상되었습니다.");
        }
        if (depth != 0) throw new InvalidDataException("서식 데이터가 손상되었습니다.");
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        using var stream = new MemoryStream(bytes, writable: false);
        try { new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.Rtf); }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        { throw new InvalidDataException("서식 데이터를 읽지 못했습니다.", ex); }
        ValidateDocument(document);
        return document;
    }

    private static void ValidateDocument(FlowDocument document)
    {
        if (new TextRange(document.ContentStart, document.ContentEnd).Text.Length > NoteView.MaxCharacters)
            throw new InvalidDataException("본문이 입력 한도를 초과했습니다.");
        int count = 0;
        void Inlines(InlineCollection inlines, int depth)
        {
            if (depth > 32) throw new InvalidDataException("문서 중첩 한도를 초과했습니다.");
            foreach (var inline in inlines)
            {
                if (++count > 200_000) throw new InvalidDataException("문서 요소 한도를 초과했습니다.");
                if (inline is Run or LineBreak) continue;
                if (inline is Span span && inline is not Hyperlink) Inlines(span.Inlines, depth + 1);
                else throw new InvalidDataException("지원하지 않는 문서 요소입니다.");
            }
        }
        void Blocks(BlockCollection blocks, int depth)
        {
            if (depth > 32) throw new InvalidDataException("문서 중첩 한도를 초과했습니다.");
            foreach (var block in blocks)
            {
                if (++count > 200_000) throw new InvalidDataException("문서 요소 한도를 초과했습니다.");
                if (block is Paragraph paragraph) Inlines(paragraph.Inlines, depth + 1);
                else if (block is List list) foreach (var item in list.ListItems) Blocks(item.Blocks, depth + 1);
                else throw new InvalidDataException("지원하지 않는 문서 요소입니다.");
            }
        }
        Blocks(document.Blocks, 0);
    }
}
