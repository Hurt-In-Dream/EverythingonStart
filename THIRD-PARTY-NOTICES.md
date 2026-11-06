# Third-party notices

EverythingonStart's original source code is licensed under GPL-3.0-only.
The components below retain their own copyright and license notices.
Their original notices are not replaced by the project's GPL license.

## Everything SDK

Copyright David Carpenter / voidtools. MIT license.

- `Native/Everything64.dll`: Everything IPC SDK; native build metadata refers to EverythingToolbar's `EverythingSDK` project.
- `Native/Everything3.dll`: Everything 1.5 named-pipe SDK; native build metadata refers to EverythingToolbar's `EverythingSDK3` project.
- SDK source and native build projects are provided in `Native/Source/EverythingSDK` and `Native/Source/EverythingSDK3`.
- Vendored source snapshot: [srwi/EverythingToolbar, commit 1260a08231602ece727ee265ad584b6610b15737](https://github.com/srwi/EverythingToolbar/tree/1260a08231602ece727ee265ad584b6610b15737).
- The existing DLLs are retained unchanged. The source snapshot is attributed to the upstream native projects; bit-for-bit reproduction of the pre-existing DLLs is not claimed.
- SDK license notices: `licenses/EverythingSDK-LICENSE.txt` and `licenses/EverythingSDK3-LICENSE.txt`.
- Official SDK documentation: https://www.voidtools.com/support/everything/sdk/
- Official SDK 3 source: https://github.com/voidtools/everything_sdk3

Everything itself must be installed and running separately; its application executable is not bundled.

## MingCute

The `mingcute:settings-4-line` vector geometry is from MingCute Icons.
Copyright MingCute contributors. Apache License 2.0.

- Source: https://github.com/mingcute-design/mingcute-icons
- License: `licenses/MingCute-LICENSE.txt`.
- Geometry is embedded in `Services/IconCacheService.cs` and used to render application-generated settings icons.

## .NET

The self-contained Windows x64 release includes unmodified Microsoft .NET runtime,
WPF and Windows Forms components. Copyright .NET Foundation and contributors.
These components retain their MIT license and applicable third-party notices.

- License: `licenses/DotNet-LICENSE.txt`.
- Runtime third-party notices: `licenses/DotNet-THIRD-PARTY-NOTICES.txt`.
- WPF third-party notices: `licenses/Wpf-THIRD-PARTY-NOTICES.txt`.
- Windows Forms third-party notices: `licenses/WinForms-THIRD-PARTY-NOTICES.txt`.
- Runtime source: https://github.com/dotnet/runtime
- WPF source: https://github.com/dotnet/wpf
- Windows Forms source: https://github.com/dotnet/winforms
- The exact bundled framework versions are recorded in the release's `EverythingonStart.runtimeconfig.json`.
