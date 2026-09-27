param(
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Find-Executable {
    if ($ExecutablePath) {
        return (Resolve-Path $ExecutablePath).Path
    }

    $candidate = Get-ChildItem -Path (Join-Path $PSScriptRoot '../../src/UniGetUI/bin') `
        -Filter 'UniGetUI.exe' -File -Recurse |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw 'UniGetUI.exe was not found under src/UniGetUI/bin.'
    }
    return $candidate.FullName
}

function Find-ElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement] $Root,
        [string] $AutomationId,
        [int] $TimeoutSeconds = 30
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition
        )
        if ($element) {
            return $element
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "UI element '$AutomationId' was not found."
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement] $Element)

    try {
        $pattern = $Element.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern
        )
        ([System.Windows.Automation.InvokePattern] $pattern).Invoke()
        return
    }
    catch {
    }

    try {
        $pattern = $Element.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern
        )
        ([System.Windows.Automation.SelectionItemPattern] $pattern).Select()
        return
    }
    catch {
    }

    try {
        $pattern = $Element.GetCurrentPattern(
            [System.Windows.Automation.LegacyIAccessiblePattern]::Pattern
        )
        ([System.Windows.Automation.LegacyIAccessiblePattern] $pattern).DoDefaultAction()
        return
    }
    catch {
    }

    $Element.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

function Ensure-ToggleOn {
    param([System.Windows.Automation.AutomationElement] $Element)

    try {
        $pattern = $Element.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern
        )
        if ($pattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            ([System.Windows.Automation.TogglePattern] $pattern).Toggle()
        }
        return
    }
    catch {
        Invoke-Element $Element
    }
}

function Select-LastComboBoxItem {
    param([System.Windows.Automation.AutomationElement] $Element)

    $Element.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{END}')
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

$appProcess = $null
try {
    Get-Process -Name UniGetUI -ErrorAction SilentlyContinue | Stop-Process -Force

    $executable = Find-Executable
    $appProcess = Start-Process `
        -FilePath $executable `
        -WorkingDirectory (Split-Path $executable) `
        -ArgumentList 'unigetui://showSettingsPage' `
        -PassThru

    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $appProcess.Id
    )
    $deadline = (Get-Date).AddSeconds(45)
    do {
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $windowCondition
        )
        if ($window) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if (-not $window) {
        throw 'UniGetUI main window was not found.'
    }

    Invoke-Element (Find-ElementByAutomationId $window 'OperationsSettingsEntry')
    [void](Find-ElementByAutomationId $window 'InstallerFileNameScheme')
    [void](Find-ElementByAutomationId $window 'ExpandEnvVarsWithPercentSyntax')

    Invoke-Element (Find-ElementByAutomationId $window 'SettingsBackButton')
    Invoke-Element (Find-ElementByAutomationId $window 'BackupSettingsEntry')
    Ensure-ToggleOn (Find-ElementByAutomationId $window 'EnablePackageBackup_LOCAL')
    Ensure-ToggleOn (Find-ElementByAutomationId $window 'EnableBackupTimestamping')
    $backupCountSelector = Find-ElementByAutomationId $window 'MaxLocalBackupCount'
    Select-LastComboBoxItem $backupCountSelector
    [void](Find-ElementByAutomationId $window 'MaxLocalBackupCountCustom')

    $navigationProcess = Start-Process `
        -FilePath $executable `
        -WorkingDirectory (Split-Path $executable) `
        -ArgumentList 'unigetui://showInstalledPage' `
        -PassThru
    $navigationProcess.WaitForExit()
    Start-Sleep -Seconds 2
    [void](Find-ElementByAutomationId $window 'ExportPackagesToCsv')

    Write-Host 'WinUI UI E2E passed: settings parity controls and CSV toolbar are reachable.'
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
