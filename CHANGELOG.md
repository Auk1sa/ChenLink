# Changelog

All notable changes to ChenLink are documented in this file.

## V1.2.0 - 未发布

### Added

- 房间口令（可选）：创建房间时设置口令，加入时需匹配，防止房间码被扫号加入。
- 端到端加密（AES-GCM）：设置房间口令后自动启用，中继服务器只能看到密文。
- 控制连接心跳保活：客户端每 25s 发 Ping，服务器 90s 无消息断开，防 NAT 空闲断链。
- 链路中断自动重连：直连/中继链路意外断开后自动重试 2 次，减少手动重连。
- 服务器限流：每 IP 最多 16 条并发连接、每分钟最多创建 8 个房间，防滥用。
- 服务器文件日志：按天滚动写入 `logs/server-yyyyMMdd.log`，便于 VPS 排查。
- 客户端文件日志 + 全局异常捕获：`%LOCALAPPDATA%\ChenLink\logs\app-yyyyMMdd.log`，崩溃可回溯。
- CI：GitHub Actions 自动构建 + 端到端自测。
- 自测新增加密中继场景（TCP + UDP 回显验证）。

### Changed

- EasyTier 轮询间隔从 900ms 降至 3000ms，减少子进程启动开销。
- EasyTier cli 调用改为异步读取 stdout/stderr，消除潜在死锁。
- Mux 帧头缓冲区复用，减少高频小对象分配。
- 无公网 IP 模式端口探测改为并行，多 peer 时更快。
- 旧模式 UI 新增房间口令输入框。

### Fixed

- 修复 Mux 并发发送时帧头缓冲区竞态。
- 修复端口占用场景下自动重连死循环。

## V1.1.0 - 2026-09-11

### Added

- System tray menu with show/exit actions and double-click restore.
- Configurable close behavior: ask every time, minimize to tray, or exit.

### Changed

- Replaced the WinForms tray component with H.NotifyIcon to reduce the single-file release from about 124 MB to about 90 MB.
- Updated application version to 1.1.0.

### Fixed

- Fixed a crash when restoring or exiting from the tray by dispatching tray callbacks to the UI thread.

## V1 - 2026-09-10

### Added

- First public release.
- Room-based peer-to-peer multiplayer connection.
- LAN direct connection, TCP hole punching, and relay fallback.
- Optional EasyTier virtual network mode for users without a public IP address.
- Single-file, self-contained Windows x64 release package.
- GitHub Pages project showcase.
