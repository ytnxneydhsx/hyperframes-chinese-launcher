# macOS 中文启动器

构建：在此目录执行 `./build.sh`，需要 macOS 自带的 Xcode Command Line Tools。默认输出为仓库 `dist/macos/HyperFrames中文启动器.app`。`LAUNCHER_OUTPUT_DIR` 可指定输出目录，`LAUNCHER_ARCH` 支持 `universal`（默认）、`arm64`、`x86_64`。构建只使用 Swift、系统工具和 `../shared/resources` 内的翻译资源及图标，仅对生成的启动器进行 ad hoc 签名。

启动器优先连接正在运行的官方 HyperFrames，再使用已保存的选择、Launch Services 查询结果，以及系统或用户 Applications 中的常见位置。候选必须为 `.app` 且 bundle identifier 为 `dev.hyperframes.desktop`；找不到时显示应用选择窗口。菜单“选择 HyperFrames 应用…”可更改位置。运行中的已选副本保持当前连接；选择另一份副本后保存新位置，等当前副本退出后再采用该位置。启动器按实际 bundle URL 和主进程 PID 校验调试端口，避免连接另一份副本。

选择仅写入用户 `~/Library/Application Support/HyperFrames Chinese Launcher/settings.json`，不修改官方 app。启动器状态和单实例锁同样位于该目录。若应用没有本机调试入口，仍需通过现有安全重启确认；保存提示或取消退出会阻止重启。退出启动器会恢复已连接界面的英文并保留 HyperFrames 运行。
