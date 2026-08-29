Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-PatchStackGit {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $output = @(& git -C $Repository @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $details = ($output | ForEach-Object { [string] $_ }) -join [Environment]::NewLine
        throw "git $($Arguments -join ' ') failed with exit code $exitCode. $details"
    }

    return $output
}

function Invoke-PatchStackGitText {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    return ((Invoke-PatchStackGit -Repository $Repository -Arguments $Arguments |
            ForEach-Object { [string] $_ }) -join [Environment]::NewLine).Trim()
}

function Read-PatchStackJson {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Patch-stack metadata was not found: $Path"
    }

    try {
        $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Patch-stack metadata is not valid JSON: $Path. $($_.Exception.Message)"
    }

    if ($null -eq $value) {
        throw "Patch-stack metadata is empty: $Path"
    }

    return $value
}

function Get-PatchStackSeries {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    $patchRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'maintenance/patches'))
    $seriesPath = Join-Path $patchRoot 'series'
    if (-not (Test-Path -LiteralPath $seriesPath -PathType Leaf)) {
        throw "Patch series file was not found: $seriesPath"
    }

    $patchRootPrefix = $patchRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $entries = [System.Collections.Generic.List[object]]::new()

    foreach ($line in Get-Content -LiteralPath $seriesPath) {
        $relativePath = $line.Trim()
        if ($relativePath.Length -eq 0 -or $relativePath.StartsWith('#')) {
            continue
        }

        if ([System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath -match '(^|[\\/])\.\.([\\/]|$)' -or
            $relativePath -match '\s') {
            throw "Invalid patch path in series: $relativePath"
        }

        if (-not $seen.Add($relativePath)) {
            throw "Patch appears more than once in series: $relativePath"
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $patchRoot $relativePath))
        if (-not $fullPath.StartsWith($patchRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch escapes the patch directory: $relativePath"
        }

        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Patch listed in series was not found: $relativePath"
        }

        $content = Get-Content -LiteralPath $fullPath -Raw
        if ($content -notmatch '(?m)^From [0-9a-f]{40} ') {
            throw "Patch is not a git-format-patch mail: $relativePath"
        }

        if ($content -notmatch '(?m)^(Upstream-Commit|Classic-Source-Commit): [0-9a-f]{40}\r?$') {
            throw "Patch has no provenance trailer: $relativePath"
        }

        $entries.Add([pscustomobject]@{
                RelativePath = $relativePath
                FullPath = $fullPath
            })
    }

    if ($entries.Count -eq 0) {
        throw "Patch series is empty: $seriesPath"
    }

    return $entries.ToArray()
}
