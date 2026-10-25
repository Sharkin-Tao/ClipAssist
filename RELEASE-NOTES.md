# ClipAssist 0.1.1 发布说明

发布日期：2026-10-25

## 发布内容

- `ClipAssist-0.1.1-win-x64-setup.exe`：当前用户级 Inno Setup 安装包；
- `ClipAssist-0.1.1-portable-win-x64.zip`：便携版压缩包；
- `SHA256SUMS.txt`：上述两个发布包的 SHA-256 校验值。

## 本次修复

### 开机自启动

- 开启开机自启动时增加写入前读取、写入后回读和状态校验；任一步失败都会回滚，不再把失败误报为成功；
- 失败信息区分读取、写入、验证和删除阶段，并包含 HResult；如果程序位于低完整性目录，会提示将程序复制到普通本地目录后重试；
- 优先打开已存在的 HKCU Run 键，只有键不存在时才创建，减少不必要的权限请求。

### 浮窗最小化

- 修复浮窗标题栏最小化后，屏幕左下角残留窗口轮廓的问题；
- 现在最小化会隐藏浮窗；仍可通过全局快捷键或托盘入口恢复。

## 主要能力

- 文本剪贴板历史；
- 通过全局快捷键快速呼出；
- 搜索、复制、删除和批量删除；
- 主题、浮窗、托盘和开机自启动；
- 安装版与便携版数据隔离；
- 本地数据目录可迁移。

## 安装

运行安装包，按向导完成安装。安装器默认安装到：

```text
%LOCALAPPDATA%\Programs\ClipAssist
```

安装不要求管理员权限。

便携版解压后直接运行 `ClipAssist.exe`。程序目录下应保留 `ClipAssist.portable`。

## 数据

- 安装版：`%APPDATA%\ClipAssist`
- 便携版：`<程序目录>\Data`
- 便携目录不可写时的回退：`%LOCALAPPDATA%\ClipAssistPortable`

卸载不会自动删除用户数据。

## 验证说明

本次发布前已在当前 Windows 环境完成：

- Release 构建：0 个警告，0 个错误；
- 主测试套件：217 项通过，0 项失败；
- 开机自启动专项测试：24 项通过，0 项失败；
- 浮窗最小化/恢复、主窗口最小化、快捷键恢复的 Win32/UIA 检查；
- 安装包静默安装/卸载、便携版解压启动：通过。

以下项目未能在当前自动化环境中完成，仍需人工验证：

- 真实注销或重启后的开机自启动；
- 浮窗标题栏的物理鼠标最小化点击（自动化受桌面窗口层叠/DPI 坐标影响）；
- 将浮窗文本拖放到 Notepad、浏览器等外部程序；
- Windows SmartScreen 的实际提示。

## 安全说明

- 本版本未进行代码签名；
- Windows SmartScreen 可能提示“未知发布者”；
- 请从可信渠道获取发布包，并使用 `SHA256SUMS.txt` 核对完整性；
- 剪贴板历史保存在本地 JSON 文件中，未额外加密；
- 请勿在不可信的共享账户或设备上记录敏感剪贴板内容。

## 许可证

ClipAssist 0.1.1 为专有软件，版权所有，保留所有权利。未经版权持有人书面许可，不得再分发、修改或用于未获授权的用途。

完整条款见发布包中的 `LICENSE`。

## 已知限制

- 只记录文本剪贴板；
- 暂无自动更新；
- 暂无跨设备同步；
- 当前版本按非商业用途发布。

## 文档

发布包中包含：

- `README.md`
- `README.zh-CN.md`
- `LICENSE`
- `PRIVACY.md`
- `THIRD-PARTY-NOTICES.md`
- `CHANGELOG.md`
- `RELEASE-NOTES.md`

---

# ClipAssist 0.1.1 Release Notes

Release date: 2026-10-25

## Downloads

- `ClipAssist-0.1.1-win-x64-setup.exe` — per-user Inno Setup installer;
- `ClipAssist-0.1.1-portable-win-x64.zip` — portable package;
- `SHA256SUMS.txt` — SHA-256 checksums for both packages.

## Fixes in This Release

### Startup at sign-in

- Startup registration now reads before writing, reads back after writing, and validates the resulting status. Any failure is rolled back instead of being reported as success.
- Failure messages now identify the failing stage (read, write, verification, or delete) and include the HResult. If the program is running from a low-integrity folder, the message explains how to move it to a normal local folder and retry.
- The existing HKCU Run key is opened first; the key is only created when it is missing, avoiding unnecessary permission requests.

### Floating window minimize

- Fixed the leftover window outline in the lower-left corner of the screen after minimizing the floating window from its title bar.
- Minimizing now hides the floating window. It can still be restored with the global shortcut or the tray entry.

## Main Capabilities

- Text clipboard history;
- Quick access through a global shortcut;
- Search, copy, single-entry delete, and bulk delete;
- Themes, floating window, tray, and startup at sign-in;
- Separate data locations for installed and portable versions;
- User-selectable local data directory.

## Installation

Run the installer and follow the wizard. The default per-user location is:

```text
%LOCALAPPDATA%\Programs\ClipAssist
```

Administrator rights are not required.

For the portable version, extract the ZIP and run `ClipAssist.exe`. Keep `ClipAssist.portable` in the program folder.

## Data

- Installed version: `%APPDATA%\ClipAssist`
- Portable version: `<program folder>\Data`
- Fallback when the portable folder is not writable: `%LOCALAPPDATA%\ClipAssistPortable`

Uninstalling does not delete user data automatically.

## Verification

The following checks were completed on the current Windows environment before this release:

- Release build: 0 warnings, 0 errors;
- Main test suite: 217 passed, 0 failed;
- Startup-specific test suite: 24 passed, 0 failed;
- Win32/UIA checks for floating-window minimize/restore, main-window minimize, and shortcut restore;
- Silent install/uninstall and portable-package extraction/launch: passed.

The following items could not be completed in the current automation environment and still require manual verification:

- Startup after a real sign-out or restart;
- A physical mouse click on the floating window's minimize button (automation was affected by desktop window stacking and DPI coordinate mapping);
- Dragging text from the floating window into Notepad, a browser, or another external application;
- The actual Windows SmartScreen prompt.

## Security

- This release is not code-signed;
- Windows SmartScreen may show an “Unknown publisher” warning;
- Obtain the package from a trusted source and verify it with `SHA256SUMS.txt`;
- Clipboard history is stored in local JSON files without additional encryption;
- Do not record sensitive clipboard content on shared or untrusted accounts and devices.

## License

ClipAssist 0.1.1 is proprietary software. Copyright is retained; all rights are reserved. Redistribution, modification, or unauthorized use requires prior written permission from the copyright holder.

See the `LICENSE` file in the package for the complete terms.

## Known Limitations

- Text clipboard only;
- No automatic updates;
- No cross-device sync;
- This release is distributed for non-commercial use.

## Documents

The package includes:

- `README.md`
- `README.zh-CN.md`
- `LICENSE`
- `PRIVACY.md`
- `THIRD-PARTY-NOTICES.md`
- `CHANGELOG.md`
- `RELEASE-NOTES.md`