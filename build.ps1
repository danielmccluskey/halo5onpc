# Local build for h5sololauncher: native DLLs, both test suites and a self-contained publish.
# Same steps as .github/workflows/release.yml, in one command.

$ErrorActionPreference = "Stop"
$rootDir = $PSScriptRoot

# $ErrorActionPreference does not cover native tools: cmake, ctest and dotnet report failure through
# their exit code. Without this check a failed publish leaves the previous output in place and the
# script still reaches [SUCCESS], so the mismatch is only found later, by the launcher rejecting a
# runtime configuration it cannot read.
function Invoke-Step([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

Write-Host "=== Step 1: Setting up environment ===" -ForegroundColor Cyan
if (Test-Path "$env:USERPROFILE\.dotnet") {
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
    $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
}

Write-Host "Using .NET: $(dotnet --version)" -ForegroundColor Green

# Publishing over a running launcher or worker loses the managed half of the build: the files are
# locked, the copy fails and what stays behind is the previous build.
$busy = @(Get-Process h5sololauncher, h5sololauncher.Worker, halo5forge -ErrorAction SilentlyContinue)
if ($busy.Count -gt 0) {
    throw "Close these before building, they lock the published files: $(($busy | ForEach-Object { '{0} ({1})' -f $_.ProcessName, $_.Id }) -join ', ')."
}

Write-Host "`n=== Step 2: Configuring & Compiling C++ Native DLLs ===" -ForegroundColor Cyan
$cmakeBuildDir = Join-Path $rootDir "artifacts/forge-reader-build"
Invoke-Step "CMake configure" { cmake -S (Join-Path $rootDir "src/h5sololauncher.ForgeReader") -B $cmakeBuildDir -G "Visual Studio 17 2022" -A x64 }
Invoke-Step "CMake build" { cmake --build $cmakeBuildDir --config Release }

Write-Host "`n=== Step 3: Running CTest Native Tests ===" -ForegroundColor Cyan
Invoke-Step "Native tests" { ctest --test-dir $cmakeBuildDir -C Release --output-on-failure }

Write-Host "`n=== Step 4: Running .NET Unit Tests ===" -ForegroundColor Cyan
Invoke-Step "Managed tests" { dotnet test (Join-Path $rootDir "h5sololauncher.slnx") -c Release }

Write-Host "`n=== Step 5: Publishing Self-Contained Launcher ===" -ForegroundColor Cyan
$publishDir = Join-Path $rootDir "artifacts/local-build"
Invoke-Step "Publish" { dotnet publish (Join-Path $rootDir "src/h5sololauncher") -c Release -r win-x64 --self-contained true -o $publishDir }

Write-Host "`n=== Step 6: Bundling Native DLLs into Launcher ===" -ForegroundColor Cyan
Copy-Item (Join-Path $cmakeBuildDir "Release/*.dll") $publishDir -Force

# The managed and native halves share wire formats, so a published folder holding one without the
# other starts and then fails inside Forge. Compare what was just built against what was published.
$built = @(Get-ChildItem (Join-Path $cmakeBuildDir "Release") -File -Filter 'h5sololauncher.*.dll')
$mismatched = @(foreach ($dll in $built) {
    $copy = Join-Path $publishDir $dll.Name
    if (!(Test-Path -LiteralPath $copy)) { $dll.Name; continue }
    $there = Get-Item -LiteralPath $copy
    if ($there.Length -ne $dll.Length -or $there.LastWriteTimeUtc -ne $dll.LastWriteTimeUtc) { $dll.Name }
})
if ($mismatched.Count -gt 0) { throw "These native DLLs did not reach the published folder: $($mismatched -join ', ')." }

Write-Host "`n[SUCCESS] Local build complete!" -ForegroundColor Green
Write-Host "Output executable: $publishDir\h5sololauncher.exe" -ForegroundColor Green
