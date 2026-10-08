# 发布与推广材料

状态：文案和演示脚本已准备；尚未向外部社区发帖、录制视频或招募真实测试者。以下数据目标不是已经取得的成果。

## GitHub About（可直接复制）

描述：轻量 Windows C 盘缓存清理工具：先扫描、看明细，再确认；支持停止与结果导出，柔和炭黑界面，无广告、无遥测。

Topics：`windows` `cache-cleaner` `disk-cleanup` `winforms` `csharp` `portable` `dark-theme` `mit-license`

主页：https://github.com/bogao6769-netizen/c-drive-cache-cleaner/releases/latest

连接器没有仓库管理权限时，维护者需要在仓库右侧 About 的齿轮中填写这些内容。不要使用自动发布令牌更改仓库管理设置。

## 中文发布文案

标题：做了一个轻量 C 盘缓存清理器：先看明细，再决定删什么

正文：

我做了一个 Windows 便携小工具，主要处理常见临时文件、Chrome / Edge 缓存和着色器缓存。界面是柔和炭黑色，不需要安装，也没有广告或遥测。

它先扫描并列出分类与规则，你可以查看完整路径和保留原因，确认后才会清理。临时文件可以保留最近 24 小时、72 小时或 7 天，崩溃诊断记录默认不勾选。1.1.0 加入停止操作、CSV 报告、文件身份校验和目录联接保护。

清理不会进入回收站，也不是“零风险、一键加速”工具。它不处理个人文档、回收站、浏览器密码和 Cookie；重要数据仍请先备份。想请大家帮忙测试不同 Windows 版本和显示缩放下的表现。

源码与下载：https://github.com/bogao6769-netizen/c-drive-cache-cleaner

欢迎提交复现步骤和脱敏截图。项目采用 MIT 许可证，也欢迎贡献。

## English short introduction

C Drive Cache Cleaner is a lightweight portable Windows utility with a soft charcoal UI. Scan first, inspect file details, then confirm cleanup. It supports cancellation and CSV reports, uses a fixed cache allowlist, and checks file identity and reparse points before deletion. No ads or telemetry. Diagnostic files stay unchecked by default. Deletion is permanent; this is not a backup tool or a promise of zero risk. Windows 10/11 compatibility feedback is welcome.

Source and downloads: https://github.com/bogao6769-netizen/c-drive-cache-cleaner

## 55 秒演示脚本

| 时间 | 画面 | 解说 |
| --- | --- | --- |
| 0–6 秒 | 打开程序，展示炭黑界面 | C 盘空间紧张时，先看看缓存，而不是盲目删文件。 |
| 6–16 秒 | 扫描结果、选择保留 72 小时 | 这个便携工具先扫描，临时文件可以保留最近使用的内容。 |
| 16–28 秒 | 双击分类查看明细 | 每一类都有具体路径、容量和保留原因，诊断记录默认不清理。 |
| 28–38 秒 | 在测试虚拟机确认清理、展示停止按钮 | 检查选择后再确认。处理中可以停止，已删除的文件不能撤销。 |
| 38–48 秒 | 展示清理结果与导出 CSV | 预计容量、文件删除量和磁盘空间变化分开显示，不夸大效果。 |
| 48–55 秒 | GitHub 下载页 | MIT 开源，无广告、无遥测。欢迎测试并提交反馈。 |

录制前遮盖用户名、私人路径和通知；只在测试虚拟机演示删除，不展示私人电脑的敏感缓存。不要伪造空间数字或录制前后对比。

## 小范围测试计划

先邀请 5–10 位知情自愿测试者，不要求管理员身份，不收集完整文件列表。第一轮只扫描，第二轮由测试者自行检查后选择少量可重建缓存。

反馈字段：软件版本、Windows 10/11 构建号、32/64 位、100/125/150/200% 显示缩放、扫描时间、窗口文字是否截断、停止按钮是否有效、错误分类、脱敏截图。

有越界或重要数据丢失反馈时，停止推广并优先调查。Windows 10 / 32 位不能因理论可运行而标记为已验证。

## 四周执行清单

1. 第一周：核验 Release 下载、设置 About / topics，邀请测试者；优先收集兼容性问题。
2. 第二周：修复已复现问题，发布补丁；录制真实操作的 55 秒演示。
3. 第三周：经维护者确认，在合适的 Windows / 开源工具社区发一篇介绍；遵守各社区发布规则，不刷屏。
4. 第四周：整理反馈和版本记录，选择最常见的一个需求进入下一版。

衡量指标：可复现反馈数、严重问题数、Windows / DPI 覆盖、下载次数、Issue 首次回应时间。Star 仅作辅助指标，不承诺增长数量。
