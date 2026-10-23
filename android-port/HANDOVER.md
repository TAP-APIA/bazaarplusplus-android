# BazaarPlusPlus 安卓端移植工作交接文档 (HANDOVER)

## 一、 当前工作区文件与环境状态 (`D:\thebarzaar`)

1. **原始安装包与核心提取文件**：
   - `D:\thebarzaar\TheBazaar.apk`：原版安卓安装包（2.83 GB，包名 `com.tempogames.thebazaar`，版本 `1.0.12825-production-android-d9601ded`，引擎 **Unity `6000.3.11f1`**，仅 `arm64-v8a` IL2CPP）。
   - `D:\thebarzaar\apk_extracted\`（已从 APK 解压出的核心二进制文件，无需重复解压 2.83 GB APK）：
     - `global-metadata.dat`（22,057,156 字节，Magic `0xFAB11BAF`，**Metadata Version `39`**）
     - `global-metadata-v29.dat`（由 `tools/convert_metadata_v39_to_v29.py` 转换出的 v29 格式元数据）
     - `libil2cpp.so`（148,673,008 字节，ARM64，保留全部 241 个标准 `il2cpp_*` 导出符号）
     - `libmain.so`（6,696 字节）
     - `libunity.so`（25,155,632 字节）
     - `AndroidManifest.xml`（21,692 字节）
2. **源码仓库与编译环境**：
   - `D:\thebarzaar\BazaarPlusPlus\`：已完整检出 `BazaarPlusPlus/BazaarPlusPlus` 仓库（已开启 `git config core.longpaths true` 解决 macOS bundle 路径过长问题）。
   - `D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\global.json`：已将 SDK 版本策略调整为 `"8.0.100"` + `"rollForward": "latestMajor"`。
   - **已安装 .NET 8 SDK (`8.0.426`)**：位于 `C:\Users\administrator\.dotnet\dotnet.exe`（系统 `C:\Program Files\dotnet` 仅有 Runtime 无 SDK，编译时请使用 `C:\Users\administrator\.dotnet\dotnet.exe` 或将其加入 `PATH`）。
3. **逆向与转换工具 (`D:\thebarzaar\tools\`)**：
   - `D:\thebarzaar\tools\convert_metadata_v39_to_v29.py`：Unity `6000.3.11f1` Metadata v39 → v29 结构转换脚本。
   - `D:\thebarzaar\tools\Il2CppDumper\Il2CppDumper.exe`：已下载解压（需设置环境变量 `$env:DOTNET_ROLL_FORWARD="Major"` 运行）。

---

## 二、 已完成的关键逆向与技术发现（直接复用，无需重查）

### 1. APK 安全性与符号完整性
* **无加壳、无反作弊、无签名校验**：`classes.dex` 中仅有 `com.tempogames.thebazaar.R` 与 `com.tempogames.networksecurity.R` 两个资源类，无任何自定义 Java 校验代码。
* **网络安全配置 (`res/8G.xml`)**：仅信任 `<certificates src="system"/>`，无 SSL Pinning。
* **代码同源度 100%**：`BazaarPlusPlus` 引用的 **109 个游戏类与 37 个核心被 Patch 方法在安卓版 `global-metadata.dat` 中 100% 存在**，且安卓版 `1.0.12825` 已原生包含 `TReferenceValueUniqueCardCount`（无需触发 `CompatibleUniqueCardCount` 的 `Reflection.Emit` 分支）。

### 2. `libil2cpp.so` 核心注册表虚拟地址（VA）与文件偏移
* **ELF `PT_LOAD` 映射关系**：
  - Segment 0: `file 0x0..0x36cefc4` → `VA 0x0`
  - Segment 1: `file 0x36cefd0..0x83147d0` → `VA 0x36d2fd0`
  - Segment 2: `file 0x83147d0..0x891efb8` → `VA 0x831c7d0`
  - Segment 3: `file 0x891efb8..0x8dc87e8` → `VA 0x892afb8`
* **`Il2CppCodeRegistration`**：
  - **Virtual Address (VA)**：`0x831cb30`（文件偏移 `0x8314b30`）
  - 包含 `codeGenModulesCount = 145`（`VA 0x831cba8`），`genericMethodPointersCount = 227801`，`invokerPointersCount = 41350`，`unresolvedVirtualCallCount = 7713`。
* **`Il2CppMetadataRegistration`**：
  - **Virtual Address (VA)**：`0x86994d8`（文件偏移 `0x86914d8`）
  - 包含 `genericClassesCount = 31471`，`genericInstsCount = 25308`，`genericMethodTableCount = 228453`，`typesCount = 84983`（`types` 数组指针 `VA 0x87246f8`），`methodSpecsCount = 271407`，`fieldOffsetsCount = 24657`，`typeDefinitionsSizesCount = 24657`。

### 3. `global-metadata.dat` Version 39 精确二进制结构（已完全破解）
Unity `6000.3.11f1` 的 Metadata v39 相比 v29 的完整结构差异如下：
1. **文件头（Header，共 380 字节）**：`magic (4B = 0xFAB11BAF)` + `version (4B = 39)` + **31 组 `(offset:i32, size:i32, count:i32)` 三元组**（v29 为 31 组 `(offset, size)` 二元组，共 256 字节）。
2. **变长索引结构差异（仅以下 5 张表与 v29 不同，其余 26 张表字节布局完全一致）**：
   - **Section 5 `methods` (32B)**：格式为 `<ihiiihIHHHH`（`nameIndex:i32, declaringType:i16, returnType:i32, returnParameterToken:i32, parameterStart:i32, genericContainerIndex:i16, token:u32, flags:u16, iflags:u16, slot:u16, parameterCount:u16`）。
   - **Section 12 `genericParameters` (14B)**：格式为 `<hiHHHH`（`ownerIndex:i16` 由 4B 缩减为 2B）。
   - **Section 19 `typeDefinitions` (82B)**：格式为 `<5ih9i8H2I`（第 6 个字段 `genericContainerIndex:i16` 由 4B 缩减为 2B，其余字段均为 4B/2B 与 v29 一致）。
   - **Section 20 `images` (36B)**：格式为 `<iihIhIiIII`（`typeStart:i16` 与 `exportedTypeStart:i16` 由 4B 缩减为 2B）。
   - **Section 21 `assemblies` (68B)**：格式为 `<17i`（在 `token` 后偏移 `8..12` 处多了一个 4B 字段 `moduleToken = 1`，去掉该 4B 即与 v29 的 64B 完全一致）。
   - **注意**：原版 `Il2CppDumper` 在读取 `global-metadata-v29.dat` 时会自动切换为 `Il2Cpp Version: 29.1`，而 `Il2CppDumper` 的 v29.1 模式假设 `libil2cpp.so` 中 `Il2CppType.data` 存储的是 `Il2CppMetadataTypeHandle` 内存指针而非 `TypeDefinitionIndex` 下标，从而在 `GetTypeName` / `Il2CppDecompiler.cs:line 27` 报 `IndexOutOfRangeException`。
   - **直接解决方案**：直接拉取 `Perfare/Il2CppDumper` 源码（或编写百行 C# `Mono.Cecil` 脚本），按上述已破解的 v39 元数据表布局 + `libil2cpp.so` 的 `types` 表（`VA 0x87246f8`，共 84,983 项），用 `C:\Users\administrator\.dotnet\dotnet.exe` 编译运行即可直接产出 145 个完整的 `DummyDll/*.dll`。

---

## 三、 已与用户确认的方案与后续执行步骤

用户已确认采用**「APK 轻量引导注入 + 外置目录热加载 `.dll`」**方案（免编译/免重打大包即可日常更新插件，游戏大更时仅需秒级流式修补引导头）：

### 下一步待办清单（供接手 Agent 直接执行）

1. **步骤 1：生成全套编译引用程序集 (`DummyDll`) 与 `Il2CppInterop` 代理库**
   - 先将 `D:\thebarzaar\tools\Il2CppDumper\config.json` 中的 `"GenerateStruct"` 设为 `false` 测试 `DummyDll` 生成；若 `Il2CppDumper` 因 Unity 6000.3 的 `Il2CppGenericClass` 结构体字段偏移变化报错，直接编写一个百行 C# (`Mono.Cecil`) 工具读取 `global-metadata.dat` (v39) + `libil2cpp.so` 的 `types` 表（`VA 0x87246f8`）直接生成 `DummyDll/*.dll`。
   - 使用 `Il2CppInterop.CLI` 将 `DummyDll` 转换为 `Il2CppInterop` 代理程序集。

2. **步骤 2：完成 `bazaarplusplus-mod` 的 Android IL2CPP 适配编译**
   - `BazaarPlusPlus.Localization`、`BazaarPlusPlus.Storage`、`BazaarPlusPlus.ModApi` 三个子工程保持原样编译（需配齐托管 `Newtonsoft.Json.dll`、`MessagePack.dll` 及 `android-arm64` 的 `libe_sqlite3.so`）。
   - 主工程 `BazaarPlusPlus` 适配要点（详见评估报告）：
     - 在 `Plugin.Awake()` 使用 `ClassInjector.RegisterTypeInIl2Cpp<T>()` 注册 31 个自定义 `MonoBehaviour` 并补齐 `(IntPtr)` 构造。
     - 重写 2 处 `[HarmonyTranspiler]`（`CollectionLockVariantPanelPatch.cs` 与 `VOPlayerPatches.cs`）。
     - 替换 2 处 `___fieldName` Harmony 字段注入（`HistoryPanelMenuPatches.cs`、`PostCombatImpactRecapPatch.cs`）为属性访问，并将 31 处 `AccessTools.Field` 兼容为属性读取。
     - 适配 4 个异步补丁文件中的 `Il2CppSystem.Threading.Tasks.Task` 返回类型。
     - 将 `BepInExPathProvider.cs` 数据根目录改为 `Application.persistentDataPath/BazaarPlusPlusV6`。

3. **步骤 3：移动端触屏交互适配**
   - 将 6 个文件中的 `Mouse.current` / `Keyboard.current` 封装为兼容 `Pointer.current` / `Touchscreen.current`。
   - 为 `LiveBuildPanel`（原 `CapsLock`）、`UpgradePreview`（原 `Shift`）、`PostCombatImpact`（原 `Shift`）补齐触屏按钮/点击切换入口。
   - 一期先在 `CombatReplayRecordingGate.cs` 中将安卓端的本地 MP4 视频录制置为不可用（保留完整的游戏内实时战斗回放与 Ghost 对战重播）。

4. **步骤 4：制作增量 APK 修补脚本与外置插件包**
   - 注入 `arm64-v8a` 版 `BepInEx.Unity.IL2CPP` 引导层 (`libdoorstop.so`) 到 `libmain.so`，配置从 `/sdcard/Android/data/com.tempogames.thebazaar/files/BepInEx/plugins/` 加载 `BazaarPlusPlus.dll`，并用 `apksigner` 签名输出可直装 APK。
