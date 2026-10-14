# install.ps1 -- copies Hand Freeze from dist\ to %LOCALAPPDATA%\Programs\HandFreeze and puts its icon on the desktop.
# Nothing is switched on: the app's ON/OFF button does that. Run again after a rebuild (close the app first).
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-From <folder with the built files>]
param([string]$From = "")
# from a source folder the built files are in dist\; from the download zip they sit next to this script
if ($From -eq "") { $From = if (Test-Path (Join-Path $PSScriptRoot "dist\HandFreeze.exe")) { Join-Path $PSScriptRoot "dist" } else { $PSScriptRoot } }
$ErrorActionPreference = "Stop"
$to = Join-Path $env:LOCALAPPDATA "Programs\HandFreeze"
New-Item -ItemType Directory -Force $to | Out-Null
foreach ($f in "HandFreeze.exe", "hand_freeze_layer.dll", "hand_freeze_layer32.dll", "hand_freeze.ico") {
    if (Test-Path (Join-Path $From $f)) { Copy-Item (Join-Path $From $f) (Join-Path $to $f) -Force }   # the 32-bit layer is new in v3.1
}
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "Hand Freeze.lnk"))
$lnk.TargetPath = Join-Path $to "HandFreeze.exe"
$lnk.WorkingDirectory = $to
$lnk.IconLocation = (Join-Path $to "hand_freeze.ico") + ",0"
$lnk.Description = "Hold both VR controllers still in any OpenXR game"
$lnk.Save()
Write-Output "Installed to $to, icon on the desktop."
