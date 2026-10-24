using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easy4K;

/// <summary>关于页面：由设置页进入，展示版本、组件与免责声明。
/// 正文直接复用 <see cref="MainWindow.AboutText"/>，与旧「关于」弹窗保持同一份内容。</summary>
public sealed partial class AboutPage : Page
{
    /// <summary>标题右侧的版本号（取程序集版本，与标题栏一致）</summary>
    public string Version => App.Services.Version;

    public AboutPage()
    {
        InitializeComponent();

        var text = App.MainWindow?.AboutText ?? "";
        // AboutText 首行是「Easy4K vX - 一键视频超分补帧工具」，页面上方已单独显示，正文去掉这行
        var firstBreak = text.IndexOf('\n');
        AboutTextBlock.Text = firstBreak >= 0 ? text[(firstBreak + 1)..].TrimStart() : text;
    }

    private void OnBack(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateToSettings();
}
