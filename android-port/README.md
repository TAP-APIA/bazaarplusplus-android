# BazaarPlusPlus Android 一体化预览版

一个 APK 同时包含 BepInEx Launcher、.NET / BepInEx 运行环境、Android SQLite 和 BPP 模组包。
安装后首次打开会自动部署或更新，不再需要单独下载 `bpp-modpack.zip`、解压或用电脑 ADB 推送。
游戏本体仍需自行安装，本应用不修改游戏 APK。

<https://github.com/TAP-APIA/bazaarplusplus-android/releases>

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
- UI 默认跟随游戏语言，游戏未保存语言时回落系统语言。可在 `[Localization] UiLanguage`
  强制 `Chinese` / `English` / `German` / `Portuguese` / `Korean` / `Italian`。
- 图鉴可检视卡牌、切换品质；说明层位于抽屉背景之上，保留原生滚动与遮罩。
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
powershell -NoProfile -ExecutionPolicy Bypass -File .\android-port\package-launcher.ps1 -VersionCode 3
```

脚本会依次调用 `package-modpack.ps1` 生成最新模组包、编译并运行安装器与 manifest 单元测试、
生成 `classes3.dex`、重写二进制 manifest、嵌入模组清单与 ZIP、进行 16 KB 对齐和同证书签名，
最后逐项校验基线的 native 库、DEX 与资源未被改动。

- `-ModpackZip` 可指定现成 ZIP，跳过重新打包。
- `-KeyStore` / `-KeyAlias` 可改用正式签名；默认使用
  `%USERPROFILE%\.android\debug.keystore` 的 `androiddebugkey`。
- `-VersionCode` 每次发布必须递增；默认输出 `dist/BepInExLauncher-bpp-android.apk`。
- `-UnsignedOnly` 只生成对齐后的未签名 APK。

发布时只需上传 APK、本说明和 SHA-256；不要再附带单独的模组 ZIP 或 ADB 部署脚本。

## 不要修改 libpine

`lib/arm64-v8a/libpine.so` 的 `Pine_init0` 若被改成直接 `ret`，会使 `Pine.arch` 未初始化，
在 `ClassLoaderHooks.installHooks` 报 `Unexpected arch 0`，游戏无法进入 Unity。
一体化 APK 保留原始 native 库；打包脚本也会拒绝 `Pine_init0 == ret` 的基线。

## 当前验证与限制

平板（vivo iPA2375 / Android 15）覆盖安装 `versionCode 2 → 3`，未卸载、未清除数据：
`BepInEx/config/BazaarPlusPlus.cfg` 与 `modpacks/bpp/config/BazaarPlusPlus.cfg` 哈希前后一致；
`modpack.json` 的 `createdAt` 保留；插件两份哈希一致；游戏启动日志包含
`plugin.initialization.succeeded`，远端阵容库恢复 5,689 套。

Android 纯行为测试 228/228、默认条件 221/221；新增安装器 Java 测试覆盖首次安装、升级、
配置/历史/第三方文件保留、损坏修复、路径穿越/未列出条目/哈希不符拒绝、未完成状态重试；
manifest 测试覆盖启动 Activity 迁移、原高级界面保留和版本号；打包脚本内置 APK 完整性校验。

仍未完整验收：上传/账号链接/Ghost 在线同步/MP4 录制/回放重复进入；Android 回放自动清理仍关闭。
同卡牌刚关闭后立即再次点击可能遇到空 tooltip，可先切换另一张卡。
大元组返回值的 Harmony detour 在 ARM64 会崩溃，相关增强保持停用。
完整桌面架构门禁仍受桌面产物与游戏程序集限制。
问题与证据：<https://github.com/TAP-APIA/bazaarplusplus-android/issues/17>。
