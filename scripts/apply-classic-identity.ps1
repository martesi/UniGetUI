$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)

$issPath = 'UniGetUI.iss'
$iss = [IO.File]::ReadAllText($issPath)
if ($iss.Contains('AppId={{E385AFF5-90A4-4296-8702-EC129F9DC40B}')) {
    Write-Host 'Classic identity is already applied.'
    exit 0
}

$replacements = [ordered]@{
    '#define MyAppName "UniGetUI"' = '#define MyAppName "UniGetUI Classic"'
    '#define MyAppPublisher "Devolutions Inc."' = '#define MyAppPublisher "Martes"'
    '#define MyAppURL "https://github.com/Devolutions/UniGetUI"' = '#define MyAppURL "https://github.com/martesi/UniGetUI"'
    'UninstallDisplayName="UniGetUI"' = 'UninstallDisplayName="{#MyAppName}"'
    'AppId={{889610CC-4337-4BDB-AC3B-4F21806C0BDE}' = 'AppId={{E385AFF5-90A4-4296-8702-EC129F9DC40B}'
    'AppPublisherURL="https://devolutions.net/unigetui/"' = 'AppPublisherURL={#MyAppURL}'
    'DefaultDirName="{autopf64}\UniGetUI"' = 'DefaultDirName="{autopf64}\UniGetUI Classic"'
    'ValueName: "WingetUI"' = 'ValueName: "UniGetUIClassic"'
    "ExpandConstant('{commonpf64}')+'\UniGetUI'" = "ExpandConstant('{commonpf64}')+'\UniGetUI Classic'"
    '; Deploy executable files (running instances already killed in PrepareToInstall).' = '; Deploy executable files. Inno Setup Restart Manager handles files in this Classic install directory.'
    '// Runs before any file is copied: shut everything down, then mark the copy window.' = '// Runs before any file is copied: mark the copy window. Restart Manager handles app shutdown.'
}
foreach ($entry in $replacements.GetEnumerator()) {
    if (-not $iss.Contains($entry.Key)) {
        throw "Expected installer identity anchor not found: $($entry.Key)"
    }
    $iss = $iss.Replace($entry.Key, $entry.Value)
}

$killBlock = '(?s)// Kills all instances of an image.*?end;\r?\n\r?\nfunction GetCurrentProcessId'
if ($iss -notmatch $killBlock) { throw 'Could not locate broad taskkill helper block.' }
$iss = [regex]::Replace($iss, $killBlock, 'function GetCurrentProcessId', 1)

if (-not $iss.Contains("    KillRunningApps;`r`n") -and -not $iss.Contains("    KillRunningApps;`n")) {
    throw 'PrepareToInstall KillRunningApps call not found.'
}
$iss = $iss.Replace("    KillRunningApps;`r`n", '').Replace("    KillRunningApps;`n", '')

$migrationLine = 'Filename: "{app}\{#MyAppExeName}"; Parameters: "--migrate-wingetui-to-unigetui"; StatusMsg: "Removing old icons...";'
if (-not $iss.Contains($migrationLine)) { throw 'Legacy WingetUI migration line not found.' }
$iss = $iss.Replace($migrationLine + "`r`n", '').Replace($migrationLine + "`n", '').Replace($migrationLine, '')

$uninstallBlock = '(?s)\r?\n\[UninstallRun\].*\z'
if ($iss -notmatch $uninstallBlock) { throw 'Legacy broad UninstallRun block not found.' }
$iss = [regex]::Replace($iss, $uninstallBlock, '')
[IO.File]::WriteAllText($issPath, $iss, $utf8)

$corePath = 'src/UniGetUI.Core.Data/CoreData.cs'
$core = [IO.File]::ReadAllText($corePath)
$coreReplacements = [ordered]@{
    'private const string GitHubReleasePageBaseUrl = "https://github.com/Devolutions/UniGetUI/releases/tag/";' = 'private const string GitHubReleasePageBaseUrl = "https://github.com/martesi/UniGetUI/releases/tag/";'
    'private const string GitHubReleaseApiBaseUrl = "https://api.github.com/repos/Devolutions/UniGetUI/releases/tags/";' = 'private const string GitHubReleaseApiBaseUrl = "https://api.github.com/repos/martesi/UniGetUI/releases/tags/";'
    'public const string ReleaseNotesUrl = "https://devolutions.net/unigetui/release-notes/";' = 'public const string ReleaseNotesUrl = "https://github.com/martesi/UniGetUI/releases";'
    '$"UniGetUI/{VersionName} (https://devolutions.net/unigetui; unigetui@devolutions.net)";' = '$"UniGetUIClassic/{VersionName} (https://github.com/martesi/UniGetUI)";'
    'public const string AppIdentifier = "MartiCliment.UniGetUI";' = 'public const string AppIdentifier = "Martes.UniGetUIClassic";'
    'public const string MainWindowIdentifier = "MartiCliment.UniGetUI.MainInterface";' = 'public const string MainWindowIdentifier = "Martes.UniGetUIClassic.MainInterface";'
}
foreach ($entry in $coreReplacements.GetEnumerator()) {
    if (-not $core.Contains($entry.Key)) {
        throw "Expected CoreData identity anchor not found: $($entry.Key)"
    }
    $core = $core.Replace($entry.Key, $entry.Value)
}

$dataPattern = '(?s)\s*string old_path = Path\.Join\(\s*Environment\.GetFolderPath\(Environment\.SpecialFolder\.UserProfile\),\s*"\.wingetui"\s*\);\s*string new_path = Path\.Join\(GetLocalDataRoot\(\), "UniGetUI"\);\s*return GetNewDataDirectoryOrMoveOld\(old_path, new_path\);'
if ($core -notmatch $dataPattern) { throw 'Could not locate upstream writable data-directory migration block.' }
$dataReplacement = @'

                string classicPath = Path.Join(GetLocalDataRoot(), "UniGetUIClassic");
                if (!Directory.Exists(classicPath))
                    Directory.CreateDirectory(classicPath);
                return classicPath;
'@
$core = [regex]::Replace($core, $dataPattern, $dataReplacement, 1)
[IO.File]::WriteAllText($corePath, $core, $utf8)

$updatedIss = [IO.File]::ReadAllText($issPath)
if (-not $updatedIss.Contains('Software\Classes\unigetui')) { throw 'unigetui:// registration changed unexpectedly.' }
if (-not $updatedIss.Contains('UniGetUI.PackageBundle')) { throw '.ubundle ProgID changed unexpectedly.' }
if ($updatedIss.Contains("TaskKillWait('UniGetUI.exe')") -or $updatedIss.Contains('/f /im UniGetUI.exe')) {
    throw 'Broad UniGetUI process killing remains in Classic installer.'
}
if ($updatedIss.Contains('--migrate-wingetui-to-unigetui')) {
    throw 'Legacy upstream migration command remains in Classic installer.'
}
