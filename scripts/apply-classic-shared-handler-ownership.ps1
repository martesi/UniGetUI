$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$path = 'UniGetUI.iss'
$text = [IO.File]::ReadAllText($path)

if ($text.Contains('function ShouldRegisterUniGetUIProtocol(): Boolean;')) {
    Write-Host 'Shared handler ownership logic is already applied.'
    exit 0
}

$codeAnchor = @'
[Code]
procedure InitializeWizard;
'@
$codeReplacement = @'
[Code]
var
  RegisterUniGetUIProtocol: Boolean;
  RegisterPackageBundle: Boolean;

function ShouldRegisterUniGetUIProtocol(): Boolean;
begin
  Result := RegisterUniGetUIProtocol;
end;

function ShouldRegisterPackageBundle(): Boolean;
begin
  Result := RegisterPackageBundle;
end;

procedure InitializeWizard;
'@
if (-not $text.Contains($codeAnchor)) { throw 'Could not locate [Code] anchor.' }
$text = $text.Replace($codeAnchor, $codeReplacement)

$initAnchor = @'
function InitializeSetup: Boolean;
begin
  try
'@
$initReplacement = @'
function InitializeSetup: Boolean;
begin
  // Do not steal shared compatibility handlers from an existing upstream installation.
  RegisterUniGetUIProtocol := not RegKeyExists(HKA, 'Software\Classes\unigetui');
  RegisterPackageBundle :=
    not RegKeyExists(HKA, 'Software\Classes\UniGetUI.PackageBundle') and
    not RegValueExists(HKA, 'Software\Classes\.ubundle', '');

  try
'@
if (-not $text.Contains($initAnchor)) { throw 'Could not locate InitializeSetup anchor.' }
$text = $text.Replace($initAnchor, $initReplacement)

$registryReplacements = [ordered]@{
  'Root: HKA; Subkey: "Software\Classes\unigetui"; ValueType: "string"; ValueData: "URL:UniGetUI Protocol"; Flags: uninsdeletekey; Tasks: regularinstall;' = 'Root: HKA; Subkey: "Software\Classes\unigetui"; ValueType: "string"; ValueData: "URL:UniGetUI Protocol"; Tasks: regularinstall; Check: ShouldRegisterUniGetUIProtocol;'
  'Root: HKA; Subkey: "Software\Classes\unigetui"; ValueType: "string"; ValueName: "URL Protocol"; ValueData: ""; Tasks: regularinstall;' = 'Root: HKA; Subkey: "Software\Classes\unigetui"; ValueType: "string"; ValueName: "URL Protocol"; ValueData: ""; Tasks: regularinstall; Check: ShouldRegisterUniGetUIProtocol;'
  'Root: HKA; Subkey: "Software\Classes\unigetui\DefaultIcon"; ValueType: "string"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: regularinstall;' = 'Root: HKA; Subkey: "Software\Classes\unigetui\DefaultIcon"; ValueType: "string"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: regularinstall; Check: ShouldRegisterUniGetUIProtocol;'
  'Root: HKA; Subkey: "Software\Classes\unigetui\shell\open\command"; ValueType: "string"; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: regularinstall;' = 'Root: HKA; Subkey: "Software\Classes\unigetui\shell\open\command"; ValueType: "string"; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: regularinstall; Check: ShouldRegisterUniGetUIProtocol;'
  'Root: HKA; Subkey: "Software\Classes\.ubundle"; ValueType: string; ValueData: "UniGetUI.PackageBundle"; Flags: uninsdeletekey; Tasks: regularinstall;' = 'Root: HKA; Subkey: "Software\Classes\.ubundle"; ValueType: string; ValueData: "UniGetUI.PackageBundle"; Tasks: regularinstall; Check: ShouldRegisterPackageBundle;'
  'Root: HKA; Subkey: "Software\Classes\UniGetUI.PackageBundle"; ValueType: string; ValueData: {cm:PackageBundleName}; Flags: uninsdeletekey; Tasks: regularinstall;' = 'Root: HKA; Subkey: "Software\Classes\UniGetUI.PackageBundle"; ValueType: string; ValueData: {cm:PackageBundleName}; Tasks: regularinstall; Check: ShouldRegisterPackageBundle;'
  'Root: HKA; Subkey: "Software\Classes\UniGetUI.PackageBundle\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey; Tasks: regularinstall;' = 'Root: HKA; Subkey: "Software\Classes\UniGetUI.PackageBundle\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Tasks: regularinstall; Check: ShouldRegisterPackageBundle;'
  'Root: HKA; Subkey: "Software\Classes\UniGetUI.PackageBundle\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: regularinstall;' = 'Root: HKA; Subkey: "Software\Classes\UniGetUI.PackageBundle\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: regularinstall; Check: ShouldRegisterPackageBundle;'
}
foreach ($entry in $registryReplacements.GetEnumerator()) {
    if (-not $text.Contains($entry.Key)) { throw "Registry anchor not found: $($entry.Key)" }
    $text = $text.Replace($entry.Key, $entry.Value)
}

$tasksAnchor = @'

[Tasks]
'@
$ownershipCode = @'

function ClassicOwnsRegistryCommand(const SubKey: String): Boolean;
var
  Command, ClassicExecutable: String;
begin
  Result := False;
  if RegQueryStringValue(HKA, SubKey, '', Command) then
  begin
    ClassicExecutable := ExpandConstant('{app}\UniGetUI.exe');
    Result := Pos(Lowercase(ClassicExecutable), Lowercase(Command)) > 0;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  BundleProgId: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  // Delete shared handlers only when their command still points to this Classic installation.
  if ClassicOwnsRegistryCommand('Software\Classes\unigetui\shell\open\command') then
    RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\unigetui');

  if ClassicOwnsRegistryCommand('Software\Classes\UniGetUI.PackageBundle\shell\open\command') then
  begin
    RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\UniGetUI.PackageBundle');
    if RegQueryStringValue(HKA, 'Software\Classes\.ubundle', '', BundleProgId) and
       (CompareText(BundleProgId, 'UniGetUI.PackageBundle') = 0) then
    begin
      RegDeleteValue(HKA, 'Software\Classes\.ubundle', '');
      RegDeleteKeyIfEmpty(HKA, 'Software\Classes\.ubundle');
    end;
  end;
end;

[Tasks]
'@
if (-not $text.Contains($tasksAnchor)) { throw 'Could not locate [Tasks] anchor.' }
$text = $text.Replace($tasksAnchor, $ownershipCode)

if ($text -match 'Software\\Classes\\unigetui.*uninsdeletekey') { throw 'Protocol still has unconditional uninstall deletion.' }
if ($text -match 'UniGetUI\.PackageBundle.*uninsdeletekey') { throw 'Bundle ProgID still has unconditional uninstall deletion.' }
if (-not $text.Contains('Check: ShouldRegisterUniGetUIProtocol;')) { throw 'Protocol ownership check missing.' }
if (-not $text.Contains('Check: ShouldRegisterPackageBundle;')) { throw 'Bundle ownership check missing.' }

[IO.File]::WriteAllText($path, $text, $utf8)
