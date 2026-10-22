using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WindowsMemo.Models;

namespace WindowsMemo.Infrastructure;

internal sealed record TemporaryFileResult(int Examined, int Removed, int Preserved, int Errors, bool Limited);

// Runs before note sessions start. Never promotes pending snapshots or deletes unique content.
internal static class TemporaryNoteMaintenance
{
    internal const int MaxFiles = 64;
    internal const long MaxReadBytes = 16 * 1024 * 1024;
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromDays(1);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte delete, uint size);

    private static FileStream OpenSnapshot(string path, bool canDelete)
    {
        FileNoteRepository.CheckLink(path);
        // OPEN_EXISTING + OPEN_REPARSE_POINT. Only pending files request DELETE, with exclusive sharing.
        var handle = OpenFile(path, canDelete ? 0x80010000u : 0x80000000u, canDelete ? 0u : 1u, 0, 3, 0x00200000, 0);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((File.GetAttributes(handle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Linked or non-file snapshots are not eligible for maintenance.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }
    private static bool SameSnapshot(NoteRecord pending, NoteRecord committed) =>
        pending.UpdatedAtUtc <= committed.UpdatedAtUtc && pending.Rtf.AsSpan().SequenceEqual(committed.Rtf)
        && (pending with { UpdatedAtUtc = committed.UpdatedAtUtc, Rtf = committed.Rtf }) == committed;

    internal static TemporaryFileResult Run(string root, DateTimeOffset now, Action<NoteRecord> validateContent)
    {
        ArgumentNullException.ThrowIfNull(validateContent);
        root = Path.GetFullPath(root); FileNoteRepository.CheckRoot(root);
        if (!Directory.Exists(root)) return new(0, 0, 0, 0, false);
        int examined = 0, removed = 0, preserved = 0, errors = 0;
        long readBytes = 0; bool limited = false;
        try
        {
            var files = Directory.EnumerateFiles(root, "*.tmp").Take(MaxFiles + 1).ToArray();
            limited = files.Length > MaxFiles;
            foreach (string path in files.Take(MaxFiles))
            {
                examined++;
                bool deleted = false;
                try
                {
                    string name = Path.GetFileName(path);
                    if (name.Length != 36 || !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        || !Guid.TryParseExact(name[..32], "N", out Guid token) || token == Guid.Empty
                        || File.GetLastWriteTimeUtc(path) > now.UtcDateTime - MinimumAge) continue;
                    FileNoteRepository.CheckRoot(root);
                    using var pendingFile = OpenSnapshot(path, canDelete: true);
                    if (File.GetLastWriteTimeUtc(pendingFile.SafeFileHandle) > now.UtcDateTime - MinimumAge) continue;
                    if (pendingFile.Length is 0 or > FileNoteRepository.MaxFileBytes) continue;
                    if (pendingFile.Length > MaxReadBytes - readBytes) { limited = true; break; }
                    readBytes += pendingFile.Length;
                    var pending = FileNoteRepository.ReadSnapshot(pendingFile);
                    string primary = new FileNoteRepository(root, pending.Id).PrimaryPath;
                    // No backup fallback: a valid matching current primary must remain locked during deletion.
                    using var committedFile = OpenSnapshot(primary, canDelete: false);
                    if (committedFile.Length is 0 or > FileNoteRepository.MaxFileBytes) continue;
                    if (committedFile.Length > MaxReadBytes - readBytes) { limited = true; break; }
                    readBytes += committedFile.Length;
                    var committed = FileNoteRepository.ReadSnapshot(committedFile);
                    if (!SameSnapshot(pending, committed)) continue;
                    validateContent(committed); // Equal RTF bytes need validation only once.
                    FileNoteRepository.CheckRoot(root);
                    byte delete = 1; // FILE_DISPOSITION_INFO contains a one-byte BOOLEAN.
                    if (!SetFileInformationByHandle(pendingFile.SafeFileHandle, 4, ref delete, 1))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    // The verified handle is deleted on close; no pathname reopen/delete race.
                    deleted = true; removed++;
                }
                catch (Exception ex) when (FileNoteRepository.IsStorageError(ex) || ex is Win32Exception or InvalidOperationException) { errors++; }
                finally { if (!deleted) preserved++; }
            }
        }
        catch (Exception ex) when (FileNoteRepository.IsStorageError(ex)) { errors++; }
        return new(examined, removed, preserved, errors, limited);
    }
}
