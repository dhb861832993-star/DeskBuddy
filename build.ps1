# 一键构建 QuickMenu（发布为自包含单文件 exe）
# 需要 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "==> dotnet publish ..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\QuickMenu\QuickMenu.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $root "dist\app")

# 复制使用说明
Copy-Item (Join-Path $root "installer\使用说明.txt") (Join-Path $root "dist\app\使用说明.txt") -Force -ErrorAction SilentlyContinue

Write-Host "==> 完成！产物位于 dist\app\QuickMenu.exe" -ForegroundColor Green
Write-Host "    - 便携安装：双击 installer\安装QuickMenu.bat"
Write-Host "    - 制作安装包：用 Inno Setup 编译 installer\QuickMenu.iss"
