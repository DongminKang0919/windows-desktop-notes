using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Velopack;

namespace WindowsMemo.Infrastructure;

// Checks integrity/structure of explicit local test packages, not publisher authenticity.
internal static class UpdatePackageValidator
{
    public static void Validate(string path, VelopackAsset asset)
    {
        LocalTestUpdateSource.Validate(asset);
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("링크된 업데이트 패키지는 사용할 수 없습니다.");
        using var stream = File.OpenRead(path);
        if (stream.Length != asset.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("업데이트 패키지가 변경되었습니다.");
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 512) throw new InvalidDataException("업데이트 파일 수가 너무 많습니다.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("링크 또는 특수 파일이 포함된 업데이트는 사용할 수 없습니다.");
            string name = entry.FullName.TrimEnd('/');
            if (name.Length == 0 || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/')
                || name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || IsDeviceName(part)
                    || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                || !names.Add(name))
                throw new InvalidDataException("잘못되거나 중복된 업데이트 경로입니다.");
            expandedBytes += entry.Length;
            if (entry.Length > LocalTestUpdateSource.MaxPackageBytes || expandedBytes > 512L * 1024 * 1024)
                throw new InvalidDataException("업데이트 압축 해제 크기가 너무 큽니다.");
        }
        foreach (string required in new[] { "lib/app/WindowsMemo.exe", "lib/app/WindowsMemo.dll" })
            if (archive.GetEntry(required) is not { Length: > 0 }) throw new InvalidDataException("필수 앱 파일이 없습니다.");
        CheckMetadata(archive.GetEntry("WindowsMemoApp.nuspec"), asset);
        CheckMetadata(archive.GetEntry("lib/app/sq.version"), asset);
    }
    private static bool IsDeviceName(string part)
    {
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9');
    }
    private static void CheckMetadata(ZipArchiveEntry? entry, VelopackAsset asset)
    {
        if (entry is not { Length: > 0 and <= 65536 }) throw new InvalidDataException("업데이트 제품 정보가 없습니다.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var root = XDocument.Load(reader).Root;
        XNamespace ns = root?.Name.Namespace ?? XNamespace.None;
        var metadata = root?.Name.LocalName == "package" ? root.Elements(ns + "metadata").ToArray() : [];
        if (metadata.Length != 1 || metadata[0].Elements(ns + "id").Count() != 1 || metadata[0].Elements(ns + "version").Count() != 1
            || metadata[0].Element(ns + "id")?.Value != asset.PackageId
            || metadata[0].Element(ns + "version")?.Value != asset.Version.ToString())
            throw new InvalidDataException("업데이트 제품 또는 버전이 일치하지 않습니다.");
    }
}
