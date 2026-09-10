using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ChenLink.EasyTier;
using ChenLink.Engine;

namespace ChenLink;

public sealed partial class MainWindow : Window
{
    readonly ObservableCollection<string> _logs = new();
    readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread()!;
    Session? _s;
    bool _busy;
    CancellationTokenSource? _srvCts;
    EasyTierRunner? _et;
    bool _etIsHost;
    int _etPort = 25565;
    string? _etInvite;
    string? _etFound;
    bool _etScanning;
    bool _etUdpOnly;
    bool _settingUi;
    readonly AppSettings _settings = AppSettings.Load();
    const string AutoRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public MainWindow()
    {
        InitializeComponent();
        // 内容延伸到标题栏：去掉“资源管理器”式系统标题，自绘顶栏 + 深色系统按钮
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        ApplyTitleBarTheme();
        Activated += (_, __) => ApplyNativeTitleBar();
        try
        {
            // 1024x768 按逻辑像素折算 DPI：高缩放下窗口物理像素更大，布局宽度仍是 1024，内容不会被裁
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            double scale = dpi >= 96 ? dpi / 96.0 : 1.0;
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1024 * scale), (int)(768 * scale)));
        }
        catch { }
        LogList.ItemsSource = _logs;
        Log("ChenLink 就绪：可勾选右上“内置信令服务器”，或连接已有服务器（默认 127.0.0.1:9100）。");
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* 低版本系统忽略 */ }
        Closed += async (_, _) =>
        {
            try { _srvCts?.Cancel(); } catch { }
            try { if (_s is not null) await _s.DisconnectAsync(); } catch { }
            try { if (_et is not null) await _et.StopAsync(); } catch { }
        };
        GameBox.SelectedIndex = 0;
        EtGameBox.SelectedIndex = 0; // 无公网IP板块预设（25565）
        _settingUi = true;
        AutoStartSwitch.IsOn = _settings.AutoStart;
        AutoProbeSwitch.IsOn = _settings.AutoProbe;
        _settingUi = false;
        SetAutoStart(_settings.AutoStart);
        if (_settings.AutoProbe)
            _ = Task.Run(async () => { await Task.Delay(1500); _dq.TryEnqueue(() => { try { EtProbeBtn_Click(EtProbeBtn, new RoutedEventArgs()); } catch { } }); });
    }

    /// 把系统最小化/最大化/关闭按钮染成深色，融入自绘顶栏
    void ApplyTitleBarTheme()
    {
        try
        {
            var tb = this.AppWindow.TitleBar;
            tb.ExtendsContentIntoTitleBar = true;
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                tb.PreferredHeightOption = TitleBarHeightOption.Collapsed; // 隐藏系统三键，由自绘按钮接管
            }
            tb.BackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(235, 255, 255, 255);
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(130, 255, 255, 255);
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(70, 255, 255, 255);
            tb.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(90, 0, 120, 215);
            tb.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
            tb.ForegroundColor = Microsoft.UI.Colors.White;
            tb.InactiveForegroundColor = Windows.UI.Color.FromArgb(150, 255, 255, 255);
        }
        catch { /* 旧系统不支持就回退默认标题栏 */ }
    }
    void TitleMinBtn_Click(object sender, RoutedEventArgs e) => (this.AppWindow.Presenter as OverlappedPresenter)?.Minimize();

    void TitleMaxBtn_Click(object sender, RoutedEventArgs e)
    {
        var p = this.AppWindow.Presenter as OverlappedPresenter;
        if (p is null) return;
        if (p.State == OverlappedPresenterState.Maximized) p.Restore(); else p.Maximize();
        SyncMaxIcon();
    }

    void TitleCloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    void SyncMaxIcon()
    {
        var p = this.AppWindow.Presenter as OverlappedPresenter;
        MaxIcon.Glyph = p is { State: OverlappedPresenterState.Maximized } ? "\uE923" : "\uE922";
    }
    bool _nativeTitleApplied;

    /// 窗口激活后再设置一次标题栏与图标（此时 HWND 才有效）；并强制 DWM 深色标题栏兜底
    void ApplyNativeTitleBar()
    {
        if (_nativeTitleApplied) return;
        _nativeTitleApplied = true;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int dark = 1;
            _ = DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, Marshal.SizeOf<int>());
            var ico = ExtractIconToTemp();
            if (ico is not null)
            {
                try { this.AppWindow.SetIcon(ico); } catch { }
                SetTaskbarIcon(hwnd, ico); // Win32 WM_SETICON 兜底，保证任务栏显示
            }
        }
        catch { }
        ApplyTitleBarTheme();
    }

    /// 从内嵌资源解出图标到本地（单文件 exe 也能用），失败返回 null
    static string? ExtractIconToTemp()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChenLink");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "ChenLink.ico");
            var asm = typeof(MainWindow).Assembly;
            using var src = asm.GetManifestResourceStream("assets.ChenLink.ico");
            if (src is null) return null;
            if (!File.Exists(dest) || new FileInfo(dest).Length != src.Length)
            {
                using var fs = File.Create(dest);
                src.CopyTo(fs);
            }
            return dest;
        }
        catch { return null; }
    }

    static IntPtr _appIcon = IntPtr.Zero;

    /// LoadImage + WM_SETICON：直接把图标设到窗口（任务栏图标最稳的兜底）
    static void SetTaskbarIcon(IntPtr hwnd, string icoPath)
    {
        try
        {
            if (hwnd == IntPtr.Zero || _appIcon != IntPtr.Zero) return;
            _appIcon = LoadImage(IntPtr.Zero, icoPath, 1 /*IMAGE_ICON*/, 0, 0, 0x10 /*LR_LOADFROMFILE*/);
            if (_appIcon != IntPtr.Zero)
            {
                _ = SendMessage(hwnd, 0x0080 /*WM_SETICON*/, new IntPtr(1) /*ICON_BIG*/, _appIcon);
                _ = SendMessage(hwnd, 0x0080, new IntPtr(0) /*ICON_SMALL*/, _appIcon);
            }
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
    // ---------- 事件 ----------

    void GameBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameBox.SelectedItem is ComboBoxItem it && it.Tag is string tag && tag.Length > 0)
        {
            GamePort.Text = tag;
            MapPort.Text = tag;
        }
    }

    void EtGameBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EtGameBox.SelectedItem is ComboBoxItem it && it.Tag is string tag && tag.Length > 0)
            EtPortBox.Text = tag; // 与旧模式同一套预设；选“自定义端口”时保留手填
    }

    async void CreateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(GamePort.Text.Trim(), out int port) || port is < 1 or > 65535)
        { Log("⚠️ 请填写正确的游戏端口（1-65535）"); return; }
        await RunAsync(async () =>
        {
            var s = NewSession();
            try { await s.CreateRoomAsync(port); }
            catch { await s.DisconnectAsync(); throw; }
            _s = s;
            CodeText.Text = s.Room;
            HostCodePanel.Visibility = Visibility.Visible;
        });
    }

    async void JoinBtn_Click(object sender, RoutedEventArgs e)
    {
        var code = RoomCode.Text.Trim().ToUpperInvariant();
        if (code.Length != 5) { Log("⚠️ 房间码为 5 位"); return; }
        if (!int.TryParse(MapPort.Text.Trim(), out int port) || port is < 1 or > 65535)
        { Log("⚠️ 请填写正确的本机连接端口（1-65535）"); return; }
        await RunAsync(async () =>
        {
            var s = NewSession();
            try { await s.JoinRoomAsync(code, port); }
            catch { await s.DisconnectAsync(); throw; }
            _s = s;
            JoinHint.Text = "已请求加入，等待房主…";
        });
    }

    async void StopBtn_Click(object sender, RoutedEventArgs e) => await StopAsync("已手动断开");

    void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
            pkg.SetText(_s?.Room ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            Log("房间码已复制");
        }
        catch { Log("复制失败，请手动复制：" + _s?.Room); }
    }

    void EmbedSrvSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (EmbedSrvSwitch.IsOn == true) StartEmbeddedServer();
        else StopEmbeddedServer();
    }

    void StartEmbeddedServer()
    {
        if (!int.TryParse(SrvPortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            Log("⚠️ 服务器端口无效（1-65535）");
            EmbedSrvSwitch.IsOn = false;
            return;
        }
        try
        {
            _srvCts?.Cancel();
            _srvCts = new CancellationTokenSource();
            var ct = _srvCts.Token;
            _ = Task.Run(async () =>
            {
                try { await ChenLinkServer.ChenLinkServerCore.RunAsync(port, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _dq.TryEnqueue(() => { Log("❌ 内置信令服务器异常退出：" + ex.Message); EmbedSrvSwitch.IsOn = false; });
                }
            });
            Log($"✅ 内置信令服务器已启动：0.0.0.0:{port}");
            var lan = LocalIPs();
            Log(lan.Length > 0
                ? $"好友“信令服务器”请填：{lan[0]}:{port}"
                : "本机似乎没有局域网 IP，跨网联机需公网 IP 或端口映射");
        }
        catch (Exception ex)
        {
            Log("❌ 内置信令服务器启动失败：" + ex.Message);
            EmbedSrvSwitch.IsOn = false;
            _srvCts = null;
        }
    }

    void StopEmbeddedServer()
    {
        _srvCts?.Cancel();
        _srvCts = null;
        Log("内置信令服务器已停止");
    }

    static string[] LocalIPs()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(ua.Address))
                        list.Add(ua.Address.ToString());
            }
        }
        catch { }
        return list.ToArray();
    }

    // ---------- 逻辑 ----------

    Session NewSession()
    {
        var s = new Session(ServerBox.Text.Trim(), NameBox.Text.Trim());
        s.Log += m => _dq.TryEnqueue(() => Log(m));
        s.Changed += () => _dq.TryEnqueue(Refresh);
        s.PeerLeft += () => _dq.TryEnqueue(async () => await StopAsync("对方已离开，已断开"));
        return s;
    }

    /// 包一层：置忙/错误兜底。会话本身失败也会触发 StopAsync（见事件监听）。
    async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        SetBusy(true);
        try { await action(); }
        catch (Exception ex) { Log("❌ " + ex.Message); }
        finally { SetBusy(false); }
    }

    async Task StopAsync(string why)
    {
        Log(why);
        var s = _s;
        _s = null;
        if (s is not null) await s.DisconnectAsync();
        HostCodePanel.Visibility = Visibility.Collapsed;
        JoinHint.Text = "提示：先让房主创建房间拿到房间码。";
        SetBusy(false);
        Refresh();
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        bool canStart = !busy && _s is null;
        CreateBtn.IsEnabled = canStart;
        JoinBtn.IsEnabled = canStart;
        StopBtn.IsEnabled = !busy;
        StopBtn.Visibility = busy || _s is not null ? Visibility.Visible : Visibility.Collapsed;
        EtHostBtn.IsEnabled = !busy && _et is null;
        EtJoinBtn.IsEnabled = !busy && _et is null;
        ServerBox.IsEnabled = _s is null;
        NameBox.IsEnabled = _s is null;
    }

    void Refresh()
    {
        var s = _s;
        if (s is null)
        {
            StatusText.Text = "空闲";
            DetailText.Text = "在上方选择房主或玩家。";
            ModeText.Text = "模式：-";
            ChanText.Text = "连接：0";
            BytesText.Text = "流量：↑ 0 ↓ 0";
            StopBtn.Visibility = Visibility.Collapsed;
            return;
        }
        StatusText.Text = s.State switch
        {
            SessionState.Waiting => s.Role == Role.Host ? "等待好友加入…" : "等待房主就绪…",
            SessionState.Punching => "正在打通链路…",
            SessionState.Ready => "已就绪，可以进游戏了！",
            SessionState.Failed => "出错了",
            _ => "空闲",
        };
        DetailText.Text = s.Detail;
        ModeText.Text = "模式：" + s.Mode;
        ModeText.Foreground = new SolidColorBrush(s.Mode == "直连" ? Microsoft.UI.Colors.LightGreen
            : s.Mode == "中继" ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.Gray);
        ChanText.Text = $"游戏连接：{s.Channels}";
        BytesText.Text = $"流量：↑ {Human(s.BytesUp)} ↓ {Human(s.BytesDown)}";
        StopBtn.Visibility = s.State is SessionState.Ready or SessionState.Punching or SessionState.Waiting
            ? Visibility.Visible : Visibility.Collapsed;
        if (s.State == SessionState.Failed) _ = Task.Run(async () => { await Task.Delay(1200); _dq.TryEnqueue(() => { if (_s == s) _ = StopAsync("会话出错，已自动断开"); }); });
    }

    static string Human(long b) => b switch
    {
        >= 1024 * 1024 => $"{b / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{b / 1024.0:F1} KB",
        _ => $"{b} B",
    };

    void Log(string msg)
    {
        _logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        while (_logs.Count > 300) _logs.RemoveAt(0);
        if (LogList.Items.Count > 0)
            try { LogList.ScrollIntoView(LogList.Items[^1]); } catch { }
    }
    // ---------- 无公网IP组网（EasyTier） ----------

    static string Rand(int n)
    {
        const string a = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, n).Select(_ => a[Random.Shared.Next(a.Length)]).ToArray());
    }

    static string[] ParseNodes(string s)
    {
        var list = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => x.StartsWith("tcp://") || x.StartsWith("udp://") || x.StartsWith("ws://")).ToArray();
        return list.Length > 0 ? list : EasyTierRunner.DefaultNodes;
    }

    async void EtHostBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_et is not null) { Log("请先断开当前虚拟网络"); return; }
        if (!int.TryParse(EtPortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        { Log("⚠️ 请先在“无公网IP”板块选游戏/填端口（1-65535）"); return; }
        if (!EnsureAdmin()) return;
        _etPort = port;
        _etIsHost = true;
        _etInvite = $"C{Rand(5)}|{Rand(8)}|{port}";
        await RunEtAsync(async () =>
        {
            var r = NewEt();
            EtHostInfo.Visibility = Visibility.Visible;
            EtInviteText.Text = _etInvite!;
            EtHostIpText.Text = "启动中…（首次请允许管理员授权）";
            EtHostHint.Text = "等待组网完成…";
            try
            {
                await r.StartAsync(_etInvite!.Split('|')[0], _etInvite.Split('|')[1], ParseNodes(EtNodesBox.Text), NameBox.Text.Trim(), "host");
            }
            catch
            {
                if (_et == r) _et = null;
                await r.StopAsync();
                throw;
            }
        });
    }

    async void EtJoinBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_et is not null) { Log("请先断开当前虚拟网络"); return; }
        var inv = EtInviteBox.Text.Trim();
        var parts = inv.Split('|');
        if (parts.Length != 3 || parts[0].Length < 3 || parts[1].Length < 3 || !int.TryParse(parts[2], out int port))
        { Log("⚠️ 请整行粘贴房主复制的邀请码（格式：网络名|口令|端口）"); return; }
        if (!EnsureAdmin()) return;
        _etPort = port;
        _etIsHost = false;
        _etInvite = inv;
        _etFound = null;
        _etUdpOnly = false;
        EtJoinInfo.Visibility = Visibility.Visible;
        EtJoinIpText.Text = "启动中…（首次请允许管理员授权）";
        EtFoundText.Text = "正在组网并自动寻找房主的游戏服务器…";
        await RunEtAsync(async () =>
        {
            var r = NewEt();
            try
            {
                await r.StartAsync(parts[0], parts[1], ParseNodes(EtNodesBox.Text), NameBox.Text.Trim(), "guest");
            }
            catch
            {
                if (_et == r) _et = null;
                await r.StopAsync();
                throw;
            }
        });
    }

    async void EtStopBtn_Click(object sender, RoutedEventArgs e)
    {
        var r = _et;
        _et = null;
        if (r is not null) await r.StopAsync();
        EtStopBtn.Visibility = Visibility.Collapsed;
        EtHostInfo.Visibility = Visibility.Collapsed;
        EtJoinInfo.Visibility = Visibility.Collapsed;
        SetBusy(false);
    }

    async void EtProbeBtn_Click(object sender, RoutedEventArgs e)
    {
        EtProbeBtn.IsEnabled = false;
        try
        {
            Log("正在获取并探测可用公共节点（逐个 TCP 测速，约几秒）…");
            var nodes = await EasyTierRunner.AutoFetchNodesAsync();
            if (nodes.Length == 0)
            {
                EtPingText.Text = "⚠️ 未探测到可用节点（可能网络受限），继续使用输入框里的节点。";
                Log("⚠️ 没探测到可达节点（可能网络受限），将沿用输入框里的默认节点");
                return;
            }
            EtNodesBox.Text = string.Join(", ", nodes.Select(n => n.Node));
            var ping = string.Join(" · ", nodes.Select(n => $"{n.Node.Replace("tcp://", "")} {n.Ms}ms"));
            EtPingText.Text = $"📶 节点延迟（升序）：{ping}";
            Log($"✅ 可用公共节点 {nodes.Length} 个（按延迟升序）：{ping}");
        }
        catch (Exception ex) { Log("❌ 自动获取失败：" + ex.Message); }
        finally { EtProbeBtn.IsEnabled = true; }
    }

    void EtCopyInviteBtn_Click(object sender, RoutedEventArgs e) => CopyText(_etInvite ?? "");

    void EtCopyAddrBtn_Click(object sender, RoutedEventArgs e) => CopyText(_etFound ?? "");

    EasyTierRunner NewEt()
    {
        var r = new EasyTierRunner();
        r.Log += m => _dq.TryEnqueue(() => Log(m));
        r.Changed += () => _dq.TryEnqueue(() =>
        {
            UpdateEt(r);
            if (!_etIsHost) _ = Task.Run(() => ScanServerAsync(r));
        });
        _et = r;
        return r;
    }

    void UpdateEt(EasyTierRunner r)
    {
        EtStopBtn.Visibility = Visibility.Visible;
        if (_etIsHost)
        {
            EtHostIpText.Text = r.OwnIp.Length > 0 ? $"本机虚拟 IP：{r.OwnIp}" : r.StatusText;
            if (r.Ready)
                EtHostHint.Text = $"✅ 组网就绪。好友加入后，让他在游戏里“直接连接 {r.OwnIp}:{_etPort}”。";
        }
        else
        {
            EtJoinIpText.Text = r.OwnIp.Length > 0 ? $"本机虚拟 IP：{r.OwnIp}（{r.StatusText}）" : r.StatusText;
            if (_etFound is not null)
            {
                EtFoundText.Text = _etUdpOnly
                    ? $"✅ 组网就绪（UDP）：游戏里直接连接 {_etFound}"
                    : $"✅ 找到房主的游戏服务器：{_etFound}";
                EtCopyAddrBtn.Visibility = Visibility.Visible;
            }
        }
        if (_s is null)
        {
            StatusText.Text = "无公网组网：" + (r.Ready ? "已就绪" : r.StatusText);
            DetailText.Text = r.OwnIp.Length > 0 ? $"本机虚拟 IP：{r.OwnIp}；请在游戏里连接房主的虚拟 IP。日志见下方。" : "正在连接 EasyTier 公共节点…";
        }
    }

    async Task ScanServerAsync(EasyTierRunner r)
    {
        if (_etScanning || _etFound is not null || !r.Ready) return;
        var peers = r.Peers.Where(p => !string.IsNullOrEmpty(p.Ipv4) && p.Ipv4 != r.OwnIp).ToList();
        if (peers.Count == 0) return;
        _etScanning = true;
        try
        {
            foreach (var p in peers)
            {
                if (await EasyTierRunner.IsPortOpenAsync(p.Ipv4, _etPort))
                {
                    _etFound = $"{p.Ipv4}:{_etPort}";
                    _etUdpOnly = false;
                    _dq.TryEnqueue(() =>
                    {
                        Log($"🎮 已找到房主的游戏服务器：{_etFound}（若游戏未开服会连不上）");
                        UpdateEt(r);
                    });
                    return;
                }
            }
            // UDP 协议没有通用“端口探测”，直接把对端虚拟 IP 亮出来，供 UDP 游戏（如基岩版）手填连接
            // ponytail: 两人房这里就是房主；多人房要选人时再加下拉
            _etUdpOnly = true;
            _etFound = $"{peers[0].Ipv4}:{_etPort}";
            _dq.TryEnqueue(() =>
            {
                Log($"🎮 未探测到 TCP 游戏服；若游戏用 UDP，请直接连对端虚拟 IP：{_etFound}");
                UpdateEt(r);
            });
        }
        finally { _etScanning = false; }
    }

    bool EnsureAdmin()
    {
        if (EasyTierRunner.IsAdministrator) return true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };
            Process.Start(psi);
            Log("需要管理员权限，已请求以管理员身份重启，请在新窗口继续操作。");
            Close();
        }
        catch { Log("❌ 无法获取管理员权限：请右键 ChenLink.exe → 以管理员身份运行"); }
        return false;
    }

    void CopyText(string text)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            Log("已复制：" + text);
        }
        catch { Log("复制失败，请手动复制：" + text); }
    }

    async Task RunEtAsync(Func<Task> act)
    {
        if (_busy) return;
        SetBusy(true);
        try { await act(); }
        catch (Exception ex) { Log("❌ " + ex.Message); }
        finally { SetBusy(false); }
    }
    // ---------- 设置 ----------

    void TitleSettings_Click(object sender, RoutedEventArgs e)
        => SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingUi) return;
        _settings.AutoStart = AutoStartSwitch.IsOn == true;
        _settings.Save();
        SetAutoStart(_settings.AutoStart);
        Log(_settings.AutoStart ? "✅ 已开启开机自启动" : "已关闭开机自启动");
    }

    void AutoProbeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingUi) return;
        _settings.AutoProbe = AutoProbeSwitch.IsOn == true;
        _settings.Save();
        Log(_settings.AutoProbe ? "✅ 下次启动将自动获取可用公共节点" : "已关闭启动自动探测");
    }

    static void SetAutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(AutoRunKey, writable: true);
            if (k is null) return;
            if (on) k.SetValue("ChenLink", "\"" + Environment.ProcessPath + "\"");
            else k.DeleteValue("ChenLink", throwOnMissingValue: false);
        }
        catch { /* 权限/环境异常忽略 */ }
    }

    sealed class AppSettings
    {
        public bool AutoStart { get; set; }
        public bool AutoProbe { get; set; }

        static string Path() => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChenLink", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                var p = Path();
                if (File.Exists(p))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var p = Path();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(this));
            }
            catch { }
        }
    }
}



















