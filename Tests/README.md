# Win 键输入交接回归测试

```powershell
pwsh -NoProfile -File .\Tests\Test-WinKeyHandover.ps1
```

需要 .NET 10 SDK 和 PowerShell 7，无需额外测试框架。

测试编译实际的 `WinKeyHookService`，并提取实际的焦点重试方法。
仅用测试替身替换时钟、Windows API、窗口/输入框和服务依赖，确定性验证：
慢速焦点切换、窗口前台但输入框未聚焦、焦点再次被抢、连续输入、按键释放与长按重复、
隐藏/超时取消、Win/Ctrl 组合键和注入输入。

另验证单按 Win 后 0/100/400/850/900ms、未知前台与切换到其他应用时，
未确认开始菜单打开就必须放行按键、不启动交接且不合成 Esc；
确认开始菜单实际打开后仍能正常接管输入。原生输入替身记录合成按键，
并检查未接管的 KeyUp 继续传递，不将这类隔离测试当作 ACE 兼容性认证。

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

## 原生 DWM 背景模糊与图标回归

```powershell
pwsh -NoProfile -File .\Tests\Test-NativeBackdrop.ps1 -AppAssemblyDirectory .\bin\x64\Release\net10.0-windows
```

使用屏幕外的真实主窗口/设置窗口与注入的失败、高对比度读数，验证默认关闭、配置兼容、
非分层 HWND、实时原生接口、圆角区域、主题更新、重新显示、关闭释放、前景无滤镜和 PNG/ICO 资源。
旧半径控件不再提供可调强度；`Test-GaussianBlur.ps1` 仅保留为新测试的兼容入口。

像素测试短暂显示两扇不激活的、完全覆盖采样区域的自有合成窗口：棋盘格验证软化，红/蓝变化验证背景无需文件或刷新即可实时更新。
仅在内存测量被这些合成窗口覆盖的 120×120 区域，不保存桌面图片；生产程序无采样或截图代码。
测试不启动/替换正在运行的托盘实例，不修改用户设置、系统壁纸或 Wallpaper Engine 配置。
这是 DWM 合成机制验证，不替代不同驱动、多显示器、Wallpaper Engine 暂停策略下的人工检查。

### 黑边 / 白边与圆角裁剪

```powershell
pwsh -NoProfile -File .\Tests\Test-WindowEdges.ps1 -AppAssemblyDirectory .\bin\x64\Release\net10.0-windows
```

用完全覆盖测试区域的自有绿色窗口衬底，测量真实主界面/设置界面的外边距、圆角及右/下边缘像素。
覆盖模糊开/关、高对比度与接口失败回退、设置窗口最小尺寸、重新打开和主界面弹出动画，另验证裁剪后仍能命中窗口缩放边缘。
仅保存自有合成窗口覆盖范围的截图；不截取用户桌面，不替换运行中的实例或用户设置。

### 菜单图标栏与分隔线配色

```powershell
pwsh -NoProfile -File .\Tests\Test-MenuChrome.ps1 -AppAssemblyDirectory .\bin\x64\Release\net10.0-windows
```

在隔离 WPF 应用中渲染真实 ContextMenu 和 MenuItem，检查深色/浅色菜单的图标栏像素、
统一底板、分隔线、勾选、禁用文字、子菜单与点击路由。只绘制测试菜单，不打开桌面弹窗或发送全局键盘输入。

## Web 网页预览与 MingCute 图标回归

```powershell
pwsh -NoProfile -File .\Tests\Test-WebPreview.ps1 -AppAssemblyDirectory .\bin\x64\Release\net10.0-windows
```

使用可控 HTML 和 HTTP 响应验证四种搜索引擎解析、站内链接、重定向解码、URL 安全、
验证码/超时/限流/无结果/页面大小限制、缓存，以及选中项切换和隐藏窗口时取消旧预览。
创建屏幕外真实 WPF 窗口检查右栏卡片、保留文件详情、MingCute 原始 SVG 几何和点击目标。
不启动用户浏览器，不修改用户设置；默认测试不发出网络请求，截图中的搜索结果为明确的测试数据。
可指定 `-LiveSmokeTest` 额外请求 Bing 的「Everything SDK」并验证实际返回的网页结果。
