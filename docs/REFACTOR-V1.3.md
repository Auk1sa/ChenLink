# ChenLink V1.3.0 重构说明

> 目标：解决「UI 排版混乱、视觉不统一、功能不完善」。网络协议层（直连 / 打洞 / 中继 / 加密）保持不动，
> 重做界面与代码结构，并补齐若干影响可用性的缺口。

---

## 1. 改造前的问题诊断

| 位置 | 问题 |
| --- | --- |
| `MainWindow.xaml.cs`（860 行） | 网络编排、EasyTier 生命周期、托盘、设置读写、关闭策略、页面文案全在一个 code-behind 里，改任何一处都要动这个文件 |
| `MainWindow.xaml`（单页长滚动） | 所有功能挤在一列 `StackPanel` 里，无导航、无分区；颜色写死 `#202020` 系；固定像素宽度，缩放窗口就错位 |
| 游戏预设 | 24 条 `ComboBoxItem` 在 XAML 里**重复写了两遍**（房主一份、加入一份），加一个游戏要改两处 |
| 错误反馈 | 房间码被占用、服务器拒绝、口令不符 → 只在日志里留一行，界面停在「连接中」，用户只能反复点按钮 |
| 状态信息 | 只有一句静态文案；没有延迟、没有流量，出问题无法判断是没连上还是卡在打洞 |
| 窗口 | 尺寸不记忆；换显示器后窗口可能比屏幕还大，底部按钮点不到 |
| 语言/主题 | `RequestedTheme="Dark"` 写死在 App.xaml，浅色系统下一半控件是深色 |

## 2. 新结构

```
src/ChenLink/
  Core/         ObservableObject · RelayCommand/AsyncRelayCommand · 转换器 · 格式化 · 局域网地址
  Models/       GamePreset + GameCatalog（游戏端口/协议的唯一定义）
  Services/     SettingsService · LogService · ThemeService · TrayService · SingleInstance · ClipboardHelper
  ViewModels/   MainViewModel · ConnectViewModel · DiagnosticsViewModel · SettingsViewModel
                RelayFormViewModel · EasyTierFormViewModel · SessionStatus（不可变状态快照）
  Views/        ConnectPage · DiagnosticsPage · SettingsPage · PageLayout（宽度限幅）
  Themes/       Tokens.xaml（浅/深两套语义画刷 + 控件样式，约 290 行）
  Engine/       Protocol · Mux（新增 Ping/Pong）· Session（注册握手 / 房间码重试）
  EasyTier/     EasyTierRunner（进程生命周期）
```

调用方向单向：

```
Views ──绑定──> ViewModels ──> Engine / EasyTier / Services
                    ▲
                    └── SessionStatus 快照（引擎事件 → 状态 → 界面）
```

要点：

- **MVVM 不引第三方包**。`AsyncRelayCommand` 自带防重入与异常回落，异步命令不会因为网络慢被连点两次。
- **状态收敛到一个 VM**。房间会话、EasyTier runner、内嵌服务器在 `ConnectViewModel` 里只有一份，
  切换联机方式不会丢会话。
- **界面不写业务判断**。`SessionStatus` 是不可变快照，含标题、详情、色调、四项指标、下一步提示，
  XAML 只负责把色调映射成颜色。
- **样式全部走令牌**。颜色/字号/圆角/卡片/按钮在 `Tokens.xaml` 里各定义一次，浅色深色各一套。

## 3. 视觉与交互

- **壳层**：48px 自绘标题栏（品牌块 + 实时状态胶囊 + 系统三键），左侧导航（连接 / 诊断 / 设置），右侧页面区。
- **连接页**：页头 → 提示条 → 状态卡（阶段 + 4 项指标 + 下一步）→ 联机方式（两张卡片切换）→ 对应表单。
  房主与玩家两栏并排，窄屏自动上下堆叠。
- **诊断页**：当前会话、运行环境（IP / 权限 / 日志目录 / 版本）、日志列表（按级别着色、可按警告/错误过滤）。
- **设置页**：外观、行为、身份、连接默认值、关于，改动即时保存。
- **响应式断点**：
  | 窗口宽度 | 侧栏 | 表单 |
  | --- | --- | --- |
  | ≥1120 | 展开 212px | 两栏并排 |
  | 700–1120 | 48px 图标轨道 | 两栏并排或堆叠（看内容区宽度 <900） |
  | <700 | 汉堡浮层 | 全部堆叠 |
- **浅色 / 深色**：切换只改 `ElementTheme` + 重设系统标题栏按钮颜色，不重启、不重建窗口。

## 4. 补齐的功能

1. **延迟探测**：`Mux` 新增 `Ping(6)` / `Pong(7)` 帧，每 3s 一次，6.5s 无响应置 `—`。
2. **注册回执**：`BeginAsync` 等服务器的 `You` / `Err`（8s 超时），失败抛出真实原因
   （房间被占用 / 服务器拒绝 / 口令不符），界面直接显示。
3. **房间码冲突重试**：最多换 5 个码，撞码不再直接失败。
4. **单实例**：`Local\ChenLink.SingleInstance.v2` 互斥量，重复启动激活已有窗口。
5. **设置持久化**：主题、窗口尺寸、开机自启、自动探测节点、关闭策略、昵称、默认游戏/端口、
   EasyTier 节点；原子写入（临时文件 + 替换），解析失败回退默认值。
6. **窗口尺寸**：记忆上次尺寸；超出当前工作区时收拢并居中。
7. **表单即时校验**：只在真正填过之后提示错误，空输入不再一启动就飘红。
8. **诊断一键复制**：会话状态 + 环境 + 最近日志。
9. **日志**：分级（Info/Success/Warning/Error）、最多 800 条内存、按天滚动写文件、界面可过滤。

## 5. 规模对照

| | 改造前 | 改造后 |
| --- | --- | --- |
| `.cs` 文件数 | 7 | 30 |
| 最大单文件 | `MainWindow.xaml.cs` 860 行 | `ConnectViewModel.cs` 872 行（纯编排）/ `Session.cs` 764 行（网络核心） |
| `MainWindow.xaml.cs` | 860 行 | 283 行（标题栏、窗口、托盘、关闭策略） |
| XAML | 1 个 252 行大页面 | 壳 + 3 页 + 令牌字典 |
| 游戏清单 | 2 处 × 24 条 | 1 处 `GameCatalog` |

## 6. 踩过的坑（都写在代码注释里，避免后人重踩）

1. `NavigationView` 用 `PaneDisplayMode="Auto"` 时，宽窗口下内容区按整窗宽度布局并整体右移，
   1120 宽的页面被右边界裁掉。改为显式 `Left/LeftCompact/LeftMinimal`，按窗口宽度切换。
2. XAML 里 `MaxWidth` + `HorizontalAlignment="Stretch"` 放进 `ScrollViewer`，实测居中位置算错，
   内容右移约 150px 并被裁切。改为 `PageLayout.Cap()`：按视口宽度显式设 `Width` 再居中。
3. `Width` 默认是 `NaN`，`Math.Abs(NaN - x) > 0.5` 恒为 `false` —— 宽度上限一直没生效。
   必须先 `double.IsNaN()` 判断。
4. `VisualStateManager` 写在 `ControlTemplate` 根节点之外会被静默忽略，模式卡片选中态不显示。
5. 在 `Window` 构造函数里改窗口尺寸，XAML 内容区（island）会跟不上窗口，底部留一条不绘制的空白。
   改为首次 `Activated` 之后再做。
6. 首屏焦点落到第一个 `TextBox` 会把页面滚下去，加载完成后需显式 `ChangeView(null, 0, ...)`。

## 7. 验收

- `dotnet build -c Release -p:Platform=x64`：**0 警告 0 错误**。
- `dotnet run --project src/ChenLinkServer -- --selftest`：**SELFTEST PASS**（同网直连 + 强制中继两条链路，
  TCP + UDP 加密回显）。
- 界面实拍：浅色 / 深色 × 宽屏（1340）/ 中等（940）/ 窄屏（820），侧栏三种形态与表单堆叠均正常。

## 8. 后续可做（本次未动）

- 打完洞后自动测试实际带宽，替代当前只测 RTT。
- 连接页记住上次填写的房间码 / 邀请码（当前只记默认游戏与端口）。
- 多语言（目前仅中文）。
