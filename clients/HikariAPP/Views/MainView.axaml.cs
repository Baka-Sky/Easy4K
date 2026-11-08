using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HikariAPP2.ViewModels;

namespace HikariAPP2.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is RemoteCore core) core.PickVideoFile = PickVideoAsync;
    }

    /// <summary>用系统文件选择器挑一个手机视频（启动处理时自动上传到电脑）。</summary>
    private async Task<IStorageFile?> PickVideoAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return null;

        var type = new FilePickerFileType("视频")
        {
            MimeTypes = new[] { "video/*" },
            Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.ts", "*.mpg", "*.wmv" }
        };

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择手机视频",
            AllowMultiple = false,
            FileTypeFilter = new[] { type }
        });

        return files.Count > 0 ? files[0] : null;
    }
}
