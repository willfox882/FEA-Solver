# setup.ps1
# One-time environment setup for FEA Solver.
# Run from repo root: .\setup.ps1
# Requires: Python 3.11+ on PATH, .NET 8 SDK

param(
    [string]$PythonExe = "python",
    [string]$VenvDir   = ".venv"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "=== FEA Solver Setup ===" -ForegroundColor Cyan
Write-Host ""

# ── 1. Verify Python ────────────────────────────────────────────────────────
Write-Host "[1/5] Checking Python..." -ForegroundColor Yellow
$pyVersion = & $PythonExe --version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Python not found: '$PythonExe'. Install Python 3.11+ and add to PATH."
}
Write-Host "      $pyVersion"

# ── 2. Create virtual environment ───────────────────────────────────────────
Write-Host "[2/5] Creating virtual environment in $VenvDir ..." -ForegroundColor Yellow
if (-not (Test-Path $VenvDir)) {
    & $PythonExe -m venv $VenvDir
} else {
    Write-Host "      (already exists)"
}

$pip = Join-Path $VenvDir "Scripts\pip.exe"
$python = Join-Path $VenvDir "Scripts\python.exe"

# ── 3. Install Python dependencies ──────────────────────────────────────────
Write-Host "[3/5] Installing Python packages..." -ForegroundColor Yellow
& $pip install --upgrade pip --quiet
& $pip install -r src\FEASolver.Scripts\requirements.txt
if ($LASTEXITCODE -ne 0) {
    Write-Error "pip install failed."
}

# Note: pythonocc-core must be installed via conda separately
Write-Host "      NOTE: pythonocc-core requires conda:" -ForegroundColor Magenta
Write-Host "      conda install -c conda-forge pythonocc-core=7.7.2" -ForegroundColor Magenta

# ── 4. Verify tools ──────────────────────────────────────────────────────────
Write-Host "[4/5] Verifying Python environment..." -ForegroundColor Yellow
& $python src\FEASolver.Scripts\verify_tools.py
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Some checks failed. Review output above."
}

# ── 5. Check .NET SDK ────────────────────────────────────────────────────────
Write-Host "[5/5] Checking .NET SDK..." -ForegroundColor Yellow
$dotnet = dotnet --version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Warning ".NET SDK not found. Install from https://dotnet.microsoft.com/download"
} else {
    Write-Host "      .NET $dotnet"
    Write-Host "      Restoring NuGet packages..."
    dotnet restore FEASolver.sln --verbosity quiet
}

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Place ccx.exe (+ DLLs) in tools\ccx\"
Write-Host "  2. Update appsettings.json: set PythonExe to .venv\Scripts\python.exe"
Write-Host "  3. Run: dotnet build FEASolver.sln"
Write-Host "  4. Or open FEASolver.sln in Visual Studio 2022"
Write-Host ""
