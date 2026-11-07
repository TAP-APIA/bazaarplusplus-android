# 运行基础依赖

本应用独立编写 Java 界面、游戏宿主、JNI 数据桥、数据库、同步引擎和 .NET 战绩 Hook。没有编译或提取旧 BPP 启动器的 Java/Kotlin/DEX、模组、业务资源。

为在 Android ARM64 中运行只读 IL2CPP Hook，构建时获取通用运行基础：

- 来源：https://github.com/NextBep/BepInEx.Android.Launcher
- 提交：`44e1e59c40e6f52df45139db2c86ad4499d2881d`
- 仅使用 `app/src/main/cpp`、`jniLibs/arm64-v8a`、`assets/BepInEx.Android.zip` 和 `assets/dotnet.zip`。
- 原始输入的逐文件 SHA-256 记录在 `foundation.lock.json`，后续构建必须完全匹配。
- 构建副本中的本地适配仅为配置 Intent 键和 Java ABI 类名；ClassLoader.loadClass 使用点分隔的独立回调类名。源码版权头保留。上游产品 Java/Kotlin、Compose、图标、布局和模组管理器不参与构建。

该快照没有顶层 LICENSE 文件；原生文件保留 XtraCube 的版权声明，不把整个快照擅自声明为 MIT。底层组件分别由其原项目维护：BepInEx、Il2CppInterop、Harmony、MonoMod、Cpp2IL、.NET/CoreCLR、Dobby、OpenSSL。压缩包内已有的许可证和声明随依赖保留。对外分发时应逐项确认这些组件及该原生快照的分发条款。

Gradle wrapper 来自 Gradle 官方 `v8.11.1`，遵循 Gradle 的 Apache-2.0 许可；distribution ZIP 的官方 SHA-256 固定在 wrapper 配置中。

游戏代理程序集是用户已安装游戏生成的编译输入，不是应用资源，不打包进 APK。首次启动会为当前安装的游戏生成独立代理缓存。
