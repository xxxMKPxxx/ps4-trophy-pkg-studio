# PS4 Trophy Tools

## `pkg-studio/` — 新的图形界面（推荐）

独立 WinForms 应用，不再往原工具的窗体里打补丁。

- 左侧表格：把游戏 `pkg` **直接拖进来**（支持多选）或点「导入 PKG」，自动解包并读 `param.sfo`
- 按 `CATEGORY` 分类：`gd` = 游戏（可打包）、`gp` = 更新、`ac` = DLC。
  后两种会标红并注明「不可用」，因为奖杯包只能从本体生成
- 右侧列出生成结果（文件名 / 大小 / 时间），双击定位到文件
- 顶部可设输出目录和模板目录；「扫描 FTP」把 PS4 上的游戏以同一张表列出
- 打包只做**等长就地替换**（`TITLE_ID` 9 字符、`CONTENT_ID` 里的同段 9 字符），
  不重建 param.sfo 结构 —— 之前整体重写会导致 PS4 拒装（CE-34629）

构建：`.github/workflows/studio.yml` 会在 `windows-latest` 上编译，并把上游的
`TrophyUnlocker/Resources`（`CONTENTS.zip`、`.gp4`、`orbis-pub-cmd.exe`）复制到
产物目录的 `template/` 下。下载 artifact **PkgStudio** 即开即用。

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
2. `/data/GoldHEN/plugins.ini` 写**完整路径**：`/data/GoldHEN/plugins/trophy_unlocker.prx`
3. 重启 GoldHEN（重新激活一次 HEN 即可），**再重新启动游戏**

日志写在 `/data/GoldHEN/trophy_unlocker.log`（不用抓 klog）。首次运行会生成
`/data/GoldHEN/trophy_unlocker.cfg`：

```ini
enable=1          # 改成 0 则插件只加载、只写日志，什么都不做（排查卡死用）
selfcontext=0     # 改成 1 才允许在抓不到游戏上下文时自己新建（有风险）
```

### 输入是怎么拿到的

不 detour 游戏函数、不调 `scePadInit`、不调 `scePadOpen`（都会让游戏在启动阶段卡死或
抢不到端口）。改用 `scePadGetHandle()` 拿**游戏已经打开的那个句柄**，只读不写。

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
4. **FTP 端口 1337 → 2121** —— 1337 是老式独立 FTP payload 的端口，GoldHEN 内置 FTP 是 2121
5. **重写扫描逻辑** —— 原来只扫 `/system_data/priv/appmeta/` 一个目录，而且要求
   `param.sfo`、`nptitle.dat`、`npbind.dat`、`TROPHY.TRP` 四个文件全取到、两次解析全成功，
   才 `gamelist.Add()`；任何一步失败那个游戏就无声消失（catch 是空的）。这就是"能连上但列表不全"。
   现在同时扫 `system_data/priv/appmeta/`、`user/appmeta/`、`mnt/ext0|ext1|usb0|usb1/user/appmeta/`，
   按 TitleId 去重，每一条（成功/跳过/原因/目录原始行数）都写进 exe 同目录的 `trophylog.txt`
6. **列表不再重复** —— `RunWorkerCompleted` 原来只 `Items.Add` 从不 `Clear`，每扫一次就再累加一遍；
   同时把 `gamelist.Single(...)` 换成 `FirstOrDefault(...)`，避免同名游戏直接抛异常
7. **应用标题按 UTF-8 写** —— `PARAM_SFO.SaveSFO()` 用系统 ANSI 代码页写字符串，
   `Unlocker for 龍が如く` 这类标题在 XMB 和 pkg 文件名里都是乱码。
   `SfoUtil.cs` 直接按 SFO 二进制结构重写 `TITLE`，写完回读校验，失败则保留原文件。
   **注意：实测重写会让 PS4 拒装（CE-34629），所以默认关闭**，
   需要在 exe 同目录 `unlocker.ini` 写 `sfofix=1` 才启用
8. **手动添加游戏** —— 原程序有手动模式（`pnlManual`），但入口取决于
   `Properties.Settings.Default.AppType`，默认 FTP 所以看不到。
   现在加了两组快捷键：`Ctrl+M` 选一个文件夹，一次读入
   `param.sfo` / `nptitle.dat` / `npbind.dat` / `trophy00.trp` 并加入列表；
   `Ctrl+1 / 2 / 3` 切换 FTP / 手动 / PKG 三个面板。
   目录结构随意：四个文件在根目录、`sce_sys/`、或 `sce_sys/trophy/` 下都能找到

## 注意

只影响本机数据。奖杯一旦与 PSN 同步即不可逆，动手前建议断网并备份
`/user/home/<uid>/trophy/`。

## 解锁顺序表 / 界面（分支 feat/unlock-order）

正式用法是 **pkg 版**：用 PkgStudio 给游戏生成 Unlocker PKG，装到 PS4 上启动，会出现界面。

| 按键 | 作用 |
|---|---|
| ↑ / ↓ | 移动光标 |
| L2 / R2 | 上下翻页 |
| `X` | 解锁光标处的奖杯 |
| `L1 + ○` | 一键解锁（按顺序表逐个；显示 **当前奖杯 ID**、名称、进度条和真实秒数倒计时；运行中再按 = 停止） |
| `△` | 刷新解锁状态 |
| `○` | 退出（完成界面里退回列表）。会显示「正在退出…」，等奖杯数据落盘后由系统关闭应用 |

> **退出机制**：PS4 的 big app 自己从 `main` 返回会被内核当成异常终止（弹 CE-34878-0），
> 所以按 ○ 后程序调用 `sceSystemServiceKillApp(appid, -1, 0, 0)` 让 shell 正常关闭
> （最后一个 0 = 不生成崩溃报告），之后常驻等待、绝不 return。
> 万一系统没自动关掉，画面会提示「请按 PS 键 → 关闭应用程序」，手动关也不会报错。

顺序表：PkgStudio 右键游戏 →「解锁顺序表」，改完生成 PKG，表会随包写进 `/app0/trophy_order.csv`。一行一个（**延迟单位是秒**）：

```
1,2      # 奖杯ID,延迟秒
2,2
0,5      # 0 号通常是白金，放最后
```

- **没有这个文件**：按奖杯 ID 顺序逐个解锁，默认每个间隔 1 秒，0 号白金自动放最后
- **有这个文件**：按文件顺序解锁，每个之后等对应**秒数**；已解锁的自动跳过。不写延迟默认 1 秒，上限 600 秒。计时用系统单调时钟，1 秒就是真实的 1 秒，不受画面帧率影响
- **中途退出**：下次启动会跳过已解锁的，从第一个未解锁的立即开始，然后按延迟继续
- 界面字体 `font.ttf` 会一起打进 PKG，中文奖杯名可正常显示
- 下面这段（`/data/GoldHEN/...` + `L3+R3` 手柄按键）是早期的 GoldHEN 插件方案，已弃用
