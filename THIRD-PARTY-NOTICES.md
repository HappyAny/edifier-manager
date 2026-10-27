# 第三方组件与研究来源

本项目原创代码使用 GPL-3.0-only。第三方组件保留原有许可。

- 共用界面、图标几何形状及 Windows Core Audio 代码基于用户自己的 PILL 桌面助手，使用 GPL-3.0-only。漫步者版图标采用独立配色，不是漫步者官方标志。
- 漫步者传统控制协议依据 mEDIFIER 的公开抓包与接口资料实现。mEDIFIER 使用 MIT 许可，版权为 Copyright (c) 2023 Self Not Found，全文见 `third-party/mEDIFIER-MIT.txt`。来源：https://github.com/wh201906/mEDIFIER ，核对提交：`10056b51ce1b354fdf2b18e35e61f8a0b8520cd5`。
- `Microsoft.Windows.SDK.NET.dll`、`WinRT.Runtime.dll` 来自 Microsoft.Windows.SDK.NET.Ref 10.0.19041.57。Windows SDK 许可见 `third-party/Windows-SDK-License.rtf`；C#/WinRT MIT 许可见 `third-party/CsWinRT-LICENSE.txt`。上游：https://github.com/microsoft/CsWinRT 。
- 单文件版内置 .NET Runtime 与 Windows Desktop Runtime 10.0.14，许可及第三方声明分别见 `third-party/DotNet-Runtime-10.0.14.txt`、`third-party/DotNet-Desktop-10.0.14.txt`、`third-party/DotNet-Runtime-ThirdPartyNotices-10.0.14.txt`。
- 官方 EDIFIER Connect APK 仅供本地核对互操作格式，不进入发布包或源码包。官方文档及协议依据见 `docs/PROTOCOL.md`。

本项目与漫步者及 Microsoft 没有隶属关系。
