# 更新日志

本文件同时是 GitHub Release 的更新说明来源：发布 `v*` 标签时，工作流会取对应章节作为 Release 正文。

## [v1.0.3] - 2026-11-12

合集类游戏（一个 Title ID 下挂多个奖杯集）终于能正常用了，另外修掉了界面符号显示不出来的问题。

### 新增

- **合集游戏可选奖杯集**：打包时从 `npbind.dat` 解析出每个 NPCOMMID 与对应的 service label，写成 `/app0/games.csv` 打进包；解锁器读它决定注册哪一个奖杯集，不再写死 `label=0`。
- **界面按 `□` 切换**：弹出「选择奖杯集」面板，↑↓ 选择、X 确认，切完自动重新注册 context、重数奖杯并刷新列表。列表顶部显示「本包 N 个奖杯集 当前 i/N：NPWRxxxxx_00」。
- 读不到 `games.csv` 时按 `npbind.dat` 兜底；两者都读不到会在启动时自动探测 service label，并把 `open` 的真实错误码写进 `/data/trophy_unlocker.log`。

### 修复

- **`CreateContext 0x80551613`：每个奖杯集只能进一次，切回去必失败**。PS4 对同一个 service label 只允许存在一个 context，旧实现每次切换都新建一对，切回去就被拒。现在每个奖杯集注册成功后保存 `ctx/hdl`，切回去直接复用（日志里是 `reuse game` / `reuse label`）。
- **合集包启动即失败（如 CUSA06133 火影三部曲）**：「3 个 commid + 只打包了第一个 commid 的 TRP」会让系统在启动阶段就挂掉，连日志都写不出来。现在 `npbind` 里有多于一个 commid 时一律不打包 `trophy00.trp`，各奖杯集都走主机 `/user/trophy/conf/` 里已有的定义（与一直可用的 CUSA01399 一致）。单 commid 游戏行为不变。
- **界面 `↑ ↓ → ○ △ □` 显示不出来**：v1.0.2 把字体从 OTF 转 TTF 时丢了整个符号区。已从旧字体把 19 个箭头/几何符号字形转换补回，并确认空心符号（`○ □ △ ◇ ☆`）没被填实。

### 注意

- **必须用本版重新打包**：解锁器是打进包里的，v1.0.2 及更早版本打出的包不会带上新逻辑。
- 只能切到主机上已有奖杯定义（`/user/trophy/conf/<NPWR>`）的奖杯集；某一作如果从未启动过，切过去可能注册失败，先在主机上启动一次该作品即可。
- 公开包按合规不含 Sony 专有文件（`orbis-pub-cmd.exe` / `CONTENTS.zip` 等），首次打包前请按 `template/README.txt` 自行补齐。

## [v1.0.2] - 2026-11-10

- 改用模板 `CONTENTS.zip` 里那份已验证可打包的 `param.sfo` 作基底，只替换 TITLE_ID / CONTENT_ID / 显示名，修掉 `img_create` 报 `unexpected Application Version (APP_VER)`（装过补丁的游戏，appmeta 里是补丁版 SFO）。
- 顺带消掉补丁 CONTENT_ID 的 `HP9000-` 前缀隐患。

## [v1.0.1] - 2026-11-10

- FTP 扫描放行 `CATEGORY=gp`（已打补丁的游戏本体），不再当成补丁包静默丢弃。
- 扫描补上外置盘 `system_data/priv/appmeta/external/`，游戏列表从个位数恢复到约 180 条。
- 界面字体改为 TTF（此版转换时丢了符号区，v1.0.3 已补回）。

## [v1.0.0] - 2026-11-06

首个版本：FTP 扫描主机上的游戏 → 选择 → 生成带奖杯解锁器的 PKG。
