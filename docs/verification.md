# 验证记录

版本：1.1.0。合并词典 2,561 条，159 个声明模板、155 个运行时规则。构建使用 .NET SDK 10.0.401；Windows 运行时依赖随单文件程序发布。

## 本机检查

- macOS arm64 与 x86_64 Universal 编译、plist 与 ad hoc 签名验证通过。
- 真实 macOS HyperFrames 启动与 CDP 接入、中文按钮检查通过。
- 官方应用复制到非默认位置后，通过保存选择启动并显示中文；官方安装包未移动或修改。
- 隔离 DOM fixture 的事件、SVG、输入值、聊天/代码/名称/字幕/片段保护、动态控件与恢复规则通过。
- C# CDP 客户端真实 WebSocket 握手、分片响应、异步事件和错误回复检查通过。
- Windows x64、ARM64 已交叉编译为自包含单文件；交叉编译不等同于 Windows 原生运行验证。

## Windows 检查

GitHub Actions 在 Windows x64 runner 执行 `--self-test`，10 项检查全部通过：监听端口归属、IPv4/IPv6 回环地址、拒绝通配及其他进程、主进程命令行、页面白名单、资源、当前用户 Mutex / NamedPipe。

`official_windows` 检查已在一次性 Windows x64 runner 验证官方 b313 ZIP（官方 SHA-256 校验通过）：启动本项目、真实 CDP 注入翻译、屏幕外临时 fixture、退出后恢复英文、原应用仍运行、exe 和 app.asar 哈希不变，全部通过。该检查使用保存的非默认应用位置；未覆盖所有安装器及注册表安装环境。

具体证据：[2026-10-12 工作流](https://github.com/ytnxneydhsx/hyperframes-chinese-launcher/actions/runs/38248996457)，代码提交 `c8dba158`。该次工作流的传输检查、Mac Universal 构建和两种 Windows 构建也全部通过。机器可读结果见 [Windows 集成报告](windows-verification.json)、[Windows 原生报告](windows-self-test.json) 和 [Mac 报告](macos-verification.json)。

## 边界

检查不登录账号、生成视频、创建项目或修改业务设置。临时 fixture 不触发原业务按钮。原生菜单、系统对话框、独立 Framey 和 iframe 内页面保留原样。Windows ARM64 在 x64 runner 上仅构建，尚需 ARM64 真机体验检查。

当前发布包的 Mac 签名为 ad hoc；Windows 程序没有发布者代码签名。普通用户环境、SmartScreen/Gatekeeper提示、不同安装/权限环境和未来上游版本仍需继续验证。
