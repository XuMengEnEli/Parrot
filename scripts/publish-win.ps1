# Windows 发布（自包含单文件，目标机免装 .NET）。
# 用法：powershell -File scripts\publish-win.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
dotnet publish src/Parrot.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o publish/win-x64
Write-Host "完成 → publish\win-x64\Parrot.App.exe"
