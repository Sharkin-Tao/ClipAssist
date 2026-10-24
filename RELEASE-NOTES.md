# ClipAssist 0.1.0 发布说明

发布日期：2026-10-23

## 发布内容

- `ClipAssist-0.1.0-win-x64-setup.exe`：按当前用户安装的 Inno Setup 安装包；
- `ClipAssist-0.1.0-portable-win-x64.zip`：便携版压缩包；
- `SHA256SUMS.txt`：上述两个发布包的 SHA-256 校验值。

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

## 安全说明

- 本版本未进行代码签名；
- Windows SmartScreen 可能提示“未知发布者”；
- 请从可信渠道获取发布包，并使用 `SHA256SUMS.txt` 核对完整性；
- 剪贴板历史保存在本地 JSON 文件中，未额外加密；
- 请勿在不可信的共享账户或设备上记录敏感剪贴板内容。

## 许可证

ClipAssist 0.1.0 为专有软件，版权所有，保留所有权利。未经版权持有人书面许可，不得再分发、修改或用于未获授权的用途。

完整条款见发布包中的 `LICENSE`。

## 已知限制

- 只记录文本剪贴板；
- 暂无自动更新；
- 暂无跨设备同步；
- 当前版本按非商业用途发布。

## 文档

发布包中包含：

- `README.md`
- `LICENSE`
- `PRIVACY.md`
- `THIRD-PARTY-NOTICES.md`
- `CHANGELOG.md`
- `RELEASE-NOTES.md`