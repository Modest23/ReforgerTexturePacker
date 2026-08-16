# Builds ReforgerTexturePacker.exe with the .NET Framework 4.8 compiler (ships with Windows - no SDK needed).
$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $PSScriptRoot "ReforgerTexturePacker.exe"
$src = Join-Path $PSScriptRoot "src"

& $csc /nologo /target:winexe /optimize+ `
    /out:"$out" `
    /win32manifest:"$src\app.manifest" `
    /win32icon:"$src\app.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "$src\*.cs"

if ($LASTEXITCODE -eq 0) { Write-Host "Built: $out" }
