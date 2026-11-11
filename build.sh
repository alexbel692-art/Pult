#!/bin/sh
# Сборка на Mac/Linux. Нужен .NET 8 SDK: https://dotnet.microsoft.com/download
set -e
cd "$(dirname "$0")"
dotnet publish Shturval.Agent/Shturval.Agent.csproj -c Release -o out/agent
dotnet publish Shturval.Viewer/Shturval.Viewer.csproj -c Release -o out/viewer
echo
echo "Готово:"
echo "  out/agent/ShturvalAgent.exe  — ставится на каждый компьютер"
echo "  out/viewer/Shturval.exe      — консоль для сервера"
