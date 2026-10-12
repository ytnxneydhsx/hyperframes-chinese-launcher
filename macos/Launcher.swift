import AppKit
import Foundation
import Darwin
import UniformTypeIdentifiers

private let launcherIdentifier = "local.hyperframes.chinese-launcher"
private let translatorVersion = "1.1.0"
private let openRequestNotification = Notification.Name("local.hyperframes.chinese-launcher.open-request")
private let hyperFramesIdentifier = "dev.hyperframes.desktop"

private enum LauncherError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self { case .message(let text): return text }
    }
}

/// Uses only the original app's local DevTools protocol. No app bundle or project writes.
@MainActor
private final class CDPConnection {
    let id: String
    private let socket: URLSessionWebSocketTask
    private var nextID = 0
    private var pending: [Int: CheckedContinuation<[String: Any], Error>] = [:]
    private var receiveTask: Task<Void, Never>?
    private(set) var closed = false

    init(id: String, url: URL, session: URLSession) {
        self.id = id
        socket = session.webSocketTask(with: url)
        socket.resume()
        receiveTask = Task { [weak self] in
            while let self, !self.closed {
                do {
                    let message = try await self.socket.receive()
                    let data: Data
                    switch message {
                    case .data(let value): data = value
                    case .string(let value): data = Data(value.utf8)
                    @unknown default: continue
                    }
                    guard let result = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                          let requestID = result["id"] as? Int,
                          let continuation = self.pending.removeValue(forKey: requestID) else { continue }
                    if let error = result["error"] as? [String: Any] {
                        continuation.resume(throwing: LauncherError.message(error["message"] as? String ?? "调试命令失败"))
                    } else {
                        continuation.resume(returning: result["result"] as? [String: Any] ?? [:])
                    }
                } catch { self.close(error: error) }
            }
        }
    }

    func call(_ method: String, parameters: [String: Any] = [:]) async throws -> [String: Any] {
        guard !closed else { throw LauncherError.message("调试连接已断开") }
        nextID += 1
        let requestID = nextID
        let payload = try JSONSerialization.data(withJSONObject: ["id": requestID, "method": method, "params": parameters])
        return try await withCheckedThrowingContinuation { continuation in
            pending[requestID] = continuation
            Task { [weak self] in
                guard let self else { return }
                do { try await self.socket.send(.string(String(decoding: payload, as: UTF8.self))) }
                catch { self.fail(requestID, error: error) }
            }
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                self?.fail(requestID, error: LauncherError.message("调试命令超时"))
            }
        }
    }

    func evaluate(_ expression: String) async throws -> [String: Any] {
        let reply = try await call("Runtime.evaluate", parameters: ["expression": expression, "returnByValue": true, "awaitPromise": true])
        if reply["exceptionDetails"] != nil { throw LauncherError.message("界面翻译脚本执行失败") }
        return (reply["result"] as? [String: Any])?["value"] as? [String: Any] ?? [:]
    }

    private func fail(_ requestID: Int, error: Error) {
        pending.removeValue(forKey: requestID)?.resume(throwing: error)
    }

    func close(error: Error = LauncherError.message("调试连接已关闭")) {
        guard !closed else { return }
        closed = true
        receiveTask?.cancel()
        receiveTask = nil
        socket.cancel(with: .goingAway, reason: nil)
        let requests = pending.values
        pending.removeAll()
        for continuation in requests { continuation.resume(throwing: error) }
    }
}

@MainActor
private final class LauncherDelegate: NSObject, NSApplicationDelegate {
    private var item: NSStatusItem!
    private var statusItem: NSMenuItem!
    private var coverageItem: NSMenuItem!
    private var toggleItem: NSMenuItem!
    private var timer: Timer?
    private var instanceLock: Int32 = -1
    private var busy = false
    private var quitting = false
    private var enabled = true
    private var port: Int?
    private var targetPID: pid_t?
    private var hyperFramesURL: URL?
    private var selectionCancelled = false
    private var connections: [String: CDPConnection] = [:]
    private var lastCoverage = 0
    private var lastDictionarySize = 0
    private var lastStatus = "正在连接"
    private var lastError = ""
    private var translatorSource = ""
    private var dictionaryJSON = "{}"
    private var patternJSON = "[]"
    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 2
        configuration.timeoutIntervalForResource = 86_400
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        return URLSession(configuration: configuration)
    }()
    private var stateURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HyperFrames Chinese Launcher/state.json")
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let existing = NSWorkspace.shared.runningApplications.first(where: {
            $0.bundleIdentifier == launcherIdentifier && $0.processIdentifier != ProcessInfo.processInfo.processIdentifier
        }) {
            existing.activate(options: [.activateIgnoringOtherApps])
            DistributedNotificationCenter.default().postNotificationName(openRequestNotification,
                object: launcherIdentifier, userInfo: nil, deliverImmediately: true)
            NSApp.terminate(nil)
            return
        }
        guard acquireInstanceLock() else {
            NSWorkspace.shared.runningApplications.first {
                $0.bundleIdentifier == launcherIdentifier && $0.processIdentifier != ProcessInfo.processInfo.processIdentifier
            }?.activate(options: [.activateIgnoringOtherApps])
            DistributedNotificationCenter.default().postNotificationName(openRequestNotification,
                object: launcherIdentifier, userInfo: nil, deliverImmediately: true)
            NSApp.terminate(nil)
            return
        }
        DistributedNotificationCenter.default().addObserver(self, selector: #selector(receiveOpenRequest(_:)),
            name: openRequestNotification, object: launcherIdentifier)
        createMenu()
        do {
            guard let sourceURL = Bundle.main.url(forResource: "translator", withExtension: "js"),
                  let dictionaryURL = Bundle.main.url(forResource: "translations.zh-CN", withExtension: "json") else {
                throw LauncherError.message("启动器缺少翻译资源，请重新构建")
            }
            translatorSource = try String(contentsOf: sourceURL, encoding: .utf8)
            let data = try Data(contentsOf: dictionaryURL)
            let dictionary = try JSONSerialization.jsonObject(with: data)
            let normalized = try JSONSerialization.data(withJSONObject: dictionary, options: [.sortedKeys, .fragmentsAllowed])
            dictionaryJSON = String(decoding: normalized, as: UTF8.self)
            if let patternURL = Bundle.main.url(forResource: "dynamic.zh-CN", withExtension: "json") {
                let patterns = try JSONSerialization.jsonObject(with: Data(contentsOf: patternURL))
                patternJSON = String(decoding: try JSONSerialization.data(withJSONObject: patterns), as: UTF8.self)
            }
        } catch { setStatus("翻译资源加载失败", error: error.localizedDescription); return }
        timer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refresh() }
        }
        Task { await connect(allowLaunch: true, allowRestartPrompt: true) }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        openHyperFrames()
        return true
    }

    @objc private func receiveOpenRequest(_ notification: Notification) { openHyperFrames() }

    private func acquireInstanceLock() -> Bool {
        do {
            try FileManager.default.createDirectory(at: stateURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let lockPath = stateURL.deletingLastPathComponent().appendingPathComponent("launcher.lock").path
            let descriptor = Darwin.open(lockPath, O_CREAT | O_RDWR, mode_t(S_IRUSR | S_IWUSR))
            guard descriptor >= 0 else { return false }
            guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else { Darwin.close(descriptor); return false }
            instanceLock = descriptor
            return true
        } catch { return false }
    }

    private func createMenu() {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.title = "HF 中"
        item.button?.toolTip = "HyperFrames 中文启动器"
        let menu = NSMenu()
        statusItem = menu.addItem(withTitle: "正在连接", action: nil, keyEquivalent: "")
        coverageItem = menu.addItem(withTitle: "词典：— · 已翻译：—", action: nil, keyEquivalent: "")
        menu.addItem(.separator())
        toggleItem = menu.addItem(withTitle: "中文已开启（点击恢复英文）", action: #selector(toggleLanguage), keyEquivalent: "")
        menu.addItem(withTitle: "打开 HyperFrames", action: #selector(openHyperFrames), keyEquivalent: "")
        menu.addItem(withTitle: "选择 HyperFrames 应用…", action: #selector(selectHyperFrames), keyEquivalent: "")
        menu.addItem(withTitle: "重新连接", action: #selector(reconnect), keyEquivalent: "")
        menu.addItem(withTitle: "以普通模式重开 HyperFrames…", action: #selector(reopenNormally), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出中文启动器", action: #selector(quitLauncher), keyEquivalent: "q")
        for entry in menu.items where entry.action != nil { entry.target = self }
        item.menu = menu
        writeState()
    }

    private var settingsURL: URL { stateURL.deletingLastPathComponent().appendingPathComponent("settings.json") }

    private func validatedAppURL(_ url: URL?) -> URL? {
        guard let url else { return nil }
        let resolved = url.standardizedFileURL.resolvingSymlinksInPath()
        var directory: ObjCBool = false
        guard resolved.pathExtension.lowercased() == "app",
              FileManager.default.fileExists(atPath: resolved.path, isDirectory: &directory), directory.boolValue,
              resolved != Bundle.main.bundleURL.standardizedFileURL.resolvingSymlinksInPath(),
              let bundle = Bundle(url: resolved), bundle.bundleIdentifier == hyperFramesIdentifier,
              bundle.executableURL != nil else { return nil }
        return resolved
    }

    private func officialRunningApps() -> [NSRunningApplication] {
        NSWorkspace.shared.runningApplications.filter {
            $0.bundleIdentifier == hyperFramesIdentifier && !$0.isTerminated && validatedAppURL($0.bundleURL) != nil
        }.sorted { $0.processIdentifier < $1.processIdentifier }
    }

    private func runningApp() -> NSRunningApplication? {
        guard let selected = hyperFramesURL else { return nil }
        let apps = officialRunningApps().filter { validatedAppURL($0.bundleURL) == selected }
        return apps.first(where: { $0.processIdentifier == targetPID }) ?? apps.first
    }

    private func savedAppURL() -> URL? {
        guard let data = try? Data(contentsOf: settingsURL),
              let settings = try? JSONSerialization.jsonObject(with: data) as? [String: String],
              let path = settings["applicationPath"] else { return nil }
        return validatedAppURL(URL(fileURLWithPath: path))
    }

    private func chooseApp() -> URL? {
        let panel = NSOpenPanel()
        panel.title = "选择 HyperFrames 应用"
        panel.message = "请选择官方 HyperFrames.app（dev.hyperframes.desktop）。"
        panel.prompt = "选择应用"
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.applicationBundle]
        panel.treatsFilePackagesAsDirectories = false
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK else { selectionCancelled = true; return nil }
        guard let url = validatedAppURL(panel.url) else {
            setStatus("应用选择无效", error: "请选择官方 HyperFrames 应用；安装器和中文启动器不能作为目标。")
            return nil
        }
        do {
            try FileManager.default.createDirectory(at: settingsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: ["applicationPath": url.path], options: [.prettyPrinted, .sortedKeys])
                .write(to: settingsURL, options: .atomic)
        } catch { setStatus("位置未能保存", error: error.localizedDescription) }
        selectionCancelled = false
        return url
    }

    private func resolveApp(allowSelection: Bool) -> URL? {
        let apps = officialRunningApps()
        if let current = runningApp(), let url = validatedAppURL(current.bundleURL) { hyperFramesURL = url; return url }
        if let app = apps.first, let url = validatedAppURL(app.bundleURL) { hyperFramesURL = url; return url }
        let candidates = [savedAppURL(), validatedAppURL(NSWorkspace.shared.urlForApplication(withBundleIdentifier: hyperFramesIdentifier)),
            validatedAppURL(URL(fileURLWithPath: "/Applications/HyperFrames.app")),
            validatedAppURL(FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications/HyperFrames.app"))]
        if let url = candidates.compactMap({ $0 }).first { hyperFramesURL = url; return url }
        hyperFramesURL = nil
        if allowSelection, !selectionCancelled { hyperFramesURL = chooseApp() }
        return hyperFramesURL
    }

    @objc private func selectHyperFrames() {
        guard !busy, !quitting else { return }
        busy = true
        defer { busy = false }
        guard let selected = chooseApp() else { return }
        if let active = runningApp() ?? officialRunningApps().first, let url = validatedAppURL(active.bundleURL) {
            hyperFramesURL = url
            if url != selected { setStatus("已保存新位置，当前继续连接正在运行的应用") }
        } else { hyperFramesURL = selected }
        Task { await connect(allowLaunch: true, allowRestartPrompt: true) }
    }

    private func command(_ executable: String, _ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return String(decoding: data, as: UTF8.self)
    }

    private func discoveredPorts(for app: NSRunningApplication) -> [Int] {
        var result: [Int] = []
        if let arguments = try? command("/bin/ps", ["-p", String(app.processIdentifier), "-o", "args="]),
           let pattern = try? NSRegularExpression(pattern: #"--remote-debugging-port(?:=|\s+)(\d+)"#),
           let match = pattern.firstMatch(in: arguments, range: NSRange(arguments.startIndex..., in: arguments)),
           let range = Range(match.range(at: 1), in: arguments), let value = Int(arguments[range]), value > 0, value < 65536 {
            result.append(value)
        }
        let activePortFile = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HyperFrames/DevToolsActivePort")
        if let content = try? String(contentsOf: activePortFile, encoding: .utf8),
           let first = content.split(whereSeparator: \.isNewline).first,
           let value = Int(first), value > 0, value < 65536, !result.contains(value) { result.append(value) }
        if let port, !result.contains(port), targetPID == app.processIdentifier { result.append(port) }
        return result
    }

    /// Refuses wildcard or LAN listeners even if a localhost request would succeed.
    private func verifyLoopbackListener(_ candidate: Int, pid: pid_t) throws {
        let output = try command("/usr/sbin/lsof", ["-nP", "-iTCP:\(candidate)", "-sTCP:LISTEN", "-F", "pn"])
        var owner: pid_t?
        var found = false
        for line in output.split(whereSeparator: \.isNewline) {
            if line.hasPrefix("p") { owner = pid_t(line.dropFirst()) }
            if line.hasPrefix("n") {
                let address = String(line.dropFirst())
                guard owner == pid, address == "127.0.0.1:\(candidate)" || address == "[::1]:\(candidate)" else {
                    throw LauncherError.message("调试端口未通过本机回环安全检查")
                }
                found = true
            }
        }
        guard found else { throw LauncherError.message("没有找到应用的本机调试监听端口") }
    }

    private func getJSON(_ path: String, port candidate: Int) async throws -> Any {
        let url = URL(string: "http://127.0.0.1:\(candidate)\(path)")!
        var request = URLRequest(url: url)
        request.timeoutInterval = 2
        request.cachePolicy = .reloadIgnoringLocalCacheData
        let (data, response) = try await session.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw LauncherError.message("本机调试入口没有响应") }
        return try JSONSerialization.jsonObject(with: data)
    }

    private func connect(allowLaunch: Bool, allowRestartPrompt: Bool) async {
        guard !busy, !quitting else { return }
        busy = true
        defer { busy = false }
        do {
            guard resolveApp(allowSelection: allowLaunch) != nil else {
                if selectionCancelled { setStatus("已取消选择应用") }
                else { setStatus("未找到 HyperFrames", error: "请从菜单选择 HyperFrames 应用…") }
                return
            }
            var app = runningApp()
            if app == nil, allowLaunch { app = try await launchApp(debug: true) }
            guard let app else { setStatus("等待 HyperFrames 启动"); return }
            app.activate(options: [])
            var lastFailure: Error?
            for candidate in discoveredPorts(for: app) {
                do {
                    try verifyLoopbackListener(candidate, pid: app.processIdentifier)
                    _ = try await getJSON("/json/version", port: candidate)
                    guard !quitting else { return }
                    await restoreAndDisconnect()
                    port = candidate
                    targetPID = app.processIdentifier
                    try await syncTargets()
                    return
                } catch { lastFailure = error }
            }
            if allowRestartPrompt {
                guard !quitting else { return }
                let alert = NSAlert()
                alert.messageText = "需要重启 HyperFrames 才能启用中文"
                alert.informativeText = "当前应用没有可用的本机调试入口。选择安全重启后，启动器会请求 HyperFrames 正常退出，并等待它处理保存提示；若退出被取消，将保留当前应用。"
                alert.addButton(withTitle: "保留当前应用")
                alert.addButton(withTitle: "安全重启并启用中文")
                NSApp.activate(ignoringOtherApps: true)
                if alert.runModal() == .alertSecondButtonReturn {
                    guard app.terminate() else { throw LauncherError.message("HyperFrames 未接受正常退出请求") }
                    for _ in 0..<40 {
                        if app.isTerminated { break }
                        try await Task.sleep(nanoseconds: 500_000_000)
                    }
                    guard app.isTerminated else { throw LauncherError.message("HyperFrames 仍在运行，重启已停止；请处理保存提示后重新连接") }
                    let restarted = try await launchApp(debug: true)
                    targetPID = restarted.processIdentifier
                    guard let candidate = port else { throw LauncherError.message("启动时没有分配调试端口") }
                    try await waitForDebugger(candidate, app: restarted)
                    try await syncTargets()
                    return
                }
            }
            setStatus("等待可用的调试入口", error: lastFailure?.localizedDescription ?? "点击重新连接可选择安全重启")
        } catch { setStatus("连接失败", error: error.localizedDescription) }
    }

    private func availablePort() throws -> Int {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw LauncherError.message("无法分配本机端口") }
        defer { Darwin.close(descriptor) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard result == 0 else { throw LauncherError.message("无法分配本机回环端口") }
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let nameResult = withUnsafeMutablePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(descriptor, $0, &length) }
        }
        guard nameResult == 0 else { throw LauncherError.message("无法读取本机端口") }
        return Int(UInt16(bigEndian: address.sin_port))
    }

    private func launchApp(debug: Bool) async throws -> NSRunningApplication {
        guard let hyperFramesURL = validatedAppURL(hyperFramesURL) ?? resolveApp(allowSelection: true) else { throw LauncherError.message("找不到有效的 HyperFrames 应用，请从菜单选择应用位置") }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        if debug {
            let candidate = try availablePort()
            port = candidate
            configuration.arguments = ["--remote-debugging-port=\(candidate)", "--remote-debugging-address=127.0.0.1"]
        }
        let app: NSRunningApplication = try await withCheckedThrowingContinuation { continuation in
            NSWorkspace.shared.openApplication(at: hyperFramesURL, configuration: configuration) { app, error in
                if let app { continuation.resume(returning: app) }
                else { continuation.resume(throwing: error ?? LauncherError.message("HyperFrames 启动失败")) }
            }
        }
        guard app.bundleIdentifier == hyperFramesIdentifier, validatedAppURL(app.bundleURL) == hyperFramesURL else {
            throw LauncherError.message("系统打开了另一份 HyperFrames；请关闭多余副本后重新连接")
        }
        targetPID = app.processIdentifier
        if debug, let candidate = port { try await waitForDebugger(candidate, app: app) }
        return app
    }

    private func waitForDebugger(_ candidate: Int, app: NSRunningApplication) async throws {
        var failure: Error = LauncherError.message("等待本机调试入口超时")
        for _ in 0..<20 {
            do {
                try verifyLoopbackListener(candidate, pid: app.processIdentifier)
                _ = try await getJSON("/json/version", port: candidate)
                return
            } catch { failure = error }
            if app.isTerminated { break }
            try await Task.sleep(nanoseconds: 500_000_000)
        }
        throw failure
    }

    private func websocketURL(_ source: String, port: Int) throws -> URL {
        guard var components = URLComponents(string: source), components.scheme == "ws",
              components.port == port, ["127.0.0.1", "localhost", "::1", "[::1]"].contains(components.host ?? ""),
              components.user == nil, components.password == nil else {
            throw LauncherError.message("应用返回了非本机调试连接")
        }
        components.host = "127.0.0.1"
        guard let url = components.url else { throw LauncherError.message("调试连接格式无效") }
        return url
    }

    private func syncTargets() async throws {
        guard !quitting else { return }
        guard let port, let pid = targetPID else { return }
        guard runningApp()?.processIdentifier == pid else { throw LauncherError.message("HyperFrames 已关闭或重新启动") }
        try verifyLoopbackListener(port, pid: pid)
        guard let targets = try await getJSON("/json/list", port: port) as? [[String: Any]] else { throw LauncherError.message("调试页面列表格式无效") }
        // Translate only the desktop studio shell. Project preview/render pages stay untouched.
        let pages = targets.filter { target in
            guard target["type"] as? String == "page",
                  (target["title"] as? String)?.caseInsensitiveCompare("HyperFrames") == .orderedSame,
                  let sourceURL = target["url"] as? String, let url = URL(string: sourceURL),
                  url.scheme == "http", ["127.0.0.1", "localhost", "::1", "[::1]"].contains(url.host ?? ""),
                  url.path == "/" || url.path == "/index.html" else { return false }
            return true
        }
        let activeIDs = Set(pages.compactMap { $0["id"] as? String })
        for id in Array(connections.keys) where !activeIDs.contains(id) || connections[id]?.closed == true {
            connections.removeValue(forKey: id)?.close()
        }
        var translated = 0
        var dictionarySize = 0
        var succeeded = 0
        var failure: Error?
        for page in pages {
            guard !quitting else { return }
            guard let id = page["id"] as? String, let source = page["webSocketDebuggerUrl"] as? String else { continue }
            do {
                let connection: CDPConnection
                let newlyConnected: Bool
                if let existing = connections[id] { connection = existing; newlyConnected = false }
                else {
                    connection = CDPConnection(id: id, url: try websocketURL(source, port: port), session: session)
                    connections[id] = connection
                    newlyConnected = true
                }
                var status = try await connection.evaluate("window.__HFZH__ ? window.__HFZH__.status() : ({})")
                guard !quitting else { return }
                if newlyConnected || status["version"] as? String != translatorVersion {
                    let reset = newlyConnected ? "window.__HFZH__?.disable?.(); delete window.__HFZH__;\n" : ""
                    let script = "\(reset)window.__HFZH_TRANSLATIONS__ = \(dictionaryJSON); window.__HFZH_PATTERNS__ = \(patternJSON);\n\(translatorSource)\n;window.__HFZH__ ? window.__HFZH__.\(enabled ? "enable" : "disable")() : ({})"
                    status = try await connection.evaluate(script)
                    guard status["version"] as? String == translatorVersion else { throw LauncherError.message("页面未完成翻译脚本初始化") }
                } else if (status["enabled"] as? Bool ?? false) != enabled {
                    status = try await connection.evaluate("window.__HFZH__.\(enabled ? "enable" : "disable")()")
                }
                translated += (status["translatedTextNodes"] as? Int ?? 0) + (status["translatedAttributes"] as? Int ?? 0)
                dictionarySize = max(dictionarySize, status["dictionarySize"] as? Int ?? 0)
                succeeded += 1
            } catch { failure = error; connections.removeValue(forKey: id)?.close() }
        }
        lastCoverage = translated
        lastDictionarySize = dictionarySize
        if succeeded > 0 { setStatus("已连接 \(succeeded) 个界面", error: failure?.localizedDescription ?? "") }
        else if let failure { throw failure }
        else { setStatus("已连接，等待应用界面") }
    }

    private func refresh() async {
        guard !busy, !quitting else { return }
        if port == nil || targetPID != runningApp()?.processIdentifier {
            busy = true
            await restoreAndDisconnect()
            busy = false
            await connect(allowLaunch: false, allowRestartPrompt: false)
            return
        }
        busy = true
        defer { busy = false }
        do { try await syncTargets() }
        catch { setStatus("连接中断，将自动重试", error: error.localizedDescription) }
    }

    private func setStatus(_ text: String, error: String = "") {
        lastStatus = text
        lastError = error
        statusItem?.title = error.isEmpty ? text : "\(text)：\(error)"
        coverageItem?.title = "词典：\(lastDictionarySize) 条 · 已翻译：\(lastCoverage) 处"
        item?.button?.title = enabled ? "HF 中" : "HF EN"
        toggleItem?.title = enabled ? "中文已开启（点击恢复英文）" : "中文已关闭（点击启用中文）"
        writeState()
    }

    private func writeState() {
        // Deliberately exclude URLs, titles, source text, samples, project data and credentials.
        var state: [String: Any] = ["launcherPID": ProcessInfo.processInfo.processIdentifier,
            "version": translatorVersion, "enabled": enabled, "status": lastStatus,
            "lastError": lastError, "connectedTargets": connections.values.filter { !$0.closed }.count,
            "translatedCount": lastCoverage, "dictionarySize": lastDictionarySize,
            "updatedAt": ISO8601DateFormatter().string(from: Date())]
        if let port { state["port"] = port }
        if let targetPID { state["targetPID"] = targetPID }
        do {
            try FileManager.default.createDirectory(at: stateURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: state, options: [.prettyPrinted, .sortedKeys]).write(to: stateURL, options: .atomic)
        } catch { /* A read-only home directory must not prevent in-memory translation. */ }
    }

    private func restoreAndDisconnect() async {
        let current = Array(connections.values)
        for connection in current where !connection.closed { _ = try? await connection.evaluate("window.__HFZH__ ? window.__HFZH__.disable() : ({})") }
        for connection in current { connection.close() }
        connections.removeAll()
        lastCoverage = 0
    }

    @objc private func toggleLanguage() {
        enabled.toggle()
        setStatus(lastStatus, error: lastError)
        Task { await refresh() }
    }

    @objc private func openHyperFrames() {
        guard !quitting else { return }
        Task {
            // A click received during a periodic refresh must still launch the app afterward.
            while busy && !quitting { try? await Task.sleep(nanoseconds: 100_000_000) }
            guard !quitting else { return }
            if let app = runningApp(), app.processIdentifier == targetPID,
               connections.values.contains(where: { !$0.closed }) {
                app.activate(options: [.activateIgnoringOtherApps])
                return
            }
            await connect(allowLaunch: true, allowRestartPrompt: true)
        }
    }

    @objc private func reconnect() {
        Task { await connect(allowLaunch: true, allowRestartPrompt: true) }
    }

    @objc private func reopenNormally() {
        guard !busy, !quitting else { return }
        let alert = NSAlert()
        alert.messageText = "以普通模式重开 HyperFrames？"
        alert.informativeText = "将恢复英文，请求 HyperFrames 正常退出，再启动原应用。重开后中文启动器将退出。未保存内容由 HyperFrames 自行处理。"
        alert.addButton(withTitle: "取消")
        alert.addButton(withTitle: "普通模式重开")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertSecondButtonReturn else { return }
        Task {
            busy = true
            defer { busy = false }
            await restoreAndDisconnect()
            if let app = runningApp() {
                guard app.terminate() else { setStatus("普通重开已停止", error: "应用未接受正常退出请求"); return }
                for _ in 0..<40 {
                    if app.isTerminated { break }
                    try? await Task.sleep(nanoseconds: 500_000_000)
                }
                guard app.isTerminated else { setStatus("普通重开已停止", error: "应用仍在运行，请处理保存提示"); return }
            }
            do { _ = try await launchApp(debug: false); finishQuit() }
            catch { setStatus("普通重开失败", error: error.localizedDescription) }
        }
    }

    @objc private func quitLauncher() { NSApp.terminate(nil) }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if quitting || item == nil { return .terminateNow }
        quitting = true
        timer?.invalidate()
        Task {
            await restoreAndDisconnect()
            enabled = false
            setStatus("启动器已退出，HyperFrames 保持运行")
            session.invalidateAndCancel()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    private func finishQuit() {
        quitting = true
        enabled = false
        timer?.invalidate()
        setStatus("已恢复普通启动")
        session.invalidateAndCancel()
        NSApp.terminate(nil)
    }
}

@main
private struct LauncherMain {
    @MainActor static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let delegate = LauncherDelegate()
        app.delegate = delegate
        withExtendedLifetime(delegate) { app.run() }
    }
}
