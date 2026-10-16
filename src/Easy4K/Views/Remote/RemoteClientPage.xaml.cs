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
        if (host.Length == 0)
        {
            ShowError("请填写对方电脑的 IP 地址");
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

    /// <summary>容错：允许直接粘贴 "192.168.1.5:11451"、"http://192.168.1.5/" 这类整串。</summary>
    public static (string host, int port) NormalizeHost(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return ("", RemoteControlServer.Port);
        s = s.Replace("http://", "").Replace("https://", "");
        var slash = s.IndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) s = s[..slash];
        s = s.Trim();
        var port = RemoteControlServer.Port;
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
                if (doc.RootElement.TryGetProperty("host", out var h)) return h.GetString() ?? "";
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
