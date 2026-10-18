# Changelog

All notable changes to ChenLink are documented in this file.

## V1.3.0 - 2026-10-18

界面与代码结构的整体重构。网络行为（直连 / 打洞 / 中继 / 加密）不变，主要是把原本堆在
`MainWindow.xaml.cs`（860 行）里的逻辑拆开，并把界面重做成统一的设计系统。

### Changed

- **代码结构**：原来网络编排、EasyTier 生命周期、托盘、设置、关闭策略、界面文案全在一个
  code-behind 里。现拆为 `Core`（通用设施）/ `Models`（游戏预设）/ `Services`（设置、日志、主题、
  托盘、单实例、剪贴板）/ `ViewModels`（连接、诊断、设置、壳层）/ `Views`（三个页面）。
- **MVVM 无第三方框架**：自建 `ObservableObject`、`RelayCommand`、`AsyncRelayCommand`
  （异步命令防重入，异常统一回落到日志与提示条）。
- **界面重做**：单页长滚动 → 导航壳（连接 / 诊断 / 设置）三页；颜色、字号、圆角、卡片与按钮样式
  统一收敛到 `Themes/Tokens.xaml`，浅色与深色各一套语义令牌。
- **响应式**：窗口宽度 ≥1120 展开侧栏、700–1120 收成 48px 图标轨道、<700 汉堡浮层；
  页面宽度按视口限幅居中（1120 / 设置页 880）；窗口 <900 时房主/玩家两列自动堆叠。
- **浅色 / 深色主题**：移除写死的深色，跟随令牌切换，系统标题栏按钮同步换色，切换不需要重启。
- **游戏预设单一来源**：原来 24 条 `ComboBoxItem` 在 XAML 里重复写了两遍，现收敛为 `GameCatalog`。
- **日志分级 + 过滤**：诊断页可按「全部 / 警告 / 错误」过滤，支持复制、清空、打开日志目录；
  一条「复制诊断信息」把会话状态、环境、最近日志一次性带走。

### Added

- **延迟探测**：Mux 新增 `Ping/Pong` 帧，每 3s 测一次往返延迟，6.5s 无响应显示 `—`。
  状态卡与诊断页不再只有「已连接」，而是有真实 RTT。
- **注册握手有回执**：`BeginAsync` 等待服务器 `You` / `Err`（8s 超时），房间被占用、服务器拒绝、
  口令不符等错误直接抛到界面，不再静默卡在「连接中」。
- **房间码冲突自动重试**：创建房间最多换 5 个码，撞码不再直接失败。
- **单实例**：重复启动不再开两个窗口抢同一个房间，第二次启动会激活已有窗口。
- **窗口尺寸记忆与收拢**：记住上次尺寸，超过当前工作区时自动收拢并居中（换显示器不再跑出屏幕）。
- **设置持久化**：主题、窗口尺寸、开机自启、自动探测节点、关闭策略、昵称、默认游戏/端口、
  EasyTier 节点列表，原子写入，损坏自动回退默认值。
- **表单即时校验**：房主 / 加入两栏分别校验，错误只在真正填过之后提示，不再一启动就飘红。

### Fixed

- 修复 `SessionException` 被 `sealed` 导致 `RoomTakenException` 无法继承的问题。
- 修复房间被占用时界面无任何提示、用户只能反复点「创建房间」的问题。
- 修复 EasyTier 子进程意外退出后仍显示「已就绪」的残留状态。
- 修复首屏焦点落到输入框、页面自动滚到中间的问题。
- 修复 `NavigationView` 在 `PaneDisplayMode="Auto"` 下内容区算错宽度、页面右侧被窗口裁掉的问题
  （改为显式面板模式 + 按视口显式限幅，见 `PageLayout.cs` 注释）。
- 修复模式卡片选中态不显示（`VisualStateManager` 写在了模板根节点之外，被静默忽略）。
- 修复单文件版被改名后启动即崩溃（WinUI 未打包模式的资源按 exe 文件名定位）：现在会弹窗说明原因并提示改回
  `ChenLink.exe`，不再静默闪退。

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
