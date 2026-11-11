#!/bin/sh
# Сборка на Mac/Linux. Нужен .NET 8 SDK: https://dotnet.microsoft.com/download
set -e
cd "$(dirname "$0")"
dotnet publish Pult.Agent/Pult.Agent.csproj -c Release -o out/agent
dotnet publish Pult.Viewer/Pult.Viewer.csproj -c Release -o out/viewer
echo
echo "Готово:"
echo "  out/agent/PultAgent.exe  — ставится на каждый компьютер"
echo "  out/viewer/Pult.exe      — консоль для сервера"
