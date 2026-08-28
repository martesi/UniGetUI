#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $WorktreePath,
    [switch] $KeepWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'patch-stack-common.ps1')

$defaultRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $defaultRoot
}
else {
    [System.IO.Path]::GetFullPath($RepositoryRoot)
}

$gitRoot = Invoke-PatchStackGitText -Repository $repoRoot -Arguments @('rev-parse', '--show-toplevel')
$repoRoot = [System.IO.Path]::GetFullPath($gitRoot)
$baseMetadata = Read-PatchStackJson -Path (Join-Path $repoRoot 'maintenance/upstream-base.json')
$stackMetadata = Read-PatchStackJson -Path (Join-Path $repoRoot 'maintenance/patch-stack.json')

$baseCommit = [string] $baseMetadata.base_commit
$stackBaseCommit = [string] $stackMetadata.base_commit
if ($baseCommit -ne $stackBaseCommit) {
    throw "Patch-stack metadata disagrees with upstream-base.json: $baseCommit vs $stackBaseCommit"
}
if ($baseCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Invalid upstream base commit: $baseCommit"
}

[void] (Invoke-PatchStackGit -Repository $repoRoot -Arguments @('cat-file', '-e', "$baseCommit^{commit}"))
$series = @(Get-PatchStackSeries -RepositoryRoot $repoRoot)

$callerProvidedWorktree = -not [string]::IsNullOrWhiteSpace($WorktreePath)
$createdWorktree = $false
if ($callerProvidedWorktree) {
    $worktree = [System.IO.Path]::GetFullPath($WorktreePath)
}
else {
    $worktree = Join-Path ([System.IO.Path]::GetTempPath()) ("unigetui-patch-stack-" + [Guid]::NewGuid().ToString('N'))
    $createdWorktree = $true
}

if (Test-Path -LiteralPath $worktree) {
    throw "Replay worktree path already exists: $worktree"
}

$worktreeParent = Split-Path -Parent $worktree
if (-not (Test-Path -LiteralPath $worktreeParent -PathType Container)) {
    New-Item -ItemType Directory -Path $worktreeParent -Force | Out-Null
}

function Repair-InstallerCrlfPostimage {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PatchPath,

        [Parameter(Mandatory = $true)]
        [string] $ReplayWorktree
    )

    $patchText = [System.IO.File]::ReadAllText($PatchPath)
    $postimageMatch = [regex]::Match(
        $patchText,
        '(?m)^diff --git a/UniGetUI\.iss b/UniGetUI\.iss\r?\nindex [0-9a-f]+\.\.([0-9a-f]+) '
    )
    if (-not $postimageMatch.Success) {
        return
    }

    # git-format-patch records the exact postimage blob, but this checked-in mail
    # has LF-normalized hunk lines while the Inno Setup source is CRLF. Applying
    # that mail textually therefore creates a mixed-EOL blob. Restore the source
    # file's CRLF form, amend the same mail commit, then require its blob to match
    # the postimage hash carried by the patch itself.
    $installerRelativePath = 'UniGetUI.iss'
    $installerPath = Join-Path $ReplayWorktree $installerRelativePath
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $installerText = [System.IO.File]::ReadAllText($installerPath, $strictUtf8)
    $lfText = $installerText.Replace("`r`n", "`n").Replace("`r", "`n")
    $crlfText = $lfText.Replace("`n", "`r`n")

    if ($installerText -cne $crlfText) {
        Write-Host "Restoring CRLF postimage for $installerRelativePath"
        [System.IO.File]::WriteAllText($installerPath, $crlfText, $utf8NoBom)
        [void] (Invoke-PatchStackGit -Repository $ReplayWorktree -Arguments @('add', '--', $installerRelativePath))
        [void] (Invoke-PatchStackGit -Repository $ReplayWorktree -Arguments @('commit', '--amend', '--no-edit', '--no-gpg-sign'))
    }

    $expectedBlobPrefix = $postimageMatch.Groups[1].Value
    $actualBlob = Invoke-PatchStackGitText -Repository $ReplayWorktree -Arguments @('rev-parse', "HEAD:$installerRelativePath")
    if (-not $actualBlob.StartsWith($expectedBlobPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer replay blob $actualBlob does not match patch postimage $expectedBlobPrefix"
    }
}

try {
    Write-Host "Creating clean replay worktree at $worktree"
    # Exact replay must not depend on a host/global core.autocrlf setting. Disable
    # conversion for the initial checkout as well as for the replay worktree.
    [void] (Invoke-PatchStackGit -Repository $repoRoot -Arguments @('-c', 'core.autocrlf=false', 'worktree', 'add', '--detach', $worktree, $baseCommit))
    [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('config', 'core.autocrlf', 'false'))
    # git am creates new commits and therefore needs a committer identity even
    # though each format-patch mail already contains the preserved author.
    [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('config', 'user.name', 'UniGetUI Patch Stack'))
    [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('config', 'user.email', 'patch-stack@localhost'))

    foreach ($entry in $series) {
        Write-Host "Applying $($entry.RelativePath)"
        try {
            # The Classic installer is intentionally CRLF while the rest of the
            # repository is LF. Keep git am --3way as the primary path, retain
            # CR characters when present in a mail, and ignore only line-ending
            # whitespace while matching a normalized mail.
            [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('am', '--3way', '--keep-cr', '--ignore-whitespace', $entry.FullPath))
            Repair-InstallerCrlfPostimage -PatchPath $entry.FullPath -ReplayWorktree $worktree
        }
        catch {
            try {
                [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('am', '--abort'))
            }
            catch {
                Write-Warning "Could not abort the failed git am operation: $($_.Exception.Message)"
            }
            throw
        }
    }

    $status = @(Invoke-PatchStackGit -Repository $worktree -Arguments @('status', '--porcelain'))
    if ($status.Count -gt 0) {
        throw "Replay worktree is dirty after applying the patch series: $($status -join [Environment]::NewLine)"
    }

    $replayedCommit = Invoke-PatchStackGitText -Repository $worktree -Arguments @('rev-parse', 'HEAD')
    Write-Host "Patch series replayed successfully at $replayedCommit"
    if ($createdWorktree -and $KeepWorktree) {
        Write-Host "Keeping replay worktree: $worktree"
    }
    Write-Output $replayedCommit
}
finally {
    if ($createdWorktree -and -not $KeepWorktree -and (Test-Path -LiteralPath $worktree)) {
        try {
            [void] (Invoke-PatchStackGit -Repository $repoRoot -Arguments @('worktree', 'remove', '--force', $worktree))
        }
        catch {
            Write-Warning "Could not remove replay worktree '$worktree': $($_.Exception.Message)"
        }
    }
}
