# BazaarPlusPlus Android

`The Bazaar` 的 BazaarPlusPlus 模组的 **Android（arm64-v8a / IL2CPP）移植**分支。

本仓库基于上游 [BazaarPlusPlus/BazaarPlusPlus](https://github.com/BazaarPlusPlus/BazaarPlusPlus)（MIT），在其 `master` 之上叠加了 Android 移植所需的改动，目标是让**配装（Live Build）**、**图鉴（Collection）**、**历史（History）** 三大模块与桌面版行为一致。

## 目录

| 路径 | 说明 |
| --- | --- |
| `bazaarplusplus-mod/` | 模组主工程（.NET Standard 2.1 / net6.0，BepInEx 插件） |
| `bazaarplusplus-installer/` | Tauri 桌面安装器 |
| `bazaarplusplus-site/` | 官网前端 |
| `bazaarplusplus-server/` | 服务端 |
| `bazaarplusplus-analyzer/` | 数据/指标分析 |
| `android-port/` | Android 移植的交接文档、分发脚本与逆向辅助工具源码 |

## Android 构建

需要 .NET SDK 8（`rollForward: Major`）与 IL2CPP 代理程序集目录（`ManagedPath`，由 Il2CppDumper + Il2CppInterop 从游戏 `global-metadata.dat` 生成，**不随仓库分发**）。

```powershell
$env:DOTNET_ROLL_FORWARD = 'Major'
& dotnet build bazaarplusplus-mod/src/BazaarPlusPlus/BazaarPlusPlus.csproj `
    -c Debug -p:BppAndroid=true `
    -p:ManagedPath=<IL2CPP 代理程序集目录> `
    -p:NuGetAudit=false -p:TreatWarningsAsErrors=false --nologo
```

`android-port/` 下另有：

- `HANDOVER-ANDROID-PORT.md` —— 移植进展、已定位的原生崩溃根因、当前待办（**接手先读**）
- `package-modpack.ps1` / `install-to-device.ps1` —— 打包与双写部署
- `patch-harmony-arm64/` —— ARM64 按值 `CancellationToken` 装箱修补工具
- `package-managed-codec/`、`codec-smoke/` —— Managed MessagePack 程序集身份打包与验证
- `ilstool/` —— Mono.Cecil 的 IL2CPP 运行时补丁工具

部署到测试机需要自备 BepInEx Android 启动器与外置 modpack 目录结构，脚本不包含任何签名材料。

## 安全说明

本仓库**不包含**任何签名密钥、口令或令牌。上游 `HANDOVER` 文档中的签名口令已脱敏为 `<REDACTED>`；keystore、APK、`.so`、真机截图与日志、逆向产物（`apk_extracted/`、`android-managed/`、`dumped*/`）均通过 `.gitignore` 排除。

## 许可

MIT，见 [LICENSE](LICENSE)。原始版权归 Xinyu YANG 与 BazaarPlusPlus Team 所有；本分支的 Android 移植改动同样以 MIT 发布。
