# BazaarPlusPlus 安卓移植 — 进展交接文档

## 2026-10-23 账号实测与录制续接（优先阅读）

- 用户已明确授权使用平板当前登录账号推进游戏以取得测试场景。run `98086029` 从第3天推进到第5天；已实际进行商店购买、存箱/移回、升级、PvE和PvP。没有现实货币消费。
- 商店“杰杰”的3件物品与阵容恢复立绘；购买冰淇淋后商店3→2、阵容5→6，白银升级治疗15→30，存箱与移回同步通过。证据见 `account-test-shop-abi-fixed.png`、`account-test-shop-after-buy.png`、`account-test-stash-return.png`。
- **新增原生根因**：HarmonySupport按x64方式装箱ARM64按值传入的8字节CancellationToken，损坏原生LoadArt取消令牌。`safe_Il2CppInterop.HarmonySupport.dll`的限定修补已双写真机并冷启动验证；重建工具为 `tools/patch-harmony-arm64/`，输入备份 `account-test-HarmonySupport-original.dll`。
- 游戏原生MessagePack代理使用3参数Serialize及Stream Deserialize；已适配。ModApi的托管MessagePack使用 `Bpp.Managed.*` 独立程序集身份，解决与游戏同名代理绑定冲突，真机formatter整组类型加载错误已消失。打包工具 `tools/package-managed-codec/`；`tools/codec-smoke/`验证94个ModApi类型及replay DTO压缩往返通过。
- Android AOT的ReceiveOrQueue聚合消息直接自递归绕过数据钩子，现主动按顺序展开并按MessageId去重。`account-test-worker.log`已捕获两场PvE及第4天PvP的opening→combat→closing；PvP对手为Scythes。
- **录制文件已确认**：Android持久化worker恢复后台保存；独立测试覆盖清单失败回滚、完成计数和退出排空。实际Data Root在 `/data/user/0/com.bepinex.android/app_bepinex_runtime/com.tempogames.thebazaar/BazaarPlusPlusV6`，不是外置BepInEx目录。已确认 `CombatReplays/00f38840fc6445c0b1163d9643280e00.payload.mpack.gz`，49,050字节；历史中第4天Scythes可启动这份本地记录。
- **bootstrap2已验证到播放结束**：修复原生SceneLoader取消令牌、AppState静态字段、原生ReplayState类型判断和SocketBehaviorBridge事件后，历史回放能启动并播放；但对手棋盘为空，结尾“继续”触发原生崩溃。证据 `account-test-replay-bootstrap2.log`、同前缀logcat及tombstone，不能声称回放全流程已通过。
- **bootstrap3退出修复已通过**：对手卡牌/技能事件和SkillCard使用IL2CPP native cast；旧回放opening恢复5件物品、4技能，技能视觉正常。原生退出崩溃定位为 `Dictionary<InstanceId,...>.Remove` 的结构体ABI错误，Android改走正确拆箱的原生非泛型IDictionary；写入处同步修复。整场结束点击Continue成功返回历史，进程仍存活，无新SIGSEGV。证据 `account-test-replay-bootstrap3.log` 与 `account-test-replay-bootstrap3-logcat.log`。
- **bootstrap4实测**：opening快照增加spawn事件Section/Socket兜底，不再以空快照覆盖有效原生位置；修正native nullable读值后恢复12条Hand（包含物品及插槽效果），位置完整。此前“对手5件物品”来自错误nullable筛选，不代表完整对手棋盘。SimPvpOpponent空nullable参数显式构造原生空结构体，头像加载和英雄选择适配原生Task/属性；对手头像及双方4技能正常，Continue返回历史成功，但对手物品仍空。剩余首条降级为HealthBarController.ForceEmptyHealthVisual初始化异常。
- **bootstrap5物品恢复通过**：另有 `SanitizeSpawnEvents` 直接读原生Section.HasValue/Value。runtime_invoke把Hand可空值装箱为enum(0)，原生Nullable getter把该0误当hasValue=false，实际从SpawnEvents移除了对手物品事件。已改为ToManagedNullable，并同步修正readiness的native类型/Section检查。真机对手物品恢复、己方8物品和双方4技能正常，头像正常；详见 `account-test-replay-bootstrap5.log`。
- **bootstrap6血条适配待回归**：HealthBar及divider配置由GetField改用兼容属性访问；Player.Attributes读取原生字典；portrait handler解除使用原生已有委托；初始化成功后才登记controller。原生HealthBar.Init写入HealthBarControl(+0x178)，旧路径跳过Init后ForceEmpty读取该空字段触发异常。`build-account-replay-bootstrap6.log`为0错误、86警告，当前常规modpack已包含本修复。
- 46项相关纯行为测试通过（`test-account-final.log`）；后台保存、失败回滚、退出排空独立测试和94个ModApi类型codec测试通过（`test-account-persistence-final.log`、`test-account-codec-final.log`）。7类临时Android probes已删除，启动失败使用正式终态日志保留最初异常，次要清理/rollback异常追加而不覆盖根因。
- `dist/bpp-modpack-diagnostic.zip`是旧诊断包，不作为最新交付。新版启动器内Runtime、HarmonySupport及SQLite已逐项校验，并沿用debug证书签名；以 `dist/README.md` 和 `SHA256SUMS.txt` 为最终交付依据。
- **bootstrap4交付部署已核对**：新版启动器 `adb install -r` 成功，设备base.apk与本机产物SHA256同为 `80379CA3BF211233C60005B51ACEDBB849F3A0805E460F8D9C020B2B1F25BE83`，未卸载或清除数据。该版主DLL双写hash均 `C4DCE5C75AC8C9921EB62B7E2ADC6959678C1C5F920D1AC554CA796B59A8657D`；Runtime两处均 `39AB3DE276D528D84E06968D8DE6261E505357A93114F105228A6A6B9A77C4FE`，HarmonySupport两处均 `55CEC5FD0585529E0EBD34175F0E512E2AB4953C5AC519C074C4C49A460DC214`。临时BppAndroidProbe已移除，冷启动插件初始化成功。后续仅更换主DLL，当前交付hash以dist/SHA256SUMS为准。
- 平板切换无线调试；`tools/account-test-screen.ps1`及`tools/restart-bpp.ps1`支持`-DeviceId`，未指定时只选择唯一在线设备，防止多设备误推。无线连接端口可能变化，执行前核对 `adb devices -l`。

## 2026-10-22 续接结果（优先阅读）

下文 16:00 快照保留作为问题定位记录，当前交付说明以 `dist/README.md` 为准。

- **P0-1**：对局探针确认 SelectionSet 是三张 EventEncounterCard，当时没有商店物品；棋盘能读到五件 ItemCard。新增 Android 空首帧快照的有限重试。真实商店场景仍需回归。
- **P0-2 已修复并真机验证**：原生 socket 是零尺寸定位点；owned board 现在独立定位卡牌并退出地毯自动布局，按格位、占格宽度及实测立绘尺寸适配，归还对象池前恢复布局。
- **补充根因**：IL2CPP runtime_invoke 返回非空 Nullable 时装箱成底层 T，代理却包装成 Nullable。直接读 hasValue/value 曾使 socket 变成 847834112。ToManagedNullable 现在识别底层装箱类型，真机格位恢复为 1、3、4、6、7。
- **P0-3 已修复并真机验证**：遮挡来自移动端 Scroll View 的 viewport、背景和裁剪，非变体面板背景。检视期间临时解除这些装饰与裁剪；Legend_Tray 单独测量并与主描述并排，关闭时还原。
- **P1-1 已修复并真机验证**：将 SQLite 放入 APK lib 目录后，AndroidNativeLibraries 从 libcoreclr 的安装目录加载它并注册 provider resolver；同时修复 SqliteStoreBase 初始化连接前仍标记不可用的问题。历史面板已显示一局朱尔斯记录。
- **P1-2 已处理**：两个回放 Task、卡面缓存 Task、用户名和排行榜签名已适配。五类尚不支持的增强用 HarmonyPrepare 明确停用，并由 plugin.android.patch_capabilities 公告；它们不是已完成移植。
- **P2 已处理**：更新打包和双写部署脚本，插件包强制带修补运行时；package-launcher.ps1 同步 APK 内运行时和 SQLite。BepInExLauncher-android-updated.apk 已沿用设备 debug 证书覆盖安装，模组选择保留。
- **验证**：Android 编译通过；29 项相关纯行为测试通过。使用 tools/dotnet10 运行 net10 测试，构建仍使用原 .NET 8 SDK。
- **未覆盖**：实际商店购买、完整新战斗记录与回放、Mod API/账号链接、语音字幕、MP4；已有 MessagePack formatter 类型扫描兼容性警告仍存在。

截图：continuation-board-fix4.png、continuation-keyword-ytest.png、continuation-history-fix4.png。
日志：build-continuation-final.log、test-continuation-pure.log、continuation-fix4.log。

> 本文是 `HANDOVER.md`（2026-10-21 的初始任务书，其 4 个阶段目标已全部达成）的**续篇**。
> `HANDOVER.md` 保留为"任务书 + 元数据 v39 逆向结论"存档；本文记录**实际落地状态、已修崩溃根因、当前待办**。
>
> 快照时间：2026-10-22 16:00 (+08:00)

---

## 〇、历史快照现状（2026-10-22 16:00，不作为当前验收结论）

当时冷启动与面板检查没有观察到进程崩溃，右上角 `[配装 Builds] [图鉴 Cards] [历史 History] [BPP]` 四个按钮挂载、三个面板能打开并渲染中文UI与立绘。此结论只覆盖当时场景；随后账号实测发现的录制与回放问题见本文顶部续接记录。

---

## 一、环境与设备（可直接复用）

### 1.1 物理测试机

| 项 | 值 |
|---|---|
| 设备 | vivo PA2573 平板（Android 16） |
| adb 序列号 | `400F45F1AX00000` |
| 物理分辨率 | `2064x3096`，密度 `400` → 横屏逻辑分辨率 `3096x2064` |
| 锁屏 PIN | `<device-pin>` |
| adb 路径 | `D:\thebarzaar\tools\platform-tools\platform-tools\adb.exe` |

### 1.2 游戏与启动链

| 项 | 值 |
|---|---|
| 包名 | `com.tempogames.thebazaar` |
| 版本 | `1.0.12825-production-android-d9601ded` |
| 引擎 | Unity `6000.3.11f1`，IL2CPP `arm64-v8a` |
| 启动器包名 | `com.bepinex.android`（NextBep 改造版，Activity 为 `.MainActivity`） |
| 游戏进程 | `com.bepinex.android:game`（子进程） |
| BepInEx | `BepInEx.Unity.IL2CPP` `6.0.0-be.790` |

### 1.3 关键路径

```
设备外置根目录   /sdcard/BepInEx_Android/com.tempogames.thebazaar/
  ├─ BepInEx/plugins/          ← 运行时实际加载的插件（10 个 DLL + 2 个 .so）
  ├─ BepInEx/core/             ← BepInEx 核心（会被 APK assets 覆盖）
  ├─ BepInEx/LogOutput.log     ← 主日志（排障第一入口）
  ├─ BepInEx/bpp-startup-trace.log  ← BPP 轻量启动埋点（每次启动前建议先删）
  ├─ modpacks/bpp/plugins/     ← 模组包源（applyModpack 会同步到 BepInEx/plugins）
  ├─ modpacks/bpp/core/        ← 模组包核心覆盖（applyModpack 会同步到 BepInEx/core）
  └─ PersistentData/           ← tombstone_XX 原生崩溃转储
```

本机工作区：

| 路径 | 说明 |
|---|---|
| `D:\thebarzaar\apk_extracted\` | APK 解包产物（`libil2cpp.so` 148 MB、`global-metadata.dat` 22 MB 等） |
| `D:\thebarzaar\android-managed\` | **DummyDll + Il2CppInterop 代理程序集，176 个 DLL**（编译引用源） |
| `D:\thebarzaar\dumped3\dump.cs` | Il2CppDumper 产物，`RVA`/`Offset` 对照查符号用 |
| `D:\thebarzaar\BazaarPlusPlus\` | 上游 monorepo 检出 |
| `D:\thebarzaar\dist\` | 交付物（APK / modpack / 脚本） |
| `D:\thebarzaar\ilstool\` | Mono.Cecil 打补丁工具（`Program.cs` 现为 `il2cpp_object_unbox` 补丁） |
| `D:\thebarzaar\safe_Il2CppInterop.Runtime.dll` | **已打好 3 个关键补丁的运行时**（见 §三） |

### 1.4 构建命令（唯一可用形态）

```powershell
$env:DOTNET_ROLL_FORWARD="Major"
& C:\Users\administrator\.dotnet\dotnet.exe build `
  "D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\src\BazaarPlusPlus\BazaarPlusPlus.csproj" `
  -c Debug -p:BppAndroid=true -p:ManagedPath=D:\thebarzaar\android-managed `
  -p:NuGetAudit=false -p:TreatWarningsAsErrors=false --nologo
```

- 必须用 `C:\Users\administrator\.dotnet\dotnet.exe`（`8.0.426`）。系统 `C:\Program Files\dotnet` 只有 Runtime。
- 必须带 `$env:DOTNET_ROLL_FORWARD="Major"`（`global.json` 已配 `rollForward: latestMajor`）。
- 输出：`bin\Debug\net6.0\BazaarPlusPlus.dll`（当前 **4,160,000 B**）。

### 1.5 部署（**必须双写**）

```powershell
$adb="D:\thebarzaar\tools\platform-tools\platform-tools\adb.exe"
$bin="D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\src\BazaarPlusPlus\bin\Debug\net6.0"
foreach ($d in @(
  "/sdcard/BepInEx_Android/com.tempogames.thebazaar/BepInEx/plugins",
  "/sdcard/BepInEx_Android/com.tempogames.thebazaar/modpacks/bpp/plugins")) {
    & $adb push "$bin\BazaarPlusPlus.dll" "$d/BazaarPlusPlus.dll"
}
& powershell -NoProfile -ExecutionPolicy Bypass -File D:\thebarzaar\dist\package-modpack.ps1 -Zip
```

> **为什么要双写**：`BootstrapActivity` 每次启动都先 `extractZipFromAssets("BepInEx.Android.zip")` 覆盖 `BepInEx/core/*`，再 `modpackManager.applyModpack()` 把 `modpacks/bpp/plugins` 同步到 `BepInEx/plugins`、把 `modpacks/bpp/<任意子目录>` 同步到 `BepInEx/<同名目录>`。只写 `BepInEx/plugins` 会在下次启动被 modpack 覆盖回去。

### 1.6 签名凭据与启动器更新 ⭐

**Keystore**：`<local keystore path — not in repo>`
**格式**：PKCS12（文件头 `30 82 0A 8E 02 01 03`）
**storePassword**：`<keystore-password>`
**keyPassword**：`<keystore-password>`（PKCS12 同值）
**alias**：`bpp`（`PrivateKeyEntry`，创建于 2026-10-21）
**证书**：`CN=BazaarPlusPlus, OU=Android, O=BPP, L=CN, ST=CN, C=CN`
**证书 SHA-256**：`F8:D5:CE:5D:85:2D:94:94:DA:8B:87:4E:CD:D0:98:28:57:BB:1B:8A:42:2D:B8:DB:9F:96:5B:84:FF:EB:A1:9C`

> 2026-10-22 用 `keytool -list` 与 `apksigner sign` 双向实测通过。

**更新启动器的唯一入口**：`D:\thebarzaar\dist\package-launcher.ps1`

```powershell
$env:JAVA_TOOL_OPTIONS = $null
$env:BPP_APK_STORE_PASSWORD = '<keystore-password>'
$env:BPP_APK_KEY_PASSWORD   = '<keystore-password>'
& powershell -NoProfile -ExecutionPolicy Bypass -File D:\thebarzaar\dist\package-launcher.ps1 `
    -SourceApk 'D:\thebarzaar\dist\BepInExLauncher-android-fixed.apk' `
    -Destination 'D:\thebarzaar\dist\BepInExLauncher-android-updated.apk'
```

该脚本做 5 件事：
1. 复制 `-SourceApk` → `<Destination>.unsigned.apk`；
2. 把 `assets/BepInEx.Android.zip` 里的 `core/Il2CppInterop.Runtime.dll` 换成 `D:\thebarzaar\safe_Il2CppInterop.Runtime.dll`；
3. 把 `lib/arm64-v8a/libe_sqlite3.so` 塞进 APK（**顺带解决 §四 P1-1 的 SQLite 不可用**）；
4. 删除旧 `META-INF/*.SF|RSA|DSA|EC` 签名 → `zipalign -P 16`；
5. `apksigner sign --ks bpp.keystore --ks-pass env:BPP_APK_STORE_PASSWORD --ks-key-alias bpp` 并 `verify --print-certs`。

> ⚠️ **本仓库不保存任何凭据，口令已脱敏为 `<keystore-password>`，原值见本地私有记录；密码从未落盘**。原 agent 只留了环境变量契约（`package-launcher.ps1` 第 56–58 行在变量为空时直接 `throw`）。密码 `<keystore-password>` 是本次 2026-10-22 通过候选恢复实测得出，已固化到本节。

> ⚠️ **签名不一致的坑（务必先读 §四 P2-1）**：平板当前安装的启动器是 **debug 证书**签的，不是 `bpp.keystore`。用 `bpp.keystore` 签的新包**无法覆盖安装**。

### 1.7 启动 + 截图标准流程

```powershell
# 1) 冷启动
& $adb shell "am force-stop com.bepinex.android"
& $adb shell "am force-stop com.tempogames.thebazaar"
& $adb shell "rm -f /sdcard/BepInEx_Android/com.tempogames.thebazaar/BepInEx/bpp-startup-trace.log"
& $adb shell "am start -n com.bepinex.android/.MainActivity"
Start-Sleep 4

# 2) **必须** uiautomator dump 找按钮，不要硬编码坐标
& $adb shell "uiautomator dump /data/local/tmp/ui.xml"
& $adb pull /data/local/tmp/ui.xml D:\thebarzaar\ui.xml
# 依次按 text 匹配点击：'The Bazaar' → '启动'（如出现 '关闭' 弹窗先点掉）

# 3) 等 ~42s 进入主界面，再截图
& $adb shell "screencap -p /data/local/tmp/s.png"
& $adb pull /data/local/tmp/s.png D:\thebarzaar\s.png
```

> 已知踩坑：启动器首页有多个游戏卡片，**必须**用 `text="The Bazaar"` 定位再取其下面的 `启动` 按钮；有弹窗时先点 `关闭`。禁止用固定坐标点启动流程。

---

## 二、已完成的里程碑

| # | 里程碑 | 产物 / 证据 |
|---|---|---|
| 1 | Metadata v39 结构完全破解 | `HANDOVER.md` §二.3；`apk_extracted/global-metadata-v29.dat` |
| 2 | DummyDll + Il2CppInterop 代理生成 | `D:\thebarzaar\android-managed\` 共 **176 个 DLL** |
| 3 | `bazaarplusplus-mod` Android 编译通过 | `BazaarPlusPlus.dll` 4,160,000 B，`-p:BppAndroid=true` |
| 4 | 启动器 APK（NextBep 改造、去 PVZ 品牌） | `dist\BepInExLauncher-android-fixed.apk`（97,529,767 B） |
| 5 | 外置 modpack 打包 | `dist\bpp-modpack.zip`（**12 个 DLL** + `core\Il2CppInterop.Runtime.dll`） |
| 6 | 触屏 UI 入口 | `BppTouchEntry`：右上角 UGUI dock，`sortingOrder = 32000`，4 个按钮 + 三指点击兜底 |
| 7 | **配装面板** 可打开并渲染 | 中文 UI（`十胜推荐/十胜阵容/阵容库/切换阵容/赞助`）、4 排棋盘、赞助商列表 |
| 8 | **图鉴面板** 可打开并渲染 | `1850` 张卡牌、36 张高清立绘、完整筛选器（英雄/品质/尺寸/标签/类型/商人） |
| 9 | **历史面板** 可打开并渲染 | 11 个页签全中文（`对局/幽灵/收藏/瓦内莎/皮格马利翁/杜利/马克/朱尔斯/卡诺克/斯黛尔/双龙`） |
| 10 | 卡牌检视窗（锁定详情）可打开 | 放大卡牌 + 词条气泡（`加速`/`暴击率`）正常 |

---

## 三、已定位并修复的崩溃/缺陷清单 ⭐ 最有价值部分

> 每一条都是**真实踩过并修好**的。接手后请勿回退这些改动，否则会重现 SIGSEGV / SIGABRT。

### 3.1 `Newtonsoft.Json` 托管版 vs IL2CPP 代理版冲突 → `SIGABRT`

- **症状**：启动 ~0.3s 后在 `Class_FromIl2CppType_Hook.Hook:line 30` 抛 `NullReferenceException`，`Aborting process`。
- **根因**：`BazaarPlusPlus` 用 NuGet 的纯托管 `Newtonsoft.Json`（723,368 B，`PublicKeyToken=30ad4fe6b2a6aeed`），但 `package-modpack.ps1` 把它排除了，运行时 CoreCLR 解析到 `BepInEx/interop/Newtonsoft.Json.dll`（1,489,920 B，IL2CPP 代理，`PublicKeyToken=null`）。`JsonConvert.DeserializeObject<T>(string)` 对任何**纯托管 DTO**（`CollectionSourceCatalogDto`、`BuildRecommendationCorpus` 等）调用代理方法时，`Il2CppClassPointerStore<T>.NativeClassPtr` 为 `0`，把 `null` `Il2CppType*` 喂给 `il2cpp_class_from_il2cpp_type` → 崩。
- **修复**：
  1. `package-modpack.ps1` 把 `Newtonsoft.Json` 从 `$gameProvided` 排除名单中豁免，**托管版随 `plugins/` 一起发**；
  2. `Plugin.cs` 的 `static Plugin()` 里用 `AssemblyLoadContext.Default.LoadFromAssemblyPath` **预加载** `BepInEx/plugins/Newtonsoft.Json.dll`，并挂 `Resolving` + `AssemblyResolve` 双保险。

### 3.2 `safe_Il2CppInterop.Runtime.dll` 的三个补丁（**必须随包发**）

文件：`D:\thebarzaar\safe_Il2CppInterop.Runtime.dll`
大小：`226,304 B`
SHA256：`39AB3DE276D528D84E06968D8DE6261E505357A93114F105228A6A6B9A77C4FE`

部署位置：**同时**放在 `modpacks/bpp/core/`（applyModpack 每次启动覆盖 `BepInEx/core/`）和 `BepInEx/core/`。

| 补丁目标 | 原实现缺陷 | 补丁 |
|---|---|---|
| `Class_FromIl2CppType_Hook::Hook` | ① 未判 `type == null` 即解引用 `type->data`；② 判断 `(nint)type->data < 0`（Android 16 ARM64 Scudo Top-Byte-Ignore 堆指针 `0xb400...` 恒为负）成立后查 `s_InjectedClasses`，却把返回的 `bool` `pop` 掉、直接 `return 0` | 加 `if (type == null) return null;`；把 `pop` 换成 `brfalse.s Original(type, throwOnError)` |
| `Il2CppObjectBase::CreateGCHandle` / `get_Pointer` / `get_WasCollected` / `Finalize` | `myGcHandle == IntPtr.Zero` 时传给 `il2cpp_gchandle_get_target(0)` / `il2cpp_gchandle_free(0)`，Unity `libil2cpp.so` 不判 0，直接 `ldrb w8,[0x20]` → 空指针解引用 | `Zero` 时 `CreateGCHandle` 直接 `ret`；`get_Pointer` 返回 `IntPtr.Zero`；`get_WasCollected` 返回 `true`；`Finalize` 跳 `free` |
| `IL2CPP::il2cpp_object_unbox` | 传 `IntPtr.Zero` 时返回 `0 + 0x10`，下游 `Nullable<T>::get_HasValue` 解引用 `0x10` → `SIGSEGV`（`fault addr 0x10`） | 改为托管包装：`ptr == 0` 时返回一块 `Marshal.AllocHGlobal(256)` 且每次 `initblk` 清零的缓冲区；否则转发 `il2cpp_object_unbox_native` |

> 重新生成方式：改 `D:\thebarzaar\ilstool\Program.cs`（Mono.Cecil），
> `dotnet run --project ilstool.csproj -- <源dll> <目标dll>`。

### 3.3 `RectTransform.GetWorldCorners(Vector3[])` 在 IL2CPP 下**静默失效**

- **症状**：所有依赖世界坐标的布局/命中/测量全部拿到全 `0`；`RowBoundsChanged` 永不触发、棋盘预览宽度为 0、卡牌检视窗 2 秒后被判"测不出尺寸"自动关闭、BPP 设置 dock 按钮报 `gear_footprint_unavailable`。
- **根因**：托管 `Vector3[]` 传给 IL2CPP 方法时会被隐式**拷贝**成临时 `Il2CppStructArray<Vector3>`，Unity 填充完临时数组即丢弃，调用方数组纹丝不动。
- **修复**：`Infrastructure/Il2CppCompat.cs` 的 `BppUnityCast` 增加
  ```csharp
  public static void GetWorldCornersManaged(this RectTransform? rect, Vector3[]? four)
  // Android: new Il2CppStructArray<Vector3>(4) → rect.GetWorldCorners(native) → 逐项拷回
  ```
  **全项目 17 处 `.GetWorldCorners(...)` 已全部迁移为 `.GetWorldCornersManaged(...)`**（`grep -n "GetWorldCorners(" 应只剩 0 处或仅剩包装内部那一处`）。新增代码请一律用 `*Managed`。

### 3.4 `foreach (Transform child in parent)` 在 IL2CPP 下抛 `InvalidCastException`

- **症状**：`Unable to cast object of type 'Il2CppSystem.Object' to type 'UnityEngine.Transform'`。
- **根因**：`Transform.GetEnumerator()` 返回 `IEnumerator`，`Current` 是 `Il2CppSystem.Object` 基类包装器，`foreach` 隐式强转 `(Transform)` 必然失败。
- **修复**：全部改为 `for (var i = 0; i < parent.childCount; i++) { var c = parent.GetChild(i); ... }`。
  已修：`BPPSupporterNativeAttributionRow.Bind`、`NativeCardCellFitter.FindDescendant`、`BppTooltipSections`、`NativePlayerPortraitBounds`。
- ⚠️ 这一条直接造成了「配装第一次点开没数据 / 第二次点不开 / 从图鉴切配装弹回对局」——因为异常发生在 `LiveBuildPanelView.Refresh` → `BPPSupporterNativeAttributionRow.Bind`，打断了 `Open()`，面板没被 `SetVisible(true)`。

### 3.5 `Il2CppSystem.Nullable<T>` 的 `default` 是 **null 引用**，不是空值

- **症状**：`Il2CppObjectBaseToPtrNotNull` 抛 `NullReferenceException`；卡牌立绘/棋盘卡牌全部空白。
- **根因**：`Il2CppSystem.Nullable<T>` 是 class（继承 `Il2CppSystem.ValueType` 但 `IsValueType == false`），`default(Il2CppSystem.Nullable<T>)` 是 `null`。
- **修复**：`ToNativeNullable<T>(this T? v)` 的 else 分支返回 `new Il2CppSystem.Nullable<T>()` 而**不是** `default`；`ToNativeGuid` 同理。
- 同理：`NativeCardPrefabLoader` 里 `InstantiateAssetAsyncByReference(reference, default)` 必须写 `new Il2CppSystem.Nullable<AssetScope>()`。

### 3.6 `InstanceId`（C# record struct 含 string）被 Il2CppInterop 生成成 **class**

- **症状**：对局中点 `[配装 Builds]` 触发 `SIGSEGV code=2 (SEGV_ACCERR)`，`Cause: trying to execute non-executable memory`，`pc` 落在 `Dictionary<InstanceId,Card>.TryGetValue`（`dump.cs` RVA `0x64485AC`）。
- **根因**：`InstanceId` 含 `string` 字段，IL2CPP 侧按引用类型布局存 `Il2CppObject*`；而 `Il2CppSystem.Collections.Generic.Dictionary<TKey,TValue>.TryGetValue` 的生成代码用 `typeof(TKey).IsValueType` 决定是否 `il2cpp_object_unbox`。判断为 `false` 后直接把**已装箱对象头指针**当作结构体内容传给原生 `FindValue`，对象头首字段是 `.data` 段的 `Il2CppClass*` → 跳进不可执行内存。
- **修复**：**绕开该字典**。
  - `LiveCardSnapshotReader.ReadShopItems` → 改用 `TheBazaar.Data.GetCard(string)`（原生 C++ 里查，不经过托管泛型字典）；
  - `EncounterStateProbe.ResolveTemplateId` → 同上；
  - `ReadContainerItems` → 遍历 `container.Sockets`（`Il2CppReferenceArray<ISocketable>`）+ `HashSet<IntPtr>` 去重推导 socket 索引，绕开 `GetCardsAndSockets()` 返回的 `List<ValueTuple<...>>`。
- ⚠️ 其它 `Dictionary<InstanceId, X>` 的托管泛型调用同样危险，碰到就换成原生入口。

### 3.7 IL2CPP 包装器**只能向下 `TryCast`** 不能 C# 强转

- **症状**：`InvalidCastException: Unable to cast 'UnityEngine.Transform' to 'UnityEngine.RectTransform'` / `'Graphic' to 'Image'`。
- **根因**：`Component.transform`、`Button.targetGraphic` 等属性返回的包装器**静态类型**是声明基类，即便底层原生对象是派生类。
- **修复**：统一用 `BppUnityCast`：
  - `t.AsRectTransform()` / `t.RequireRectTransform()`
  - `g.AsImage()` / `g.AsTmpText()`
  - `obj.AsIl2Cpp<T>()`（替代 `is T` / `as T`）
  - `GetComponent` 拿到的是 `Component` 基类包装器，需要 `.AsIl2Cpp<CardPreviewBase>()` 才能当 `CardPreviewBase` 用，否则 `IsInstanceOfType` 恒 false、反射 `Invoke` 抛 `TargetException`。

### 3.8 IL2CPP 字段是 **PropertyInfo** 不是 FieldInfo

- **症状**：`AccessTools.Field(type, name)` 返回 `null`。
- **修复**：`BppFieldAccessor.Find(type, name)` 在 Android 下先查 `AccessTools.Property`，并提供 `GetValue<T>` / `GetReferenceArray<T>` / `SetReferenceArray<T>` / `SetValue`。

### 3.9 CoreCLR 主线程 `SynchronizationContext.Current` 为 `null` → `await` 续体跑到后台线程

- **症状**：`Graphics device is null`，随后 `SIGTRAP`（Unity 从 `.NET TP Worker` 调图形 API）。
- **根因**：Unity 的 `UnitySynchronizationContext` 活在 IL2CPP 域，CoreCLR 域取不到；`async` 方法 `await` 后默认在 `TaskScheduler.Default` 续体执行。
- **修复**：`GameInterop/BppCoroutines.cs`
  - `InitializeMainThread()`：记录主线程 id 并 `SynchronizationContext.SetSynchronizationContext(SyncContext)`（在 `Plugin.Load()` 首行调用）；
  - `Update()` 里每帧重新 `SetSynchronizationContext`（防被覆盖）→ 排空 `MainThreadQueue` → 推进 `IEnumerator` → 推进 `Pollers`；
  - `Il2CppCompat.AsManagedTask<T>(this Il2CppSystem.Threading.Tasks.Task<T>)` 和 `AsManagedTask(this Il2CppSystem.Threading.Tasks.Task)`：用 `BppCoroutines.PollOnMainThread` 轮询 `IsCompleted`，**保证结果在 UnityMain 上落 TCS**。
- ⚠️ 绝不要再写 `while (!t.IsCompleted) await Task.Yield();`。

### 3.10 `BppGameObjectFactory.Create` 必须走 `GameObject(string, Il2CppReferenceArray<Il2CppSystem.Type>)`

- **症状**：`GetComponent<RectTransform>()` 返回 `null`，随后 `Clear()` 里 `gameObject` 抛 `NullReferenceException`。
- **根因**：`new GameObject(name)` 已内置一个 `Transform`；再 `AddComponent(Il2CppType.From(typeof(RectTransform)))` **不会**把它升级成 `RectTransform`，`GetComponent<RectTransform>()` 得 `null`，对象被 parent 到根场景，切场景即销毁。
- **修复**：
  ```csharp
  if (components.Length == 0) return new GameObject(name);
  var types = new Il2CppReferenceArray<Il2CppSystem.Type>(components.Length);
  for (var i = 0; i < components.Length; i++) types[i] = Il2CppType.From(components[i]);
  return new GameObject(name, types);
  ```
  （空数组仍走无参构造，因为带类型数组的重载即便为空也会解引用 null class。）

### 3.11 UITK `generateVisualContent` + `context.Allocate` 在 Android 上被 strip

- **症状**：`MeshWriteData.SetNextVertex` / `SetNextIndex` 直接 `throw new NotSupportedException("Method unstripping failed")`，或 `NativeSlice<Vertex>.get_Length` 空指针。
- **修复**：`CollectionPanelView` 中所有 `context.Allocate(...)` 分支加 `#if !BPP_ANDROID`，Android 用 USS 样式（背景色/边框/圆角）替代；`context.painter2D` 路径保留可用。
- 涉及：`DrawControlsScrollShadow`、`DrawCollectionFilterCardBackground`、`DrawHeroGradient`、`DrawFacetMatchModeSegment`；图标 `DrawCloseIcon` / `DrawResetIcon` / `DrawSortIcon` 也用文字兜底。

### 3.12 排序层级在 Android 下必须整体抬高

`Infrastructure/UiTokens/BppOverlaySorting.cs`：

| 常量 | 桌面 | Android |
|---|---|---|
| `PanelUiToolkit`（图鉴 UITK） | 26 | **2600** |
| `NativeCardPreview`（原生卡牌层） | 27 | **2601** |
| `PanelForeground`（面板前景） | 28 | **2602** |
| `InspectionBackdrop` | 1000 | **3000** |
| `InspectionControls` | 1001 | **3001** |
| `InspectionContent` | 1002 | **3002** |
| `InspectionVariantControls` | 1003 | **3003** |

`BppTouchEntry` 的 Canvas 固定 `sortingOrder = 32000`（最高，永远可见）。

### 3.13 Harmony 补丁在 Android 下的返回类型适配

- `ref Task __result` 必须改成 `ref Il2CppSystem.Threading.Tasks.Task __result`，`Task.CompletedTask` 改成 `Il2CppSystem.Threading.Tasks.Task.CompletedTask`。
- 已改：`NativeTooltipSuppressionPatches`（`ShowSecondaryCardTooltipController`）、`CombatReplayFinalizeRunInitializationPatch`；`CombatSimPatch` 在 Android 下退化为纯 Postfix 观测。
- `CardPreviewBase.SetUp` 的 `CancellationToken` 参数在 Android 是 `Il2CppSystem.Threading.CancellationToken`，反射查 `AccessTools.Method` 的类型数组必须带 `#if BPP_ANDROID`。

### 3.14 `CollectionTierTooltipPatch` 改写 `List<TooltipSegment>` 会破坏池

- **症状**：`NullReferenceException at TooltipPools.ReturnTooltipSegments` / `CardTooltipData.CompileNestedSegments`。
- **根因**：`TooltipSegment` 是含 string 的 struct，同样被生成为 class；`List<T>.set_Item` 把它当引用类型写入，破坏原生结构体数组。
- **修复**：Android 下 `CollectionTierActiveTooltipPatch.Postfix` 直接 `return`（保留原生单阶描述，不做多阶合并）。

---

## 四、当前待办（用户最新反馈，按优先级）

### P0-1　配装面板「当前商店选择里没有物品」

- **现象**：进入对局后点 `[配装 Builds]`，`商店物品` 行显示"当前商店选择里没有物品。"，`箱子物品` 显示"箱子没有物品。"，`当前阵容` 只有 2/20/4 三个数值但无卡牌。
- **位置**：`GameInterop/LiveCards/LiveCardSnapshotReader.cs`
  - `ReadShopItems`：取 `runState.SelectionSet`（`List<string>`），对每个 entry 调 `TheBazaar.Data.GetCard(entry)`，再 `AsIl2Cpp<ItemCard>()`。
  - `ReadContainerItems`：`inventory.AsIl2Cpp<CardContainer>()?.Container` → 遍历 `container.Sockets`。
- **待查方向**（按怀疑度排序）：
  1. `Data.CurrentState.AsIl2Cpp<RunState>()` 拿到的是否是 `null`（对局中 `CurrentState` 可能是别的 AppState 子类）；
  2. `runState.SelectionSet` 的 entry 是否**不是** instance id 而是 template guid（`Data.GetCard` 查不到 → 返回 null）；
  3. `run.Player.Hand` / `run.Player.Stash` 的 `CardContainer.Container` 是否为空（`Sockets` 全 null）；
  4. `ItemCard` 判断是否被 `TCardInstanceItem` 取代（用 `AsIl2Cpp<TCardInstanceItem>()` 试）。
- **验证手段**：在 `Read()` 里临时加 `BppLog.InfoEvent`，打印 `run?.Player == null`、`runState == null`、`SelectionSet.Count`、`Sockets.Length`、首个 entry 的值。

### P0-2　配装面板看不到当前阵容里的物品图片

- **现象**：4 排棋盘（`十胜推荐` / `商店物品` / `箱子物品` / `当前阵容`）只显示蓝色金边地毯底图，无卡牌立绘。
- **位置**：`Game/LiveBuildPanel/Preview/LiveBuildPreviewRenderer.cs` + `GameInterop/MonsterBoardPreview/OwnedMonsterBoardPreview.cs`
- **注意**：`GetWorldCornersManaged` 已修，`RowBoundsChanged` 现在应该能触发；需确认：
  1. `LiveBuildPanelView.RowBoundsChanged` → `LiveBuildPanel.SetBounds` → `_previewRenderer.SetBounds` 链路是否通；
  2. `OwnedMonsterBoardPreview.Replace` 里 `NativeCardPrefabLoader.RentInactiveAsync` 是否成功（曾有 `AssetLoader.LoadAssetAsyncByAddress not found`，已改为直接调用 `LoadAssetAsyncByAddress<T>(address, AssetScope.Global)`）；
  3. `NativeBoardCardRental.Prepare` 里的 `Card.SetUp(...)` 是否 await 完成（已改 `AsManagedTask`）；
  4. `_view._carpetImage.transform` 作为 fallback parent 是否可用。
- 日志关键字：`live_build_panel.card_preview.degraded`、`native_preview_unavailable`。

### P0-3　图鉴检视窗右侧被大遮罩挡住文字

- **现象**：放大卡牌左侧正常，右侧「造成 10 伤害 / 霜冻时：加速此物…」文本被一块半透明深色矩形盖住；左上角词条气泡（`加速`/`暴击率`）反而正常。
- **遮罩左边界**：原图 x ≈ `3096 * 0.645 ≈ 2000`（预览图 2508 里的 1618），**与 `CollectionPanelView` 右侧控制栏左边界吻合**。
- **待验证假设**（尚未定论，按 AGENTS.md 需先探针验证）：
  1. `CardLockVariantPanel._panelRoot` 在 `NativeInspectionPresentation.PromoteVariantControls()` 里被 `lease.Promote(BppOverlaySorting.InspectionVariantControls /* 3003 */, detach: false)` 提升，若 `_panelRoot` 是一个覆盖右半屏的大 RectTransform 且自带深色背景，就会盖住 `InspectionContent`(3002) 的 tooltip 文本；
  2. 或者 tooltip 主描述其实挂在**第三个** canvas 上（不在 `LockModeCanvas` / `RootCanvasComponent` 之列），未被提升，仍留在 < 2600 被 UITK 盖住。
- **建议验证**：临时在 `PromoteVariantControls` 里打印 `root.rect`、`root.GetComponent<Image>()`、`_canvases` 每个 canvas 的 `sortingOrder` 与 `renderMode`；再临时把 `InspectionVariantControls` 降到 `2600` 以下看遮罩是否消失。
- **可能的修复方向**：只把 `_panelRoot` 内的 `Selectable`/`TMP_Dropdown` 提升，不提升 `_panelRoot` 本身；或把 `_panelRoot` 的背景 `Image` 设为 `enabled = false`。

### P1-1　SQLite 不可用（`RunLoggingModule` / `BundleSealCoordinator` 降级）

- **日志**：`Unable to load shared library 'e_sqlite3' ... dlopen failed: library ".../plugins/libe_sqlite3.so" needed or dlopened by ".../libcoreclr.so" is not accessible for the namespace "clns-10"`
- **已查明**：`libe_sqlite3.so` **确实已在** `/sdcard/.../BepInEx/plugins/libe_sqlite3.so`（本机源：`bin\Debug\net6.0\runtimes\android-arm64\native\libe_sqlite3.so`，1,772,160 B），但 Android linker namespace `clns-10` **禁止**从 `/storage/emulated/0` 加载 `.so`。
- **修复方向**：把 `libe_sqlite3.so` 打进 `BepInExLauncher` APK 的 `lib/arm64-v8a/`（与 `libcoreclr.so` 同目录），或在 `SqliteStoreBase` 里用绝对路径 + `NativeLibrary.Load`/`dlopen` 绕过默认探测。
- 影响面：本地对局日志、Bundle 封包、History 面板的本地记录。

### P1-2　Harmony 补丁降级 10 个

`[Warning][Plugin] event=plugin.patches.degraded failed_patch_count=10 reason_code=patch_classes_failed`

已知其中 3 个是**无法修**的（返回类型不可赋值）：
- `CardPreviewItem::LoadArt` —— 返回 `Il2CppSystem.Threading.Tasks.Task`，patch 声明 `System.Threading.Tasks.Task`；
- `CardTooltipData::GetPassiveTooltipBlock` —— 返回 `Il2CppSystem.ValueTuple<...>`；
- `CardTooltipController::SetupLockVariantPanel` —— `CollectionLockVariantPanelPatch.Transpiler` 找不到唯一的 `IUpgradeableCard` `isinst` 守卫，抛 `"Native lock variant eligibility guard changed."`（Transpiler 在 Android 的 IL 上失效，需重写或禁用）。

其余 7 个需逐条看 `LogOutput.log` 的 `[Error][HarmonyX] Failed to patch ...` 行。

### P2-1　启动器签名不一致 ⚠️ 会阻断覆盖升级

**实测事实（2026-10-22 取证）**：

| 对象 | 签名证书 DN | 证书 SHA-256 |
|---|---|---|
| 平板已装 `com.bepinex.android`（`versionCode=5` / `1.0.0-ci.5` / 2026-10-22 12:29:14） | `C=US, O=Android, CN=Android Debug` | `80D0E5389E158C59EB97B0C013E1AE52DF9C22727F7D505D53EC30BC5F0A6FD1` |
| `dist\BepInExLauncher-android-fixed.apk` | 同上（即平板装的就是它） | 同上 |
| `bpp.keystore` / `tools\launcher\launcher-bpp.apk` | `CN=BazaarPlusPlus, OU=Android, O=BPP, L=CN, ST=CN, C=CN` | `F8D5CE5D852D9494DA8B874ECDD0982857BB1B8A422DB8DB9F965B84FFEBA19C` |

**结论**：平板当前装的是 **debug 证书**签的包；`bpp.keystore` 是 2026-10-21 新建的另一套密钥。
用 `bpp.keystore` 签的新包**签名与已装包不一致 → `adb install -r` 会报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`**，用户必须卸载重装。

**两条可选路径**：

1. **保留现有 debug 身份的覆盖升级**（不丢数据，推荐给当前测试机）
   ```powershell
   # 用 debug keystore 重新签名，签名一致即可覆盖
   # debug keystore 位置（AGP 自动生成）：
   #   %USERPROFILE%\.android\debug.keystore   alias=androiddebugkey  storepass=android  keypass=android
   ```
   或直接用 Gradle 的 `assembleDebug` 出包（自动 debug 签名）。

2. **切换到 BPP 自有签名**（正式交付形态，需用户卸载一次旧包）
   ```powershell
   $env:BPP_APK_STORE_PASSWORD = '<keystore-password>'
   & powershell -NoProfile -ExecutionPolicy Bypass -File D:\thebarzaar\dist\package-launcher.ps1
   adb uninstall com.bepinex.android       # 会清掉 launcher 的配置（含已选 modpack）
   adb install D:\thebarzaar\dist\BepInExLauncher-android-updated.apk
   ```
   卸载后需在 launcher 里**重新选择 `bpp` 模组包**并重授 Shizuku/存储权限。

**顺带解决**：`package-launcher.ps1` 第 43–45 行会把 `lib/arm64-v8a/libe_sqlite3.so` 写进 APK，同时修掉 §四 P1-1 的 SQLite 不可用。

**其它过期内容**：两个 APK 内部 `assets/BepInEx.Android.zip` 的 `Il2CppInterop.Runtime.dll` 仍是旧的 `nohook` 版；当前靠 `modpacks/bpp/core/` 每次启动覆盖兜底，走一次 `package-launcher.ps1` 即彻底修掉。

### P2-2　`dist/README.md` 待更新

仍是 2026-10-21 23:59 的内容，未反映 §三 的 14 条修复与 §四 的待办。其第 17 行「覆盖安装即可，签名相同」在 §四 P2-1 的实测下**已不成立**，需改写。

---

## 五、关键代码地图（改动集中区）

```
src/BazaarPlusPlus/
├─ Plugin.cs                                   # static Plugin() 预加载 Newtonsoft；Load() 首行 InitializeMainThread
├─ Infrastructure/
│  ├─ Il2CppCompat.cs                          # ⭐ BppUnityCast（AsRectTransform/RequireRectTransform/AsImage/AsTmpText/AsIl2Cpp/GetWorldCornersManaged） + BppFieldAccessor + ToManagedNullable/ToNativeNullable/AsManagedTask
│  └─ UiTokens/BppOverlaySorting.cs            # ⭐ Android 下排序常量放大（26→2600, 1000→3000）
├─ GameInterop/
│  ├─ BppCoroutines.cs                         # ⭐ MainThreadSyncContext + PollOnMainThread + IEnumerator 驱动
│  ├─ BppGameObjectFactory.cs                  # ⭐ new GameObject(name, Il2CppReferenceArray<Type>)；GetComponent 走 TryCast
│  ├─ BppTouchEntry.cs                         # 右上角 UGUI dock（配装/图鉴/历史/BPP），sortingOrder 32000
│  ├─ AssetLoading/NativeGlobalAssetLoader.cs   # Android 直调 LoadAssetAsyncByAddress/ByReference(AssetScope.Global).AsManagedTask()
│  ├─ AssetLoading/NativeCardPrefabLoader.cs    # Android 直调 InstantiateAssetAsyncByReference
│  ├─ CardPreview/NativeCardPreviewArtworkMeasurement.cs  # TryMeasure 用 GetWorldCornersManaged
│  ├─ CardPreview/NativeCardPreviewReflection.cs # SetUpMethod 参数类型按平台分支
│  ├─ CardPreview/NativeCardPreviewRuntime.cs    # InvokeSetUpSafe 直调 previewBase.SetUp(...).AsManagedTask()
│  ├─ LiveCards/LiveCardSnapshotReader.cs        # ⭐ 待办 P0-1/P0-2 核心
│  ├─ MonsterBoardPreview/OwnedMonsterBoardPreview*.cs  # 棋盘卡牌渲染（待办 P0-2）
│  ├─ Tooltips/NativeInspectionPresentation.cs   # ⭐ 待办 P0-3 核心（CanvasLease/PromoteVariantControls）
│  ├─ Encounter/EncounterStateProbe.cs           # ResolveTemplateId 改 Data.GetCard
│  ├─ HeroPortraits/NativePlayerPortraitBounds.cs
│  └─ Fonts/NativeGameTypography.cs              # Android 字体兜底（NotoFontFallbackRuntime / OS sans-serif）
├─ Game/
│  ├─ CollectionPanel/                            # 图鉴：CollectionPanel/View/Grid/Virtualizer/Tooltips
│  ├─ LiveBuildPanel/                             # 配装：LiveBuildPanel/View/Preview/Renderer/Recommendations
│  ├─ HistoryPanel/                               # 历史
│  ├─ Supporters/Ui/BPPSupporterNativeAttributionRow.cs  # ⭐ 3.4 的 foreach 修复点
│  └─ Settings/                                   # BPP 设置 dock
└─ Patches/
   ├─ CollectionPanel/CollectionTierTooltipPatch.cs   # Android 下 return（3.14）
   ├─ Tooltips/NativeTooltipSuppressionPatches.cs      # Task → Il2CppSystem...Task
   └─ Combat/*.cs                                      # 同上
```

---

## 六、踩坑速查表（写新代码前必读）

| ❌ 禁止 | ✅ 必须 |
|---|---|
| `(RectTransform)t` / `t as RectTransform` | `t.RequireRectTransform()` / `t.AsRectTransform()` |
| `g is Image` / `(Image)g` | `g.AsImage()` |
| `obj is Foo` / `obj as Foo` | `obj.AsIl2Cpp<Foo>()` |
| `AccessTools.Field(t, n)` | `BppFieldAccessor.Find(t, n)` |
| `rect.GetWorldCorners(arr)` | `rect.GetWorldCornersManaged(arr)` |
| `foreach (Transform c in parent)` | `for (i) parent.GetChild(i)` |
| `x.HasValue`（`Il2CppSystem.Nullable<T>`） | `x.ToManagedNullable()` 后再判 |
| `SomeNullable = default` | `new Il2CppSystem.Nullable<T>()` |
| `while (!t.IsCompleted) await Task.Yield();` | `.AsManagedTask()` |
| `new GameObject(n); go.AddComponent(RectTransform);` | `BppGameObjectFactory.Create(n, typeof(RectTransform), ...)` |
| `Dictionary<InstanceId, X>.TryGetValue` | `TheBazaar.Data.GetCard(string)` 等原生入口 |
| `context.Allocate(...)`（UITK，Android） | USS 样式 |
| `ref Task __result` | `ref Il2CppSystem.Threading.Tasks.Task __result` |
| 固定坐标 `input tap` 点启动流程 | `uiautomator dump` 按 `text=` 定位 |

---

## 七、给接手 Agent 的建议顺序

1. **先跑通基线**：按 §1.4 编译一次，确认 0 error；按 §1.6 启动，确认 `plugin.initialization.succeeded` 且 `com.bepinex.android:game` 存活 60s 不掉。
2. **P0-1**：在 `LiveCardSnapshotReader.Read()` 加临时探针日志，把 `runState`/`SelectionSet`/`Sockets` 的真实值打出来（这一步不做等于盲猜）。
3. **P0-2**：依赖 P0-1 的数据先能读出来，再看 `LiveBuildPreviewRenderer` 是否拿到卡牌。两者很可能是同一个根因。
4. **P0-3**：先打探针确认遮罩属于哪个 canvas（§四 P0-3 的验证手段），再决定是"不提升 `_panelRoot`"还是"隐藏其背景 Image"。
5. **P1-1**：`libe_sqlite3.so` 塞进 APK `lib/arm64-v8a/`，这一步独立、收益明确（History 面板才有本地数据）。
6. **最后**才处理 P1-2 / P2，然后更新 `dist/README.md` 并重打 `bpp-modpack.zip` + `BepInExLauncher-android-fixed.apk`。

> 每次改动后请按 §1.5 双写推送，并删 `bpp-startup-trace.log` 后冷启动验证，不要只看编译通过。
