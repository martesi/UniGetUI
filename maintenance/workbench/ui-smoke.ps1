param([string] $SourceRoot, [string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$exe = Get-ChildItem "$SourceRoot/src/UniGetUI/bin/x64/Debug" -Filter UniGetUI.exe -Recurse | Select-Object -First 1
if (!$exe) { throw 'The native Classic executable was not built.' }
$config = Join-Path $env:LOCALAPPDATA 'UniGetUIClassic/Configuration'
[IO.Directory]::CreateDirectory($config) | Out-Null
foreach ($name in @('DisableAutoUpdateWingetUI','DisableAutoCheckforUpdates','DisableSystemTray','DisableWinGetMalfunctionDetector','DisableNewWinGetTroubleshooter','DisableNotifications')) {
    [IO.File]::WriteAllText((Join-Path $config $name), '')
}
[IO.File]::WriteAllText((Join-Path $config 'PreferredLanguage'), 'en')
[IO.File]::WriteAllText((Join-Path $config 'PreferredTheme'), 'light')
$disabled = @{}
foreach ($name in @('WinGet','Scoop','Chocolatey','Pip','Npm','PowerShell','PowerShell7','Dotnet','Cargo','vcpkg','Homebrew','Gem')) { $disabled[$name] = $true }
[IO.File]::WriteAllText((Join-Path $config 'DisabledManagers.json'), ($disabled | ConvertTo-Json -Compress))
$process = Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
$condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
$script:window = $null
$deadline = [DateTime]::UtcNow.AddSeconds(90)
while ([DateTime]::UtcNow -lt $deadline) {
    if ($process.HasExited) { throw "Classic exited during startup with $($process.ExitCode)." }
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
    foreach ($item in $windows) {
        if ($item.Current.Name -like '*UniGetUI*' -and $item.Current.BoundingRectangle.Width -gt 400) { $script:window = $item; break }
    }
    if ($script:window) { break }
    Start-Sleep -Milliseconds 500
}
if (!$script:window) { throw 'No visible Classic window was exposed to UI Automation.' }

function Dump-Tree([string] $Name) {
    $all = $script:window.FindAll([Windows.Automation.TreeScope]::Subtree, [Windows.Automation.Condition]::TrueCondition)
    $lines = foreach ($element in $all) {
        try { "$($element.Current.ControlType.ProgrammaticName) | $($element.Current.AutomationId) | $($element.Current.Name) | offscreen=$($element.Current.IsOffscreen)" } catch { }
    }
    $lines | Set-Content (Join-Path $OutputDirectory "$Name.txt")
}

function Screenshot([string] $Name) {
    $bounds = $script:window.Current.BoundingRectangle
    $bitmap = New-Object Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $OutputDirectory "$Name.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    Dump-Tree $Name
}

function Find-Element([string] $Name, [int] $Seconds = 15) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $until) {
        $all = $script:window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $all) {
            try {
                if (!$element.Current.IsOffscreen -and ($element.Current.Name -eq $Name -or $element.Current.AutomationId -eq $Name)) { return $element }
            } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    Dump-Tree 'failure-tree'
    throw "Visible native control not found: $Name"
}

function Invoke-Element([string] $Name) {
    $element = Find-Element $Name
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
    elseif ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
    elseif ($element.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) { $pattern.Expand() }
    else {
        $element.SetFocus()
        [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
    Start-Sleep -Milliseconds 400
}

try {
    $windowPattern = $null
    if ($script:window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$windowPattern)) {
        $windowPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Maximized)
    }
    Find-Element 'Settings' 60 | Out-Null
    Screenshot '01-packages'
    Invoke-Element 'Settings'
    Find-Element 'Scheduled maintenance' | Out-Null
    Screenshot '02-settings'
    Invoke-Element 'Configure'
    Find-Element 'Check for package updates' | Out-Null
    Screenshot '03-scheduler'
    Invoke-Element 'Check for package updates'
    Find-Element 'Save' | Out-Null
    Invoke-Element 'Save'
    if (!(Test-Path (Join-Path $config 'MaintenanceSchedules.json'))) { throw 'Saving the schedule did not persist native settings.' }
    $saved = Get-Content (Join-Path $config 'MaintenanceSchedules.json') -Raw | ConvertFrom-Json
    $saved | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutputDirectory 'saved-schedule.json')
    Screenshot '04-schedule-saved'
    Invoke-Element 'Manage automatic updates'
    Find-Element 'Only marked' | Out-Null
    Screenshot '05-automatic-updates'
    Invoke-Element 'Cancel'
    'Native UI paths passed: application startup; Settings; scheduler; schedule persistence; automatic-update editor; cancel.' | Set-Content (Join-Path $OutputDirectory 'result.txt')
} finally {
    try { Screenshot 'final-state' } catch { }
    if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
    $logRoot = Join-Path $env:LOCALAPPDATA 'UniGetUIClassic'
    Get-ChildItem $logRoot -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.log','.txt' } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $OutputDirectory ($_.Name + '.captured')) -ErrorAction SilentlyContinue
    }
}
