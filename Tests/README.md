# Win 键输入交接回归测试

```powershell
pwsh -NoProfile -File .\Tests\Test-WinKeyHandover.ps1
```

需要 .NET 10 SDK 和 PowerShell 7，无需额外测试框架。

测试编译实际的 `WinKeyHookService`，并提取实际的焦点重试方法。
仅用测试替身替换时钟、Windows API、窗口/输入框和服务依赖，确定性验证：
慢速焦点切换、窗口前台但输入框未聚焦、焦点再次被抢、连续输入、按键释放与长按重复、
隐藏/超时取消、Win/Ctrl 组合键和注入输入。

这些是回归测试，不替代真实 Windows 桌面上的输入法、全屏应用和多显示器人工验证。
编译生成文件放在临时目录，不会向用户正在使用的其他程序发送按键。

## 主题与配色回归测试

先构建应用，再指定构建产物目录：

```powershell
pwsh -NoProfile -File .\Tests\Test-AppTheme.ps1 -AppAssemblyDirectory .\bin\x64\Release\net10.0-windows
```

测试创建真实的屏幕外 WPF 窗口，注入系统主题/强调色读数并触发通知处理，
验证三种主题、独立强调色开关、已打开窗口动态更新、设置序列化、旧配置默认值及文本对比度。
设置文件写入隔离临时目录，不更改用户的 Windows 主题或开机启动项。

## 全屏窗口切换层级回归测试

```powershell
pwsh -NoProfile -File .\Tests\Test-FullscreenSwitch.ps1
```

提取并执行主窗口的实际全屏降级/恢复方法，使用屏幕外的真实 HWND 和原生层级调用，
覆盖原本置顶/非置顶的全屏窗口、置顶/非置顶任务栏，以及选择普通窗口后收起搜索面板的情况。
验证全屏窗口不会因任务栏定位重新置顶，收起面板不会覆盖新选中的窗口，置顶任务栏仍高于全屏窗口。
前台句柄、显示器尺寸与 Shell 通知使用隔离输入，不发送全局 Alt+Tab，不修改用户窗口或系统任务栏。
这验证窗口层级缺陷，不替代具体全屏游戏的人工切换测试。

## 背景高斯模糊回归测试

```powershell
pwsh -NoProfile -File .\Tests\Test-GaussianBlur.ps1 -AppAssemblyDirectory .\bin\x64\Release\net10.0-windows
```

使用屏幕外 WPF 窗口及注入的棋盘格背景，验证默认关闭、设置保存/重载、首次显示的壁纸图层、
高对比度和失败回退、隐藏/关闭后清除像素、背景独立输入区域、半径范围及真实高斯渲染像素。
用前景细线的边缘验证前景不模糊。截图只包含合成背景，不保存用户桌面；配置写入隔离目录，不操作用户设置或其他窗口。
可额外指定 `-WallpaperSmokeTest` 验证壁纸图层读取接口：只在内存生成 8×8 壁纸区域，不截屏、不打印像素或保存图像。

## 桌面壁纸图层（禁止截图）回归

```powershell
pwsh -NoProfile -File .\Tests\Test-DesktopWallpaper.ps1 -AppAssemblyDirectory .\bin\x64\Release\net10.0-windows
```

验证截屏方法已删除，桌面壁纸来源明确；用合成壁纸验证六种布局、负坐标与跨显示器区域、
纯色/缺失壁纸回退、窗口移动和缩放后更新、空闲时不持续采样、隐藏/关闭时释放图层。
可指定 `-LiveWallpaperSmokeTest` 验证只读 Windows 壁纸配置和内存中的小区域渲染；不修改系统壁纸、不截图，也不记录壁纸路径或图像内容。

## Web 网页预览与 MingCute 图标回归

```powershell
pwsh -NoProfile -File .\Tests\Test-WebPreview.ps1 -AppAssemblyDirectory .\bin\x64\Release\net10.0-windows
```

使用可控 HTML 和 HTTP 响应验证四种搜索引擎解析、站内链接、重定向解码、URL 安全、
验证码/超时/限流/无结果/页面大小限制、缓存，以及选中项切换和隐藏窗口时取消旧预览。
创建屏幕外真实 WPF 窗口检查右栏卡片、保留文件详情、MingCute 原始 SVG 几何和点击目标。
不启动用户浏览器，不修改用户设置；默认测试不发出网络请求，截图中的搜索结果为明确的测试数据。
可指定 `-LiveSmokeTest` 额外请求 Bing 的「Everything SDK」并验证实际返回的网页结果。
