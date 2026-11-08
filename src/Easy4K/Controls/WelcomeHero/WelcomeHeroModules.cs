// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Ported from Microsoft PowerToys (https://github.com/microsoft/PowerToys)
// Original: src/settings-ui/Settings.UI/SettingsXAML/Controls/WelcomeHero/WelcomeHeroModules.cs
// License: MIT (Copyright (c) Microsoft Corporation). See THIRD-PARTY-NOTICES in the repo for details.
using System.Collections.Generic;

namespace Easy4K.Controls.WelcomeHero
{
    /// <summary>
    /// The curated set of modules that warp into the Welcome hero.
    /// The order matters: the first modules land closest to the logo.
    /// </summary>
    /// <remarks>
    /// The icons live in <c>Assets/Settings/Modules/OOBE/Welcome/</c>. In Easy4K they are Sun Valley (Fluent)
    /// glyphs rendered from the FluentSystemIcons font to PNG, and the entries below describe Easy4K features
    /// instead of PowerToys modules; the rendering code is otherwise unchanged from the PowerToys original.
    /// </remarks>
    public static class WelcomeHeroModules
    {
        public const string AssetFolder = "Assets/Settings/Modules/OOBE/Welcome/";

        public const string LogoAssetName = "Easy4K";

        public static IReadOnlyList<WelcomeHeroModule> All { get; } =
        [
            new("Upscale", "Upscale", "画质超分"),
            new("Video", "Video", "视频处理"),
            new("Movie", "Movie", "影片增强"),
            new("Film", "Film", "胶片修复"),
            new("Clip", "Clip", "片段裁剪"),
            new("Queue", "Queue", "批量队列"),
            new("Fullscreen", "Fullscreen", "4K 输出"),
            new("Image", "Image", "图像增强"),
            new("Audio", "Audio", "音频超分"),
            new("Music", "Music", "音乐修复"),
            new("Headphones", "Headphones", "监听对比"),
            new("Speaker", "Speaker", "音轨输出"),
            new("Mic", "Mic", "录音输入"),
            new("Gauge", "Gauge", "性能监控"),
            new("Speed", "Speed", "极速模式"),
            new("Rocket", "Rocket", "一键加速"),
            new("Timer", "Timer", "任务计时"),
            new("Battery", "Battery", "功耗控制"),
            new("Remote", "Remote", "远程控制"),
            new("Desktop", "Desktop", "桌面端"),
            new("Wifi", "Wifi", "局域网"),
            new("Server", "Server", "服务端"),
            new("Download", "Download", "下载成品"),
            new("Upload", "Upload", "上传素材"),
            new("Folder", "Folder", "素材目录"),
            new("Document", "Document", "任务记录"),
            new("Drive", "Drive", "缓存管理"),
            new("Box", "Box", "成品库"),
            new("Sparkle", "Sparkle", "AI 增强"),
            new("Beaker", "Beaker", "实验特性"),
            new("Settings", "Settings", "偏好设置"),
            new("Search", "Search", "文件扫描"),
            new("History", "History", "历史记录"),
            new("Chart", "Chart", "数据统计"),
            new("Check", "Check", "完成校验"),
        ];

        public static string GetAssetUri(string assetName) => $"ms-appx:///{AssetFolder}{assetName}.png";
    }
}
