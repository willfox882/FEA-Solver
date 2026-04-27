@echo off
title FEA Solver
cd /d "C:\Users\willf\OneDrive - University of Victoria\0. Personal\PROJECTS\FEA SOLVER"
echo Starting FEA Solver...
dotnet run --project "src/FEASolver.App/FEASolver.App.csproj" --configuration Release
if %ERRORLEVEL% neq 0 (
    echo.
    echo FEA Solver exited with error code %ERRORLEVEL%.
    pause
)
