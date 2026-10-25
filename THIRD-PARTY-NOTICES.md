# ClipAssist 第三方组件与声明

ClipAssist 0.1.1 使用或随附以下第三方组件。各组件适用其自身许可证；本文件不改变这些许可证。

## 1. .NET 10、WPF 和 Windows Forms

- 提供方：Microsoft 及 .NET 贡献者；
- 用途：应用程序运行时、WPF 界面和 Windows Forms 托盘图标；
- 许可证：MIT License；
- 项目主页：https://github.com/dotnet/runtime
- 许可证文本：https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

Copyright (c) .NET Foundation and Contributors.

## 2. Inno Setup 6.7.3

- 提供方：Jordan Russell、Martijn Laan；
- 用途：生成 Windows 安装包；安装包中包含 Inno Setup 的安装器引擎；
- 许可证：Inno Setup License；
- 项目主页：https://jrsoftware.org/isinfo.php
- 许可证文本：https://jrsoftware.org/files/is/license.txt

Copyright (C) 1997-2026 Jordan Russell. All rights reserved.
Portions Copyright (C) 2000-2026 Martijn Laan. All rights reserved.

当前 ClipAssist 0.1.1 按非商业用途发布。Inno Setup 的版权和许可声明适用于安装包中的安装器引擎。

## 3. Inno Setup 简体中文翻译

- 文件：`installer\ChineseSimplified.isl`；
- 维护者：Zhenghan Yang (Kira)；
- 来源：Inno Setup 官方翻译仓库；
- 用途：安装器简体中文界面；
- 项目地址：https://github.com/jrsoftware/issrc/tree/main/Files/Languages

该翻译文件按 Inno Setup 翻译文件的分发方式随安装器使用。

## 4. 图标

- 文件：`Assets\ClipAssist.ico`；
- 用途：程序、托盘、快捷方式和安装器图标；
- 权利状态：项目所有者确认拥有该图标或有权随本软件公开分发。

该图标不作为第三方组件单独授权；其使用权随 ClipAssist 的发布授权一并提供。

## 5. 其他

当前项目没有引入额外的 NuGet 第三方包。

如果你再分发本软件，必须同时保留本文件、LICENSE 以及适用第三方许可证要求的版权和许可声明。

Copyright (c) 2026 ClipAssist. All rights reserved.