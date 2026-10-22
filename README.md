# Codex 重置雷达

一个不调用大模型的 Windows 托盘小组件。它每 5 分钟读取公开重置信号和本机已登录 Codex 的使用限额。

每次刷新还会合并 X 主页时间线和备用公开源，去重后缓存最近 10 条消息。窗口和托盘菜单中的“最近 10 条”可查看完整原文；可点“翻译全部”并随时切回原文。翻译仅在当前查看窗口保存，不会覆盖缓存原帖。包含 `banked reset` 的公告会标作“重置卡”并参与倒计时识别。

## 倒计时含义

- **官方倒计时**：优先读取公开源的 `official_window`；缺失时，从重置公告正文解析明确时刻和时区（如 `6pm PST`、`18:30 UTC`）或明确相对小时数。按公告发布时间选择最新有效预告，避免旧窗口遮住新公告。
- **时区说明**：`PST` 按 UTC−08:00、`PDT` 按 UTC−07:00 原文解释；`PT` 使用太平洋地区夏令时规则。界面显示换算后的本地日期和时间。夏季原帖写 `PST` 时，会另列作者若指 `PDT` 的可能时间，不静默改写原文。缺时区或仅有模糊日期的主源正文不会被当作具体官宣时刻。
- **官方时间窗已到**：预告时间已经到，但公开源尚未确认落地。
- **没有公开预告**：优先显示本机已登录 Codex 的额度刷新窗口；历史统计只作参考，不当作下一次全局重置的倒计时。
- **回复中的预告**：只有能关联到同一公开时间线中的重置上文时，才把“明天几点前降落”等回复识别为重置预告。`before` 和 `by EOD` 显示为最迟时间，不冒充准确落地时刻；“明天”按作者所写时区的日期理解。
- **完成确认**：识别原帖中的完成表述以及归档中明确的完成记录。归档时间标为公开确认或监测时间，不冒充服务端精确重置时刻。较新的完成确认优先于较早的预告。
- **本机额度核验**：保存同一账号每周额度的前后两次读取。如果预计正常刷新还很远，而已用额度从至少 5% 降至 2% 以下且至少下降 5 个百分点，显示“本机每周额度已重置”。这只证明当前登录账号的额度变化；实际变化位于两次读取之间。正常周期刷新和切换账号不会按特殊重置处理。
- 首次运行只建立基线，不会把旧消息当成新消息弹窗。

## 构建

在此目录运行：

```powershell
.\build.ps1 -Clean
```

构建只使用 Windows 自带的 .NET Framework C# 编译器，以及 `System.Windows.Forms`、`System.Drawing` 和 `System.Web.Extensions`。

## 参数

- `CodexResetRadar.exe`：显示窗口，自动注册当前 exe 到 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- `CodexResetRadar.exe --background`：后台启动并驻留托盘。
- `CodexResetRadar.exe --check-now`：联网检查一次，将状态、原帖链接及识别到的 UTC/本地目标时间写到当前控制台后退出。
- `CodexResetRadar.exe --self-test`：运行离线解析和提醒去重测试。

## 安装与自动更新

从 [GitHub Releases](https://github.com/987144016/codex-reset-radar/releases/latest) 下载 `CodexResetRadar.exe`，放在当前用户有写入权限的文件夹中运行一次。程序启动时以及驻留托盘后每 6 小时从最新 Release 的 `latest.json` 检查版本；发现新版后下载 GitHub Release 中的程序，核对 SHA-256，退出旧进程、替换并在后台重新启动。断网或更新失败时保留旧版本，详情写入 `%LOCALAPPDATA%\CodexResetRadar\update.log`。另一台电脑不需要 GitHub 登录。

本机 Codex 的 `auth.json` 只用于在本机读取额度；不会被上传到 GitHub。公开仓库仅包含源码、构建脚本、说明和版本清单。额度快照保存在 `%LOCALAPPDATA%\CodexResetRadar\state.json`，不会进入仓库。

## 数据源

主源：`https://codex-reset.com/api/feed` 与 `https://codex-reset.com/api/forecast`

备用源：`https://api.dayclaw.com/api/source/public/x/thsottiaux/items`

这些接口是公开聚合源，具体预告仍以所链接原帖为依据；正文已出现时刻，不代表聚合源一定已经填写结构化时间字段。

时间线补充源：`https://x.com/thsottiaux`。若 X 暂时拒绝公开读取，程序仍会保留上次成功缓存的 10 条记录，并继续使用备用源。

当主源的最新重置消息只有截断摘要时，程序会按该消息的公开 X 帖子 ID 从 FxTwitter、VxTwitter 读取完整正文，以识别正文末尾的重置时间；不需要 API Key。两个补充源均不可用时，会保留主源摘要并明确不把没有完整时间的消息伪装成倒计时。

主源已经提供结构化公开时间窗时，程序也会将其作为全文补充失败时的兜底，并在界面标注为“公开信号时间窗 · 聚合源标注”。

最后一次成功结果缓存在 `%LOCALAPPDATA%\CodexResetRadar\state.json`，断网时会继续显示并明确标记为离线缓存。
