#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $TargetCommit
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

$expectedCommit = if ([string]::IsNullOrWhiteSpace($TargetCommit)) {
    [string] $stackMetadata.classic_source_commit
}
else {
    $TargetCommit
}
if ($expectedCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Invalid expected Classic source commit: $expectedCommit"
}

[void] (Invoke-PatchStackGit -Repository $repoRoot -Arguments @('cat-file', '-e', "$baseCommit^{commit}"))
[void] (Invoke-PatchStackGit -Repository $repoRoot -Arguments @('cat-file', '-e', "$expectedCommit^{commit}"))
[void] (& git -C $repoRoot merge-base --is-ancestor $baseCommit $expectedCommit)
if ($LASTEXITCODE -ne 0) {
    throw "Expected Classic source commit is not based on the requested upstream base: $expectedCommit"
}

$replayWorktree = Join-Path ([System.IO.Path]::GetTempPath()) ("unigetui-patch-stack-verify-" + [Guid]::NewGuid().ToString('N'))
$applyScript = Join-Path $PSScriptRoot 'apply-patch-stack.ps1'

try {
    $applyOutput = @(& $applyScript -RepositoryRoot $repoRoot -WorktreePath $replayWorktree)
    if ($LASTEXITCODE -ne 0) {
        throw "Patch-stack apply script failed with exit code $LASTEXITCODE. $($applyOutput -join [Environment]::NewLine)"
    }

    $replayedCommit = [string] ($applyOutput | Select-Object -Last 1)
    if ($replayedCommit -notmatch '^[0-9a-f]{40}$') {
        throw "Patch-stack apply script did not return a replay commit: $replayedCommit"
    }

    $replayStatus = @(Invoke-PatchStackGit -Repository $replayWorktree -Arguments @('status', '--porcelain'))
    if ($replayStatus.Count -gt 0) {
        throw "Replay worktree is not clean: $($replayStatus -join [Environment]::NewLine)"
    }

    & git -C $repoRoot diff --quiet $expectedCommit $replayedCommit --
    $diffExitCode = $LASTEXITCODE
    if ($diffExitCode -ne 0) {
        $diffSummary = @(& git -C $repoRoot diff --stat $expectedCommit $replayedCommit -- 2>&1)
        throw "Patch stack does not reproduce $expectedCommit. $($diffSummary -join [Environment]::NewLine)"
    }

    Write-Output "Patch stack verified: $replayedCommit exactly matches Classic source $expectedCommit"
}
finally {
    if (Test-Path -LiteralPath $replayWorktree) {
        try {
            [void] (Invoke-PatchStackGit -Repository $repoRoot -Arguments @('worktree', 'remove', '--force', $replayWorktree))
        }
        catch {
            Write-Warning "Could not remove verification worktree '$replayWorktree': $($_.Exception.Message)"
        }
    }
}
