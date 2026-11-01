using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Easy4K.LuminolEngine;

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
        BuildReverse();
    }

    /// <summary>译文 → 简体中文原文。界面上的文字可能已经是别的语言的译文（比如页面上一次切换后
    /// 才创建），只靠正向表查不到，需要先还原成中文原文再翻译成目标语言。</summary>
    private static Dictionary<string, Dictionary<string, string>> _reverse = new();

    private static void BuildReverse()
    {
        var reverse = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (lang, table) in _map)
        {
            var rev = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (source, text) in table)
                if (!string.IsNullOrEmpty(text) && !rev.ContainsKey(text))
                    rev[text] = source;
            reverse[lang] = rev;
        }
        _reverse = reverse;
    }

    /// <summary>按中文原文取译文。传进来的也可能是别的语言的译文，会先还原成中文原文再翻译，
    /// 因此对同一段文字重复调用是安全的（切换语言时不需要缓存"首次见到的原文"）。</summary>
    public static string Tr(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;

        if (_current != Fallback
            && _map.TryGetValue(_current, out var table)
            && table.TryGetValue(source, out var text)
            && !string.IsNullOrEmpty(text))
            return text;

        // 没有直接命中：可能是上一次切换后残留的译文，还原成中文原文
        var zh = ResolveSource(source);
        if (zh is null) return source;
        if (_current == Fallback) return zh;

        return _map.TryGetValue(_current, out table)
               && table.TryGetValue(zh, out text)
               && !string.IsNullOrEmpty(text)
            ? text
            : zh;
    }

    /// <summary>把任意语言的译文还原成简体中文原文；找不到返回 null。</summary>
    private static string? ResolveSource(string text)
    {
        foreach (var rev in _reverse.Values)
            if (rev.TryGetValue(text, out var zh) && zh != text)
                return zh;
        return null;
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

    /// <summary>XAML 里给动态文案元素打的标记：内容由代码实时写入，不参与字符串本地化。</summary>
    public const string NoLocalizeTag = "NoLocalize";

    /// <summary>遍历界面树，把已知文案替换成当前语言译文（未收录的文案原样保留）。
    /// 顺带把内嵌字体刷到每个文本元素上：主题字典覆盖管不到控件模板里写死 FontFamily 的地方，
    /// 模板又是运行期才实例化的，只能在树上逐个兑现。
    /// 注意：代码里 new 出来、还没挂进界面的面板（如 ContentDialog 的正文）没有可视子节点，
    /// 只能走逻辑树；两种都要覆盖。</summary>
    public static void LocalizeTree(DependencyObject? root)
    {
        if (root is null) return;

        ApplyAppFont(root);
        LocalizeElement(root);

        var visualCount = 0;
        try { visualCount = VisualTreeHelper.GetChildrenCount(root); } catch { }

        if (visualCount == 0)
        {
            // 未挂进可视树的面板 / 内容控件：按逻辑树继续
            foreach (var child in LogicalChildren(root))
                LocalizeTree(child);
            return;
        }

        for (var i = 0; i < visualCount; i++)
        {
            DependencyObject? child = null;
            try { child = VisualTreeHelper.GetChild(root, i); } catch { }
            // 单个节点异常不能中断同级其它节点（模板没应用完时偶发）
            if (child is not null) LocalizeTree(child);
        }
    }

    /// <summary>取一个元素的逻辑子元素（可视树为空时使用）。</summary>
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject root)
    {
        switch (root)
        {
            case Panel panel:
                foreach (var child in panel.Children) yield return child;
                break;
            case Border { Child: not null } border:
                yield return border.Child;
                break;
            case ContentControl { Content: DependencyObject content }:
                yield return content;
                break;
            case ContentPresenter { Content: DependencyObject presented }:
                yield return presented;
                break;
            case ItemsControl items:
                foreach (var item in items.Items)
                    if (item is DependencyObject obj) yield return obj;
                break;
            case Popup { Child: not null } popup:
                yield return popup.Child;
                break;
        }
    }

    /// <summary>把当前所有已打开的下拉层（ComboBox 列表、ToolTip、Flyout）也本地化一遍：
    /// 它们挂在 XamlRoot 的弹出层，不在常规可视树里，切语言时不会被遍历到。</summary>
    public static void LocalizeOpenPopups(XamlRoot? root)
    {
        if (root is null) return;
        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.Child is not null) LocalizeTree(popup.Child);
        }
        catch
        {
            // 弹出层取不到时忽略
        }
    }

    /// <summary>只做字体处理（按字重换真实粗体字面、小字透明度折算），不碰任何文案。
    /// 用于运行期才实例化模板的元素——比如 ContentDialog：标题在弹窗模板里写死 SemiBold，
    /// 不刷一遍就会由 Regular 合成假粗体，边缘发毛。</summary>
    public static void ApplyFonts(DependencyObject? root)
    {
        if (root is null) return;
        try
        {
            ApplyAppFont(root);

            // Popup（下拉列表、ToolTip、教学提示、弹窗）的内容不在常规可视树里，
            // VisualTreeHelper 取不到，必须显式进去刷一遍
            if (root is Popup { Child: not null } popup) ApplyFonts(popup.Child);

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                ApplyFonts(VisualTreeHelper.GetChild(root, i));
        }
        catch
        {
            // 树结构异常时跳过，不影响弹窗显示
        }
    }

    // ===================== 内嵌字体兜底 =====================

    private static FontFamily? _appFont;
    private static FontFamily? _appBoldFont;

    /// <summary>内嵌字体（App.xaml 里的 AppFontFamily）。取不到就退回系统默认，不让界面崩。</summary>
    private static FontFamily AppFont => _appFont ??= ResolveFont("AppFontFamily", "Segoe UI");

    /// <summary>内嵌粗体（真实 Bold 字面）。不单独指定的话，SemiBold/Bold 会由 Regular 合成假粗体，
    /// 笔画发虚、边缘发毛，小字号下尤其明显。</summary>
    private static FontFamily AppBoldFont => _appBoldFont ??= ResolveFont("AppFontBoldFamily", "Segoe UI Semibold");

    /// <summary>供运行期自建文本（如 ContentDialog 标题）使用：真实粗体字面。</summary>
    public static FontFamily BoldFontFamily => AppBoldFont;

    private static FontFamily ResolveFont(string key, string fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is FontFamily f)
                return f;
        }
        catch { }
        return new FontFamily(fallback);
    }

    /// <summary>按字重选字面：SemiBold 及以上用真实 Bold 文件，其余用 Regular。</summary>
    private static FontFamily FontForWeight(FontWeight weight) =>
        weight.Weight >= FontWeights.SemiBold.Weight ? AppBoldFont : AppFont;

    /// <summary>该字族是不是内嵌的 HarmonyOS（Regular / Medium / Bold 三个文件都算）。</summary>
    private static bool IsAppFont(FontFamily? f)
        => (f?.Source ?? "").Contains("HarmonyOS", StringComparison.OrdinalIgnoreCase);

    /// <summary>图标字体与等宽日志是刻意保留的，不能替换。</summary>
    private static bool IsIconFont(FontFamily? f)
    {
        var s = f?.Source ?? "";
        return s.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Fluent Icons", StringComparison.OrdinalIgnoreCase)
            || s.Contains("MDL2", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Symbol", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是不是系统默认字族（这类要换成内嵌字体）。</summary>
    private static bool IsSystemFont(FontFamily? f)
    {
        var s = f?.Source ?? "";
        if (s.Length == 0) return true;
        return s.Contains("Segoe", StringComparison.OrdinalIgnoreCase)
            || s.Contains("YaHei", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Arial", StringComparison.OrdinalIgnoreCase)
            || s.Contains("XamlAutoFontFamily", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Global User Interface", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyAppFont(DependencyObject el)
    {
        try
        {
            switch (el)
            {
                case TextBlock tb:
                    tb.FontFamily = PickFace(tb.FontFamily, tb.FontWeight);
                    FoldOpacityIntoForeground(tb);
                    break;
                case Control c:
                    c.FontFamily = PickFace(c.FontFamily, c.FontWeight);
                    break;
                // 正文（ContentDialog.Content 是字符串时）由模板里的 ContentPresenter 自己绘制，
                // 它不是 Control，上面那个分支管不到，会一直用主题里的系统字族
                case ContentPresenter cp:
                    cp.FontFamily = PickFace(cp.FontFamily, cp.FontWeight);
                    break;
            }
        }
        catch { }
    }

    /// <summary>小字发毛的根因：给 TextBlock 设 Opacity &lt; 1（界面里大量 0.6 的说明文字）
    /// 会把整段文字推进一个独立图层，ClearType 子像素渲染直接失效，只剩灰度抗锯齿，
    /// 10~12px 的小字边缘就成锯齿状。把透明度折算进文字颜色的 alpha 里，观感完全一致，
    /// 但文字仍与背景在同一层，ClearType 正常工作。
    /// 注意：折算后 Foreground 变成"本地实色"，主题画笔不再更新它（切到浅色主题文字仍是白的），
    /// 所以要记下原始透明度，主题切换时重新折算（见 <see cref="RefoldOpacities"/>）。</summary>
    private static void FoldOpacityIntoForeground(TextBlock tb)
    {
        var opacity = tb.Opacity;
        if (opacity >= 0.99 || opacity <= 0.05) return;
        if (tb.Foreground is not SolidColorBrush src || src.Color.A == 0) return;

        lock (OpacityFolds)
        {
            OpacityFolds.RemoveAll(f => !f.Target.TryGetTarget(out _));
            // 同一个 TextBlock 只登记一次（重复 localize 不会再登记，也不会重复挂事件）
            var exists = false;
            foreach (var f in OpacityFolds)
                if (f.Target.TryGetTarget(out var t) && ReferenceEquals(t, tb)) { exists = true; break; }
            if (!exists)
            {
                var rec = new OpacityFold { Target = new WeakReference<TextBlock>(tb), Opacity = opacity };
                OpacityFolds.Add(rec);
                // 关键：订阅元素自己的 ActualThemeChanged —— 这时它已经切到新主题，
                // 不能只在窗口根上统一切换（子元素的实际主题是异步更新的，读到的是旧画笔）
                tb.ActualThemeChanged += (_, _) => ScheduleRebake(tb, opacity);
            }
        }
        BakeOpacity(tb, opacity);
    }

    /// <summary>主题变化后恢复透明度 → 清掉本地 Foreground → 下一轮消息循环重新折算。
    /// 必须延后一拍：清值时主题画笔才刚重新解析，同步读取拿到的还是旧颜色。</summary>
    private static void ScheduleRebake(TextBlock tb, double opacity)
    {
        try
        {
            tb.Opacity = opacity;
            tb.ClearValue(TextBlock.ForegroundProperty);
        }
        catch { return; }

        void Again()
        {
            try
            {
                // 已经被别的路径折过一次（Opacity 已回到 1）就不再重复折，否则 alpha 会被乘两次、文字越来越淡
                if (tb.Opacity <= 0.99) BakeOpacity(tb, opacity);
            }
            catch { }
        }

        var dq = tb.DispatcherQueue;
        if (dq is null) Again();
        else dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Again);
    }

    /// <summary>把透明度折算进前景色（已折算过的 TextBlock Opacity 为 1，不会重复记录）。</summary>
    private static void BakeOpacity(TextBlock tb, double opacity)
    {
        if (tb.Foreground is not SolidColorBrush src || src.Color.A == 0) return;
        var alpha = (byte)Math.Round(src.Color.A * opacity);
        tb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, src.Color.R, src.Color.G, src.Color.B));
        tb.Opacity = 1;
    }

    private sealed class OpacityFold
    {
        public WeakReference<TextBlock> Target { get; init; } = null!;
        public double Opacity { get; init; }
    }

    private static readonly List<OpacityFold> OpacityFolds = new();

    /// <summary>主题切换时补一遍（元素级 ActualThemeChanged 已经处理大部分，
    /// 这里兜底处理当时不在树上的元素 / 事件被吞掉的情况）。</summary>
    public static void RefoldOpacities()
    {
        var alive = new List<(TextBlock Tb, double Opacity)>();
        lock (OpacityFolds)
        {
            OpacityFolds.RemoveAll(f => !f.Target.TryGetTarget(out _));
            foreach (var f in OpacityFolds)
                if (f.Target.TryGetTarget(out var tb)) alive.Add((tb, f.Opacity));
        }
        foreach (var (tb, op) in alive) ScheduleRebake(tb, op);
    }

    /// <summary>按字重挑真实字面：SemiBold 及以上用 Bold 文件，其余用 Regular。
    /// 注意：全局隐式 TextBlock 样式会给每个文本挂上 Regular，如果这里只判断"不是系统字体就跳过"，
    /// 所有 SemiBold/Bold 都会被 DirectWrite 合成假粗体 —— 笔画发毛、边缘锯齿，小字号尤其明显。
    /// 例外：显式用了 Medium 家族且不是粗体时保持 Medium，那正是它的用途。</summary>
    private static FontFamily PickFace(FontFamily? current, FontWeight weight)
    {
        if (IsIconFont(current)) return current ?? AppFont;

        var s = current?.Source ?? "";
        var bold = weight.Weight >= FontWeights.SemiBold.Weight;

        if (!bold && s.Contains("HarmonyOS Sans SC Medium", StringComparison.OrdinalIgnoreCase))
            return current!;

        if (IsAppFont(current) || IsSystemFont(current))
            return FontForWeight(weight);

        return current ?? AppFont;
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
                    barItem.Title = Tr(barItem.Title);
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
                    sub.Text = Tr(sub.Text);
                    LocalizeFlyoutItems(sub.Items);
                    break;
                case MenuFlyoutItem leaf:
                    leaf.Text = Tr(leaf.Text);
                    break;
            }
        }
    }

    /// <summary>下拉项容器是打开时按数据生成的，不在界面树里；挂一次事件，等它展开后再翻译
    /// （不直接改 Items，避免选中项被替换后 SelectedIndex 归零）。</summary>
    private static readonly ConditionalWeakTable<ComboBox, object> _comboHooked = new();

    private static void EnsureComboLocalization(ComboBox combo)
    {
        if (_comboHooked.TryGetValue(combo, out _)) return;
        _comboHooked.Add(combo, new object());
        combo.DropDownOpened += (sender, _) =>
        {
            if (sender is not ComboBox box) return;
            box.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => LocalizeOpenPopups(box.XamlRoot));
        };
    }

    private static void LocalizeElement(DependencyObject element)
    {
        // 运行期动态写入的文案（通知条标题/正文等）不参与本地化
        if (element is FrameworkElement { Tag: NoLocalizeTag }) return;

        // ToolTipService.ToolTip 是附加属性：ToolTip 对象挂在元素上而不是可视树里，
        // 只靠下面的元素遍历扫不到，必须单独取出来翻译
        if (element is FrameworkElement && ToolTipService.GetToolTip(element) is string attachedTip)
            ToolTipService.SetToolTip(element, Tr(attachedTip));

        switch (element)
        {
            case TextBlock tb:
                tb.Text = Tr(tb.Text);
                break;

            // 只翻译占位提示，不动用户输入内容
            case TextBox textBox:
                textBox.PlaceholderText = Tr(textBox.PlaceholderText);
                break;

            case ComboBox combo:
                combo.PlaceholderText = Tr(combo.PlaceholderText);
                EnsureComboLocalization(combo);
                break;

            case ToggleSwitch toggle:
                toggle.Header = Tr(toggle.Header as string ?? "");
                toggle.OnContent = Tr(toggle.OnContent as string ?? "");
                toggle.OffContent = Tr(toggle.OffContent as string ?? "");
                break;

            case SelectorBarItem selectorItem:
                selectorItem.Text = Tr(selectorItem.Text);
                break;

            // NavigationViewItem 由 LocalizeNavView 按 Tag 逐项处理：
            // 顶栏/折叠面板的容器会被重建，这里再写一次容易把译文当原文

            case AppBarButton appBarButton:
                appBarButton.Label = Tr(appBarButton.Label);
                break;

            // Button / CheckBox / RadioButton / ToggleButton：Content 为字符串时才替换
            // （Content 是图标或自定义面板的按钮要原样保留）
            case ButtonBase button when button.Content is string buttonText:
                button.Content = Tr(buttonText);
                break;

            case ContentControl contentControl when contentControl.Content is string contentText:
                contentControl.Content = Tr(contentText);
                break;
        }
    }
}

/// <summary>ContentDialog 显示前自动本地化的入口：把调用点的 <c>.ShowAsync()</c> 换成
/// <c>.ShowLocalizedAsync()</c> 即可，弹窗的标题/内容/按钮都会按当前语言翻译。</summary>
internal static class ContentDialogLocalizationExtensions
{
    public static async System.Threading.Tasks.Task<ContentDialogResult> ShowLocalizedAsync(this ContentDialog dialog)
    {
        Loc.LocalizeDialog(dialog);

        // 弹窗模板里的标题是「FontWeight=SemiBold + Regular 字族」的 ContentControl，第一帧一定用
        // 合成假粗体画出来（Loading/Opened 再刷已经晚了），所以直接把字符串标题换成自带真实粗体
        // 字面的 TextBlock：字号/字重/颜色仍继承模板里的 ContentControl，只改字族，视觉不变但首帧就清晰。
        if (dialog.Title is string titleText && titleText.Length > 0)
        {
            dialog.Title = new TextBlock
            {
                Text = titleText,
                FontFamily = Loc.BoldFontFamily
            };
        }

        void ApplyFonts(DependencyObject sender) => Loc.ApplyFonts(sender);

        // 弹窗正文（XAML 里给的那块内容）在 ShowAsync 之前就能刷，首帧就是内嵌字体
        if (dialog.Content is DependencyObject dialogContent) ApplyFonts(dialogContent);

        void OnLoading(FrameworkElement sender, object args) => ApplyFonts(sender);

        void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            ApplyFonts(sender);

            // ContentDialog 的实际内容挂在 XamlRoot 的弹出层（独立 Popup），从 dialog 自身遍历
            // 可能进不去，正文就会保留模板里的系统字族。这里把所有打开的 popup 一并刷一遍。
            try
            {
                if (sender.XamlRoot is not null)
                {
                    foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(sender.XamlRoot))
                        if (popup.Child is not null) ApplyFonts(popup.Child);
                }
            }
            catch { /* 取不到弹出层时保持原样 */ }
        }

        dialog.Loading += OnLoading;
        dialog.Opened += OnOpened;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            dialog.Loading -= OnLoading;
            dialog.Opened -= OnOpened;
        }
    }
}
