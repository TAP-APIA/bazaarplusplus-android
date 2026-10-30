# BazaarPlusPlus Android 一体化预览版

一个 APK 同时包含 BepInEx Launcher、.NET / BepInEx 运行环境、Android SQLite 和 BPP 模组包。
安装后首次打开会自动部署或更新，不再需要单独下载 `bpp-modpack.zip`、解压或用电脑 ADB 推送。
游戏本体仍需自行安装，本应用不修改游戏 APK。

<https://github.com/TAP-APIA/bazaarplusplus-android/releases>

游戏窗口允许左右两种横屏方向随设备转动切换；BPP 启动器首页保持自身方向策略。

## 安装与更新

1. 下载 `BepInExLauncher-bpp-android.apk`。
2. 在同一台设备上覆盖安装；vivo 若提示同版本，选择“重新安装”。
   ```powershell
   adb -s <设备ID> install -r .\BepInExLauncher-bpp-android.apk
   ```
3. 打开 **BPP Android**。首次启动会自动解包运行环境并部署内置模组；进度完成后点击
   **启动 The Bazaar（BPP 模组）**。
4. 更新时直接覆盖安装新 APK。不要卸载、不要清除应用数据；`adb install -r` 会保留应用数据。
   若出现 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`，说明新 APK 的签名与设备上已安装版本不同：
   停止更新，先备份，不能靠卸载绕过。
5. 旧版本遗留的 `BepInEx/config`、`modpacks/bpp/config`、`BazaarPlusPlusV6` 历史库和入口位置
   会继续沿用；新版本只更新插件与修补运行时。

安装后的主界面提供：

- **启动 The Bazaar（BPP 模组）**：自动确保内置模组已就绪后启动游戏。
- **重新校验 / 修复模组**：重新校验全部内置文件；损坏或缺失时自动恢复。游戏进程运行时
  会先停止 `:game` 进程再修复。
- **打开原版高级启动器**：保留原 BepInEx Launcher 的模组管理、日志等高级功能。
- **首次启动**：游戏画面会灰几分钟，这是在生成模组文件，不是卡死。进度直接画在游戏窗口上
  （阶段、已用时、interop / Unity 基础库文件数），不是另一个对话框。通知会让游戏进程保持前台，
  请不要划掉通知，也不要切回启动器。
- **导出诊断日志**：打一个 zip，保存到系统「下载」，并打开系统分享面板。包内有设备与屏幕信息、
  启动器和游戏包版本、签名证书 SHA-256、libil2cpp 大小/哈希/ELF 对齐、内置模组部署状态、
  BepInEx / Unity / BPP 日志、logcat、ApplicationExitInfo（含 `:game`）和启动器配置。
  打开启动器就会开始滚动记录 logcat；灰屏或闪退后回到本页再导出，即可带上这次启动。
  读不到的路径记在 `manifest.txt`，不会让启动器崩溃。不含对局历史数据库、录像和签名私钥。
  不新增权限：logcat 只能读到本应用自己的缓冲区，写入「下载」走 Android 10 及以上的 MediaStore。
- **去 GitHub 反馈**：先按同样流程导出诊断 zip 到系统「下载」，然后打开本仓库的 GitHub 新建 issue 页面，
  自动填入中文模板（设备、启动器/内置版本、ABI、诊断包文件名）。需要你手动把「下载」文件夹里的 zip
  拖到页面正文上传；应用不会自动上传任何文件，标题、正文和 URL 只使用 zip 文件名，不写绝对路径、IP 或令牌。
- **检查启动器更新**：一体化首页每次新建时自动检查本仓库的 GitHub Release，也可手动重试。
  包含 `android-v` 标签的预览版，仅提示带有已上传 `BepInExLauncher-bpp-android.apk` 的新版；
  按版本数字比较，不按发布日期或字符串排序。检查最近 100 个 Release，草稿和无安装包版本忽略。
  发现新版后可在 App 内下载 APK，并显示进度；下载由系统服务继续执行，退出首页或重启启动器后仍可查看。
  完成后点击安装，首次需在系统设置中允许 BPP 安装应用，返回后再次点击安装；系统安装界面仍需用户确认。
  安装前核对 APK 大小、包名和版本，签名兼容性由系统安装器验证。失败可重试或打开 GitHub 下载页；不会静默安装或替换上游资源。
  网络失败或 GitHub 限流会显示检查失败，不会阻止启动游戏，也不会误报为“已是最新版”。
  此功能需先安装包含更新检查的新 APK，已发布的 preview.37 不会自动获得该能力。

更新检查回归测试：`python android-port/tools/bpp-launcher/test_launcher_update.py`。
下载状态与安装包校验回归测试：`python android-port/tools/bpp-launcher/test_apk_download.py`。
设置 `JAVA_HOME` 指向 JDK 17+；首次运行下载并校验固定 SHA-256 的 `org.json` JVM 测试依赖，
不将此依赖打入 APK（Android 自带 JSON 实现）。加 `--live` 可另做真实 GitHub 连通性检查。

## 部署目录

```text
/sdcard/Android/data/com.bepinex.android/files/BepInEx_Android/com.tempogames.thebazaar/
  modpacks/bpp/modpack.json
  modpacks/bpp/plugins/        ← 内置 BPP 插件与依赖
  modpacks/bpp/core/           ← 修补的 Il2CppInterop Runtime / HarmonySupport
  modpacks/bpp/config/         ← 只新增或补齐，不改用户配置
  modpacks/bpp/logs/
  BepInEx/plugins/             ← 自动同步的当前运行插件
  BepInEx/core/                ← 自动同步的当前运行核心
  BepInEx/config/              ← 用户配置，更新时保留
  BepInEx/LogOutput.log
```

旧启动器若使用共享存储目录，升级到本 APK 后仍以应用专属目录为准；请先备份旧目录。

## 数据安全边界

内置安装器只拥有以下文件：

- `modpacks/bpp/modpack.json`：首次创建；已有合法文件时只更新 `modCount`，保留 `mods` 等用户字段。
- 清单中列出的 `plugins/**` 与 `core/**` 文件：写入 `modpacks/bpp/` 和 `BepInEx/` 各一份。
- 更新时只删除上一版清单拥有、且内容仍与上一版哈希一致的文件；用户改过或第三方文件不动。
- `.cfg` 只做缺失补齐和已有配置同步，不覆盖已有内容；`BazaarPlusPlusV6` 历史库不读取、不写入、不删除。

整个安装先解压到私有暂存目录并逐文件校验大小与 SHA-256，全部通过后才写入目标位置；
每个文件使用临时文件 + 原子替换。安装完成标记只有在校验全部通过后写入；中途失败会保持
“未完成”状态，下次打开或修复时重试，且不会启动游戏。进程内锁和文件锁防止重复安装。

## 使用说明

- 触屏入口为深色金边竖排 BPP / 配装 / 图鉴 / 历史。长按 BPP 后拖动可移动整条入口，位置随
  `BepInEx/config/BazaarPlusPlus.cfg` 保存。任一 BPP 面板打开时入口隐藏，关闭后恢复。
- 浮动入口底部的“字号”按钮循环切换面板文字与控件大小：100% / 150% / 200% / 250%。
  关闭面板后调整，再打开即可查看。
  偏好保存在同一配置的 `[OverlayUI] ScalePercent`，重启和覆盖更新后保留；只影响 BPP 图鉴、
  历史和配装面板，不改游戏本体、原生卡牌说明或字幕已有的字号设置。
- 超大字号下，配装页的赞助名单可独立上下滑动，关闭、更新阵容库和翻页按钮保持可见。
- 启动器的可滚动内容避让状态栏、导航栏和屏幕开孔；横竖屏切换后重新读取系统安全边距。
- 游戏进程通过 Android UI 线程单独隐藏系统栏并延伸到屏幕开孔区域，启动器仍保留状态栏避让。
  Pad 上已验证 Unity 渲染面达到物理显示的 2800×1968，系统栏手势后也能恢复。
- 已验证的 HarmonyOS NEXT ANCO 设备会在 Unity Activity 交接时调用 `UnityPlayer.kill()`；启动器
  检测到 ANCO 系统属性后自动启用已有的 kill 拦截，普通 Android 仍沿用高级设置中的选择。
- 应用图标使用《大巴扎》徽标的白底版本，自适应图标与旧版方形/圆形图标一并替换。
- UI 默认跟随游戏语言，游戏未保存语言时回落系统语言。可在 `[Localization] UiLanguage`
  强制 `Chinese` / `English` / `German` / `Portuguese` / `Korean` / `Italian`。
- 图鉴使用原生右侧说明抽屉；Pad 上已验证卡牌名称、标签、技能说明及品质/附魔切换后的更新。
  返回键先关闭检视，再关闭图鉴，不退出游戏。
- 十胜阵容页点击更新应成功；无进行中对局时推荐为 0/0，不等于阵容库下载失败。
- 日志：上述目录的 `BepInEx/LogOutput.log`。正常启动包含
  `plugin.initialization.succeeded`、`plugin.android.tls.registered` 和
  `live_build_panel.corpus.ready` / `recovered`。

## 从源码构建

前置条件：

- JDK 17（Android Studio 自带 JBR 可用）、Android SDK Build-Tools、`android.jar`；
- Python 3.11；
- .NET SDK；
- 与游戏版本匹配的 Android IL2CPP 代理程序集目录；
- 已验证的基线启动器 `dist/app-bpp-fixed.apk`，以及修补后的
  `safe_Il2CppInterop.Runtime.dll`、`safe_Il2CppInterop.HarmonySupport.dll`。

先编译 Android 插件：

```powershell
dotnet build .\bazaarplusplus-mod\src\BazaarPlusPlus\BazaarPlusPlus.csproj -c Debug `
  -p:BppAndroid=true -p:ManagedPath=<Android代理程序集目录> `
  -p:NuGetAudit=false -p:TreatWarningsAsErrors=false
```

再生成一体化 APK：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\android-port\package-launcher.ps1 -VersionCode <递增版本号>
```

脚本会依次调用 `package-modpack.ps1` 生成最新模组包、编译并运行安装器与 manifest 单元测试、
生成 `classes3.dex`、重写二进制 manifest、嵌入模组清单与 ZIP、进行 16 KB 对齐和同证书签名，
最后逐项校验基线 native 库、原有 DEX 与资源表未改动，资源差异仅限允许替换的图标 PNG。

- `-ModpackZip` 可指定现成 ZIP，跳过重新打包。
- `-KeyStore` / `-KeyAlias` 可改用正式签名；默认使用
  `%USERPROFILE%\.android\debug.keystore` 的 `androiddebugkey`。
- `-VersionCode` 每次发布必须递增；默认 `44`，版本名 `6.0.0-preview.44`
  （preview.43 是 `43`，preview.42 是 `42`）。默认输出
  `dist/BepInExLauncher-bpp-android.apk`。
- `-UnsignedOnly` 只生成对齐后的未签名 APK。

发布时只需上传 APK、本说明和 SHA-256；不要再附带单独的模组 ZIP 或 ADB 部署脚本。

## 不要修改 libpine

`lib/arm64-v8a/libpine.so` 的 `Pine_init0` 若被改成直接 `ret`，会使 `Pine.arch` 未初始化，
在 `ClassLoaderHooks.installHooks` 报 `Unexpected arch 0`，游戏无法进入 Unity。
一体化 APK 保留原始 native 库；打包脚本也会拒绝 `Pine_init0 == ret` 的基线。

## 当前验证与限制

平板（vivo iPA2375 / Android 15）从早期版本一路覆盖安装到 `6.0.0-preview.30`，全程未卸载、
未清除应用数据：`BepInEx/config/BazaarPlusPlus.cfg` 与 `modpacks/bpp/config/BazaarPlusPlus.cfg`
哈希一致，`modpack.json` 的 `createdAt` 保留；字号档位循环测试后配置字节未变化。

已在 Pad 实机验证：
- 游戏渲染面铺满 `2800×1968`，系统栏手势后恢复；启动器仍避让状态栏。
- 图鉴右侧使用游戏原生说明抽屉，卡名、标签、技能文本正常；品质与附魔切换会同步更新大图与说明。
- 系统返回键先关闭卡牌检视，再关闭面板（图鉴/历史/配装均验证），不退出游戏。
- 字号档位 100% / 150% / 200% / 250%；配装空地毯不随字号缩小，左栏可滚动，切换字号并重开面板后
  右侧关闭、更新、翻页控件仍可见。
- 进行中对局里配装面板可打开并读到真实对局数据（候选匹配显示 `1 / 277`）。

当前工作区 Android 纯行为测试 284/284、默认条件纯行为测试 274/274；安装器 Java 测试覆盖首次安装、升级、
配置/历史/第三方文件保留、损坏修复、路径穿越/未列出条目/哈希不符拒绝、未完成状态重试；
manifest 测试覆盖启动 Activity 迁移、原高级界面保留和版本号；打包脚本内置 APK 完整性校验。

本版新增但**尚未完成实机验收**的候选修复：
- 图鉴偶发白图：共享材质不再在面板关闭前被中途销毁，检视大图改用图鉴美术缓存。
- 图鉴左侧大图随字号档位继续放大（上限受左栏可用空间约束）。
- 对局中点击物品打开左侧大图检视，可查看等级与附魔，返回键退出。
- 触控 Dock 的“配装”按钮在战斗中不再被拦截（键盘路径已验证，触控路径待复测）。

仍未完整验收：上传/账号链接/Ghost 在线同步/MP4 录制/回放重复进入；Android 回放自动清理仍关闭。
同卡牌刚关闭后立即再次点击可能遇到空 tooltip，可先切换另一张卡。
大元组返回值的 Harmony detour 在 ARM64 会崩溃，相关增强保持停用。
完整桌面架构门禁仍受桌面产物与游戏程序集限制。
问题与证据：<https://github.com/TAP-APIA/bazaarplusplus-android/issues/15>；一体化安装见 <https://github.com/TAP-APIA/bazaarplusplus-android/issues/17>。

