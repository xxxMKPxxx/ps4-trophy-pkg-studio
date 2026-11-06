# PS4 Trophy Pkg Studio

给 PS4 游戏生成「奖杯解锁器」PKG 的 Windows 图形工具。解锁通过系统奖杯管理器
（`sceNpTrophyUnlockTrophy`）完成，带真实时间戳与完成率；支持解锁顺序表（按秒控制节奏）、
中文界面、图标合成，并对多组 DLC 奖杯游戏做了自动适配。

> ⚠️ **注意**：只影响本机数据。奖杯一旦与 PSN 同步即不可逆，动手前建议断网并备份
> `/user/home/<uid>/trophy/`。本项目仅供学习研究，请自行承担使用风险。

## `pkg-studio/` —— 主工具（推荐）

独立 WinForms 应用。

- 左侧表格：把游戏 `pkg` **直接拖进来**（支持多选）或点「导入 PKG」，自动解包并读 `param.sfo`
- 按 `CATEGORY` 分类：`gd` = 游戏（可打包）、`gp` = 更新、`ac` = DLC。
  后两种会标红并注明「不可用」，因为奖杯包只能从本体生成
- 顶部可设输出目录和模板目录；「扫描 FTP」把 PS4 上的游戏以同一张表列出
  （同时识别 `CUS*` 游戏与 `XDPX*` 旧解锁器 —— **旧解锁器可直接当提取源再出新包**，
  原游戏 PKG 删了也不用重新下载）
- 打包只做**等长就地替换**（`TITLE_ID` 9 字符、`CONTENT_ID` 里的同段 9 字符），
  不重建 param.sfo 结构 —— 整体重写会导致 PS4 拒装（CE-34629）
- **多组 DLC 奖杯保护**：自动统计 TRP 里的奖杯组数（`GRxxx.PNG` 图标），单组游戏正常
  打包 `trophy00.trp`；多组游戏自动不带 TRP，解锁器注册到主机上已生成的奖杯集
  （需该游戏在主机上启动过一次）。多组游戏带 TRP 出包可能导致主机奖杯库出现
  「空记录」，进而全主机奖杯操作报 NP-32339-0
- 顺序表文件按 `trophy_order_<TITLE_ID>.csv` 命名放在输出目录即随包打入

构建：`.github/workflows/studio.yml` 在 `windows-latest` 上编译，并把上游
`TrophyUnlocker/Resources` 的 `.gp4` 工程与 `icon1.png` 等复制到产物目录的 `template/` 下。
**`orbis-pub-cmd.exe` 与 `CONTENTS.zip`（Sony SDK 专有文件）不随本仓库的产物分发**，
首次使用请按产物里 `template/README.txt` 的指引自行从上游
[PS4_Tools](https://github.com/xXxTheDarkprogramerxXx/PS4_Tools) 仓库获取并放入 `template/`。
下载 artifact **PkgStudio** 即可开始。

## `unlocker-app/` —— 解锁器本体（C，随 PKG 走）

用 OpenOrbis 工具链构建的独立 pkg 应用：`VideoOut` 出画面、`Pad` 读手柄、
`FreeType` 画中文字，链接官方 `libSceNpTrophy` stub。
构建产物为 SCE ELF（`create-fself -out=`），塞进 pkg 当 `eboot.bin`。

游戏内按键：

| 按键 | 作用 |
|---|---|
| ↑ / ↓ | 移动光标 |
| L2 / R2 | 上下翻页 |
| `X` | 解锁光标处的奖杯 |
| `L1 + ○` | 一键解锁（按顺序表逐个；显示 **当前奖杯 ID**、名称、进度条和真实秒数倒计时；运行中再按 = 停止） |
| `△` | 刷新解锁状态 |
| `○` | **不再退出程序**；单独按只弹一条提示，完成界面里仍可退回列表 |

> **关闭应用**：按 **PS 键 → 关闭应用程序**。PS4 的 big app 自己从 `main` 返回会被
> 内核当成异常终止（弹 CE-34878-0），所以界面是常驻的，程序绝不主动 return；
> 启动失败时才会调 `sceSystemServiceKillApp(appid, -1, 0, 0)`（不生成崩溃报告）自行关闭。

### 解锁顺序表

PkgStudio 右键游戏 →「解锁顺序表」，改完生成 PKG，表会随包写进 `/app0/trophy_order.csv`。
一行一个（**延迟单位是秒**）：

```
1,2      # 奖杯ID,延迟秒
2,2
0,5      # 0 号通常是白金，放最后（实测全收集后系统也会自动授予白金，可不写）
```

- **没有这个文件**：按奖杯 ID 顺序逐个解锁，默认每个间隔 1 秒，0 号白金自动放最后
- **有这个文件**：按文件顺序解锁，每个之后等对应**秒数**；已解锁的自动跳过。
  不写延迟默认 1 秒，上限 600 秒。计时用系统单调时钟，1 秒就是真实的 1 秒
- **中途退出**：下次启动会跳过已解锁的，从第一个未解锁的立即开始，然后按延迟继续
- 界面字体 `font.ttf` 会一起打进 PKG，中文奖杯名可正常显示

### PKG 显示名与图标

- **显示名**：`奖杯：<游戏名>`。`param.sfo` 里所有语言的 `TITLE` / `STITLE` 字段就地加前缀，
  只写数据区并更新 `used_len`，不重建 SFO 表结构（避免 CE-34629）；字段空间不够时按 UTF-8
  字符边界裁剪，不会出现半个汉字。标题已带前缀时自动跳过（支持从旧解锁器再出包）
- **图标**：用游戏自己的 `icon0.png` 作底图，中间叠透明奖杯图标（`template/icon1.png`），
  输出 512×512 不带 alpha 的 PNG。本地 PKG 直接从条目表取 `ICON0_PNG`，FTP 导入则在打包时
  从 `appmeta/<TITLE_ID>/icon0.png` 下载；取不到时退回模板自带图标

## `trophy-unlocker-patch/` —— 给上游 TrophyUnlocker 的补丁（历史方案）

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

补丁要点：

1. **唯一 Title ID** —— 每个游戏派生 `XDPX#####`，多个 Unlocker 可共存
2. **文件名保留非英文** —— 原实现只允许 `0-9A-Za-z`，中日韩标题会被剥空成 `\.pkg`
3. **修正 csproj 引用** —— 上游仓库源码不完整（缺 `PS4_Tools.csproj` 工程、
   `PARAM.SFO` 的 `HintPath` 是作者本机绝对路径），`apply-patch.ps1 -ReferenceDir`
   改指向 `TrophyUnlocker\bin\Release` 里的预编译 DLL
4. **FTP 端口 1337 → 2121**，扫描目录扩到 `appmeta` 全部位置并写详细日志
5. **SFO 标题按 UTF-8 重写**（`SfoUtil.cs`）—— 默认关闭（实测重写会让 PS4 拒装 CE-34629）

## 目录结构

| 目录 | 内容 |
|---|---|
| `pkg-studio/` | 主工具（WinForms / C#） |
| `unlocker-app/` | 解锁器本体（C，OpenOrbis 工具链，随 PKG 分发） |
| `trophy-unlocker-patch/` | 给上游 PS4_Tools TrophyUnlocker 的补丁（历史方案） |
| `ps4-trophy-plugin/` | 早期 GoldHEN 插件方案（已弃用，仅保留源码） |

## 致谢与引用

本项目的实现建立在这些优秀项目与研究成果之上，向原作者致谢：

- **[PS4_Tools](https://github.com/xXxTheDarkprogramerxXx/PS4_Tools)**
  by [xXxTheDarkprogramerxXx](https://github.com/xXxTheDarkprogramerxXx)
  —— `trophy-unlocker-patch` 的补丁对象（TrophyUnlocker 即出自该项目）；
  其 `TrophyUnlocker/Resources`（`CONTENTS.zip`、`.gp4` 工程、`orbis-pub-cmd.exe`）
  是本工具打包模板的来源。**`orbis-pub-cmd` 为 Sony SDK 专有工具，本仓库不分发**，
  请从上游仓库获取。
- **[OpenOrbis PS4 Toolchain](https://github.com/OpenOrbis/OpenOrbis-PS4-Toolchain)**
  —— `unlocker-app` 的编译工具链（clang/lld 交叉编译、`create-fself` 生成 SCE ELF、
  官方 stub 链接方式）。
- **[GoldHEN](https://github.com/alfhen/GoldHEN)**（SiSTRo 及项目贡献者）
  —— 提供内置 FTP（端口 2121）与 fPKG 安装运行环境。
- **PS4 homebrew 社区** —— SCE ELF / SELF 格式、`libSceNpTrophy`、param.sfo 结构、
  奖杯数据存储布局等公开研究成果，是本项目所有关键判断的依据。
- **[思源黑体 Source Han Sans](https://github.com/adobe-fonts/source-han-sans)**
  （Adobe 与 Google 联合开发，SIL Open Font License 1.1）—— 解锁器界面中文字体
  （仓库内为 SC Bold，`unlocker-app/assets/font.ttf`，经 FreeType 渲染）。
  简繁全覆盖，可自由再分发。

## License

MIT，见 [LICENSE](LICENSE)。其中引用/衍生自上游项目的组件遵循其原许可证；
`orbis-pub-cmd` 等 Sony SDK 工具不在本仓库分发范围内。
