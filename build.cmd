@echo off
rem Сборка на Windows. Нужен .NET 8 SDK.
cd /d "%~dp0"
dotnet publish Shturval.Agent\Shturval.Agent.csproj -c Release -o out\agent || exit /b 1
dotnet publish Shturval.Viewer\Shturval.Viewer.csproj -c Release -o out\viewer || exit /b 1
echo.
echo Готово: out\agent\ShturvalAgent.exe и out\viewer\Shturval.exe
