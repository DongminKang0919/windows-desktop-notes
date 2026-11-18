# Shared by the ZIP and installer verifiers. No extraction or execution.
function Assert-ArchiveLayout([IO.Compression.ZipArchive]$Archive) {
    if ($Archive.Entries.Count -gt 1000) { throw 'ZIP entry count limit exceeded.' }
    $paths = New-Object 'System.Collections.Generic.Dictionary[string,bool]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$expanded = 0
    foreach ($entry in $Archive.Entries) {
        $fullName = $entry.FullName
        $directory = $fullName.EndsWith('/')
        $name = if ($directory) { $fullName.Substring(0, $fullName.Length - 1) } else { $fullName }
        if (-not $name -or $fullName.Contains('\') -or $fullName.StartsWith('/')) {
            throw 'Unsafe archive relative path.'
        }
        foreach ($part in $name.Split('/')) {
            $stem = ($part.Split('.')[0]).TrimEnd(' ')
            if (-not $part -or $part -in @('.', '..') -or $part.TrimEnd(' ', '.') -ne $part -or
                $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                $stem -match '^(CON|PRN|AUX|NUL|COM[1-9\u00B9\u00B2\u00B3]|LPT[1-9\u00B9\u00B2\u00B3])$') {
                throw 'Unsafe archive relative path.'
            }
        }
        $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
        if ($unixType -notin @(0, 0x8000, 0x4000) -or
            ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -or
            (($unixType -eq 0x4000 -or ($entry.ExternalAttributes -band [int][IO.FileAttributes]::Directory)) -and -not $directory)) {
            throw 'Archive links or special files are not supported.'
        }
        if ($paths.ContainsKey($name)) { throw 'Duplicate archive path.' }
        $paths.Add($name, $directory)
        if ($entry.Length -gt 16MB -or ($directory -and $entry.Length -ne 0)) { throw 'Archive entry size limit exceeded.' }
        $expanded += $entry.Length
        if ($expanded -gt 256MB) { throw 'Archive expanded size limit exceeded.' }
    }
    foreach ($name in $paths.Keys) {
        $parent = $name
        while ($parent.LastIndexOf('/') -ge 0) {
            $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
            if ($paths.ContainsKey($parent) -and -not $paths[$parent]) { throw 'Archive file/directory path conflict.' }
        }
    }
}
