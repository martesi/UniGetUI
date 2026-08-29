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

try {
    Write-Host "Creating clean replay worktree at $worktree"
    # Exact replay must not depend on a host/global core.autocrlf setting.
    [void] (Invoke-PatchStackGit -Repository $repoRoot -Arguments @('-c', 'core.autocrlf=false', 'worktree', 'add', '--detach', $worktree, $baseCommit))
    [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('config', 'core.autocrlf', 'false'))
    # git am creates new commits and therefore needs a committer identity even
    # though each format-patch mail already contains the preserved author.
    [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('config', 'user.name', 'UniGetUI Patch Stack'))
    [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('config', 'user.email', 'patch-stack@localhost'))

    foreach ($entry in $series) {
        Write-Host "Applying $($entry.RelativePath)"
        try {
            [void] (Invoke-PatchStackGit -Repository $worktree -Arguments @('am', '--3way', '--keep-cr', '--ignore-whitespace', $entry.FullPath))
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
