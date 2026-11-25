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

function Open-ColumnToggle {
    param(
        [System.Windows.Automation.AutomationElement] $Root,
        [System.Windows.Automation.ExpandCollapsePattern] $OrderMenu,
        [string] $Column
    )

    if ($OrderMenu.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $OrderMenu.Expand()
    }
    $columnsMenu = Find-ElementByAutomationId $Root 'ColumnsMenu'
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

    $item = Find-ToggleByAutomationId $Root "Column$Column"
    return [System.Windows.Automation.TogglePattern]$item.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern
    )
}

function Set-ColumnVisibility {
    param(
        [System.Windows.Automation.AutomationElement] $Root,
        [string] $Column,
        [bool] $Visible
    )

    $expected = if ($Visible) { [System.Windows.Automation.ToggleState]::On } else {
        [System.Windows.Automation.ToggleState]::Off
    }
    $orderButton = Find-ElementByAutomationId $Root 'OrderByButton'
    $orderMenu = [System.Windows.Automation.ExpandCollapsePattern]$orderButton.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern
    )
    $toggle = Open-ColumnToggle $Root $orderMenu $Column
    $current = $toggle.Current.ToggleState
    Write-Host "Column '$Column' menu state is $current; setting $expected and checking the UI."
    if ($current -ne $expected) {
        $toggle.Toggle() # Invokes the item and dismisses the flyout.
    }
    else {
        $orderMenu.Collapse()
    }

    $deadline = (Get-Date).AddSeconds(10)
    while ($orderMenu.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        if ((Get-Date) -ge $deadline) {
            throw "Order By flyout did not close after setting column '$Column' to $expected."
        }
        Start-Sleep -Milliseconds 100
    }
}

function Wait-ColumnVisibility {
    param(
        [System.Windows.Automation.AutomationElement] $Root,
        [string] $AutomationId,
        [bool] $Visible,
        [int] $TimeoutSeconds = 30
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $headers = @()
        try {
            $headers = @($Root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition
            ))
        }
        catch {
        }

        $visibleHeader = $null
        foreach ($header in $headers) {
            try {
                if ((-not $header.Current.IsOffscreen) -and
                    ($header.Current.BoundingRectangle.Width -gt 1)) {
                    $visibleHeader = $header
                    break
                }
            }
            catch {
            }
        }

        if ($Visible -and $visibleHeader) {
            return $visibleHeader
        }
        if (-not $Visible -and ($headers.Count -eq 0 -or -not $visibleHeader)) {
            if ($headers.Count -eq 0) {
                return $null
            }
            return $headers[-1]
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    $expected = if ($Visible) { 'visible' } else { 'hidden' }
    $states = foreach ($header in $headers) {
        try {
            "$($header.Current.IsOffscreen)/$($header.Current.BoundingRectangle.Width)"
        }
        catch {
            'stale'
        }
    }
    Write-Host "UI column '$AutomationId' timeout; candidates=$($headers.Count); states=$($states -join ',')"
    throw "UI column '$AutomationId' did not become $expected."
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

function Search-DiscoverForDotnetsay {
    param([System.Windows.Automation.AutomationElement] $Root)

    $query = Find-ElementByAutomationId $root 'MegaQueryBlock' -TimeoutSeconds 120
    $value = [System.Windows.Automation.ValuePattern]$query.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern
    )
    $value.SetValue('dotnetsay')
    Invoke-Element (Find-ElementByAutomationId $root 'MegaFindButton')
}

$appProcess = $null
try {
    Get-Process -Name UniGetUI -ErrorAction SilentlyContinue | Stop-Process -Force

    $executable = Find-Executable
    Write-Host 'Checking standalone Agent Skills CLI before launching WinUI'
    $skillsHelpOutput = [System.IO.Path]::GetTempFileName()
    try {
        $skillsHelp = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) `
            -ArgumentList @('skills', '--help') -RedirectStandardOutput $skillsHelpOutput -PassThru
        if (-not $skillsHelp.WaitForExit(25000)) {
            $skillsHelp.Kill()
            throw 'Standalone skills --help did not exit.'
        }
        $help = Get-Content -LiteralPath $skillsHelpOutput -Raw
        if ($skillsHelp.ExitCode -ne 0 -or $help -notmatch 'Manage Skills:') {
            throw "Standalone skills --help did not return usage (exit $($skillsHelp.ExitCode))."
        }
    }
    finally {
        Remove-Item -LiteralPath $skillsHelpOutput
    }

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

    Write-Host 'Verifying Agent Skills remains opt-in'
    Invoke-Element (Find-ElementByAutomationId $window 'AgentSkillsSettingsEntry' -TimeoutSeconds 120)
    $skillsSwitch = Find-ToggleByAutomationId $window 'AgentSkillsEnabled'
    $skillsToggle = [System.Windows.Automation.TogglePattern]$skillsSwitch.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern
    )
    if ($skillsToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw 'Agent Skills was enabled by default.'
    }
    $skillsToggle.Toggle()
    $libraryView = Find-ElementByAutomationId $window 'AgentSkillsMode'
    $typeFilter = Find-ElementByAutomationId $window 'AgentSkillsTypeFilter'
    [void](Find-ElementByAutomationId $window 'AgentSkillsToolFilter')
    $deadline = (Get-Date).AddSeconds(45)
    while (-not $libraryView.Current.IsEnabled -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (-not $libraryView.Current.IsEnabled) {
        throw 'The unified Agent Skills library did not finish opening.'
    }
    Select-LastComboBoxItem $libraryView
    $selection = [System.Windows.Automation.SelectionPattern]$libraryView.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern
    )
    if ($selection.Current.GetSelection()[0].Current.Name -ne 'Sources') {
        throw 'Sources must be the last unified library view, with no separate Plugins view.'
    }
    if ($typeFilter.Current.IsOffscreen) {
        throw 'The Type filter must remain available on Sources.'
    }
    $skillsToggle.Toggle()
    if ($skillsToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw 'Agent Skills could not be disabled.'
    }
    Invoke-Element (Find-ElementByAutomationId $window 'SettingsBackButton')

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

    Write-Host 'Opening Manager Health from Settings'
    Invoke-Element (Find-ElementByAutomationId $window 'SettingsBackButton')
    Invoke-Element (Find-ElementByAutomationId $window 'ManagersSettingsShortcut')
    Invoke-Element (Find-ElementByAutomationId $window 'ManagerHealthEntry')
    [void](Find-ElementByAutomationId $window 'ManagerHealthFilter')
    [void](Find-ElementByAutomationId $window 'ManagerHealthCheckAgain')

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
    $redirect = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) `
        -ArgumentList 'unigetui://showDiscoverPage' -PassThru
    try {
        Wait-Process -Id $redirect.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    catch {
    }
    Start-Sleep -Milliseconds 1000
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    [void](Find-ElementByAutomationId $root 'MegaQueryBlock' -TimeoutSeconds 120)
    Set-ColumnVisibility $root 'ReleaseDate' $true
    Search-DiscoverForDotnetsay $root
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
    Invoke-Element (Wait-ColumnVisibility $root 'ReleaseDateHeader' $true -TimeoutSeconds 60)

    Write-Host 'Checking the New version column on Updates'
    $redirect = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) `
        -ArgumentList 'unigetui://showUpdatesPage' -PassThru
    try {
        Wait-Process -Id $redirect.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    catch {
    }
    Start-Sleep -Milliseconds 1000
    [void](Wait-ColumnVisibility $root 'NewVersionHeader' $true -TimeoutSeconds 120)
    Set-ColumnVisibility $root 'NewVersion' $false
    [void](Wait-ColumnVisibility $root 'NewVersionHeader' $false -TimeoutSeconds 30)
    Set-ColumnVisibility $root 'NewVersion' $true
    [void](Wait-ColumnVisibility $root 'NewVersionHeader' $true -TimeoutSeconds 30)

    Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $appProcess.Id -ErrorAction SilentlyContinue
    $appProcess = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) `
        -ArgumentList 'unigetui://showDiscoverPage' -PassThru
    Search-DiscoverForDotnetsay $root
    $dateHeader = Wait-ColumnVisibility $root 'ReleaseDateHeader' $true -TimeoutSeconds 120
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
