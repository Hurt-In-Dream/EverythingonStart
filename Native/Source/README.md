# Everything SDK source

These unmodified SDK source directories and Visual C++ build projects are copied
from EverythingToolbar commit `1260a08231602ece727ee265ad584b6610b15737`:
https://github.com/srwi/EverythingToolbar/tree/1260a08231602ece727ee265ad584b6610b15737

The SDK code retains David Carpenter / voidtools' MIT license. See each source
header, `EverythingSDK3/LICENSE`, and the notices in the repository's `licenses/`.
This is not a copy of the EverythingToolbar application or its UI.

Building these native projects requires Visual Studio C++ build tools (MSVC v143)
and a Windows SDK. For example, from a Visual Studio Developer PowerShell:

```powershell
msbuild .\EverythingSDK\EverythingSDK.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild .\EverythingSDK3\EverythingSDK3.vcxproj /p:Configuration=Release /p:Platform=x64
```

Normal EverythingonStart builds use the existing DLLs in `Native/`; publishing
does not replace them with newly compiled SDKs. The SDK snapshot preserves source
and build instructions, but does not claim bit-for-bit reproduction of the
pre-existing DLLs or the exact original compiler/toolchain used to build them.
