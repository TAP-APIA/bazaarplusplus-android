# BazaarPlusPlus Android

> **这不是上游项目。** 这是 [BazaarPlusPlus](https://github.com/BazaarPlusPlus/BazaarPlusPlus) 的 **Android 移植分支**，由个人维护。
> 上游官网、下载、Ko-fi 与发行版均与本仓库无关；官方 README 已归档到 [`docs/upstream/`](docs/upstream/README.md)。
> 遇到问题请在本仓库提 issue，**不要**去上游仓库反馈与 Android 移植相关的内容。

把 `The Bazaar`（Unity 6000.3 / IL2CPP / arm64-v8a）上的 BazaarPlusPlus 模组移植到 Android 平板，目标是在触屏环境里做到与桌面版行为一致的三大模块：

| 模块 | 状态 | 说明 |
| --- | --- | --- |
| **配装**（Live Build） | 进行中 | 局内实时 shop / board / stash，按候选物品匹配十胜终局阵容 |
| **图鉴**（Collection） | 进行中 | 全卡牌目录、筛选器、原生卡牌检视窗 |
| **历史**（History） | 进行中 | 对局记录、收藏、战斗回放与本地录制 |

> 具体进度、已定位的原生根因和当前待办见 [`android-port/HANDOVER-ANDROID-PORT.md`](android-port/HANDOVER-ANDROID-PORT.md)。这是接手时最该先读的文件。

## 与上游的差异

- 新增 `BppAndroid` 编译目标，插件以 .NET 6 + BepInEx 6 IL2CPP 运行在游戏子进程内
- 触屏 UI 入口（`BppTouchEntry`），右上角 UGUI dock 替代桌面快捷键
- 大量 IL2CPP 互操作适配：`Il2CppCompat`（`GetWorldCornersManaged`、`BppUnityCast`）、`BppCoroutines`（主线程续体）、ARM64 按值 `CancellationToken` 修补
- 依赖装配调整：托管 `Newtonsoft.Json` 与 `Bpp.Managed.*` MessagePack 使用独立程序集身份，避免与游戏原生代理冲突
- Android 上显式降级的功能会在启动日志的 `plugin.android.patch_capabilities` 中列出，不静默失败

## 构建

需要 .NET SDK 8（`$env:DOTNET_ROLL_FORWARD='Major'`），以及由 Il2CppDumper + Il2CppInterop 从游戏 `global-metadata.dat` 生成的代理程序集目录。**该目录是逆向产物，不随仓库分发**，需自行生成。

```powershell
$env:DOTNET_ROLL_FORWARD = 'Major'
& dotnet build bazaarplusplus-mod/src/BazaarPlusPlus/BazaarPlusPlus.csproj `
    -c Debug -p:BppAndroid=true `
    -p:ManagedPath=<IL2CPP 代理程序集目录> `
    -p:NuGetAudit=false -p:TreatWarningsAsErrors=false --nologo
```

产物为 `bin/Debug/net6.0/BazaarPlusPlus.dll`，部署时需要**双写**到设备的 `BepInEx/plugins/` 与 `modpacks/bpp/plugins/`（启动器每次启动会用 modpack 覆盖前者）。

## 仓库结构

| 路径 | 说明 |
| --- | --- |
| `bazaarplusplus-mod/` | 模组主工程（本仓库改动的主体） |
| `bazaarplusplus-installer/` | 上游 Tauri 桌面安装器（Android 未适配） |
| `bazaarplusplus-site/` `bazaarplusplus-server/` `bazaarplusplus-analyzer/` | 上游其余组件，未针对 Android 修改 |
| `android-port/` | 移植交接文档、分发脚本、逆向辅助工具源码 |
| `docs/upstream/` | 上游原始 README，仅作出处存档 |

`android-port/` 内含：

- `HANDOVER-ANDROID-PORT.md` —— 进展、根因清单、待办
- `package-modpack.ps1` / `install-to-device.ps1` —— 打包与双写部署
- `patch-harmony-arm64/` —— ARM64 按值 `CancellationToken` 装箱修补工具
- `package-managed-codec/`、`codec-smoke/` —— Managed MessagePack 程序集身份打包与验证
- `ilstool/` —— Mono.Cecil 的 IL2CPP 运行时补丁工具

## 不在本仓库的内容

为了不让仓库变成构建产物仓库，以下内容全部通过 `.gitignore` 排除：

- 签名材料（keystore / jks / p12）与任何口令、令牌 —— 文档中的历史口令已脱敏为 `<REDACTED>`
- APK、`.so`、`.zip` 等二进制交付物
- 逆向产物（`apk_extracted/`、`android-managed/`、`dumped*/`）
- 真机截图、UI dump、logcat 与崩溃 tombstone

因此**本仓库不能直接产出可安装的 APK**，需要本地自备上述产物。

## 许可与致谢

MIT，见 [LICENSE](LICENSE)。原始版权归 Xinyu YANG 与 BazaarPlusPlus Team；本分支的 Android 移植改动同样以 MIT 发布。上游项目地址：<https://github.com/BazaarPlusPlus/BazaarPlusPlus>。
