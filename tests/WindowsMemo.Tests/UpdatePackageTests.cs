using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using Velopack;
using WindowsMemo.Infrastructure;

namespace WindowsMemo.Tests;

internal static class UpdatePackageTests
{
    public static void ValidateKnownPackage(string path)
    {
        var asset = VelopackAsset.FromNupkgGenerateChecksumAsync(path).GetAwaiter().GetResult();
        UpdatePackageValidator.Validate(path, asset);
        Console.WriteLine("PASS existing Velopack package structure and integrity");
    }
    public static void Run(Action<string, Action> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "update-package-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "WindowsMemoApp-0.9.1-full.nupkg");
        const string valid = "<package><metadata><id>WindowsMemoApp</id><version>0.9.1</version></metadata></package>";
        void Package(string? metadata = null, string? extra = null, bool missingApp = false, bool linked = false,
            (string Name, string Text, int Attributes)[]? entries = null)
        {
            using var file = File.Create(path); using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            void Entry(string name, string text, int? attributes = null)
            {
                var entry = archive.CreateEntry(name);
                if (attributes is not null) entry.ExternalAttributes = attributes.Value;
                if (linked && name == extra) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var writer = new StreamWriter(entry.Open()); writer.Write(text);
            }
            Entry("WindowsMemoApp.nuspec", metadata ?? valid); Entry("lib/app/sq.version", metadata ?? valid);
            Entry("lib/app/WindowsMemo.dll", "synthetic, never executed");
            if (!missingApp) Entry("lib/app/WindowsMemo.exe", "synthetic, never executed");
            if (extra is not null) Entry(extra, "test");
            foreach (var item in entries ?? []) Entry(item.Name, item.Text, item.Attributes);
        }
        VelopackAsset Asset()
        {
            using var file = File.OpenRead(path);
            return VelopackAssetFeed.FromJson(JsonSerializer.Serialize(new { Assets = new[] { new
            {
                PackageId = "WindowsMemoApp", Version = "0.9.1", Type = "Full", FileName = Path.GetFileName(path),
                Size = file.Length, SHA256 = Convert.ToHexString(SHA256.HashData(file))
            } } })).Assets.Single();
        }
        check("업데이트 패키지: 정상 구조와 외부 해시 재계산 뒤 다른 제품·버전 거부", () =>
        {
            Package(); UpdatePackageValidator.Validate(path, Asset());
            Package(valid.Replace("WindowsMemoApp", "OtherApp")); Throws(() => UpdatePackageValidator.Validate(path, Asset()));
            Package(valid.Replace("0.9.1", "0.1.0")); Throws(() => UpdatePackageValidator.Validate(path, Asset()));
            Package(valid.Replace("</id>", "</id><id>OtherApp</id>")); Throws(() => UpdatePackageValidator.Validate(path, Asset()));
        });
        check("업데이트 패키지: 경로 이탈·Windows 경로 충돌·누락 파일 거부", () =>
        {
            foreach (string name in new[] { "../escape.exe", "lib/app/../escape", "lib/app/WindowsMemo.EXE", "lib/app/a./file", "lib/app/a:stream", "lib/app/NUL.txt", @"lib\app\file" })
            { Package(extra: name); Throws(() => UpdatePackageValidator.Validate(path, Asset())); }
            Package(extra: "lib/app/link", linked: true); Throws(() => UpdatePackageValidator.Validate(path, Asset()));
            Package(missingApp: true); Throws(() => UpdatePackageValidator.Validate(path, Asset()));
        });
        check("업데이트 패키지: XML 외부 엔티티·과대 제품 정보·손상 ZIP 거부", () =>
        {
            Package("<!DOCTYPE package [<!ENTITY x SYSTEM 'file:///untrusted'>]><package>&x;</package>");
            Throws(() => UpdatePackageValidator.Validate(path, Asset()));
            Package(new string(' ', 65537)); Throws(() => UpdatePackageValidator.Validate(path, Asset()));
            File.WriteAllBytes(path, [1,2,3]); Throws(() => UpdatePackageValidator.Validate(path, Asset()));
        });
        void RejectEntries(params (string Name, string Text, int Attributes)[] entries)
        {
            Package(entries: entries);
            byte[] before = File.ReadAllBytes(path);
            Throws(() => UpdatePackageValidator.Validate(path, Asset()));
            if (!before.SequenceEqual(File.ReadAllBytes(path)))
                throw new InvalidOperationException("Package validation modified its input.");
        }
        check("업데이트 패키지: 명시적·암시적 디렉터리와 항목 순서 허용", () =>
        {
            Package(entries: [("lib/app/assets/nested/file.txt", "test", 0),
                ("lib/app/assets/", "", 0x40000000), ("lib/", "", (int)FileAttributes.Directory),
                ("lib/app/assets/nested/", "", 0),
                ("lib/app/COM10.txt", "valid", 0), ("lib/app/CONSOLE.txt", "valid", 0),
                ("lib/app/한글.txt", "valid", unchecked((int)0x80000000))]);
            UpdatePackageValidator.Validate(path, Asset());
        });
        check("업데이트 패키지: 파일을 부모로 쓰는 경로를 순서·대소문자와 무관하게 거부", () =>
        {
            RejectEntries(("lib/app/assets", "file", 0), ("lib/app/ASSETS/nested/file", "child", 0));
            RejectEntries(("lib/app/ASSETS/nested/", "", 0), ("lib/app/assets", "file", 0));
            RejectEntries(("lib", "file", 0)); // conflicts with the required application files
        });
        check("업데이트 패키지: Windows 예약 이름 확장자·공백·위 첨자 거부", () =>
        {
            foreach (string name in new[] { "COM¹.txt", "LPT²", "COM³.dat", "lpt¹.txt", "com²", "LPT³.dat",
                "CON .txt", "NUL .data", "com1 .txt" })
                RejectEntries(($"lib/app/{name}", "test", 0));
        });
        check("업데이트 패키지: 디렉터리 데이터와 파일·디렉터리 속성 불일치 거부", () =>
        {
            RejectEntries(("lib/app/assets/", "data", 0));
            RejectEntries(("lib/app/assets", "", 0x40000000));
            RejectEntries(("lib/app/assets", "", (int)FileAttributes.Directory));
            RejectEntries(("lib/app/assets/", "", unchecked((int)0x80000000)));
            RejectEntries(("lib/app/assets", "file", 0), ("lib/app/ASSETS/", "", 0));
        });
        check("업데이트 패키지: 반복 끝 구분자와 빈 경로 구성요소 거부", () =>
        {
            foreach (string name in new[] { "lib/app/assets//", "lib//app/assets/", "/", "//" })
                RejectEntries((name, "", 0));
        });
    }
    private static void Throws(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException) { return; }
        throw new InvalidOperationException("Expected invalid package rejection.");
    }
}
