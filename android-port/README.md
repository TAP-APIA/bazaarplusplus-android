# BazaarPlusPlus Android 预览版

通过独立 BepInEx Launcher 加载插件，不修改游戏 APK。下载入口：
<https://github.com/TAP-APIA/bazaarplusplus-android/releases>。
这是 Android ARM64 预览版，不是桌面安装器，也不代表所有桌面功能已移植。

## Release 附件

| 文件 | 用途 |
| --- | --- |
| BepInExLauncher-bpp-android.apk | 已在平板验证的启动器，保留原始 native 库，使用测试用 Android Debug 证书签名。 |
| bpp-modpack.zip | 插件及依赖、Android SQLite、完整 BepInEx core 和修补的 Runtime / ARM64 HarmonySupport；不含配置或用户数据。 |
| install-to-device.ps1 | Windows PowerShell 部署脚本，同时更新 modpack 源和实际运行目录。 |
| DEPLOYMENT-zh-CN.md | 本说明的离线副本。 |
| SHA256SUMS.txt | 下载文件的 SHA-256 校验值。 |

游戏本体需自行安装，Release 不包含游戏、账号、配置、历史库、签名私钥或视频素材。
运行部署包不需要 .NET SDK；从源码重建才需要。

## 安装与更新（Windows + ADB）

1. 安装 Android SDK Platform-Tools，将 `adb` 放入 PATH，开启设备 USB 调试或无线调试。
   用 `adb devices -l` 确认设备在线；无线调试先按设备提示配对，再 `adb connect IP:连接端口`。
2. 将上述附件放在同一目录，解压 `bpp-modpack.zip` 到该目录的 `bpp-modpack/`，
   确保其下直接是 `modpack.json`、`plugins/`、`core/`，而不是再套一层目录。
3. 首次安装启动器，或覆盖更新：

   ```powershell
   adb -s <设备ID> install -r .\BepInExLauncher-bpp-android.apk
   ```

   vivo 若询问同版本重新安装，确认覆盖。**不要卸载启动器，也不要清除其应用数据。**
   若出现 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`，说明现有签名不同：停止更新，先保留原安装并备份数据，
   不能靠卸载解决。Debug 签名仅用于当前预览版，不作为正式生产签名。
4. 在启动器授予其请求的文件访问权限，添加/选择已安装的 The Bazaar，启动一次以创建目录，再退出。
5. 在附件目录运行（脚本会停止启动器后更新，不删除配置或历史）：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install-to-device.ps1 -DeviceId <设备ID>
   ```

   多设备时必须指定 `-DeviceId`；`-Adb <adb.exe完整路径>` 可指定工具位置。
   `-WhatIf` 仅检查前置条件并显示目标，不停止应用、不写设备。
   旧启动器若确实使用共享存储目录，显式传 `-StorageRoot /sdcard/BepInEx_Android`；
   不要同时向多个猜测路径复制。默认目录不存在时脚本会停止并提示。
6. 重新打开启动器，选择 The Bazaar 和 **bpp** 模组包，再启动游戏。

默认游戏目录（部署脚本的 `StorageRoot` 参数是其上一级）：

```text
/sdcard/Android/data/com.bepinex.android/files/BepInEx_Android/com.tempogames.thebazaar/
  modpacks/bpp/modpack.json
  modpacks/bpp/plugins/
  modpacks/bpp/core/
  BepInEx/plugins/
  BepInEx/core/
```

启动器会把所选 modpack 应用到运行目录，所以只更新其中一份不够。
Android 文件管理器可能无法访问 `Android/data`；推荐使用上述 ADB 脚本。

## 使用与验证

- 触屏入口为深色金边竖排 BPP / 配装 / 图鉴 / 历史。长按 BPP 后拖动可移动整条入口，位置随配置保存。
  任一 BPP 面板打开期间入口隐藏，关闭后恢复；面板使用整屏安全区，不预留右侧黑缝。
- UI 默认跟随游戏语言，游戏未保存语言时回落系统语言。可在 `BepInEx/config/BazaarPlusPlus.cfg`
  的 `[Localization] UiLanguage` 设为 `Chinese` 或 `English`，默认 `Auto`。
- 图鉴可检视卡牌、切换品质；说明层位于抽屉背景之上，保留原生滚动与遮罩。
- 十胜阵容页点击更新应成功；无进行中对局时推荐为 0/0，不等于阵容库下载失败。
  语料日期保留远端真实生成时间。
- 日志位于上述默认游戏目录的 `BepInEx/LogOutput.log`。检查
  `plugin.initialization.succeeded`、`plugin.android.tls.registered`，以及
  `live_build_panel.corpus.ready` / `live_build_panel.corpus.recovered`。
  正常证书验证仍启用，不需要关闭 SSL 验证或手工导入阵容缓存。

本轮在 vivo iPA2375 / Android 15 实测：远端取得 5,689 套阵容，手动刷新成功，冷启动读取缓存成功；
严格拒绝证书的临时反向探针确认请求确实被拒绝，探针已从发布代码移除。
Android 条件纯行为测试 228/228，默认条件 221/221；Android 构建 0 错误、95 个现有警告。
完整桌面架构门禁未通过，不能据此宣称全项目验收通过。

## 已知限制

- 暂停语音回调、图鉴原生品质/附魔控制、被动描述多品质合并、升级数值元组改写、包裹商人摘要；
  原生描述保留，BPP 自有检视品质选择可用。大元组返回值的 Harmony detour 会触发 ARM64 崩溃，不能强行启用。
- 同一卡牌刚关闭后立即再次点击可能遇到空 `CurrentTooltipData`；切换另一张卡后可继续检视。
- 历史与本地回放有前轮实机覆盖，但上传、账号链接、Ghost 在线同步、MP4 录制及回放重复进入未完整验收。
  Android 回放自动清理仍关闭。本轮演示平板没有历史记录，不把空界面视为完整回放验收。
- 游戏更新可能使 IL2CPP 接口失效；问题及验证证据见
  <https://github.com/TAP-APIA/bazaarplusplus-android/issues/15>。

## 从源码重建

从仓库根目录执行，`<Android代理程序集目录>` 需由匹配游戏版本的 IL2CPP 工具链生成，不随 Release 分发：

```powershell
dotnet build bazaarplusplus-mod/src/BazaarPlusPlus/BazaarPlusPlus.csproj -c Debug -p:BppAndroid=true -p:ManagedPath=<Android代理程序集目录> -p:NuGetAudit=false -p:TreatWarningsAsErrors=false
.\android-port\package-modpack.ps1 -Zip
```

`package-modpack.ps1` 的参数可指定编译输出、代理快照、原始启动器 APK、修补 Runtime / HarmonySupport 和目标目录；
这些输入必须自行准备。`android-port/tools/` 保存运行时补丁与托管 codec 打包工具。
`package-launcher.ps1` 是早期本地重签脚本，仍有机器专用路径，**不是本 Release 的安装入口**。
本次直接分发已验证 APK，不调用它重新签名。

### 不要修改 libpine

`lib/arm64-v8a/libpine.so` 的 `Pine_init0` 若被改成直接 `ret`，会使 `Pine.arch` 未初始化，
在 `ClassLoaderHooks.installHooks` 报 `Unexpected arch 0`，游戏无法进入 Unity。
发布 APK 保留原始 native 库；不要为了启动“修复”而将 `.so` 打桩。
