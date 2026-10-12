# HyperFrames 中文启动器

独立的 macOS / Windows 简体中文启动器。通过原应用的本机调试接口临时翻译界面，原版 HyperFrames 单独安装。此项目为非官方项目。

[下载版本](https://github.com/ytnxneydhsx/hyperframes-chinese-launcher/releases) · [构建与验证](https://github.com/ytnxneydhsx/hyperframes-chinese-launcher/actions)

| 平台 | 启动程序 | 运行依赖 |
| --- | --- | --- |
| macOS 13+，Apple Silicon / Intel | `HyperFrames中文启动器.app` | 系统框架；无需 Node.js / Python |
| Windows 64 位，x64 / ARM64 | `HyperFramesChinese.exe` | 自包含 .NET；无需另装 .NET / Node.js / Python |

先安装官方 HyperFrames，再打开中文启动器。菜单栏或系统托盘可以切换语言、重新连接、选择原应用位置或退出启动器。再次双击启动器会打开 HyperFrames，已经连接时会切到前台。

## 应用如何定位

没有固定用户名或个人目录。

- macOS：运行中的 `dev.hyperframes.desktop` 应用 → 已保存选择 → Launch Services 按应用标识查找 → 常见 Applications 目录回退 → 用户选择 `.app`。候选校验官方 bundle identifier，连接关联实际路径和 PID。
- Windows：运行中的官方主进程 → 已保存选择 → HKCU/HKLM 的 App Paths / Uninstall 注册信息 → 用户及系统的常见安装目录回退 → 用户选择 `.exe`。文件名和产品信息校验后再启动，子进程与 Node 预览进程会排除。

已在运行的副本会保持当前连接。选择另一份应用时保存下次使用的位置，避免在工作中启动第二份副本。

## 使用和恢复

1. 打开启动器。原应用未运行时，启动器带本机调试参数启动它。
2. 原应用已在普通模式运行时，保存工作，再确认正常重启。取消保存或退出会停止重启。
3. 菜单切换语言可恢复英文。正常退出启动器会恢复英文，保留原应用运行。
4. 本机调试端口随原进程保留。需要关闭入口时，选择“普通模式重开”。Windows 应用若驻留托盘，请通过原应用正常退出后重试；启动器不会强制结束它。
5. 官方更新自动重启后，如未保留调试参数，双击启动器或选择“重新连接”，按提示正常重启以恢复中文。

强制结束启动器时，内存中的中文可能保留至页面刷新或原应用重开。

## 翻译范围

两端共用 `shared/resources` 的 JavaScript 引擎、中文词典和动态模板。当前合并 **2,561 个词条**，声明 159 个动态模板，运行时编译 155 个。包括 Windows 的文件资源管理器、回收站与更新错误提示。词条数表示适配规模，不代表全部界面覆盖率。

只调整已知界面文本、title 和 placeholder。保留原按钮事件、输入值、聊天、项目/文件/合成/元素名、代码、字幕、时间线片段和预览内容。所有 aria 标识保持原值；iframe 不进入。CodeMirror 的只读占位提示可翻译，可编辑内容保留。

原生菜单、系统对话框、独立 Framey 窗口、iframe 内页及未知错误仍可能显示英文。上游版本变化可能需要维护文案和保护选择器。没有修改或重新签名官方应用，也没有在线翻译请求。

## 从源码构建

macOS 需要 Xcode Command Line Tools：

```sh
bash macos/build.sh
# 默认 Universal；也支持 LAUNCHER_ARCH=arm64 或 x86_64
```

Windows 需要 .NET 10 SDK（仓库固定 10.0.401）：

```powershell
./windows/build.ps1 -Runtime win-x64
./windows/build.ps1 -Runtime win-arm64
```

输出位于 `dist/macos`、`dist/windows/<runtime>`。`EnableWindowsTargeting` 支持使用其他平台的 SDK 交叉编译 Windows 程序；Windows 原生 API 和 GUI 仍需在 Windows 验证。

修改分词典后，使用 Python 3 合并：

```sh
python3 scripts/merge-translations.py
```

## 验证

```sh
dotnet run --projecttests/TransportTests/TransportTests.csproj -c Release
```

Windows 原生自检：

```powershell
./HyperFramesChinese.exe --self-test
# 正常退出已有启动器，恢复英文并保留原应用：
./HyperFramesChinese.exe --quit
```

GitHub Actions 构建两端程序，运行传输与 Windows 原生检查。手动运行工作流并勾选 `official_windows` 可在一次性 Windows runner 上安装官方应用，验证真实 CDP、隔离 DOM fixture、退出恢复和原文件哈希。该检查不触发业务按钮、不生成视频或登录账号；测试脚本只允许在一次性 GitHub runner 执行。具体证据和边界见 [验证记录](docs/verification.md)。

## 源码与许可

```
macos/          Swift 菜单栏应用及构建脚本
windows/        C# WinForms 托盘应用及构建脚本
shared/         两端共同使用的翻译引擎、词典、图标
scripts/        词典合并工具
tests/         传输、原生与界面验证
audit/         文案来源统计
```

项目原创代码与翻译贡献采用 [Apache-2.0](LICENSE)。文案整理依据包括公开 HyperFrames Studio 0.8.149 与本机官方桌面应用的静态审查。完整官方应用和可执行文件需要从上游单独获取。图标源自官方应用，相关权利归原权利人，详见 [NOTICE](NOTICE)。

当前 Mac 发布包为 ad hoc 签名，Windows 发布包未进行发布者代码签名。面向大众分发时，应完成 Apple Developer ID 签名、公证及 Windows 代码签名。项目不会关闭系统安全设置。
