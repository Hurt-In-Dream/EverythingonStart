# EverythingonStart v1.4.1

## 修复

- 修复主界面原生亚克力合成产生的黑边、设置界面的白边及圆角露底；关闭模糊、高对比度和接口失败时也保持正确裁剪。
- 修复弹出动画期间背景与卡片边界错位；保留前景动画及设置窗口边缘缩放。
- 修复主界面选项、排序和筛选等 WPF 菜单左侧白色图标/勾选栏；菜单底板、分隔线、勾选、禁用文字和子菜单跟随深色/浅色主题。
- 保留 Wallpaper Engine 等第三方壁纸的实时 DWM/Accent 合成，不使用截图、GIF 或静态壁纸预览，不注入其他进程。

## 下载与升级

- `EverythingonStart-v1.4.1-Setup-x64.exe`：按用户安装，无需管理员权限，默认安装到 `%LOCALAPPDATA%\Programs\EverythingonStart`。
- `EverythingonStart-v1.4.1-Portable-x64.exe`：绿色单文件、自包含 .NET 与原生 SDK。
- `EverythingonStart-v1.4.1-source.zip` 和 `SHA256SUMS.txt`：对应源码与下载校验。

升级前请从托盘退出旧实例。安装器不强制关闭程序，不更改开机启动项，卸载保留用户设置。
模糊半径仍由 Windows 管理，动态壁纸动画仍受 Wallpaper Engine 自身暂停策略影响。文件检索需独立安装并运行 Everything。
发行文件尚未代码签名，请从本仓库下载并核对 SHA256。
