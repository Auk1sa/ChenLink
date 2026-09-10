<p align="center">
  <img src="docs/assets/logo.png" alt="ChenLink logo" width="180">
</p>

<h1 align="center">ChenLink</h1>
<p align="center">P2P 联机工具（WinUI 3）</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-Apache--2.0-blue.svg" alt="License: Apache-2.0"></a>
  <a href="https://github.com/Auk1sa/ChenLink/actions/workflows/ci.yml"><img src="https://github.com/Auk1sa/ChenLink/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/Auk1sa/ChenLink/actions/workflows/pages.yml"><img src="https://github.com/Auk1sa/ChenLink/actions/workflows/pages.yml/badge.svg" alt="GitHub Pages"></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0078d4.svg" alt="Platform: Windows 10/11">
  <img src="https://img.shields.io/badge/.NET-8-512BD4.svg" alt=".NET 8">
</p>

**让只能局域网联机的游戏，跨网络也能一起玩。** 房主把“游戏服务器端口”通过房间码共享给好友，
好友在本地得到一个映射端口，在游戏里“直接连接 `127.0.0.1:映射端口`”即可。
能打洞就 P2P 直连，打洞失败自动走服务器中继，**任何网络都可用**（直连与否只影响延迟与带宽）。

> 项目展示页（GitHub Pages）：[Auk1sa.github.io/ChenLink](https://Auk1sa.github.io/ChenLink/)

---

## ✨ 功能特点

- 🎮 **开箱即用**：内置常见游戏端口（Minecraft / Terraria / Stardew Valley / Valheim），TCP / UDP 游戏都支持（UDP 数据报与 TCP 走同一条链路，端口号不变，无需另开端口）。
- 🌐 **两种联机模式**：
  - **无公网 IP 模式（推荐）**：借助 EasyTier 公共节点自动打洞/中继，无需自建服务器、无需公网 IP；
  - **旧模式（自建服务器）**：使用自己部署的信令/中继服务器，客户端也内置服务器，单机即可演示。
- 🔀 **直连优先，中继兜底**：同网直连 / TCP 同时打开打洞 / 服务器中继，按顺序自动选择。
- 🖥️ **现代 WinUI 3 界面**：深色主题、自绘标题栏、实时状态与流量统计。
- 📦 **单文件自包含**：目标机器无需安装 .NET 运行时或 Windows App SDK。

## 📦 安装 / 获取

当前版本：**V1**。有两种方式获得程序：

1. **下载单文件版**：前往 [Releases](https://github.com/Auk1sa/ChenLink/releases) 下载 `ChenLink.exe`。
2. **自己构建**：见下方 [从源码构建](#-从源码构建)。

> 需要 **Windows 10/11（64 位）**。无公网 IP 模式需要**管理员权限**（用于安装 EasyTier 虚拟网卡）。

## 🚀 快速开始（源码版）

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)（Windows App SDK 的 NuGet 首次还原需联网）。

### 0) 端到端自测（验证核心逻辑，无需任何游戏）

```powershell
dotnet run --project src/ChenLinkServer -- --selftest
# 期望输出 SELFTEST PASS：同网直连 + 强制中继两条链路都回显通过
```

### 1) 构建并运行客户端

```powershell
dotnet build src/ChenLink/ChenLink.csproj -c Release -p:Platform=x64
# 产物：src/ChenLink/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/ChenLink.exe
```

### 2) 方式一：无公网 IP 联机（推荐，EasyTier）

双方都运行客户端，点击顶部 **“无公网 IP 联机”** 卡片：

1. **房主**：选好游戏/端口 → 点 **“＋ 创建无公网房间”** → 等待组网就绪 → 把**整行邀请码**发给好友。
2. **玩家**：点 **“＋ 加入无公网房间”** 前，把房主的邀请码粘贴到输入框 → 等待自动找到房主的游戏服务器。
3. 双方都显示“组网就绪”后，玩家在游戏里选择“多人游戏 / 直接连接”，填入提示的 `虚拟IP:端口`。

> 首次使用会请求**管理员权限**（安装虚拟网卡）。公共节点为社区提供的共享节点，
> 若连接不稳定，可点 **“⚡ 自动获取可用公共节点”** 或填写自己的节点。

### 3) 方式二：旧模式（自建信令/中继服务器）

需要一台双方都能访问的机器（局域网演示可直接用本机）：

```powershell
dotnet run --project src/ChenLinkServer          # 默认 0.0.0.0:9100
dotnet run --project src/ChenLinkServer -- 9000  # 自定义端口
```

放公网 VPS 时：防火墙放行 TCP 9100 即可（信令与中继共用该端口）。也可以直接在客户端里
勾选 **“内置信令服务器”**（默认端口 9100），本机即兼作服务器。

然后在“旧模式”卡片操作：

- **房主**：填本机游戏端口 → 创建房间 → 把 5 位**房间码**发给好友（可同时开启内置信令服务器）。
- **玩家**：填服务器地址 + 房间码 + 本机连接端口 → 加入房间。
- 状态变为 **“已就绪”** 后，在游戏里“直接连接”，填 `127.0.0.1:本机连接端口`。

### 常用游戏端口

| 游戏 | 端口 | 协议 |
|---|---|---|
| Minecraft 我的世界 (Java) | 25565 | TCP |
| Terraria 泰拉瑞亚 | 7777 | TCP |
| Stardew Valley 星露谷物语 | 24642 | TCP |
| Valheim 英灵神殿 | 2456 | TCP |
| Minecraft Bedrock 基岩版 | 19132 | UDP |

其它支持“直接连接 IP:端口”的 TCP/UDP 局域网游戏一般也能用（选“自定义端口”）。

## 🔨 从源码构建

```powershell
# 单文件自包含版（约 200MB，内置 .NET 8 + Windows App SDK，目标机器免安装运行时）
dotnet publish src/ChenLink/ChenLink.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o dist
```

> 客户端 WinUI 3 未打包、自包含（`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`），
> 目标机器无需安装 Windows App Runtime。当前仅提供 x64 Release，运行时资源仅保留 `en-US` 与 `zh-CN`。

## 📁 项目结构

```
ChenLink.sln
src/
  ChenLink/            WinUI 3 客户端（现代 UI，未打包、自包含）
    Engine/            纯 BCL 网络核心：Protocol / Mux / Session
    EasyTier/          EasyTier 子进程驱动（虚拟组网，独立进程调用）
    native/            EasyTier / wintun 等第三方二进制（见 THIRD_PARTY_NOTICES.md）
  ChenLinkServer/      信令 + 中继服务器（单文件，跨平台，可放公网 VPS）
docs/                  GitHub Pages 展示页（静态站点）
.github/workflows/     CI：自动部署 GitHub Pages
```

## 🧠 工作原理

1. 房主创建房间（随机 5 位码），玩家凭码加入 —— 信令服务器只负责**交换双方公网地址**。
2. 双方在同一网络（服务器看到的公网 IP 相同）：房主监听、玩家连房主局域网 IP —— 纯局域网直连。
3. 跨网络：双方用同一本地端口**同时发起 TCP 连接**（TCP 同时打开打洞）。
   家用路由器多为端口保留型 NAT，此方式可直连；不支持的（对称 NAT/CGNAT）自动失败。
4. 直连失败 → 双方各连一条服务器中继，服务器桥接 —— 保证一定可用。
5. 成功后数据走**一条持久链路**（直连或中继），内部用轻量帧复用多条游戏连接（OPEN / OPEN_OK / DATA / CLOSE / UDP）。

## ⚠️ 限制与安全

- 数据**未加密**：中继服务器能看到明文。请只使用自己信任的服务器、只和好友联机。
- 跨网直连成功率取决于双方 NAT 类型；失败自动走中继，不影响可用性。
- UDP 游戏的数据报会封装进同一条 TCP 链路转发（端口号不变，无需额外开端口）；对延迟极敏感的对战可优先用“无公网 IP 模式”（EasyTier 虚拟局域网）。
- 映射端口只监听 `127.0.0.1`，避免局域网他人误连。

## 🤝 贡献

欢迎提交 Issue 与 PR。请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 🔒 安全

发现安全问题时，请**不要公开提交 Issue**，按 [SECURITY.md](SECURITY.md) 中的方式私下联系维护者。

## 📄 许可证

- 本项目自身代码以 **Apache License 2.0** 发布，见 [LICENSE](LICENSE)。
- 内置的 EasyTier / wintun 等第三方二进制为 **LGPL-3.0 / 各自许可证**，以独立子进程方式调用、未链接，
  许可与源码信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 🙏 致谢

- [OpenP2P](https://github.com/openp2p-cn/openp2p)（MIT）：提供了“节点 + 端口转发 + 打洞/中继”的参考思路。
  本项目未引用其 Go 代码，是独立的 C# 精简重写，仅保留核心形态：房间码信令替代账号/控制台，
  TCP 同时打开打洞替代 QUIC/UDP 打洞，服务器中继替代共享节点中继。
- [EasyTier](https://github.com/EasyTier/EasyTier)（LGPL-3.0）：提供跨公网虚拟组网能力。
- [WireGuard / wintun](https://www.wintun.net/)：提供虚拟网卡驱动。