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

function Find-ToggleByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement] $Root,
        [string] $AutomationId,
        [int] $TimeoutSeconds = 30
    )

    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId
        ),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty,
            $true
        )
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) {
            return $element
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "UI toggle '$AutomationId' was not found."
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

    Invoke-Element (Find-ElementByAutomationId $window 'OperationsSettingsEntry' -TimeoutSeconds 120)
    [void](Find-ElementByAutomationId $window 'InstallerFileNameScheme')
    [void](Find-ElementByAutomationId $window 'ExpandEnvVarsWithPercentSyntax')

    Invoke-Element (Find-ElementByAutomationId $window 'SettingsBackButton')
    Write-Host 'Opening interface settings'
    Invoke-Element (Find-ElementByAutomationId $window 'InterfaceSettingsEntry')
    Write-Host 'Enabling background mode'
    Ensure-ToggleOn (Find-ToggleByAutomationId $window 'DisableSystemTray')
    Write-Host 'Checking tray icon toggle'
    $trayIcon = Find-ToggleByAutomationId $window 'HideSystemTrayIcon'
    $deadline = (Get-Date).AddSeconds(15)
    while (-not $trayIcon.Current.IsEnabled -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (-not $trayIcon.Current.IsEnabled) {
        throw 'The tray icon setting did not become enabled with background mode.'
    }
    Ensure-ToggleOn $trayIcon
    $trayToggle = [System.Windows.Automation.TogglePattern]($trayIcon.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern
    ))
    $trayToggle.Toggle()
    if ($trayToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw 'The tray icon setting did not turn off.'
    }
    $trayToggle.Toggle()
    if ($trayToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw 'The tray icon setting did not turn on again.'
    }

    Invoke-Element (Find-ElementByAutomationId $window 'SettingsBackButton')
    Invoke-Element (Find-ElementByAutomationId $window 'BackupSettingsEntry')
    Ensure-ToggleOn (Find-ElementByAutomationId $window 'EnablePackageBackup_LOCAL')
    Ensure-ToggleOn (Find-ElementByAutomationId $window 'EnableBackupTimestamping')
    $backupCountSelector = Find-ElementByAutomationId $window 'MaxLocalBackupCount'
    Select-LastComboBoxItem $backupCountSelector
    [void](Find-ElementByAutomationId $window 'MaxLocalBackupCountCustom')

    Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    $appProcess = Start-Process `
        -FilePath $executable `
        -WorkingDirectory (Split-Path $executable) `
        -ArgumentList 'unigetui://showInstalledPage' `
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
        throw 'UniGetUI installed-page window was not found.'
    }

    [void](Find-ElementByAutomationId ([System.Windows.Automation.AutomationElement]::RootElement) 'ExportPackagesToCsv')

    Write-Host 'Enabling the release date column on Discover'
    $null = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) `
        -ArgumentList 'unigetui://showDiscoverPage' -PassThru
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $query = Find-ElementByAutomationId $root 'MegaQueryBlock' -TimeoutSeconds 120
    Invoke-Element (Find-ElementByAutomationId $root 'OrderByButton')
    $columnsMenu = Find-ElementByAutomationId $root 'ColumnsMenu'
    try {
        $expand = [System.Windows.Automation.ExpandCollapsePattern]$columnsMenu.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern
        )
        $expand.Expand()
    }
    catch {
        $columnsMenu.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('{RIGHT}')
    }
    Invoke-Element (Find-ElementByAutomationId $root 'ColumnReleaseDate')
    [void](Find-ElementByAutomationId $root 'ReleaseDateHeader')
    $value = [System.Windows.Automation.ValuePattern]$query.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern
    )
    $value.SetValue('dotnetsay')
    Invoke-Element (Find-ElementByAutomationId $root 'MegaFindButton')
    $dateCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PackageReleaseDate'
    )
    $deadline = (Get-Date).AddSeconds(180)
    $dateFound = $false
    do {
        foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $dateCondition)) {
            $parsedDate = [datetime]::MinValue
            if ([datetime]::TryParse($element.Current.Name, [ref]$parsedDate)) {
                $dateFound = $true
                break
            }
        }
        if (-not $dateFound) { Start-Sleep -Milliseconds 500 }
    } while (-not $dateFound -and (Get-Date) -lt $deadline)
    if (-not $dateFound) { throw 'A package release date was not displayed.' }
    Invoke-Element (Find-ElementByAutomationId $root 'ReleaseDateHeader')

    Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $appProcess.Id -ErrorAction SilentlyContinue
    $appProcess = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) `
        -ArgumentList 'unigetui://showDiscoverPage' -PassThru
    $dateHeader = Find-ElementByAutomationId $root 'ReleaseDateHeader' -TimeoutSeconds 120
    if ($dateHeader.Current.ProcessId -ne $appProcess.Id) {
        throw 'The release date column did not remain enabled after restart.'
    }

    $toolsBefore = (& dotnet tool list --global | Out-String)
    $packageLink = 'unigetui://package?manager=dotnet-tool&id=dotnetsay&version=2.1.4'
    Write-Host 'Opening a package link in the running app'
    $redirect = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) `
        -ArgumentList $packageLink -PassThru
    $details = Find-ElementByAutomationId ([System.Windows.Automation.AutomationElement]::RootElement) `
        'PackageDetailsName' -TimeoutSeconds 180
    if ($details.Current.Name -notmatch '(?i)dotnetsay') {
        throw 'The running app did not open the requested package.'
    }
    Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $appProcess.Id -ErrorAction SilentlyContinue

    Write-Host 'Opening a package link at startup'
    $appProcess = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) `
        -ArgumentList $packageLink -PassThru
    $details = Find-ElementByAutomationId ([System.Windows.Automation.AutomationElement]::RootElement) `
        'PackageDetailsName' -TimeoutSeconds 180
    if ($details.Current.ProcessId -ne $appProcess.Id -or $details.Current.Name -notmatch '(?i)dotnetsay') {
        throw 'The app did not open the requested package at startup.'
    }
    $toolsAfter = (& dotnet tool list --global | Out-String)
    if ($toolsBefore -ne $toolsAfter) {
        throw 'Opening a package link changed the installed dotnet tools.'
    }

    Write-Host 'WinUI UI E2E passed: settings, tray icon control, CSV toolbar, and package links.'
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
