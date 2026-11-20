param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $Root "publish\$Runtime"
}

$running = Get-Process -Name "PersonalLauncher" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running PersonalLauncher instance(s) before publishing..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

Write-Host "Restoring with locked mode..."
dotnet restore (Join-Path $Root "PersonalLauncher.slnx") --locked-mode

Write-Host "Building solution ($Configuration)..."
dotnet build (Join-Path $Root "PersonalLauncher.slnx") -c $Configuration --no-restore

Write-Host "Running automated tests..."
dotnet test (Join-Path $Root "PersonalLauncher.slnx") -c $Configuration --no-build

Write-Host "Publishing self-contained $Runtime build to $OutputDir..."
dotnet publish (Join-Path $Root "src\PersonalLauncher.App\PersonalLauncher.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $OutputDir

# Strip debug symbols and unused LibVLC architectures for a clean, lean distribution
Get-ChildItem -Path $OutputDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($unusedArch in @("win-x86", "win-arm64")) {
    if ($unusedArch -ne $Runtime) {
        $archDir = Join-Path $OutputDir "libvlc\$unusedArch"
        if (Test-Path $archDir) {
            Remove-Item -Path $archDir -Recurse -Force
        }
    }
}

Write-Host "Done! Output: $OutputDir"
