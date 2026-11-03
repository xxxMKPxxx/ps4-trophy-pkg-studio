#!/usr/bin/env bash
# 在 Ubuntu / WSL 上一次性构建 trophy_unlocker.prx
#
# 用法：
#   bash build-ubuntu.sh
#
# 首次会下载 OpenOrbis 工具链（约 100MB）并克隆 GoldHEN Plugin SDK，之后可增量编译。

set -euo pipefail
cd "$(dirname "$0")"

echo "[1/4] 安装依赖"
sudo apt-get update -qq
sudo apt-get install -y -qq llvm lld curl git build-essential

echo "[2/4] 准备 OpenOrbis 工具链"
if [ ! -d OpenOrbis/PS4Toolchain ]; then
    # 官方 GoldHEN CI 用的就是这个打包好的 toolchain.tar.gz
    curl -sL https://github.com/illusion0001/OpenOrbis-PS4-Toolchain/releases/latest/download/toolchain.tar.gz | tar xz -C .
fi
export OO_PS4_TOOLCHAIN="$PWD/OpenOrbis/PS4Toolchain"
echo "    OO_PS4_TOOLCHAIN=$OO_PS4_TOOLCHAIN"

echo "[3/4] 构建 GoldHEN Plugin SDK（需要 crtprx.o 和 libGoldHEN_Hook.a）"
if [ ! -d SDK ]; then
    git clone --depth 1 https://github.com/GoldHEN/GoldHEN_Plugins_SDK.git SDK
fi
export GOLDHEN_SDK="$PWD/SDK"
make -C SDK PRINTF=1
echo "    GOLDHEN_SDK=$GOLDHEN_SDK"

echo "[4/4] 编译插件"
make clean
make

echo
if [ -f trophy_unlocker.prx ]; then
    ls -l trophy_unlocker.prx
    echo
    echo "装法："
    echo "  1) 把 trophy_unlocker.prx 拷到 PS4 的 /data/GoldHEN/plugins/"
    echo "  2) 在 /data/GoldHEN/plugins.ini 里加一行： trophy_unlocker"
    echo "  3) 重启 GoldHEN 后进游戏，按 L3+R3 呼出"
else
    echo "构建失败：没有生成 trophy_unlocker.prx" >&2
    exit 1
fi
