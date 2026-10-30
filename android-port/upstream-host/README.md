# 上游官方插件包接入（实验）

让本仓库的 BepInEx 启动器直接运行上游 [BazaarPlusPlus](https://github.com/BazaarPlusPlus/BazaarPlusPlus)
**官方发布的** Android 托管插件包（Managed Payload），并由启动器自动下载官方新版本。插件代码与官方逐字节一致，
不在本仓库编译。

状态：已在真机上启动到主菜单（见「真机验收」）；对局内功能、启动器 APK 切换与插件内联网**尚未验收**。
验收通过前，`package-launcher.ps1` 默认仍打包本仓库原插件，加 `-Upstream` 才打包这里的版本。

## 工作方式

官方的 Android 发布清单 `https://cdn.bazaarplusplus.com/installer/latest/mod/android-aarch64.json` 给出
`managedPayload`：一个带 minisign 签名清单的 zip，内容是 `MelonLoader/Mods` 与 `MelonLoader/UserLibs` 下的文件。
它的目录结构正是上游 Updater（ADR-0014）的暂存格式 `BazaarPlusPlusV6/Updates/<版本>/`。

1. **启动器（`BppOfficialPayload`，Java）**：模组包就绪后在后台读一次官方清单。版本比已安装的新、游戏版本与
   已安装的游戏一致、`nonManagedPayloadDigest` 与已安装清单一致、且不在拒绝列表里时，下载 zip，按清单核对大小与
   SHA-256，解压到 `<游戏数据目录>/BazaarPlusPlusV6/Updates/<版本>`。启动不等下载；赶上的话本次启动生效，否则下次。
   下载放在启动器，是因为游戏进程里 CoreCLR 的 HTTPS 不可用。
2. **桥接插件（`BazaarPlusPlus.BepInExBridge`）启动时，经 `BazaarPlusPlus.OfficialPayload`**：
   - 首次或内置版本更新时，把 APK 内置的官方包（`plugins/BazaarPlusPlus.OfficialPayload.zip`）暂存进 `Updates/`；
   - 对每个比已安装更新的暂存包：用上游的签名公钥校验 minisign 签名和每个文件的哈希；用 Mono.Cecil 核对它引用的
     每个程序集、类型、成员在本机都存在（interop、BepInEx core、兼容层），版本不低于所引用的；不通过就删除并记入
     `BazaarPlusPlusV6/bpp-official-payload.rejected`，启动器以后不再下载该版本；
   - 交给**上游 Updater 原版源码**（子模块编译进来）做带预写日志的原子替换，装进 `<游戏数据目录>/MelonLoader/`；
   - 已安装版本连续 3 次没走到主菜单（`LaunchGuard`）就放弃该版本：记入拒绝列表、撤掉其版本标记，下次启动重新
     装回内置版本；
   - 从 `MelonLoader/Mods` 加载带 `[MelonInfo]` 的程序集，经 MelonLoader 兼容层运行，转发帧、场景、退出回调；
   - 另外装上本仓库原插件的去黑边补丁（`LetterBoxPatches`）：上游没有它，3:2 平板上会出现游戏自带的 16:10 黑边。

官方换了游戏版本或加载器（`nonManagedPayloadDigest` 变化）时，启动器和 Updater 都不会接收，用户停在当前版本，
需要本仓库发新启动器。其余更新自动接收。

## 组成

| 目录 | 作用 |
|---|---|
| `../../upstream/BazaarPlusPlus` | 上游仓库的 git submodule；只用其中 `BazaarPlusPlus.Updater` 的源码，不做修改 |
| `BazaarPlusPlus.OfficialPayload` | 内置包暂存、签名与兼容性筛查、拒绝列表、启动守卫；编译上游 Updater 源码。无 BepInEx/Unity 引用 |
| `BazaarPlusPlus.BepInExBridge` | BepInEx 插件：安装并加载官方包，加载 SQLite 原生库，去黑边 |
| `MelonLoader` | MelonLoader API 兼容层，程序集名 `MelonLoader`、版本跟随 LemonLoader（0.7.4） |
| `official-payload.json` | 打进 APK 的官方包版本、地址、大小与 SHA-256 |
| `../tools/patch-bepinex-interop-prefix` | 给启动器的 `BepInEx.Unity.IL2CPP.dll` 打补丁，让设备生成 MelonLoader 式 `Il2Cpp` 前缀的 interop |

官方包还引用 LemonLoader 自带的 `Microsoft.NET.StringTools`，以及 APK 里的 `libe_sqlite3.so`，二者由桥接插件随
`BepInEx/plugins` 提供。

### 为什么要给 BepInEx 打前缀补丁

官方包按 MelonLoader 的 interop 命名编译（`Il2CppTheBazaar`、`Il2Cpp.BoardManager`），对应 Il2CppInterop 的
`Il2CppPrefixMode.OptOut`；BepInEx 默认 `OptIn`，生成的是 `TheBazaar`。补丁让 BepInEx 用 `OptOut` 生成，并在
interop 哈希上加标记，使已有的无前缀 interop 在下次启动时重新生成一次。interop 仍按当前安装的游戏在设备上生成。

## 构建

前置：.NET SDK 10（`global.json`）；`git submodule update --init upstream/BazaarPlusPlus`。

1. **设备生成的带前缀 interop**：先装一次打过前缀补丁的 core，启动游戏让 BepInEx 生成 interop，再拉到本机作编译引用：

   ```powershell
   adb pull /sdcard/Android/data/com.bepinex.android/files/BepInEx_Android/com.tempogames.thebazaar/BepInEx/interop D:\thebarzaar\android-interop-prefixed
   ```

2. **编译**：

   ```powershell
   dotnet build .\android-port\upstream-host\BazaarPlusPlus.BepInExBridge\BazaarPlusPlus.BepInExBridge.csproj -c Release `
     -p:InteropPath=D:\thebarzaar\android-interop-prefixed
   ```

3. **打包**：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\android-port\package-launcher.ps1 -Upstream -VersionCode <递增版本号> `
     -InteropSnapshot D:\thebarzaar\android-interop-prefixed
   ```

   `-Upstream` 会：从 `BazaarPlusPlus.BepInExBridge/bin/Release/net10.0` 取插件，按 `official-payload.json` 下载并核对
   官方包后放进 `plugins/`，给 `core/BepInEx.Unity.IL2CPP.dll` 打前缀补丁，跳过托管 codec 改名。

## 升级内置的官方包

更新 `official-payload.json`（版本、地址、大小、SHA-256 取自官方清单），重新打包。已装用户不依赖它：启动器会自己下载。

需要改代码的情况，桥接插件的兼容性筛查会在日志里写明原因（`official_payload.staged.rejected reason_code=...`）：

- `assembly_too_old:MelonLoader...`：官方换了 LemonLoader 版本，提高 `MelonLoader.csproj` 的 `Version`；
- `member_missing:MelonLoader...` / `type_missing:MelonLoader...`：在 `MelonLoader/` 补上官方新用到的 API；
- `assembly_missing:<名字>`：官方依赖了 LemonLoader 自带、包里没有的程序集，随桥接插件提供；
- `member_missing:Il2Cpp...`：设备 interop 与 LemonLoader 的生成结果不一致，需要单独排查；
- `target_framework:<N>`：官方升级了目标框架，启动器运行时需要随之升级。

## 测试

```bash
dotnet test android-port/upstream-host/BazaarPlusPlus.OfficialPayload.Tests/BazaarPlusPlus.OfficialPayload.Tests.csproj
```

```bash
dotnet test android-port/upstream-host/MelonLoader.Tests/MelonLoader.Tests.csproj
```

```bash
dotnet test android-port/tools/patch-bepinex-interop-prefix/tests/Tests.csproj
```

```bash
python android-port/tools/bpp-launcher/test_launcher_update.py
```

最后一个也运行 `BppOfficialPayloadTests`（清单解析、跳过条件、zip 条目白名单）。启动器安装器的 Java 测试
（`package-launcher.ps1` 第 1 步）覆盖两种模组包结构，以及从原插件切换到这里时的旧文件清理。

## 真机验收

2026-10-30 在 vivo PA2573（Android 16，启动器 6.0.0-preview.46，游戏 1.0.12825）上手工部署桥接插件与内置官方 6.0.2：

- 首次启动：暂存内置包 → 设备上签名与兼容性筛查通过 → 上游 Updater 替换 13 个文件 → 加载官方 6.0.2（sha256 与
  官方清单一致）→ 26 个 Harmony 补丁全部安装 → 主菜单显示 `BPP 6.0.2`；
- 再次启动：不再暂存，Updater 无事可做，直接加载；
- 去黑边补丁生效，3:2 屏幕全屏；
- 场景事件从 `AppStart` 起都能收到。游戏的 IL2CPP 构建没有 `Scene.GetBuildIndexInternal_Injected`，`buildIndex` 传 -1。

仍待验收：

- 用 `-Upstream` 打包的启动器 APK 覆盖安装后从原插件切换，以及启动器的后台下载（验收时用的是旧启动器，内部数据目录
  无法用 adb 写入）；
- 对局内功能；
- 插件内 HTTPS：CoreCLR 的 `HttpClient` 在设备上全部报 `AndroidCrypto SslException`（Bundle 上传、阵容库、赞助名单、
  官方自带的更新检查）。

## 待决定

上传与联网照上游行为：Bundle 上传和 Ghost 同步走 `mod-api-v6.bazaarplusplus.com`，阵容库和赞助名单走
`cdn.bazaarplusplus.com`，账号绑定走 `bazaardb.gg`。启动器每次打开时读一次官方发布清单。要不要向上游说明这个 fork
的流量，需要维护者决定。

官方插件自带的 `AndroidVersionCheck` 在本启动器里照常运行：插件内 HTTPS 修好后，它可能提示玩家去装官方 APK。
