using System;
using System.IO;
using System.Text.Json;
using Easy4K.Services;
using Easy4K.Services.RemoteControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easy4K.Views.Remote;

/// <summary>「遥控另一台 Easy4K」的入口页：只负责连接。
///
/// 连上后**不在这里操作** —— 由 <see cref="RemoteSession"/> 把远端状态镜像进 MainViewModel，
/// 然后直接切回「主页」，用本机原有的「主页 / 高级 / 进行中」三个页面（同一套控件）去遥控对方。
/// 窗口顶部进度条下方会出现黄色「已连接至 …」横幅，横幅上的「断开」用于结束会话。</summary>
public sealed partial class RemoteClientPage : Page
{
    /// <summary>当前页面实例（供主窗口横幅上的「断开」使用）</summary>
    public static RemoteClientPage? Instance { get; private set; }

    public RemoteClientPage()
    {
        Instance = this;
        InitializeComponent();
        HostBox.Text = LoadSavedHost();
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var (host, port) = NormalizeHost(HostBox.Text);
        // 地址里没写端口时用右侧端口框的值（否则那个框填了也不生效）
        if (port == RemoteControlServer.Port &&
            int.TryParse(PortBox.Text.Trim(), out var boxPort) && boxPort is > 0 and < 65536)
            port = boxPort;

        if (host.Length == 0)
        {
            ShowError("请填写对方电脑的 IP 或域名（IPv6 写成 [地址]:端口，例如 [240e::1]:11451）");
            return;
        }

        ConnectError.IsOpen = false;
        ConnectButton.IsEnabled = false;
        ConnectBusy.Visibility = Visibility.Visible;
        ConnectStep.Text = "准备连接 …";

        RemoteSession? session = null;
        try
        {
            var vm = App.Services;
            if (App.MainWindow is null) { ShowError("主窗口不可用"); return; }

            // 已有会话先断开，避免残留连接
            if (RemoteSession.Current is not null) await RemoteSession.Current.DisconnectAsync();

            session = new RemoteSession(vm, DispatcherQueue);
            session.Step += s => DispatcherQueue.TryEnqueue(() => ConnectStep.Text = s);
            await session.ConnectAsync(host, port);

            SaveHost(host, port);
            App.MainWindow.ShowRemoteConnectedBanner(session.Address);
            App.MainWindow.NavigateToHome();
        }
        catch (Exception ex)
        {
            if (session is not null) await session.DisconnectAsync();
            ShowError(ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectBusy.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string msg)
    {
        ConnectError.Message = msg;
        ConnectError.IsOpen = true;
    }

    /// <summary>把用户填的地址拆成（主机, 端口）。支持：
    /// · IPv4 / 域名，可带端口：<c>192.168.31.156</c>、<c>home.example.com:11451</c>
    /// · IPv6，推荐 <c>[240e:xxxx::1]:11451</c> 或 <c>[240e:xxxx::1]</c>（方括号会被去掉，因为 TcpClient 只吃裸地址）
    /// · 裸 IPv6（<c>240e:xxxx::1</c>）：冒号超过一个就整串当地址，端口用默认值 —— 否则末尾那段会被误当端口
    /// · 带协议/路径的粘贴形式：<c>http://host:11451/xxx</c> 会先剥掉 scheme 与路径</summary>
    public static (string host, int port) NormalizeHost(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return ("", RemoteControlServer.Port);
        s = s.Replace("http://", "").Replace("https://", "");
        var slash = s.IndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) s = s[..slash];
        s = s.Trim();

        var port = RemoteControlServer.Port;

        // 带方括号的 IPv6：[addr] 或 [addr]:port
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close > 1)
            {
                var tail = s[(close + 1)..];
                if (tail.StartsWith(':') &&
                    int.TryParse(tail[1..].Trim(), out var p6) && p6 is > 0 and < 65536)
                    port = p6;
                return (s[1..close].Trim(), port);
            }
        }

        // 裸 IPv6：两个以上冒号且没有方括号 → 整串都是地址，不能再按"最后一个冒号"切端口
        if (s.Count(c => c == ':') >= 2) return (s, port);

        var colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..].Trim(), out var p) && p is > 0 and < 65536)
        {
            port = p;
            s = s[..colon];
        }
        return (s.Trim(), port);
    }

    private static string PrefFile => Path.Combine(AppPaths.WritableRoot, "remote_client.json");

    private static string LoadSavedHost()
    {
        try
        {
            if (File.Exists(PrefFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(PrefFile));
                if (doc.RootElement.TryGetProperty("host", out var h))
                {
                    var host = h.GetString() ?? "";
                    if (host.Length == 0) return "";
                    var port = doc.RootElement.TryGetProperty("port", out var p) && p.TryGetInt32(out var pv)
                        ? pv
                        : RemoteControlServer.Port;
                    if (port == RemoteControlServer.Port) return host;
                    // 非默认端口时把端口一起回填，否则下次连接会悄悄退回 11451。
                    // IPv6 必须加方括号，不然末尾那段会被当成端口。
                    return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
                }
            }
        }
        catch { }
        return "";
    }

    private static void SaveHost(string host, int port)
    {
        try { File.WriteAllText(PrefFile, $"{{\"host\":\"{host}\",\"port\":{port}}}"); } catch { }
    }
}
