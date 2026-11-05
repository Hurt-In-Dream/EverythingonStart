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
