# Codex 定时发送台（桌面版）

给远端 Codex 会话按计划自动发消息的 Windows 桌面程序。窗口里选会话、写内容、定时间和周期，到点由程序通过 SSH 执行：

```
codex queue --thread <会话id> --message <内容> --resume
```

等同于你在 Codex 界面里手动输入一条消息并回车：消息进入该会话的队列，守护进程把会话重新跑起来。

单文件 exe，不需要安装 .NET 运行时之外的任何东西，也不需要浏览器。

---

## 启动

- 双击文件夹里的 `CodexScheduler.exe`
- 或桌面快捷方式「Codex 定时发送台」
- 已注册登录自启任务 `CodexSchedulerApp`，每次登录会自动在后台起来（关闭窗口只是收进托盘）

## 界面

**左侧「1 · 选择会话」**
会话列表从远端 `~/.codex/state_5.sqlite` 读取，按最近活跃排序，显示名称与最近活动时间。支持搜索、勾选多条、全选/清空/刷新。

**右侧三个页签**

| 页签 | 用途 |
|---|---|
| 新建 / 编辑任务 | 任务名称、发送内容（含快捷短语）、周期、结束条件 |
| 任务列表 | 查看下次执行时间与状态；立即执行 / 编辑 / 暂停启用 / 删除 |
| 运行日志 | 每次执行的逐会话结果，双击看完整输出 |

周期四种：

- **单次**：指定日期时间；错过不超过 6 小时会补发一次，然后自动停用
- **每天**：指定时刻
- **每周**：选星期 + 时刻
- **间隔**：每 N 分钟，可选开始时间

结束条件可留空，也可设「最多执行 N 次」或「截止日期」。

`预览命令` 只显示将要执行的命令，不会发任何东西。

## 数据文件

都在 exe 同级的 `data` 目录：

| 文件 | 内容 |
|---|---|
| `jobs.json` | 任务定义 |
| `run-log.jsonl` | 每次执行的详细结果 |
| `app.log` | 启动、会话刷新、触发记录（排查「为什么没发」先看这个） |

## 原理与限制

- 通过 SSH 别名 `cad-3-5090-fgy` 调远端 `codex`，主机名可在窗口右上角直接改。
- 程序每 10 秒扫一次任务；检测到 `jobs.json` 被外部修改会自动重载。
- 到点时程序必须在运行（登录自启已装）。**电脑关机期间不会执行**，恢复后按上面的规则处理。
- 远端账号限流时消息会被吞，日志里会写 `usage limit`，等窗口重置后重新执行即可。

## 自检与排错

```powershell
.\CodexScheduler.exe --selftest   # 检查周期计算、远端连接、数据读写
.\CodexScheduler.exe --layout     # 导出控件坐标（界面异常时用）
```

结果写到 `data\selftest.log`、`data\layout.txt`。

## 从源码构建

只需要 Windows 自带的 .NET Framework 4.x（`csc.exe`），无需 SDK、NuGet 或联网：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物 `CodexScheduler.exe` 在仓库根目录。源码只有 `src/CodexScheduler.cs` 一个文件（约 1900 行，WinForms 全代码构建）、`src/app.manifest`（高 DPI）和 `src/app.ico`。

## 卸载

```powershell
schtasks /delete /tn CodexSchedulerApp /f
```

然后删掉桌面快捷方式和本文件夹即可。
