# PS4 Trophy Tools

两个独立的东西，解决同一件事的两条路线。

## `ps4-trophy-plugin/` — GoldHEN 插件（一个工具管所有游戏，真解锁）

插件被 GoldHEN 注入到正在运行的游戏进程里，进程的 NP Communication ID 天然就是当前游戏，
所以 `sceNpTrophyUnlockTrophy` 走的是系统真正的奖杯管理器：会写 `TROPUSR.DAT`、打时间戳、
计完成率，不是只改 `trophy_local.db` 的那种表面解锁。

原理：dlsym `libSceNpTrophy` → 对 `sceNpTrophyRegisterContext` 装 Detour，偷下游戏自己的
`(context, handle)` 复用 → 调 `sceNpTrophyUnlockTrophy`。

游戏内操作：

| 按键 | 作用 |
|---|---|
| `L3 + R3` | 呼出 / 收起奖杯浏览 |
| 十字键 上/下 | 切换奖杯（横幅显示奖杯名与状态） |
| `X` | 解锁当前 |
| `三角` | 解锁全部 |
| `O` | 收起 |

### 构建

Actions 会自动构建，从 artifact 下 `trophy_unlocker.prx`。

本地构建（WSL / Ubuntu）：

```bash
bash ps4-trophy-plugin/build-ubuntu.sh
```

### 安装

1. `trophy_unlocker.prx` 拷到 `/data/GoldHEN/plugins/`
2. `/data/GoldHEN/plugins.ini` 加一行：`trophy_unlocker`
3. 重启 GoldHEN，进游戏按 `L3+R3`

首次运行先看 klog 有没有 `[trophy] captured trophry context`，有这行才说明 hook 命中。

## `trophy-unlocker-patch/` — 给 PS4_Tools 的 TrophyUnlocker 打补丁

原工具 `newpkgid` 硬编码成 `XDPX30000_00`，所有 Unlocker 抢同一个 Title ID，装第二个就把
第一个顶掉。补丁改成按游戏 Title ID 派生唯一的 `XDPX#####`，多个可以共存。

```bat
git clone https://github.com/xXxTheDarkprogramerxXx/PS4_Tools.git
cd PS4_Tools
build.bat  D:\PS4_Tools
```

也可以直接在 GitHub 上打包：`build windows tool` 这个 workflow 会在
`windows-latest` 上克隆上游仓库、打补丁、用 MSBuild 编译，产物是
artifact **TrophyUnlocker**（整个 `bin\Release` 目录，含运行时依赖 DLL）。

### 为什么需要额外修引用

上游仓库的源码其实是不完整的，干净克隆后直接编译一定失败：

- `ProjectReference` 指向 `..\PS4_Tools\PS4_Tools.csproj`，但仓库里**没有**这个目录
- `PARAM.SFO` 的 `HintPath` 是作者本机的绝对路径（`..\..\..\..\GitHub\PS3Tools\...`）

好在 `TrophyUnlocker\bin\Release` 里提交了预编译的 `PS4_Tools.dll` 和
`PARAM.SFO.dll`。`apply-patch.ps1 -ReferenceDir` 会把这两处引用改指向它们。
另外原项目 target 是 .NET Framework 4.5.2，CI 上直接重定向到 4.8。

`apply-patch.ps1` 可重复执行，会备份为 `.bak`；`selftest.ps1` 和
`selftest-csproj.ps1` 分别验证 Form1 改写和 csproj 引用改写。

### 补丁做了什么

1. **唯一 Title ID** —— 每个游戏派生 `XDPX#####`，多个 Unlocker 可共存
2. **文件名保留非英文** —— 原 `RemoveSpecialCharacters` 只允许 `0-9A-Za-z`，
   中日韩标题会被剥空成 `\.pkg`，`File.Move` 抛异常后被后台线程静默吞掉，
   表现为"这个游戏被跳过"。改为保留 Unicode 字母数字，只剔除 `\ / : * ? " < > |`
3. **修正 csproj 引用** —— 见上一节

## 注意

只影响本机数据。奖杯一旦与 PSN 同步即不可逆，动手前建议断网并备份
`/user/home/<uid>/trophy/`。
