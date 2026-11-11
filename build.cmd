@echo off
rem Сборка на Windows. Нужен .NET 8 SDK.
cd /d "%~dp0"
dotnet publish Pult.Agent\Pult.Agent.csproj -c Release -o out\agent || exit /b 1
dotnet publish Pult.Viewer\Pult.Viewer.csproj -c Release -o out\viewer || exit /b 1
echo.
echo Готово: out\agent\PultAgent.exe и out\viewer\Pult.exe
