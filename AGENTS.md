# 仓库约定

本仓库只发布 **大巴扎助手 Android**（`bazaar-helper-android/`，包名 `com.bazzarhelper.android`）。
上游 BazaarPlusPlus Android 移植的源码已移除，历史仍在 git 记录里；需要参考时去
[TAP-APIA/bazaarplusplus-android](https://github.com/TAP-APIA/bazaarplusplus-android)。

- 说明与使用文档：`README.md`（产品）、`bazaar-helper-android/README.md`（构建、测试、数据边界）。
- 构建：`cd bazaar-helper-android && python3 tools/build.py --interop .local/game-interop`；`.local/` 是忽略目录，放游戏 interop 等编译输入。
- 验证：`python3 tools/check-apk.py build/BazaarHelper-*.apk`，真机测试见 `bazaar-helper-android/README.md` 的 `tools/test-device.py`。
- CI：`.github/workflows/bazaar-helper-android.yml`，签名与 interop 走仓库 secrets，不入库也不进 APK。
- `master` 受保护：改动走分支 + `gh pr create` + merge，合并后删除分支。提交信息用 Conventional Commits，scope 用 `bazaar-helper` 或 `docs`。
- 不提交 `.local/`、`build/`、`.keystore/`、游戏程序集或任何凭据。
