# BazaarPlusPlus Android 分发说明

2026-10-23 账号实测续接：vivo PA2573 / Android 16，游戏 1.0.12825-production-android-d9601ded / Unity 6000.3.11f1。
通过独立 BepInEx 启动器加载外置插件，不修改游戏 APK。

## 当前产物

| 文件 | 用途 |
| --- | --- |
| BepInExLauncher-android-updated.apk | 包含修补的 Runtime、ARM64 HarmonySupport 和 SQLite，沿用平板的 Android Debug 签名；已验证签名、嵌入文件并覆盖安装成功。 |
| bpp-modpack.zip / bpp-modpack/ | plugins/ 内15个插件与依赖DLL，core/ 内修补的 Runtime、HarmonySupport 两个DLL。 |
| package-modpack.ps1 | 从 Android 编译输出重新打包，强制带上修补运行时。 |
| package-launcher.ps1 | 更新 APK 内的两份运行时和 SQLite，进行对齐、签名及签名验证。 |
| install-to-device.ps1 | 同时推送 modpack 源与实际运行目录，覆盖 plugins/ 和 core/。 |
| SHA256SUMS.txt | 当前交付文件校验值。 |

旧的 -android.apk 和 -android-fixed.apk 保留作历史基线。
.unsigned.apk、.aligned.apk 是打包中间文件，安装使用 -updated.apk。
`bpp-modpack-diagnostic.zip` 是旧诊断包，不作为当前交付；`-pre-bootstrap3.apk` 是本轮替换前备份。

## 安装与更新

1. 用 adb 覆盖安装 BepInExLauncher-android-updated.apk。vivo 提示已安装同版本时，选择“重新安装”完成覆盖更新。
2. 首次使用启动器时授予所需的所有文件访问权限。
3. 运行 install-to-device.ps1，或将插件包解压到下列路径。
4. 在启动器选择 The Bazaar，确认选中 bpp 模组包，然后启动游戏。
5. 右上角使用“配装 / 图鉴 / 历史 / BPP”触屏入口。

    /sdcard/BepInEx_Android/com.tempogames.thebazaar/modpacks/bpp/

启动器每次启动会重建核心目录并应用 modpack，因此更新脚本同时维护源目录和运行目录。
覆盖升级必须沿用已安装包的签名；测试平板的 debug 证书与 bpp.keystore 证书不同。
签名参数与历史取证见 ../HANDOVER-ANDROID-PORT.md 的签名章节；密码不写入脚本。
无线调试先用 `adb connect IP:连接端口` 建立连接，再向安装脚本传 `-DeviceId IP:连接端口`；端口可能变化，以平板当前页面和 `adb devices -l` 为准。未传DeviceId时仅允许唯一在线设备。

## 重建

    $env:DOTNET_ROLL_FORWARD = 'Major'
    & C:\Users\administrator\.dotnet\dotnet.exe build D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\src\BazaarPlusPlus\BazaarPlusPlus.csproj -c Debug -p:BppAndroid=true -p:ManagedPath=D:\thebarzaar\android-managed -p:NuGetAudit=false -p:TreatWarningsAsErrors=false --nologo
    & D:\thebarzaar\dist\package-modpack.ps1 -Zip
    & D:\thebarzaar\dist\install-to-device.ps1

更新启动器时，在当前进程设置签名环境变量后调用 package-launcher.ps1。
测试平板覆盖升级传入 -KeyStore "$env:USERPROFILE\.android\debug.keystore" -KeyAlias androiddebugkey。
脚本读取 BPP_APK_STORE_PASSWORD 和可选的 BPP_APK_KEY_PASSWORD；-UnsignedOnly 仅准备待签名文件。

## 本轮验证结果

- 多次冷启动后插件初始化成功，配装、图鉴、历史可以切换。
- 阵容按原生格位和尺寸显示；早期五物品阵容及账号推进后的八物品回放均已验证，修复零尺寸插槽、自动布局抢占和 Nullable 装箱误读。
- 检视窗不再被移动端侧栏遮罩覆盖；关键词与主描述分开布局，关闭时恢复原生状态。
- SQLite 从启动器安装目录加载，历史面板已显示新记录的朱尔斯对局。
- 商店和阵容立绘恢复；商店购买、存箱/移回、冰淇淋升级至白银已用当前账号验证。HarmonySupport已修复ARM64按值CancellationToken被当成指针的错误。
- ModApi使用Bpp.Managed.MessagePack等独立程序集身份，避免与游戏原生MessagePack代理冲突；94个ModApi类型加载及replay DTO压缩往返通过。
- 聚合消息按顺序展开、MessageId去重，Android后台持久化worker恢复；清单失败回滚、退出排空等独立测试通过。
- 第4天Scythes战斗已生成49,050字节payload，历史可启动本地回放。旧记录由原始opening恢复12条Hand（包含插槽效果）及4技能；对手物品、双方技能和头像已显示。结束点击Continue返回历史成功、进程存活，无新原生崩溃。
- SceneLoader原生取消令牌、AppState静态字段、ReplayState类型判断和原生事件桥已适配；退出的InstanceId字典ABI错误改用原生IDictionary拆箱，真机退出通过。
- 无 plugin.patches.degraded 或 Harmony Failed to patch；不支持的增强功能在启动日志中单独列出。
- 本轮编译0错误、86警告；46项相关纯行为测试、后台持久化测试与codec测试通过。日志为工作区 `build-account-final.log`、`test-account-final.log`、`test-account-persistence-final.log`、`test-account-codec-final.log`。

## 当前范围与限制

- 测试账号run98086029已从第3天推进到第5天；没有现实货币消费。当前回归使用已保存回放，不继续推进账号对局。
- Android 暂停五类增强：语音回调、图鉴原生品质/附魔控制、被动描述多品质合并、升级数值元组改写、包裹商人摘要。原生描述保留。
- Mod API 上传、账号链接、语音字幕和 MP4 录制沿用先前 Android 降级策略。
- 历史记录、payload保存、双方回放画面及结束返回已验证；最新血条初始化修复和重复进入仍在验收，不能据此声称所有桌面功能已移植。
- bootstrap5 真机日志中仍有 `playback.degraded reason_code=player_attributes_unavailable`，根因是 `HealthBarController.ForceEmptyHealthVisual` 读取未被 `Init` 写入的 `HealthBarControl`(0x178) 后抛原生 NRE。当前源码改为：`Init` 后校验 `HealthBarControl` 非空才算就绪，未就绪则登记跳过、不再调用 `ShowEmptyPlayerHealthBar`/`RefillHealthBar` 等原生空血条路径，并记降级事件而不是崩溃。该改动已编译（0错误/86警告）并通过 178 项纯行为测试，**尚未上真机回归**。
- Android的回放自动清理仍关闭；属性预览、账号链接、上传、MP4等未完整覆盖。

## 日志

    /sdcard/BepInEx_Android/com.tempogames.thebazaar/BepInEx/LogOutput.log
    /sdcard/BepInEx_Android/com.tempogames.thebazaar/BepInEx/bpp-startup-trace.log

正常启动包含 plugin.android.sqlite.loaded 和 plugin.initialization.succeeded。
plugin.android.patch_capabilities 列出不支持的增强功能。

实际账号数据和回放在启动器内部目录：

    /data/user/0/com.bepinex.android/app_bepinex_runtime/com.tempogames.thebazaar/BazaarPlusPlusV6/

回放文件在该目录的 `CombatReplays/`。不要仅搜索外置BepInEx目录后认定录制缺失；不要为更新启动器而卸载清除内部数据。

