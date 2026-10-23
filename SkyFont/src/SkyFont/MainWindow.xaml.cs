using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkyFont.Core;

namespace SkyFont;

public partial class MainWindow : Window
{
    private const int CharMapCap = 640;

    private readonly AppSettings _settings;
    private readonly FontCache _cache;
    private readonly FontActivator _activator;
    private readonly List<SourceItem> _sources = new();
    private readonly TaskCompletionSource _initDone = new();
    private readonly DispatcherTimer _statusTimer;

    private List<FontEntry> _all = new();
    private List<FontEntry> _visible = new();
    private SourceItem? _currentSource;
    private FontEntry? _selected;
    private string _search = "";
    private int _sortMode;
    private string _charRange = "latin";
    private bool _shotMode;
    private bool _scanning;
    private bool _batchRunning;
    private volatile bool _batchCancel;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _cache = FontCache.Load(AppSettings.CachePath);
        _activator = ((App)Application.Current).Activator;
        _sortMode = _settings.SortMode switch { "FileName" => 1, "Weight" => 2, _ => 0 };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            UpdateStatusCounts();
        };

        SizeSlider.ValueChanged += SizeSlider_ValueChanged;

        ApplySettingsToUi();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ==================================================================== 初始化

    private void ApplySettingsToUi()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        if (_settings.PreviewPaneWidth > 300) PreviewColumn.Width = new GridLength(_settings.PreviewPaneWidth);

        SampleBox.Text = _settings.SampleText;
        SizeSlider.Value = Math.Clamp(_settings.PreviewSize, 10, 120);
        RecursiveCheck.IsChecked = _settings.RecursiveScan;
        DeactivateOnExitCheck.IsChecked = _settings.DeactivateOnExit;
        BoldToggle.IsChecked = _settings.PreviewBold;
        ItalicToggle.IsChecked = _settings.PreviewItalic;
        WrapToggle.IsChecked = _settings.PreviewWrap;

        SortButton.Content = SortLabel();
        ApplyPreviewStyle();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            SetStatus("初始化失败：" + ex.Message);
        }
        finally
        {
            _initDone.TrySetResult();
        }
    }

    private async Task InitializeAsync()
    {
        ReconcileActivation();

        SetBusy("正在扫描字体…");
        var progress = new Progress<string>(SetBusy);

        await Task.Run(() => ReloadAll(progress));

        RebuildSources();
        SelectSource(_sources[0]);
        SetIdle();
        UpdateStatusCounts();
    }

    /// <summary>
    /// 处理上次运行遗留的激活记录：正常退出会清空记录，
    /// 因此这里出现记录意味着上次异常结束，按配置清理或恢复。
    /// </summary>
    private void ReconcileActivation()
    {
        if (_settings.LastActivePaths.Count == 0) return;

        try
        {
            if (_settings.DeactivateOnExit)
            {
                foreach (string path in _settings.LastActivePaths)
                    _activator.Deactivate(path, broadcast: false);
                FontActivator.BroadcastFontChange();
            }
            else
            {
                foreach (string path in _settings.LastActivePaths)
                    _activator.Activate(path, broadcast: false);
                FontActivator.BroadcastFontChange();
            }
        }
        catch
        {
            // 忽略残留清理失败。
        }

        _settings.LastActivePaths.Clear();
    }

    private void ReloadAll(IProgress<string> progress)
    {
        var entries = new List<FontEntry>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---- 1. 已安装字体（标准目录）----
        var installedRoots = new List<string> { InstalledFonts.WindowsFontsFolder };
        if (Directory.Exists(InstalledFonts.UserFontsFolder))
            installedRoots.Add(InstalledFonts.UserFontsFolder);

        foreach (string root in installedRoots)
        {
            progress.Report($"扫描已安装字体：{root}");
            var scan = FontScanner.Scan(root, recursive: true, InstalledSource.Name, _cache);
            foreach (var entry in scan.Entries)
            {
                entry.IsInstalled = true;
                if (seenKeys.Add(entry.Key)) entries.Add(entry);
            }
        }

        // ---- 2. 注册表登记但不在标准目录中的字体 ----
        var coveredPaths = new HashSet<string>(entries.Select(e => e.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string path in InstalledFonts.GetRegisteredFiles())
        {
            if (coveredPaths.Contains(path)) continue;

            var parsed = FontParser.Parse(path);
            if (!parsed.Ok) continue;

            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch
            {
                // 尺寸仅用于展示。
            }

            foreach (var face in parsed.Faces)
            {
                var entry = new FontEntry(path, InstalledSource.Name, face, size, parsed.Flavor) { IsInstalled = true };
                if (seenKeys.Add(entry.Key)) entries.Add(entry);
            }
        }

        // ---- 3. 用户字体库 ----
        foreach (string folder in _settings.FontFolders)
        {
            if (!Directory.Exists(folder)) continue;
            progress.Report($"扫描字体库：{folder}");
            var scan = FontScanner.Scan(folder, _settings.RecursiveScan, folder, _cache);
            foreach (var entry in scan.Entries)
            {
                entry.IsInstalled = coveredPaths.Contains(entry.FilePath);
                if (seenKeys.Add(entry.Key)) entries.Add(entry);
            }
        }

        // ---- 4. 冲突 / 重复 / 收藏 / 激活状态 ----
        var installedStyleKeys = new HashSet<string>(
            entries.Where(e => e.IsInstalled).Select(StyleKey), StringComparer.OrdinalIgnoreCase);

        var styleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string key = StyleKey(entry);
            styleCounts[key] = styleCounts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        var favorites = new HashSet<string>(_settings.Favorites, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            string styleKey = StyleKey(entry);
            if (!entry.IsInstalled && installedStyleKeys.Contains(styleKey)) entry.IsInstalledConflict = true;
            if (!entry.IsInstalled && styleCounts[styleKey] > 1) entry.IsDuplicate = true;
            if (favorites.Contains(entry.Key)) entry.IsFavorite = true;
            if (_activator.IsActive(entry.FilePath) && !entry.IsInstalled) entry.IsActive = true;
        }

        _all = entries;
    }

    private static string StyleKey(FontEntry e) =>
        (e.Family + "|" + e.Subfamily).ToLowerInvariant();

    // ==================================================================== 来源

    private static readonly SourceItem InstalledSource = new("installed", "已永久安装", "▣");

    private void RebuildSources()
    {
        _sources.Clear();
        _sources.Add(new SourceItem("all", "全部字体", "◎"));

        _sources.Add(SourceItem.Header("激活状态"));
        _sources.Add(InstalledSource);
        _sources.Add(new SourceItem("active", "已临时激活", "●"));
        _sources.Add(new SourceItem("inactive", "未激活（字体库）", "○"));

        _sources.Add(SourceItem.Header("其他"));
        _sources.Add(new SourceItem("favorites", "收藏", "★"));

        var folders = _settings.FontFolders.Where(Directory.Exists).ToList();
        if (folders.Count > 0) _sources.Add(SourceItem.Header("字体库"));

        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = folder;
            _sources.Add(new SourceItem("folder", name, "▤", folder));
        }

        SourceList.ItemsSource = null;
        SourceList.ItemsSource = _sources;
        NoFolderHint.Visibility = _settings.FontFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSourceCounts();
    }

    private void RefreshSourceCounts()
    {
        foreach (var source in _sources)
        {
            source.Count = CountFor(source);
        }
    }

    private int CountFor(SourceItem source) => source.Kind switch
    {
        "all" => _all.Count,
        "active" => _all.Count(e => e.IsActive),
        // 永久安装的字体不需要激活，因此不计入「未激活」
        "inactive" => _all.Count(e => !e.IsInstalled && !e.IsActive),
        "installed" => _all.Count(e => e.IsInstalled),
        "favorites" => _all.Count(e => e.IsFavorite),
        "folder" => _all.Count(e => string.Equals(e.RootFolder, source.Path, StringComparison.OrdinalIgnoreCase)),
        _ => 0
    };

    private void SelectSource(SourceItem? source)
    {
        if (source is null || source.IsHeader) return;

        _currentSource = source;
        SourceList.SelectedItem = source;
        ListTitle.Text = source?.Name ?? "全部字体";
        ApplyFilter();
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceList.SelectedItem is not SourceItem source) return;

        // 键盘导航可能落到分组标题上，回退到当前来源
        if (source.IsHeader)
        {
            SourceList.SelectedItem = _currentSource;
            return;
        }

        SelectSource(source);
    }

    // ==================================================================== 过滤

    private void ApplyFilter()
    {
        IEnumerable<FontEntry> query = _all;

        string kind = _currentSource?.Kind ?? "all";
        string folder = _currentSource?.Path ?? "";

        query = kind switch
        {
            "active" => query.Where(e => e.IsActive),
            "inactive" => query.Where(e => !e.IsInstalled && !e.IsActive),
            "installed" => query.Where(e => e.IsInstalled),
            "favorites" => query.Where(e => e.IsFavorite),
            "folder" => query.Where(e => string.Equals(e.RootFolder, folder, StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(_search))
        {
            string term = _search.Trim();
            query = query.Where(e =>
                e.Family.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Subfamily.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.PostScriptName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Folder.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        query = _sortMode switch
        {
            1 => query.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.FaceIndex),
            2 => query.OrderBy(e => e.WeightClass).ThenBy(e => e.Family, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(e => e.Family, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(e => e.WeightClass)
                      .ThenBy(e => e.Subfamily, StringComparer.OrdinalIgnoreCase)
        };

        string? previousKey = _selected?.Key;

        // 视图变化时清空勾选，防止对当前不可见的条目执行批量操作
        foreach (var entry in _all)
        {
            if (entry.IsChecked) entry.IsChecked = false;
        }

        _visible = query.ToList();

        FontList.ItemsSource = null;
        FontList.ItemsSource = _visible;

        ListCount.Text = _search.Length == 0
            ? $"{_visible.Count:N0} 个"
            : $"{_visible.Count:N0} / {_all.Count:N0} 个";

        EmptyHint.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionButtons();

        var keep = previousKey is null ? null : _visible.FirstOrDefault(e => e.Key == previousKey);
        if (keep is not null)
        {
            FontList.SelectedItem = keep;
            FontList.ScrollIntoView(keep);
        }
        else
        {
            SelectFirst();
        }
    }

    private void SelectFirst()
    {
        if (_visible.Count > 0) FontList.SelectedIndex = 0;
        else ClearSelection();
    }

    private void ClearSelection()
    {
        _selected = null;
        PreviewText.Text = "选择左侧字体以预览";
        PreviewText.FontFamily = new FontFamily("Segoe UI");
        PreviewText.FontSize = SizeSlider.Value;
        StatusRight.Text = "";
        InfoHost.Children.Clear();
        GlyphHost.ItemsSource = null;
        CharsSummary.Text = "";
    }

    // ==================================================================== 选择与预览

    private void FontList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = FontList.SelectedItem as FontEntry;
        if (_selected is null)
        {
            UpdateActionButtons();
            ClearSelection();
            return;
        }

        UpdateActionButtons();
        PreviewText.FontFamily = _selected.PreviewFamily;
        ApplyPreviewStyle();
        StatusRight.Text = _selected.FilePath;
        UpdateInfo();
        if (PaneChars.Visibility == Visibility.Visible) RebuildCharMap();
    }

    private void ApplyPreviewStyle()
    {
        PreviewText.Text = SampleBox.Text;
        PreviewText.FontSize = SizeSlider.Value;
        PreviewText.FontWeight = BoldToggle.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        PreviewText.FontStyle = ItalicToggle.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
        PreviewText.TextWrapping = WrapToggle.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        SizeLabel.Text = $"{SizeSlider.Value:F0} px";
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreviewText is null) return;
        PreviewText.FontSize = e.NewValue;
        SizeLabel.Text = $"{e.NewValue:F0} px";
        _settings.PreviewSize = e.NewValue;
    }

    private void SampleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewText is null) return;
        PreviewText.Text = SampleBox.Text;
        _settings.SampleText = SampleBox.Text;
    }

    private void StyleToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyPreviewStyle();
        _settings.PreviewBold = BoldToggle.IsChecked == true;
        _settings.PreviewItalic = ItalicToggle.IsChecked == true;
    }

    private void WrapToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyPreviewStyle();
        _settings.PreviewWrap = WrapToggle.IsChecked == true;
    }

    private void InsertSample_Click(object sender, RoutedEventArgs e)
    {
        const string sample = "敏捷的棕色狐狸跳过了懒狗 ABCabc 0123456789";
        SampleBox.Text = string.IsNullOrWhiteSpace(SampleBox.Text) ? sample : SampleBox.Text + "\n" + sample;
        SampleBox.CaretIndex = SampleBox.Text.Length;
    }

    // ==================================================================== 激活 / 反激活

    /// <summary>勾选只是标记，不触发任何激活行为。</summary>
    private void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateActionButtons();

    /// <summary>
    /// 「已临时激活 / 未激活」是按状态过滤的视图，激活状态一变成员就变了，
    /// 必须重新过滤，否则会出现「已反激活却还留在已激活列表里」。
    /// </summary>
    private void RefreshIfStateView()
    {
        if (_currentSource?.Kind is "active" or "inactive") ApplyFilter();
    }

    /// <summary>
    /// 勾选 / 取消勾选当前列表中<b>可激活</b>的全部条目。
    /// 永久安装的字体不参与勾选（其复选框为禁用态）。
    /// </summary>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var selectable = _visible.Where(x => x.CanActivate).ToList();
        if (selectable.Count == 0)
        {
            SetStatus("当前列表没有可激活的字体");
            return;
        }

        // 依据真实状态决定方向，不依赖三态复选框的点击循环
        bool target = !selectable.All(x => x.IsChecked);

        foreach (var entry in selectable) entry.IsChecked = target;
        UpdateActionButtons();
        SetStatus(target ? $"已勾选 {selectable.Count:N0} 个可激活字体" : "已取消全部勾选");
    }

    private void UpdateActionButtons()
    {
        int selectable = _visible.Count(e => e.CanActivate);
        int count = _visible.Count(e => e.IsChecked);

        ActivateButton.IsEnabled = count > 0;
        DeactivateButton.IsEnabled = count > 0;
        ActivateButton.Content = count > 0 ? $"激活 {count}" : "激活";
        DeactivateButton.Content = count > 0 ? $"反激活 {count}" : "反激活";

        if (SelectAllCheck is null) return;

        // 只统计可激活条目：全部勾选 / 部分勾选 / 全未勾选
        SelectAllCheck.IsEnabled = selectable > 0;
        SelectAllCheck.IsChecked = selectable == 0 || count == 0 ? false
                                 : count >= selectable ? true
                                 : (bool?)null;
    }

    private async void ActivateSelected_Click(object sender, RoutedEventArgs e) => await BatchActivateAsync(true);

    private async void DeactivateSelected_Click(object sender, RoutedEventArgs e) => await BatchActivateAsync(false);

    private void CancelBatch_Click(object sender, RoutedEventArgs e)
    {
        _batchCancel = true;
        BusyCancel.IsEnabled = false;
        BusyDetail.Text = "正在停止…";
    }

    /// <summary>
    /// 批量激活 / 反激活。
    ///
    /// 逐字体调用 <c>AddFontResourceEx</c> 会让 GDI 逐个加载、注册字体，单个耗时 10–100 ms，
    /// 上千个字体如果跑在 UI 线程上界面必然假死。因此整个循环放到后台线程，
    /// UI 只接收进度回调；<c>WM_FONTCHANGE</c> 只在全部结束后广播一次。
    /// </summary>
    private async Task BatchActivateAsync(bool activate)
    {
        if (_batchRunning) return;

        var targets = _visible.Where(e => e.IsChecked && e.CanActivate).ToList();
        if (targets.Count == 0)
        {
            SetStatus("请先在列表中勾选要激活的字体");
            return;
        }

        var paths = targets.Select(t => t.FilePath)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();

        if (paths.Count > 50)
        {
            var answer = MessageBox.Show(
                this,
                $"即将对 {paths.Count:N0} 个字体文件执行{(activate ? "激活" : "反激活")}，过程中可随时取消。\n\n是否继续？",
                "SkyFont",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.OK);

            if (answer != MessageBoxResult.OK) return;
        }

        _batchRunning = true;
        _batchCancel = false;
        ShowBusy(activate ? "正在激活字体…" : "正在反激活字体…", paths.Count);

        var results = new List<(string Path, bool Success)>(paths.Count);

        try
        {
            IProgress<(int Done, int Total, string File)> progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                BusyBar.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                BusyCount.Text = $"{p.Done:N0} / {p.Total:N0}";
                BusyDetail.Text = p.File;
            });

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long lastReport = 0;

            await Task.Run(() =>
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (_batchCancel) break;

                    string path = paths[i];
                    bool ok = activate
                        ? _activator.Activate(path, broadcast: false).Success
                        : _activator.Deactivate(path, broadcast: false).Success;

                    results.Add((path, ok));

                    // 限流：避免密集回调把 UI 线程压垮
                    if (stopwatch.ElapsedMilliseconds - lastReport >= 40 || i == paths.Count - 1)
                    {
                        lastReport = stopwatch.ElapsedMilliseconds;
                        progress.Report((i + 1, paths.Count, Path.GetFileName(path)));
                    }
                }
            });

            FontActivator.BroadcastFontChange();

            var applied = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, ok) in results)
            {
                if (ok) applied[path] = activate;
            }

            foreach (var entry in _all)
            {
                if (applied.TryGetValue(entry.FilePath, out bool state)) entry.IsActive = state;
            }
        }
        catch (Exception ex)
        {
            SetStatus("批量操作失败：" + ex.Message);
        }
        finally
        {
            HideBusy();
            _batchRunning = false;
        }

        RefreshSourceCounts();
        UpdateStatusCounts();
        RefreshIfStateView();

        // 立即落盘：即使之后进程被强杀，下次启动也能据此清理或恢复
        _settings.LastActivePaths = _activator.ActivePaths.ToList();
        _settings.Save();

        int success = results.Count(r => r.Success);
        int failed = results.Count - success;

        SetStatus(_batchCancel
            ? $"已取消：完成 {results.Count:N0} / {paths.Count:N0} 个文件"
            : activate
                ? $"批量激活完成：成功 {success:N0} 个文件" + (failed > 0 ? $"，失败 {failed:N0} 个" : "")
                : $"批量反激活完成：{success:N0} 个文件" + (failed > 0 ? $"，失败 {failed:N0} 个" : ""));
    }

    private void ShowBusy(string title, int total)
    {
        BusyTitle.Text = title;
        BusyDetail.Text = "准备中…";
        BusyCount.Text = $"0 / {total:N0}";
        BusyBar.Value = 0;
        BusyCancel.IsEnabled = true;
        BusyOverlay.Visibility = Visibility.Visible;
    }

    private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;

    // ==================================================================== 命令

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _sortMode = (_sortMode + 1) % 3;
        SortButton.Content = SortLabel();
        _settings.SortMode = _sortMode switch { 1 => "FileName", 2 => "Weight", _ => "Family" };
        ApplyFilter();
    }

    private string SortLabel() => _sortMode switch
    {
        1 => "排序：文件名",
        2 => "排序：字重",
        _ => "排序：字族"
    };

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text ?? "";
        if (_currentSource is not null) ApplyFilter();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        await InitializeAsync();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择字体文件夹",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true) return;

        foreach (string folder in dialog.FolderNames)
        {
            if (!_settings.FontFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                _settings.FontFolders.Add(folder);
        }

        await InitializeAsync();
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSource?.Kind != "folder")
        {
            SetStatus("请先在左侧选中一个自定义字体文件夹");
            return;
        }

        _settings.FontFolders.RemoveAll(f => string.Equals(f, _currentSource.Path, StringComparison.OrdinalIgnoreCase));
        await InitializeAsync();
    }

    private void DeactivateOnExit_Click(object sender, RoutedEventArgs e)
    {
        _settings.DeactivateOnExit = DeactivateOnExitCheck.IsChecked == true;
        SetStatus(_settings.DeactivateOnExit
            ? "关闭软件时将卸载本次会话激活的字体"
            : "关闭软件时保留激活；下次启动会自动恢复");
    }

    private void Recursive_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecursiveScan = RecursiveCheck.IsChecked == true;
        SetStatus(_settings.RecursiveScan ? "已开启子文件夹扫描，点「重新扫描」生效" : "已关闭子文件夹扫描，点「重新扫描」生效");
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>把拖入的文件夹（或字体文件所在目录）加入字体库。</summary>
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        bool changed = false;
        foreach (string path in paths)
        {
            string folder = Directory.Exists(path)
                ? path
                : FontScanner.IsFontFile(path) ? Path.GetDirectoryName(path) ?? "" : "";

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            if (_settings.FontFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;

            _settings.FontFolders.Add(folder);
            changed = true;
        }

        if (changed)
        {
            _settings.Save();
            await InitializeAsync();
            SetStatus($"已添加 {_settings.FontFolders.Count} 个字体库文件夹");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Rescan_Click(sender, e);
            e.Handled = true;
        }

    }

    // ==================================================================== 右侧面板

    private void PaneTab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanePreview is null || PaneChars is null || PaneInfo is null) return;

        string? tag = (sender as RadioButton)?.Tag as string;

        PanePreview.Visibility = tag == "preview" ? Visibility.Visible : Visibility.Collapsed;
        PaneChars.Visibility = tag == "chars" ? Visibility.Visible : Visibility.Collapsed;
        PaneInfo.Visibility = tag == "info" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "chars") RebuildCharMap();
        if (tag == "info") UpdateInfo();
    }

    private static readonly (uint Start, uint End)[] LatinRanges = { (0x20, 0x7E) };

    private static readonly (uint Start, uint End)[] LatinExtRanges =
    {
        (0xA0, 0x24F), (0x1E00, 0x1EFF)
    };

    private static readonly (uint Start, uint End)[] PunctRanges =
    {
        (0x2000, 0x206F), (0x20A0, 0x20BF), (0x2100, 0x214F), (0x2190, 0x21FF), (0x2200, 0x22FF)
    };

    private static readonly (uint Start, uint End)[] CjkRanges =
    {
        (0x3000, 0x303F), (0x4E00, 0x9FFF), (0xFF00, 0xFFEF)
    };

    private IEnumerable<(uint Start, uint End)> CurrentRanges() => _charRange switch
    {
        "latinext" => LatinExtRanges,
        "punct" => PunctRanges,
        "cjk" => CjkRanges,
        _ => LatinRanges
    };

    private void CharRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton pressed) return;

        foreach (var child in LogicalTreeHelper.GetChildren(pressed.Parent))
        {
            if (child is ToggleButton other && !ReferenceEquals(other, pressed)) other.IsChecked = false;
        }

        pressed.IsChecked = true;
        _charRange = pressed.Tag as string ?? "latin";
        RebuildCharMap();
    }

    private void RebuildCharMap()
    {
        if (_selected is null)
        {
            GlyphHost.ItemsSource = null;
            CharsSummary.Text = "选择一个字体以查看字符";
            return;
        }

        GlyphHost.FontFamily = _selected.PreviewFamily;
        var coverage = _selected.Coverage;

        var items = new List<GlyphCell>();
        int total = 0;

        if (_charRange == "all")
        {
            if (coverage is not null)
            {
                total = coverage.Count;
                foreach (uint code in coverage.OrderBy(c => c))
                {
                    if (items.Count >= CharMapCap) break;
                    if (IsSurrogate(code)) continue;
                    items.Add(new GlyphCell(code));
                }
            }
        }
        else
        {
            foreach (var (start, end) in CurrentRanges())
            {
                for (uint code = start; code <= end; code++)
                {
                    if (IsSurrogate(code)) continue;
                    if (coverage is not null && !coverage.Contains(code)) continue;
                    total++;
                    if (items.Count < CharMapCap) items.Add(new GlyphCell(code));
                }
            }
        }

        GlyphHost.ItemsSource = items;
        GlyphScroll.ScrollToTop();

        CharsSummary.Text = coverage is null
            ? "无法读取该字体的字符映射表（cmap）"
            : $"字符覆盖 {coverage.Count:N0} 个码位 · 当前区间显示 {items.Count:N0} / {total:N0}";
    }

    private static bool IsSurrogate(uint code) => code is >= 0xD800 and <= 0xDFFF;

    private void Glyph_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: GlyphCell cell }) return;
        SampleBox.Text += cell.Glyph;
        SampleBox.CaretIndex = SampleBox.Text.Length;
        SetStatus($"已插入 {cell.Code} 到预览文本");
    }

    private void UpdateInfo()
    {
        InfoHost.Children.Clear();
        if (_selected is null) return;

        var entry = _selected;

        AddInfoHeading(entry.DisplayName, entry.PreviewFamily);
        AddInfoRow("字族", entry.Family);
        if (!string.IsNullOrWhiteSpace(entry.TypographicFamily) && entry.TypographicFamily != entry.Family)
            AddInfoRow("排版字族", entry.TypographicFamily);
        AddInfoRow("样式", entry.Subfamily);
        AddInfoRow("字重", $"{entry.WeightClass} · {entry.WeightLabel}{(entry.IsItalic ? " · Italic" : "")}");
        AddInfoRow("格式", entry.FormatLabel);
        AddInfoRow("可变字体", entry.IsVariable ? "是" : "否");
        AddInfoRow("字形数量", entry.GlyphCount > 0 ? entry.GlyphCount.ToString("N0") : "未知");
        AddInfoRow("字符覆盖", entry.Coverage is { } cov ? cov.Count.ToString("N0") + " 个码位" : "无法解析");
        AddInfoRow("unitsPerEm", entry.UnitsPerEm.ToString("N0"));
        AddInfoRow("版本", string.IsNullOrWhiteSpace(entry.Version) ? "—" : entry.Version);
        AddInfoRow("厂商代码", string.IsNullOrWhiteSpace(entry.Vendor) ? "—" : entry.Vendor);
        AddInfoRow("PostScript", string.IsNullOrWhiteSpace(entry.PostScriptName) ? "—" : entry.PostScriptName);
        AddInfoRow("文件大小", entry.FileSizeLabel);

        string state = entry.IsInstalled
            ? "已永久安装"
            : entry.IsActive ? "已临时激活（本会话）" : "未激活";
        AddInfoRow("状态", state);
        if (entry.IsInstalledConflict) AddInfoRow("提示", "系统已存在同名字族，激活后可能被系统字体优先使用");
        if (entry.IsDuplicate) AddInfoRow("提示", "字体库中存在重复的字族/样式");

        AddInfoRow("文件", entry.FilePath);
    }

    private void AddInfoHeading(string title, FontFamily family)
    {
        var text = new TextBlock
        {
            Text = title,
            FontFamily = family,
            FontSize = 26,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = (Brush)FindResource("Fg")
        };
        InfoHost.Children.Add(text);
    }

    private void AddInfoRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("FgMuted"),
            VerticalAlignment = VerticalAlignment.Top
        };

        var content = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Fg")
        };

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(caption);
        grid.Children.Add(content);
        InfoHost.Children.Add(grid);
    }

    // ==================================================================== 状态

    private void SetBusy(string message)
    {
        _scanning = true;
        BusyDot.Visibility = Visibility.Visible;
        StatusLeft.Text = message;
    }

    private void SetIdle()
    {
        _scanning = false;
        BusyDot.Visibility = Visibility.Collapsed;
        UpdateStatusCounts();
    }

    private void SetStatus(string message)
    {
        StatusLeft.Text = message;
        _statusTimer.Stop();
        if (!_scanning) _statusTimer.Start();
    }

    private void UpdateStatusCounts()
    {
        if (_scanning) return;
        int installed = _all.Count(e => e.IsInstalled);
        int active = _all.Count(e => e.IsActive);
        int idle = _all.Count(e => !e.IsInstalled && !e.IsActive);
        StatusLeft.Text = $"共 {_all.Count:N0} 个字体 · 已永久安装 {installed:N0} · 已临时激活 {active:N0} · 未激活 {idle:N0}";
    }

    // ==================================================================== 关闭与持久化

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _batchCancel = true;

        if (_shotMode) return;

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        _settings.PreviewPaneWidth = PreviewColumn.Width.Value;
        _settings.SortMode = _sortMode switch { 1 => "FileName", 2 => "Weight", _ => "Family" };
        _settings.RecursiveScan = RecursiveCheck.IsChecked == true;
        _settings.SampleText = SampleBox.Text;
        _settings.PreviewSize = SizeSlider.Value;
        _settings.PreviewBold = BoldToggle.IsChecked == true;
        _settings.PreviewItalic = ItalicToggle.IsChecked == true;
        _settings.PreviewWrap = WrapToggle.IsChecked == true;
        _settings.Favorites = _all.Where(e => e.IsFavorite).Select(e => e.Key).ToList();
        _settings.DeactivateOnExit = DeactivateOnExitCheck.IsChecked == true;
        _settings.LastActivePaths = _activator.ActivePaths.ToList();

        if (_settings.DeactivateOnExit && _settings.LastActivePaths.Count > 0)
        {
            _activator.DeactivateAll();
            _settings.LastActivePaths.Clear();
        }

        _settings.Save();
        _cache.Save(AppSettings.CachePath);
    }

    // ==================================================================== 截图（调试）

    public async Task RunScreenshotAsync(string outputPath, string pane = "preview", string? crop = null, int scale = 1)
    {
        _shotMode = true;
        await _initDone.Task;

        if (pane.StartsWith("search:", StringComparison.Ordinal))
        {
            SearchBox.Text = pane[7..];
            await Task.Delay(400);
        }

        bool demoActivation = pane == "active";

        switch (pane)
        {
            case "chars":
                TabChars.IsChecked = true;
                break;
            case "info":
                TabInfo.IsChecked = true;
                break;
        }

        if (demoActivation)
        {
            var folderSource = _sources.FirstOrDefault(s2 => s2.Kind == "folder");
            if (_currentSource?.Kind != "folder" && folderSource is not null) SelectSource(folderSource);

            var library = _visible.FirstOrDefault(e => !e.IsInstalled && !e.IsInstalledConflict)
                          ?? _visible.FirstOrDefault(e => !e.IsInstalled);
            if (library is not null)
            {
                FontList.SelectedItem = library;
                FontList.ScrollIntoView(library);
                library.IsChecked = true;
                UpdateActionButtons();
                await BatchActivateAsync(true);
            }
        }

        await Task.Delay(900);

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double factor = dpi.DpiScaleX * Math.Max(1, scale);
            int w = (int)Math.Round(ActualWidth * factor);
            int h = (int)Math.Round(ActualHeight * factor);

            var bitmap = new RenderTargetBitmap(w, h, 96 * factor, 96 * factor, PixelFormats.Pbgra32);
            bitmap.Render((Visual)Content);

            BitmapSource output = bitmap;
            if (!string.IsNullOrWhiteSpace(crop))
            {
                var parts = crop.Split(',');
                if (parts.Length == 4)
                {
                    int cx = (int)(int.Parse(parts[0]) * factor);
                    int cy = (int)(int.Parse(parts[1]) * factor);
                    int cw = Math.Min((int)(int.Parse(parts[2]) * factor), w - cx);
                    int ch = Math.Min((int)(int.Parse(parts[3]) * factor), h - cy);
                    if (cw > 0 && ch > 0)
                        output = new CroppedBitmap(bitmap, new Int32Rect(cx, cy, cw, ch));
                }
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using (var stream = File.Create(outputPath))
            {
                encoder.Save(stream);
            }
        }
        catch
        {
            // 截图失败不影响主流程。
        }

        _batchCancel = true;
        await Task.Delay(300);
        _activator.DeactivateAll();
        Close();
    }
}
