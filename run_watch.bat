@echo off
title Aravals Stream - Live Watch & Hot Reload
cd /d "%~dp0"
echo ========================================================
echo   Starting Aravals Stream with Hot Reload (Auto-Change)
echo ========================================================
echo Restoring NuGet packages...
call dotnet restore "AravalsStream.sln"
if %ERRORLEVEL% neq 0 (
    echo Restore failed. Retrying...
    call dotnet restore "AravalsStream.sln"
)
echo Starting live file watcher...
dotnet watch --project "src/AravalsStream.App/AravalsStream.App.csproj" run
pause
