using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsMemo.Infrastructure;

namespace WindowsMemo.Services;

internal enum PendingRecoveryStatus
{
    Created, AlreadyRecovered, Busy, SourceChanged, SourceMissing, SourceUnavailable,
    InvalidSource, UnsupportedSource, ExistingCopyUnavailable, LimitReached, SaveFailed
}

internal sealed record PendingRecoveryResult(Guid Id, PendingRecoveryStatus Status)
{
    internal bool Succeeded => Status is PendingRecoveryStatus.Created or PendingRecoveryStatus.AlreadyRecovered;
    public bool AlreadyRecovered => Status == PendingRecoveryStatus.AlreadyRecovered;
    internal bool CanRetry => Status is PendingRecoveryStatus.Busy or PendingRecoveryStatus.SourceUnavailable
        or PendingRecoveryStatus.ExistingCopyUnavailable or PendingRecoveryStatus.LimitReached or PendingRecoveryStatus.SaveFailed;
    // Never expose exception messages, local paths or document content to the feedback surface.
    internal string Message => Status switch
    {
        PendingRecoveryStatus.Created => "새 사본을 저장했습니다. ‘메모 목록 열기’에서 확인하세요. 원본은 보존됩니다.",
        PendingRecoveryStatus.AlreadyRecovered => "이전에 복구한 사본이 있습니다. 메모 목록·보관함·휴지통에서 확인하세요. 기존 편집과 상태는 유지했습니다.",
        PendingRecoveryStatus.Busy => "다른 작업이 진행 중입니다. 끝난 뒤 다시 시도하세요. 원본은 보존됩니다.",
        PendingRecoveryStatus.SourceChanged => "미리보기 뒤 파일이 변경됐습니다. 다시 선택해 최신 내용을 확인하세요. 원본은 보존됩니다.",
        PendingRecoveryStatus.SourceMissing => "선택한 임시 파일이 없습니다. ‘처음부터 확인’으로 목록을 갱신하세요.",
        PendingRecoveryStatus.SourceUnavailable => "임시 파일을 읽지 못했습니다. 사용 중인 프로그램이나 폴더 접근 권한을 확인한 뒤 다시 시도하세요. 원본은 보존됩니다.",
        PendingRecoveryStatus.InvalidSource => "이 계정에서 파일 내용을 확인할 수 없습니다. 손상되었거나 지원하지 않는 내용일 수 있습니다. 원본은 보존됩니다.",
        PendingRecoveryStatus.UnsupportedSource => "더 새로운 메모 저장 형식입니다. 앱을 업데이트한 뒤 다시 확인하세요. 원본은 보존됩니다.",
        PendingRecoveryStatus.ExistingCopyUnavailable => "기존 복구 사본을 확인하지 못했습니다. 메모 목록·보관함·휴지통과 파일 접근 상태를 확인한 뒤 다시 시도하세요. 파일은 덮어쓰지 않았습니다.",
        PendingRecoveryStatus.LimitReached => "메모 한도 1,000개에 도달했습니다. 필요 없는 메모를 휴지통에서 영구 삭제한 뒤 다시 시도하세요. 원본은 보존됩니다.",
        _ => "사본을 저장하지 못했습니다. 저장 공간과 폴더 권한을 확인한 뒤 다시 시도하세요. 계속 실패하면 앱을 다시 실행하세요. 원본은 보존됩니다."
    };
    internal static PendingRecoveryResult Failed(PendingRecoveryStatus status) => new(Guid.Empty, status);
    internal static PendingRecoveryResult SourceFailure(Exception error) => Failed(error switch
    {
        PendingSourceChangedException => PendingRecoveryStatus.SourceChanged,
        FileNotFoundException or DirectoryNotFoundException => PendingRecoveryStatus.SourceMissing,
        Win32Exception { NativeErrorCode: 2 or 3 } => PendingRecoveryStatus.SourceMissing,
        UnsupportedNoteSchemaException => PendingRecoveryStatus.UnsupportedSource,
        InvalidDataException or JsonException or CryptographicException or ArgumentException or NotSupportedException => PendingRecoveryStatus.InvalidSource,
        _ => PendingRecoveryStatus.SourceUnavailable
    });
}
