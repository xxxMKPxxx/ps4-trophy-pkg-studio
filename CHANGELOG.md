# 更新日志

本文件同时是 GitHub Release 的更新说明来源：发布 `v*` 标签时，工作流会取对应章节作为 Release 正文。

## [未发布]

### v1.0.8 修复：连接解锁点击后闪退
- SQLite 打开改用直接 Windows 路径（原先的 `file:C:/...` 缺 `///`，被当成相对路径打不开，
  异常在 UI 回调里未捕获直接崩进程）
- 「连接并读取」的 UI 回调加 try-catch，出错改为弹窗+日志，不再闪退
- 全局异常兜底：任何未处理异常写入 `%TEMP%\pkgstudio-crash.log` 并显示详情

新增**连接解锁**功能（主界面蓝色按钮）：直连 PS4 的 GoldHEN FTP（端口 2121），
读取 `/user/home/<uid>/trophy/db/trophy_local.db`，给奖杯写入**自定义解锁时间戳**——
不需要打包 PKG、不需要调主机时钟、不需要碰加密的 `trophy.img`。
- 选游戏 → 选奖杯 → 手动填本机日期时间（自动转 UTC）→ 写入，或导入 CSV
  （格式 `ID,YYYY-MM-DD HH:mm:ss`，表头自动跳过）
- 只改 `tbl_trophy_flag` 的 `unlocked`/两个时间字段 + `tbl_trophy_title` 的进度统计；
  **绝不动 revision / 游戏名 / 身份摘要**（实测改了会被服务判定不符、整体重建为 0）
- 写入前自动在主机留 `.bak-trophyedit-<时间戳>` 备份，只处理选中的游戏
- 白金杯默认跳过（全收集后系统自动授予），勾选后可强制写入
- 实测：情热传说后另一游戏 35 杯单组，写入 2024-03-15 时间戳，重启 + 打开奖杯页后保持，UI 正确显示

导入 PKG 时不再要求奖杯文件必须叫 `trophy00.trp`：实测《PROTOTYPE BIOHAZARD》(CUSA06545)
只有 `trophy01.trp` / `trophy02.trp`，鬼泣合集、Uncharted Legacy 等也带多个 TRP，旧版只认
`trophy00` 会把这类包判成缺件、丢掉已读到的 param/nptitle/npbind 转去走 orbis 解包而失败。
现在按 `TROPHY__TROPHY<编号>_TRP` 前缀匹配任意编号、收集全部 TRP，主 TRP 取编号最小的；
`trophy*.trp` 在解包路径里也改成通配。TRP 变为可选：包里没有 TRP 时自动走 no-trophy-trp
（靠主机 `/user/trophy/conf/` 已有定义注册），和合集包的处理一致。

## [v1.0.6] - 2026-11-21

奖杯列表右侧新增**奖杯种类**显示：白金 / 金 / 银 / 铜，按等级用不同颜色。
等级来自 `OrbisNpTrophyDetails.Unk00`（OpenOrbis 头文件里没有命名字段，数值与奖杯库
`tbl_trophy_flag.grade` 一致：1 白金、2 金、3 银、4 铜）；启动时把头 8 个奖杯的
`Unk00 / Unk01` 原始值写进 `/data/trophy_unlocker.log`，方便核对字段位置。

另外放宽 0 号奖杯的限制：**只有确认它是白金（等级 = 1）时才禁止单独解锁、并在一键解锁里
排到最后**；没有白金的游戏、以及**等级读取不到**（多为隐藏奖杯，解锁前不暴露等级，而隐藏
奖杯不可能是白金）的情况，一律按普通奖杯处理 —— 可以直接按 X 解锁，也会按正常顺序进入
一键解锁计划。若系统仍拒绝白金解锁（0x8055160d），会给出明确提示。

## [v1.0.5] - 2026-11-20

解锁器界面在奖杯名称下方增加**奖杯说明**：数据取自 `sceNpTrophyGetTrophyInfo` 的
`TrophyDetails`，换行压成空格，按 UTF-8 字符边界截断并在一行内显示。
已解锁的名称为白色、说明为绿色（亮色）；未解锁的名称和说明都是暗灰色，一眼能分清。
行高由 56 增到 68，一页从 14 条调整为 11 条，保证底部状态栏不被盖住。

## [v1.0.4] - 2026-11-16

界面和出包细节的一轮收拾：包名不再丢中文、图标能从主机上取到、输出目录默认跟着 exe 走。

### 新增

- **包名保留非英文字符**：以前文件名词白名单只留 `A-Z 0-9 空格 - _`，中文标题全被吞掉，出来的包只剩 `_XDPXxxxx.pkg`。现在只替换 Windows 不允许的 `\ / : * ? " < > |`（换成对应全角字符），中文、日文、`™` 等都原样保留。
- **FTP 图标**：打包时按 `/user/appmeta/<TID>/icon0.png` → `/user/appmeta/external/<TID>/icon0.png` → `/mnt/ext0|ext1|usb0|usb1/user/appmeta[/external]/<TID>/icon0.png` 依次找游戏封面，找到就做解锁器图标底图（`appmeta` 里只有 `npbind/nptitle`，以前从那儿取所以永远取不到）。光盘版 / 从未启动过 / 本体已删的游戏仍回退到模板图标。
- **输出目录默认 `<exe 所在目录>\PKG`**，程序启动即创建；输入框支持相对路径（按 exe 目录展开）。

### 变更

- **顶部工具条重做**：拆成两行普通控件，按钮带边框和悬停反馈（原来是一个 ToolStrip，窗口一窄后面的控件就掉进溢出菜单）。`...` 改为「浏览..」，输出路径框可以直接点进去编辑，改动即时生效。
- **模板路径固定**为 exe 同目录的 `template/`，不再提供手动输入（相对路径跟着程序走，换机器不会失效）。
- **Release 包改为完整版**：`orbis-pub-cmd.exe`、`CONTENTS.zip` 等模板文件随包提供，解压即可打包，不需要再去上游仓库自己找。

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
