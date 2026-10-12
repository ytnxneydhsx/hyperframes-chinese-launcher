# HyperFrames 中文启动器（Windows）1.1.0

使用 .NET 10 WinForms，发布为包含运行时的单文件 EXE。用户无需安装 Node、Python 或 .NET。构建机器需要 .NET 10 SDK：

```powershell
./build.ps1
# ARM64 可选
./build.ps1 -Runtime win-arm64
```

运行 `dist/win-x64/HyperFramesChinese.exe`。启动器查找已安装 HyperFrames，必要时让用户选择 `HyperFrames.exe`，不会下载或修改原应用。首次开启中文时，如果应用已经运行，会询问正常关闭并重开；请先保存工作并处理原应用保存提示。取消或 30 秒超时会保留原应用。

托盘提供打开、重新连接、选择应用、中文开关、普通模式重开及退出。退出恢复页面英文并保留原应用运行；普通模式重开会正常关闭带调试参数的应用，以普通方式启动并退出助手。关闭被取消或应用驻留托盘时启动器继续运行，方便再次处理。重复启动通过当前用户命名管道唤醒现有启动器。

注入前检查 IPv4/IPv6 LISTEN 端口仅绑定 loopback、PID 属于所选主应用或其同路径子进程，页面必须为 `HyperFrames` 的 `page`，HTTP 本机 URL 路径仅 `/` 或 `/index.html`；WebSocket 也必须使用同一端口。每 3 秒刷新连接。内置翻译字典、动态模板与脚本。用户配置保存于 LocalAppData/HyperFramesChinese/config.json；state.json 仅存版本、开关、连接状态、计数、PID、端口，不保存项目、消息或凭证。

本机 macOS 环境仅用于源码及交叉编译检查；Windows GUI、保存提示、打包 EXE 与真实应用连接仍需在 Windows 验证。发布目录需由发布者按目标架构构建。

`HyperFramesChinese.exe --self-test` 在 Windows 无 GUI 检查资源、页面策略、本机监听地址及 PID 归属、当前用户 Mutex/管道。输出 JSON，失败退出码为 1；不会启动官方应用或产生业务数据。

`HyperFramesChinese.exe --quit` 请求当前用户已有助手恢复页面英文并退出，保留 HyperFrames 运行；没有助手运行时直接返回，不启动原应用。
