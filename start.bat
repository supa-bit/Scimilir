@echo off
rem Double-click this file to start Scimilir.
cd /d "%~dp0"
title Scimilir

where dotnet >nul 2>nul || goto :need_dotnet
dotnet --list-sdks | findstr /b "10." >nul || goto :need_dotnet

echo Starting Scimilir... The first start takes a minute or two.
dotnet run --project Scimilir -c Release
echo.
echo Scimilir has stopped.
pause
exit /b

:need_dotnet
echo Scimilir needs the .NET 10 SDK, which is not installed yet.
echo Your browser will now open the download page. Under ".NET 10", download the
echo SDK for Windows x64, install it, then double-click start.bat again.
start "" https://dotnet.microsoft.com/download/dotnet/10.0
pause
