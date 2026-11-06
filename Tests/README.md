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
