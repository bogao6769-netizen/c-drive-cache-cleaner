# 参与贡献

感谢帮助 C 盘缓存清理器变得更可靠。欢迎提交可复现的问题、界面改进和经过验证的缓存规则。

## 报告问题

通过 Issues 的 Bug 模板提供软件版本、Windows 版本、显示缩放、复现步骤和期望结果。不要上传含用户名、私人文件名或内网路径的完整报告；导出报告仅替换用户目录前缀，其他信息仍需人工检查。

涉及越界删除或安全绕过，请先阅读 [SECURITY.md](SECURITY.md)。

## 本地开发

使用 Windows PowerShell 5.1，而不是 PowerShell 7 的 Add-Type：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
$p = Start-Process .\dist\CDriveCacheCleaner.exe -ArgumentList '--self-test', 'self-test.json' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw 'Safety tests failed' }
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\test-ui.ps1
```

安全测试只操作随机临时沙盒，界面测试只扫描、不清理真实缓存。提交修改后，GitHub Actions 会在 Windows 环境重新构建并自检。

## 新增清理规则

规则必须是具体缓存目录或文件名模式，不能接受磁盘根目录、用户任意路径或整个应用配置目录。解释它保存什么、何时重建、会损失什么。不可重建的诊断数据默认不勾选。遵守路径白名单、保留时间、文件身份校验和链接拒绝规则。

每项改动需有沙盒测试；不要在 CI 中使用真实用户缓存做删除测试。PR 请说明动机、影响、测试结果和界面截图。不要提交个人日志、临时报告或密钥。

界面沿用柔和炭黑主题，正白限于标题、容量和关键操作；不要增加大面积白底或降低普通文字的可读性。

代码和贡献采用 MIT 许可证。发布版本由维护者确认版本号和更新说明后在默认分支构建。
