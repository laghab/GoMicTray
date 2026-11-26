<#
  GoMic Tray launcher (script form).
  Compiles the same C# sources used for GoMicTray.exe in memory and runs the tray app.
  Use this if you can't run the prebuilt exe (e.g. Application Control blocks unsigned executables).
    -Install    start with Windows (adds a Run-key entry)
    -Uninstall  remove autostart
#>
param([switch]$Install, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$dir = Split-Path $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
# Combine the sources into one compilation unit: hoist 'using' lines to the top, then the bodies.
$files  = 'GoMic.cs', 'Ui.cs', 'Program.cs' | ForEach-Object { Get-Content (Join-Path $dir $_) -Raw -Encoding UTF8 }
$usings = $files | ForEach-Object { [regex]::Matches($_, '(?m)^using [^;]+;') | ForEach-Object { $_.Value } } | Sort-Object -Unique
$bodies = $files | ForEach-Object { [regex]::Replace($_, '(?m)^using [^;]+;\r?\n?', '') }
Add-Type -TypeDefinition (($usings -join "`n") + "`n" + ($bodies -join "`n")) `
    -ReferencedAssemblies System.Windows.Forms, System.Drawing -Language CSharp

$ps = (Get-Command powershell.exe).Source
[GoMicTray.Program]::AutostartCommand = "`"$ps`" -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""

[GoMicTray.Program]::LaunchTarget = $ps
[GoMicTray.Program]::LaunchArgs   = "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""

# migrate from the old Startup-folder shortcut used by earlier versions
$oldLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\GoMic Tray.lnk'
if (Test-Path $oldLnk) { Remove-Item $oldLnk -Force; [GoMicTray.TrayApp]::SetAutostart($true) }

if ($Install)   { [GoMicTray.TrayApp]::SetAutostart($true);  Write-Host 'Autostart enabled';  return }
if ($Uninstall) { [GoMicTray.TrayApp]::SetAutostart($false); Write-Host 'Autostart disabled'; return }

[GoMicTray.Program]::Run()
