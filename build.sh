#!/usr/bin/env bash
# CadSmart 一键脚本：构建 / 测试 / bench / 发布打包
# 用法:
#   ./build.sh build   构建解决方案
#   ./build.sh test    跑全部测试
#   ./build.sh bench   跑模拟器自动化测试
#   ./build.sh publish 发布（模拟器/引擎服务/WPF 到 dist/）
set -e
DOTNET="${DOTNET:-$HOME/dotnet-sdk/dotnet}"
ROOT="$(cd "$(dirname "$0")" && pwd)"

ACTION="${1:-build}"

case "$ACTION" in
  build)
    "$DOTNET" build "$ROOT/CadSmart.sln" -v q
    ;;
  test)
    "$DOTNET" test "$ROOT/tests/CadSmart.Core.Tests/CadSmart.Core.Tests.csproj" -v q --nologo
    ;;
  bench)
    "$DOTNET" run --project "$ROOT/src/CadSmart.Simulator/CadSmart.Simulator.csproj" -- --bench
    ;;
  publish)
    OUT="$ROOT/dist"
    rm -rf "$OUT"
    "$DOTNET" publish "$ROOT/src/CadSmart.Simulator/CadSmart.Simulator.csproj" -c Release -o "$OUT/simulator"
    "$DOTNET" publish "$ROOT/src/CadSmart.Server/CadSmart.Server.csproj" -c Release -o "$OUT/server"
    "$DOTNET" publish "$ROOT/src/CadSmart.Simulator.Wpf/CadSmart.Simulator.Wpf.csproj" -c Release -o "$OUT/wpf-simulator"
    echo "已发布到 dist/: simulator / server / wpf-simulator"
    ;;
  *)
    echo "未知动作: $ACTION（build/test/bench/publish）"
    exit 1
    ;;
esac
