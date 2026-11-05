using System.IO;
using EverythingonStart.Models;

namespace EverythingonStart.Services;

public sealed class StartMenuIndexService
{
    private sealed record IndexedEntry(
        string Title,
        string Subtitle,
        string TargetPath,
        string DirectoryPath,
        string SearchKeywords,
        SearchResultKind Kind,
        DateTime? LastModified
    );

    private readonly List<IndexedEntry> _appEntries = [];
    private readonly List<IndexedEntry> _settingEntries = [];
    private readonly object _lock = new();
    private volatile bool _isIndexed;

    public StartMenuIndexService()
    {
        InitializeSettingsIndex();
    }

    public void RebuildAppIndex()
    {
        lock (_lock)
        {
            if (_isIndexed && _appEntries.Count > 0)
                return;

            var entries = new List<IndexedEntry>(128);
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddSystemQuickApps(entries, seenTitles);

            (string Path, bool Recurse)[] roots =
            [
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true),
                (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false)
            ];

            foreach (var (root, recurse) in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                foreach (var file in EnumerateShortcutFilesSafe(root, recurse))
                {
                    var title = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    if (title.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("卸载", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!seenTitles.Add(title))
                        continue;

                    var dir = Path.GetDirectoryName(file) ?? root;
                    var folderName = Path.GetFileName(dir);
                    entries.Add(
                        new IndexedEntry(
                            Title: title,
                            Subtitle: string.IsNullOrEmpty(folderName) ? "开始菜单应用" : $"应用 · {folderName}",
                            TargetPath: file,
                            DirectoryPath: dir,
                            SearchKeywords: $"{title} {folderName}".ToLowerInvariant(),
                            Kind: SearchResultKind.App,
                            LastModified: null
                        )
                    );
                }
            }

            _appEntries.Clear();
            _appEntries.AddRange(entries);
            _isIndexed = true;
        }
    }

    private static IEnumerable<string> EnumerateShortcutFilesSafe(string root, bool recurse)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = recurse,
            MaxRecursionDepth = 3,
            ReturnSpecialDirectories = false
        };

        foreach (var file in Directory.EnumerateFiles(root, "*.*", options))
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static void AddSystemQuickApps(List<IndexedEntry> entries, HashSet<string> seenTitles)
    {
        var sysDir = Environment.SystemDirectory;
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        (string Title, string Subtitle, string TargetPath, string Dir, string Keywords)[] builtIns =
        [
            ("我的电脑", "系统 · 此电脑与磁盘驱动器", Path.Combine(winDir, "explorer.exe"), winDir, "我的电脑 此电脑 this pc computer explorer 资源管理器 wd wddn"),
            ("文件资源管理器", "系统应用", Path.Combine(winDir, "explorer.exe"), winDir, "文件资源管理器 explorer 文件夹"),
            ("任务管理器", "系统工具", Path.Combine(sysDir, "Taskmgr.exe"), sysDir, "任务管理器 task manager taskmgr 进程 性能"),
            ("控制面板", "系统工具", Path.Combine(sysDir, "control.exe"), sysDir, "控制面板 control panel"),
            ("命令提示符", "系统工具", Path.Combine(sysDir, "cmd.exe"), sysDir, "命令提示符 cmd command prompt 终端"),
            ("Windows PowerShell", "系统工具", Path.Combine(sysDir, @"WindowsPowerShell\v1.0\powershell.exe"), sysDir, "powershell 终端 命令行"),
            ("注册表编辑器", "系统工具", Path.Combine(winDir, "regedit.exe"), winDir, "注册表编辑器 regedit registry"),
            ("计算器", "系统应用", "calc.exe", sysDir, "计算器 calculator calc"),
            ("记事本", "系统应用", Path.Combine(sysDir, "notepad.exe"), sysDir, "记事本 notepad 文本")
        ];

        foreach (var item in builtIns)
        {
            if (seenTitles.Add(item.Title))
            {
                entries.Add(
                    new IndexedEntry(
                        item.Title,
                        item.Subtitle,
                        item.TargetPath,
                        item.Dir,
                        $"{item.Title} {item.Keywords}".ToLowerInvariant(),
                        SearchResultKind.App,
                        null
                    )
                );
            }
        }
    }

    private void InitializeSettingsIndex()
    {
        (string Title, string Subtitle, string Uri, string Keywords)[] settings =
        [
            ("Windows 设置", "系统设置主页", "ms-settings:", "设置 settings 选项 系统设置"),
            ("显示设置", "系统 > 屏幕、亮度、夜间模式、HDR、缩放与布局", "ms-settings:display", "显示 分辨率 缩放 屏幕 刷新率 亮度 display resolution scale hdr"),
            ("声音设置", "系统 > 扬声器、麦克风、音量混合器", "ms-settings:sound", "声音 音频 音量 麦克风 扬声器 耳机 sound audio volume mic"),
            ("通知与操作", "系统 > 应用通知与专注助手", "ms-settings:notifications", "通知 免打扰 专注 notification focus"),
            ("电源和电池", "系统 > 睡眠、电源模式、电池使用情况", "ms-settings:powersleep", "电源 电池 睡眠 休眠 power battery sleep"),
            ("存储", "系统 > 磁盘空间、存储感知、临时文件清理", "ms-settings:storagesense", "存储 磁盘 空间 清理 垃圾 storage disk"),
            ("多任务处理", "系统 > 贴靠窗口、虚拟桌面", "ms-settings:multitasking", "多任务 贴靠 分屏 multitasking snap"),
            ("系统信息 / 关于", "系统 > 设备规格、重命名电脑、Windows 规格", "ms-settings:about", "关于 系统信息 设备 处理器 内存 about system info"),
            ("蓝牙和其他设备", "蓝牙、鼠标、键盘、打印机", "ms-settings:bluetooth", "蓝牙 设备 鼠标 键盘 耳机 bluetooth mouse keyboard"),
            ("打印机和扫描仪", "蓝牙和其他设备 > 打印机管理", "ms-settings:printers", "打印机 扫描仪 printer scanner"),
            ("网络和 Internet", "WLAN、以太网、VPN、代理、网络状态", "ms-settings:network-status", "网络 联网 网卡 network internet"),
            ("WLAN (Wi-Fi)", "网络和 Internet > 无线网络连接", "ms-settings:network-wifi", "wifi wlan 无线网 热点"),
            ("VPN 设置", "网络和 Internet > 虚拟专用网络", "ms-settings:network-vpn", "vpn 虚拟专用网"),
            ("代理设置", "网络和 Internet > 手动与自动代理服务器", "ms-settings:network-proxy", "代理 proxy 端口"),
            ("个性化", "背景、颜色、主题、锁屏界面、开始、任务栏", "ms-settings:personalization", "个性化 主题 壁纸 外观 personalization theme"),
            ("背景壁纸", "个性化 > 桌面背景图片与纯色", "ms-settings:personalization-background", "壁纸 背景 桌面图片 wallpaper background"),
            ("颜色与深色模式", "个性化 > 浅色/深色模式、透明效果、主题色", "ms-settings:colors", "颜色 深色模式 暗黑模式 透明 亚克力 dark mode color"),
            ("开始菜单设置", "个性化 > 开始菜单布局与最近添加的应用", "ms-settings:personalization-start", "开始菜单 start menu"),
            ("任务栏设置", "个性化 > 任务栏行为、对齐方式、系统托盘图标", "ms-settings:taskbar", "任务栏 居中 自动隐藏 托盘 taskbar"),
            ("字体设置", "个性化 > 已安装字体管理", "ms-settings:fonts", "字体 font"),
            ("已安装的应用", "应用 > 管理、修改或卸载程序", "ms-settings:appsfeatures", "应用 卸载 程序 安装 apps uninstall programs"),
            ("默认应用", "应用 > 浏览器、图片查看器、文件扩展名默认打开方式", "ms-settings:defaultapps", "默认应用 默认浏览器 打开方式 default apps browser"),
            ("启动应用", "应用 > 开机自动启动程序管理", "ms-settings:startupapps", "开机自启 启动项 startup autostart"),
            ("账户信息", "账户 > Microsoft 账户与本地账户设置", "ms-settings:yourinfo", "账户 用户 头像 account user"),
            ("登录选项 (PIN / 指纹 / 密码)", "账户 > Windows Hello、密码、PIN 码", "ms-settings:signinoptions", "密码 登录 pin 指纹 面部 password sign in hello"),
            ("时间和语言", "日期、时间、时区与区域设置", "ms-settings:dateandtime", "时间 日期 时区 同步 time date timezone"),
            ("语言和输入法", "时间和语言 > 显示语言、键盘与中文输入法", "ms-settings:regionformatting", "语言 输入法 键盘 中文 拼音 language ime keyboard"),
            ("游戏模式", "游戏 > Game Bar、屏幕截图、游戏模式优化", "ms-settings:gaming-gamemode", "游戏 game mode xbox"),
            ("辅助功能", "视觉、听觉、交互辅助选项", "ms-settings:easeofaccess", "辅助功能 放大镜 字幕 accessibility"),
            ("隐私和安全性", "Windows 安全中心、权限管理、开发者选项", "ms-settings:privacy", "隐私 安全 权限 privacy security"),
            ("开发者选项", "系统 > 开发人员模式、PowerShell 策略、文件资源管理器选项", "ms-settings:developers", "开发者 开发人员 developer sudo"),
            ("Windows 更新", "检查系统补丁、更新历史记录与高级选项", "ms-settings:windowsupdate", "更新 升级 补丁 windows update")
        ];

        foreach (var s in settings)
        {
            _settingEntries.Add(
                new IndexedEntry(
                    s.Title,
                    s.Subtitle,
                    s.Uri,
                    "ms-settings",
                    $"{s.Title} {s.Subtitle} {s.Keywords}".ToLowerInvariant(),
                    SearchResultKind.Setting,
                    null
                )
            );
        }
    }

    public IReadOnlyList<SearchResultItem> QueryApps(string query, int maxResults)
    {
        if (!_isIndexed)
            RebuildAppIndex();

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return _appEntries
                    .Take(maxResults)
                    .Select(ToResultItem)
                    .ToList();
            }

            var q = query.Trim().ToLowerInvariant();
            return _appEntries
                .Select(e => (Entry: e, Score: ComputeMatchScore(e, q)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Entry.Title.Length)
                .Take(maxResults)
                .Select(x =>
                {
                    var item = ToResultItem(x.Entry);
                    item.MatchScore = x.Score;
                    return item;
                })
                .ToList();
        }
    }

    public IReadOnlyList<SearchResultItem> QuerySettings(string query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _settingEntries
                .Take(maxResults)
                .Select(ToResultItem)
                .ToList();
        }

        var q = query.Trim().ToLowerInvariant();
        return _settingEntries
            .Select(e => (Entry: e, Score: ComputeMatchScore(e, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x =>
            {
                var item = ToResultItem(x.Entry);
                item.MatchScore = x.Score;
                return item;
            })
            .ToList();
    }

    private static int ComputeMatchScore(IndexedEntry entry, string queryLower)
    {
        var titleLower = entry.Title.ToLowerInvariant();
        if (titleLower == queryLower)
            return 100;
        if (titleLower.StartsWith(queryLower, StringComparison.Ordinal))
            return 85;
        if (titleLower.Contains(queryLower, StringComparison.Ordinal))
            return 70;
        if (entry.SearchKeywords.Contains(queryLower, StringComparison.Ordinal))
            return 50;
        return 0;
    }

    private static SearchResultItem ToResultItem(IndexedEntry e) =>
        new()
        {
            Title = e.Title,
            Subtitle = e.Subtitle,
            FullPath = e.TargetPath,
            DirectoryPath = e.DirectoryPath,
            Kind = e.Kind,
            DateModified = e.LastModified
        };
}
