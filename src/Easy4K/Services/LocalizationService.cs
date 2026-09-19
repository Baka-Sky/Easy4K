using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Easy4K.Services;

/// <summary>多语言文案服务。以「中文原文」为键，翻译表放在 exe 旁的 Loc/strings.json。
/// 界面本地化用 <see cref="LocalizeTree"/> 遍历可视树替换文案（找不到译文就保留原文，因此
/// 缺少词条时界面不会出现空白或键名）；语言切换立即生效，无需重启。
/// 语言代码同时写入 appsettings.json 的 Language 字段，下次启动恢复。</summary>
public static class Loc
{
    /// <summary>支持的语言（代码 → 显示名，顺序即菜单顺序）</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"),
        ("ja-JP", "日本語"),
        ("ko-KR", "한국어"),
        ("en-US", "English")
    };

    private const string Fallback = "zh-CN";

    /// <summary>语言代码 → （中文原文 → 译文）</summary>
    private static Dictionary<string, Dictionary<string, string>> _map = new();

    private static string _current = Fallback;

    /// <summary>语言切换后触发（窗口据此重新本地化界面树）</summary>
    public static event Action? LanguageChanged;

    /// <summary>当前语言代码</summary>
    public static string Current => _current;

    /// <summary>当前语言显示名</summary>
    public static string CurrentName
    {
        get
        {
            foreach (var (code, name) in Languages)
                if (code == _current) return name;
            return _current;
        }
    }

    static Loc() => Reload();

    /// <summary>重新读取 Loc/strings.json（缺失/损坏时退化为空表，界面保留中文原文）</summary>
    public static void Reload()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Loc", "strings.json");
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            _map = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new();
        }
        catch
        {
            // 读不到就当作没有译文：界面显示中文原文，不影响功能
        }
    }

    /// <summary>按中文原文取译文；当前是简体中文或没有词条时返回原文。</summary>
    public static string Tr(string source)
    {
        if (string.IsNullOrEmpty(source) || _current == Fallback) return source;
        if (_map.TryGetValue(_current, out var table) && table.TryGetValue(source, out var text) && !string.IsNullOrEmpty(text))
            return text;
        return source;
    }

    /// <summary>切换当前语言（不落盘；持久化由 ViewModel 负责）。</summary>
    public static void Set(string? code, bool notify = true)
    {
        var normalized = Normalize(code);
        if (_current == normalized) return;
        _current = normalized;
        if (notify) LanguageChanged?.Invoke();
    }

    /// <summary>把常见写法归一化成四种语言代码（zh-Hans/zh-Hant/ja/ko 等）。</summary>
    private static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Fallback;
        foreach (var (c, _) in Languages)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return c;

        var lower = code.ToLowerInvariant();
        if (lower.StartsWith("zh"))
            return lower.Contains("tw") || lower.Contains("hk") || lower.Contains("hant") ? "zh-TW" : "zh-CN";
        if (lower.StartsWith("ja")) return "ja-JP";
        if (lower.StartsWith("ko")) return "ko-KR";
        if (lower.StartsWith("en")) return "en-US";
        return Fallback;
    }

    // ===================== 界面本地化 =====================

    /// <summary>元素 → 首次见到时的原文。切换语言时用原文重新查表，避免"译文再翻译"。</summary>
    private static readonly ConditionalWeakTable<DependencyObject, string> _origin = new();

    /// <summary>遍历可视树，把已知的中文原文替换成当前语言译文（未收录的文案原样保留）。</summary>
    public static void LocalizeTree(DependencyObject? root)
    {
        if (root is null) return;
        try
        {
            LocalizeElement(root);
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                LocalizeTree(VisualTreeHelper.GetChild(root, i));
        }
        catch
        {
            // 树结构异常（元素尚未加载完）时跳过，下次导航会再试
        }
    }

    /// <summary>代码里 new 出来的 ContentDialog 不在可视树里（弹窗挂在 XamlRoot 的 popup 层），
    /// 显示前调一次这里：标题/内容/三个按钮全部按当前语言翻译。内容为字符串时直接查表，
    /// 为面板时递归遍历其可视树。</summary>
    public static void LocalizeDialog(ContentDialog? dialog)
    {
        if (dialog is null) return;
        try
        {
            if (dialog.Title is string title && !string.IsNullOrEmpty(title))
                dialog.Title = Tr(title);

            switch (dialog.Content)
            {
                case string text when !string.IsNullOrEmpty(text):
                    dialog.Content = Tr(text);
                    break;
                case DependencyObject panel:
                    LocalizeTree(panel);
                    break;
            }

            if (!string.IsNullOrEmpty(dialog.PrimaryButtonText)) dialog.PrimaryButtonText = Tr(dialog.PrimaryButtonText);
            if (!string.IsNullOrEmpty(dialog.SecondaryButtonText)) dialog.SecondaryButtonText = Tr(dialog.SecondaryButtonText);
            if (!string.IsNullOrEmpty(dialog.CloseButtonText)) dialog.CloseButtonText = Tr(dialog.CloseButtonText);
        }
        catch { }
    }

    /// <summary>NavigationView 的导航项单独处理：窄窗口（LeftMinimal）下这些项在折叠面板里，
    /// 不在可视树上，直接遍历 LocalizeTree 扫不到，所以按数据逐项翻译。
    /// 原文按 Tag 硬映射（不用元素实例记录）：NavigationView 切换顶栏/折叠面板时会重建容器，
    /// 新容器里已是译文，用元素记原文会把译文当原文，导致之后卡住切不动。</summary>
    private static readonly (string Tag, string Source)[] NavSources =
    {
        ("ez", "主页"),
        ("adv", "高级"),
        ("progress", "进行中")
    };

    public static void LocalizeNavView(NavigationView? nav)
    {
        if (nav is null) return;
        try
        {
            LocalizeNavItems(nav.MenuItems);
            LocalizeNavItems(nav.FooterMenuItems);
        }
        catch { }
    }

    private static void LocalizeNavItems(IEnumerable<object>? items)
    {
        if (items is null) return;
        foreach (var obj in items)
        {
            if (obj is not NavigationViewItem item) continue;
            foreach (var (tag, source) in NavSources)
            {
                if ((item.Tag as string) == tag)
                {
                    item.Content = Tr(source);
                    break;
                }
            }
        }
    }

    /// <summary>菜单栏单独处理：MenuBarItem / MenuFlyoutItem 不在可视树里，需要按 Items 走。</summary>
    public static void LocalizeMenuBar(MenuBar? bar)
    {
        if (bar is null) return;
        try
        {
            foreach (var item in bar.Items)
            {
                if (item is MenuBarItem barItem)
                {
                    barItem.Title = Tr(Origin(barItem, barItem.Title));
                    LocalizeFlyoutItems(barItem.Items);
                }
            }
        }
        catch { }
    }

    private static void LocalizeFlyoutItems(IEnumerable<MenuFlyoutItemBase>? items)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            switch (item)
            {
                case MenuFlyoutSubItem sub:
                    sub.Text = Tr(Origin(sub, sub.Text));
                    LocalizeFlyoutItems(sub.Items);
                    break;
                case MenuFlyoutItem leaf:
                    leaf.Text = Tr(Origin(leaf, leaf.Text));
                    break;
            }
        }
    }

    /// <summary>取首次记录的原文。</summary>
    private static string Origin(DependencyObject element, string current)
    {
        if (_origin.TryGetValue(element, out var text)) return text;
        _origin.Add(element, current);
        return current;
    }

    private static void LocalizeElement(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock tb:
                tb.Text = Tr(Origin(tb, tb.Text));
                break;

            // 只翻译占位提示，不动用户输入内容
            case TextBox textBox:
                textBox.PlaceholderText = Tr(Origin(textBox, textBox.PlaceholderText));
                break;

            case ComboBox combo:
                combo.PlaceholderText = Tr(Origin(combo, combo.PlaceholderText));
                break;

            case ToggleSwitch toggle:
                toggle.Header = Tr(Origin(toggle, toggle.Header as string ?? ""));
                toggle.OnContent = Tr(OriginToggleOn(toggle, toggle.OnContent as string));
                toggle.OffContent = Tr(OriginToggleOff(toggle, toggle.OffContent as string));
                break;

            case SelectorBarItem selectorItem:
                selectorItem.Text = Tr(Origin(selectorItem, selectorItem.Text));
                break;

            // NavigationViewItem 不在这里处理：见 LocalizeNavView（按 Tag 映射，避免容器重建后原文丢失）

            case AppBarButton appBarButton:
                appBarButton.Label = Tr(Origin(appBarButton, appBarButton.Label));
                break;

            // Button / CheckBox / RadioButton / ToggleButton：Content 为字符串时才替换
            // （Content 是图标或自定义面板的按钮要原样保留）
            case ButtonBase button when button.Content is string buttonText:
                button.Content = Tr(Origin(button, buttonText));
                break;

            case ContentControl contentControl when contentControl.Content is string contentText:
                contentControl.Content = Tr(Origin(contentControl, contentText));
                break;
        }
    }

    /// <summary>ToggleSwitch 的 On/OffContent 各自记原文（两个独立槽位，避免相互覆盖）。</summary>
    private static readonly ConditionalWeakTable<DependencyObject, string> _originToggleOn = new();
    private static readonly ConditionalWeakTable<DependencyObject, string> _originToggleOff = new();

    private static string OriginToggleOn(DependencyObject element, string? current)
        => OriginIn(_originToggleOn, element, current ?? "");

    private static string OriginToggleOff(DependencyObject element, string? current)
        => OriginIn(_originToggleOff, element, current ?? "");

    private static string OriginIn(ConditionalWeakTable<DependencyObject, string> table, DependencyObject element, string current)
    {
        if (table.TryGetValue(element, out var text)) return text;
        table.Add(element, current);
        return current;
    }
}

/// <summary>ContentDialog 显示前自动本地化的入口：把调用点的 <c>.ShowAsync()</c> 换成
/// <c>.ShowLocalizedAsync()</c> 即可，弹窗的标题/内容/按钮都会按当前语言翻译。</summary>
internal static class ContentDialogLocalizationExtensions
{
    public static async System.Threading.Tasks.Task<ContentDialogResult> ShowLocalizedAsync(this ContentDialog dialog)
    {
        Loc.LocalizeDialog(dialog);
        return await dialog.ShowAsync();
    }
}
