using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easy4K;

/// <summary>
/// 开发版的引导演示页：用 <see cref="Controls.GuideOverlay"/> 演示引导的观感，
/// 真正的「使用引导」入口在主导航的「使用引导」里（见 MainWindow.StartTour）。
/// </summary>
public sealed partial class GuidePage : Page
{
    private sealed record Step(FrameworkElement Target, string Title, string Body);

    private readonly List<Step> _steps = [];
    private int _index;

    public GuidePage()
    {
        InitializeComponent();

        _steps.Add(new(TargetPick, "选择要处理的视频", "点这里挑选素材，也支持把文件直接拖进来批量加入队列。"));
        _steps.Add(new(TargetQuality, "挑选画质档位", "4K 超分、RIFE 补帧、HDR 映射都在这里配，预设一键套用。"));
        _steps.Add(new(TargetStart, "开始处理", "点一下就开始；进度、日志和报错都会同步到手机端。"));
        _steps.Add(new(TargetRemote, "手机远程控制", "手机连上同一台电脑，就能选片、看进度、下载成品。"));

        Guide.StepRequested += OnStepRequested;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        _index = 0;
        Show(0);
    }

    private void OnStepRequested(object? sender, int index)
    {
        if (index < 0 || index >= _steps.Count)
        {
            Guide.Close();
            return;
        }

        Show(index);
    }

    private void Show(int index)
    {
        _index = index;
        var step = _steps[index];
        Guide.ShowStep(step.Target, step.Title, step.Body, index, _steps.Count);
    }
}
