# C 盘缓存清理器

> 先扫描、看明细，再确认。轻量 Windows 便携工具，柔和炭黑界面，无广告、无遥测。

[下载最新版本](https://github.com/bogao6769-netizen/c-drive-cache-cleaner/releases/latest) · [反馈问题](https://github.com/bogao6769-netizen/c-drive-cache-cleaner/issues/new/choose) · [MIT 许可证](LICENSE) · [更新记录](CHANGELOG.md)

![Windows 构建与安全测试](https://github.com/bogao6769-netizen/c-drive-cache-cleaner/actions/workflows/windows.yml/badge.svg)

![界面预览](ui-preview.png)

## 能做什么

- 扫描 Adobe ODIS 旧下载包、用户和 Windows 临时文件、Chrome / Edge 缓存、DirectX / NVIDIA 着色器和资源管理器缩略图。
- 临时文件可保留最近 24 小时、72 小时或 7 天；不把“旧”当作一定可以删除的保证。
- 双击分类或点击“查看扫描明细”，查看路径、容量、候选数量和保留原因；可导出全部明细。
- 清理前二次确认，只处理本次扫描列出的候选文件。
- 支持停止扫描和后续清理；已完成的删除不能撤销。
- CSV 结果分别显示预计容量、已删除文件容量和 C 盘可用空间变化，不混为“实际释放”。
- 崩溃转储与错误报告是不可重建的诊断数据，默认不勾选。

## 下载与使用

1. 从 [Releases](https://github.com/bogao6769-netizen/c-drive-cache-cleaner/releases/latest) 下载便携 ZIP，解压；也可直接下载 EXE。
2. 对照同一 Release 的 `SHA256SUMS.txt` 核验文件：
   `Get-FileHash .\CDriveCacheCleaner.exe -Algorithm SHA256`。
3. 双击运行，等待扫描，检查分类和明细，再勾选需要处理的项目。
4. 点击“清理所选缓存”，阅读确认提示。完成后可导出结果。

无需安装，也不要求管理员身份。没有权限的系统文件会跳过，不建议为了多删文件而提权。当前 EXE 没有商业代码签名；遇到未知发布者或安全软件警告，请先核验来源、哈希及源码，不要关闭防护。

仓库内的 [EXE](dist/CDriveCacheCleaner.exe) 是本机构建；Release 附件由 Windows CI 从对应提交构建，两次编译的哈希可能不同，应核对各自对应的哈希。

## 安全边界与局限

程序只接受内置缓存目录白名单，不接受用户任意路径；不主动处理桌面、文档、下载、回收站、浏览器书签、密码、历史记录或 Cookie。它不会识别你主动存放在缓存目录中的重要文件。

删除前检查根目录及祖先重解析点、文件身份、实际路径与修改时间，并通过验证过的 Windows 文件句柄执行删除。占用、只读、硬链接、无权限或扫描后发生变化的文件会跳过。不递归删除空目录。

**删除不会进入回收站，也不是零风险操作。** 重要数据请先备份，关闭相关应用后再检查选项。着色器和浏览器缓存重建可能暂时增加加载时间。缓存之外的磁盘占用不在此工具范围内。

“临时文件策略 / 保留时间”只针对临时文件与部分错误报告；浏览器、着色器、缩略图使用各自规则。程序不执行磁盘优化、不保证加速、不承诺固定释放量。详情见 [安全政策](SECURITY.md)。

## 兼容性

目标环境：Windows 10 / 11 与 .NET Framework 4.x。无需 Python、Node、Visual Studio 或额外 NuGet 包。

已在本机 Windows 11 64 位构建、运行沙盒测试与默认 / 最小窗口检查；CI 使用 Windows Server 2022 runner 构建与自检。Windows 10、32 位及不同 DPI 仍需真实设备反馈，不应视作已全面认证。

## 隐私

- 没有广告、遥测、账户或后台上传；“反馈问题”仅在点击时通过默认浏览器打开 GitHub。
- 日志只保存在本机 `%LocalAppData%\CDriveCacheCleaner\Logs`。
- CSV 导出会把当前用户目录前缀替换成 `%USERPROFILE%`，但其他路径与文件名仍可能敏感；分享前请人工检查。

## 开发与验证

在 Windows PowerShell 5.1 执行，不要用 PowerShell 7 的 Add-Type 替代：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

# 只操作随机临时沙盒，不删除真实缓存
$p = Start-Process .\dist\CDriveCacheCleaner.exe -ArgumentList '--self-test', 'self-test.json' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw 'Safety tests failed' }

# 真实目录只读扫描，输出 JSON；不清理
Start-Process .\dist\CDriveCacheCleaner.exe -ArgumentList '--scan-report', 'scan-report.json' -Wait

# 窗口截图与布局冒烟检查，同样不清理真实缓存
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\test-ui.ps1

# 构建 + 安全自检 + ZIP + SHA-256
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\package.ps1
```

`Program.cs`：界面、目录规则与路径策略；`Engine.cs`：扫描快照、文件句柄删除和报告；`SafetyTests.cs`：隔离自检。

GitHub Actions 对 push / PR 构建和测试。默认分支的新版本通过验证后自动生成 Release；已有同版本 Release 保持不变。升级时同时修改程序集版本及对应 `docs/RELEASE-x.y.z.md`，不上传密钥，不在 CI 清理真实缓存。

欢迎 [参与贡献](CONTRIBUTING.md)。[推广材料](docs/PROMOTION.md) 提供中文 / 英文介绍、55 秒演示脚本及小范围测试计划。

## License / English summary

MIT © 2026 bogao6769-netizen. 允许使用、修改和再发布，须保留版权与许可声明；软件按原样提供，不提供担保。

A lightweight portable Windows cache cleaner: scan first, inspect details, then confirm. Soft charcoal UI, cancellation, CSV reports, fixed allowlist, file identity checks, no ads or telemetry. Diagnostic data stays unchecked by default. Deletion is permanent; back up important data. See [Releases](https://github.com/bogao6769-netizen/c-drive-cache-cleaner/releases/latest) for downloads.
