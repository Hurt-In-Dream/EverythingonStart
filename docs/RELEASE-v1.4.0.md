# EverythingonStart v1.4.0

## 更新内容

- 接入新的透明应用图标：EXE、主窗口、设置中心与系统托盘统一使用多尺寸 ICO / PNG。
- 背景模糊改为每窗口原生 DWM / Accent Acrylic，Windows 11 SystemBackdrop 回退；参考 DWMBlurGlass 的接口路径，不注入其他进程。
- Wallpaper Engine 等动态壁纸直接参与系统合成，移除静态壁纸/GIF 预览和生产截图来源，避免底图与实际壁纸不符。
- 原生背景跟随主题、不透明度及圆角区域，支持系统不可用/高对比度回退；保留旧配置，默认不开启。
- 保留 Web 右栏结果卡片、MingCute 图标、主题设置、Win 输入交接和全屏 Alt+Tab 层级修复。
- 新增无需管理员权限的按用户安装版、无需旁置 DLL 或 .NET 的绿色单文件版，以及内嵌许可证与无副作用包诊断。
- README 增加主界面与设置中心截图、下载入口和第三方壁纸注意事项。

## 下载

- `EverythingonStart-v1.4.0-Setup-x64.exe`：默认安装到 `%LOCALAPPDATA%\Programs\EverythingonStart`，不更改开机启动项，卸载保留设置。
- `EverythingonStart-v1.4.0-Portable-x64.exe`：单 EXE 自包含；首次运行时 .NET 会提取原生库到用户临时缓存，配置仍保存在用户目录。
- `EverythingonStart-v1.4.0-source.zip`：对应发布提交的完整 GPL-3.0 源码。
- `SHA256SUMS.txt`：三个下载文件的 SHA256 校验值。

更新前请手动退出旧的托盘实例。文件检索仍需要独立安装并运行 Everything；推荐 Windows 11 22H2+ x64。
原生模糊半径由系统管理，旧半径设置不再控制效果；可调节背景不透明度。Wallpaper Engine 的暂停策略仍由它自身决定，不会被本程序改写。
原生模糊合成实际后方内容，并不是只提取壁纸。高对比度/系统不支持时回退主题背景，不截屏、不读取本地预览。
发行文件未进行代码签名；首次运行可能出现 SmartScreen 提示，请从此仓库下载并核对校验值。
