# 上游 Android Host 接入（实验）

把上游 [BazaarPlusPlus](https://github.com/BazaarPlusPlus/BazaarPlusPlus) 的 Android Host
（`bazaarplusplus-mod/src/BazaarPlusPlus.Android`，一个 LemonLoader MelonMod）原样编译，跑在本仓库的
BepInEx 启动器里。目的：插件功能直接跟随上游，不再逐次手工移植。

状态：容器内能编译的部分已编译和测试；涉及游戏程序集的编译与全部运行时行为**尚未在真机验收**。
验收通过前，`package-launcher.ps1` 默认仍打包本仓库原插件，加 `-Upstream` 才打包这里的版本。

## 组成

| 目录 | 作用 |
|---|---|
| `../../upstream/BazaarPlusPlus` | 上游仓库的 git submodule，固定在一个提交上；源码不做任何修改 |
| `BazaarPlusPlus.ModApi/Storage/Core` | 上游共享库的 net6.0 工程，只引用 submodule 里的源码 |
| `BazaarPlusPlus.Android` | 上游 Android Host 的 net6.0 工程；`Overrides/` 替换其中两个文件（见下） |
| `MelonLoader` | MelonLoader API 兼容层，程序集名就叫 `MelonLoader`，只实现上游用到的部分 |
| `BazaarPlusPlus.BepInExBridge` | BepInEx 插件：建立兼容层、加载 SQLite 原生库，把 Unity 的帧/场景/退出回调转给上游的 melon |
| `Polyfills` | 上游用到、.NET 6 没有的 BCL 成员（C# 14 静态扩展成员），调用处不用改 |
| `../tools/patch-bepinex-interop-prefix` | 给启动器的 `BepInEx.Unity.IL2CPP.dll` 打补丁，让设备上生成的 interop 带 MelonLoader 式 `Il2Cpp` 前缀 |

### 为什么要给 BepInEx 打前缀补丁

上游代码按 MelonLoader 的 interop 命名编写（`using Il2CppTheBazaar;`、`Il2Cpp.BoardManager`），对应
Il2CppInterop 的 `Il2CppPrefixMode.OptOut`；BepInEx 默认用 `OptIn`，生成的是 `TheBazaar`。补丁让 BepInEx
用 `OptOut` 生成，并在 interop 哈希上加标记，使已有的无前缀 interop 在下次启动时重新生成一次。

interop 仍在设备上、按当前安装的游戏生成，所以游戏通过商店更新后照常自动重新生成。不分发上游的
Interop Snapshot：它绑定上游 APK 的确切游戏版本，与已安装的游戏不一致时会按错误的元数据 token 调用原生方法。

前缀同时改了程序集名（`Il2CppNewtonsoft.Json`、`Il2CppMessagePack`），所以插件自带的托管
Newtonsoft/MessagePack 不再与游戏代理程序集同名，原插件的 `package-managed-codec` 改名步骤在这里不需要。

### Overrides

上游的 `Lobby/AndroidVersionCheck.cs` 和 `Lobby/AndroidApkUpdatePrompt.cs` 会读取上游的 Android 发布清单、
暂存上游的模组更新包、引导玩家去装上游签名的 APK。这些在本启动器里都不成立，更新由启动器的
`BppLauncherUpdateChecker` 负责，所以 `Overrides/` 用同样的类型签名提供不联网的空实现。

`BazaarPlusPlus.Android.csproj` 记录了被替换文件的 sha256；上游改了它们，构建会报警告，提醒对照更新 override。

## 构建

前置：.NET SDK 10（`global.json`）；`git submodule update --init upstream/BazaarPlusPlus`。

1. **首次在设备上生成带前缀的 interop**：用 `-Upstream` 打包并安装启动器（下文第 3 步），启动游戏一次，
   等 BepInEx 生成完 interop（启动器首次启动的灰屏阶段）。然后把它拉到本机，作为编译引用：

   ```powershell
   adb pull /sdcard/Android/data/com.bepinex.android/files/BepInEx_Android/com.tempogames.thebazaar/BepInEx/interop D:\thebarzaar\android-interop-prefixed
   ```

   编译用的 interop 必须是设备生成的那一份：两边 Cpp2IL 版本不同，成员名可能不同。游戏更新后，
   如果上游代码用到的成员变了名，重新拉取一次。

   第一次还没有带前缀的 interop 时，宿主编译不过。先用原插件的 modpack 加上打过补丁的 core 安装一次，
   让设备生成 interop；或者直接用上游 `snapshot-android` 对同一游戏 APK 生成的快照作为首次编译引用。

2. **编译**：

   ```powershell
   dotnet build .\android-port\upstream-host\BazaarPlusPlus.BepInExBridge\BazaarPlusPlus.BepInExBridge.csproj -c Release `
     -p:InteropPath=D:\thebarzaar\android-interop-prefixed `
     -p:BepInExCorePath=<启动器 BepInEx\core 目录>
   ```

   `BepInExCorePath` 提供启动器自带的 `0Harmony.dll` 与 `Il2CppInterop.Runtime.dll`（编译引用，不打包）。

3. **打包**：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\android-port\package-launcher.ps1 -Upstream -VersionCode <递增版本号> `
     -InteropSnapshot D:\thebarzaar\android-interop-prefixed
   ```

   `-Upstream` 会：从 `BazaarPlusPlus.BepInExBridge/bin/Release/net6.0` 取插件，给 `core/BepInEx.Unity.IL2CPP.dll`
   打前缀补丁，跳过托管 codec 改名。`-InteropSnapshot` 只用来按名字排除游戏程序集，不会打包它们。

## 跟进上游

```bash
git -C upstream/BazaarPlusPlus fetch origin master
```

```bash
git -C upstream/BazaarPlusPlus checkout <上游提交>
```

然后重新编译、打包、真机冒烟，再提交 submodule 指针。需要手工处理的只有：

- 编译报某个 BCL 成员不存在：在 `Polyfills/` 补上（.NET 7 以后的 API）；
- 编译报某个 MelonLoader API 不存在：在 `MelonLoader/` 补上；
- 构建警告 `Upstream changed ... which Overrides/ replaces`：对照上游新版本更新 override，再更新 sha256；
- 上游 `src/BazaarPlusPlus.{Core,Storage,ModApi,Android}/*.csproj` 改了包引用或嵌入资源：同步到这里的工程；
- 新的 Harmony 补丁在本启动器的 ARM64 运行时上崩溃：只能真机发现，可用上游的
  `[Android] PatchMode`（`MelonLoader/UserData/BazaarPlusPlus.cfg`）先切到 `Observe`/`Disabled` 定位。

## 测试

```bash
dotnet test android-port/upstream-host/MelonLoader.Tests/MelonLoader.Tests.csproj
```

```bash
dotnet test android-port/tools/patch-bepinex-interop-prefix/tests/Tests.csproj
```

启动器安装器的 Java 测试（`package-launcher.ps1` 第 1 步）覆盖了两种模组包结构，以及从原插件切换到这里时的旧文件清理。

## 真机待验收

- 补丁能否作用于 be.790 的 `BepInEx.Unity.IL2CPP.dll`（测试基于 BepInEx master 的同名方法结构；结构不同时补丁工具报错，不会写坏文件）；
- 补丁后 interop 是否真的重新生成且带前缀；BepInEx 自身只依赖 `Il2Cppmscorlib` 与 `UnityEngine.*`，两种模式下名字相同；
- `BridgePlugin` 的 `AddComponent`、场景事件委托转换是否正常；BepInEx 在第一次场景切换时才加载插件，第一个场景的 `OnSceneWasLoaded` 可能收不到；
- `e_sqlite3` 加载（沿用原插件 `AndroidNativeLibraries` 的做法）；
- 数据目录：melon 基础目录是游戏根目录下的 `MelonLoader/`，按上游 `AndroidPaths` 推得的 Data Root 是游戏根目录下的 `BazaarPlusPlusV6`，与原插件相同，两边 `user_version` 都是 1。设置文件换成 `MelonLoader/UserData/BazaarPlusPlus.cfg`，原插件的 `BepInEx/config/BazaarPlusPlus.cfg` 不再读取；
- 上游的每个 Harmony 补丁在 ARM64 上能否安装（原插件踩过 `Nullable<int>` 参数与大元组返回值的崩溃）。

## 待决定

上传与联网照上游行为：Bundle 上传和 Ghost 同步走 `mod-api-v6.bazaarplusplus.com`，阵容库和赞助名单走
`cdn.bazaarplusplus.com`，账号绑定走 `bazaardb.gg`。User-Agent 带的是上游的 `android-aarch64` 平台标记。
没有 `BazaarPlusPlus.version` 文件，所以上报的版本没有正式版的 `.prod` 后缀。要不要向上游说明这个 fork 的流量，
或在请求上加区分标记，需要维护者决定。
