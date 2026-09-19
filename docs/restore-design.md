# 恢复待办 · 施工蓝图

> 2026-09-20 与 Leo 经 grill-me 三轮敲定。本文是唯一施工依据；与本文冲突的口头记忆以本文为准。

## 0. 问题

清除/删除待办后，撤销入口只有 10 秒 toast（`App.cs` 的 `_undo` + `_toastTimer`），过期即清空，再无任何入口找回任务。`history/*.json` 快照虽含全量数据，但按内容去重、按变更触发，无法回答"哪些任务是被清除的"。

## 1. 数据层：`cleared.jsonl`

位置：与 `tasks.json` 同目录。

格式：JSON Lines，每行一条清除记录（仿 `completions.jsonl`，UTF-8 无 BOM、`\n` 分行、行尾有 `\n`）：

```json
{"id":"...","text":"买牛奶","due":"2026-09-21T18:00","done":true,"cleared":"2026-09-20T05:10:00"}
```

- `id`：任务 Id（Guid N 格式）。
- `text`：清除瞬间的任务文本快照（含换行原样落盘）。
- `due`：DueAt 原样字符串（`"yyyy-MM-ddTHH:00"`），可为 null。
- `done`：清除瞬间是否已完成。
- `cleared`：清除时刻，本地时间，秒级 `"yyyy-MM-ddTHH:mm:ss"`。

规则：
- **只追加，永不删除**（与 completions.jsonl 同哲学）。同一任务删了又恢复又删会产生同 id 多行，属预期。
- 损坏行：读时跳过；本类不重写整个文件，因此**不做** `.bad-*.jsonl` 留存（completions 的留存逻辑挂在它的重写路径上，cleared.jsonl 没有重写路径）。
- 并发：沿用 `StorageLock` 同目录互斥。
- 写入失败（IO/权限）：吃掉异常 + toast 提示（键 `toast.restoreArchiveFailed`），不阻塞删除本身。**先写归档再删任务**，与 `CompletionToggled` 的"先日志后保存"同序，避免嵌套持锁。

类：`Storage.cs` 新增 `ClearedLog`
- `ClearedLog(string directory)`。
- `void Append(ClearedEntry entry)`：参数校验（Id 非空），锁内 `FileMode.Append` 追加一行。
- `List<ClearedEntry> ReadAll()`：锁内读、逐行 Parse、坏行跳过。
- `ClearedEntry { Id, Text, Due, Done, ClearedAt }`：Due 保存原样字符串（解析交给展示层 DueLogic.Parse），ClearedAt 是 DateTime。
- Parse 容错与 CompletionLog.Parse 同风格：id 缺失/空 → 跳过；cleared 不合法 → 跳过；due 不合法 → 存 null；done 缺失 → false。**额外规则：due 合法性用 `DueLogic.Normalize` 判定（hour-precise 语义，非法存 null）**，展示时再 `DueLogic.Parse`。

## 2. 纯逻辑层：`Logic.cs` 新增 `RestoreLogic`（无 WPF 依赖）

```csharp
public enum RestoreRange { Day = 0, Week = 1, Month = 2, Year = 3, All = 4, ExactDate = 5 }

public static class RestoreLogic
{
    // 各档滚动窗口时长（天）；All/ExactDate 不使用
    public static readonly double[] WindowDays = { 1, 7, 30, 365, 0, 0 };

    // age = now - clearedAt <= 窗口（含边界）。All 恒真。ExactDate 恒假（由 ExactMatch 单独处理）。
    public static bool InWindow(RestoreRange range, DateTime clearedAt, DateTime now);

    // 精确日期：clearedAt 落在所选自然日 [day 00:00, day+1 00:00) 内
    public static bool ExactMatch(DateTime clearedAt, DateTime day);

    // 按 id 取最新一条（后写的赢），再排除 currentTaskIds 中已有的 id，返回列表按 clearedAt 倒序
    public static List<ClearedEntry> Restorable(IEnumerable<ClearedEntry> entries, HashSet<string> currentTaskIds, DateTime now);
    // 注：Restorable 只做"最新+排除"，不做时间过滤；调用方再组合 InWindow/ExactMatch/文本过滤。

    // 文本匹配：忽略大小写子串（OrdinalIgnoreCase）
    public static bool TextMatches(string text, string keyword);
}
```

设计决策：Restorable 不内置时间过滤，菜单计数和预览列表各自叠加筛选，逻辑可独立断言。

## 3. App.cs：写入与入口

### 3.1 归档写入（两处，结构同 CompletionToggled）

```csharp
private readonly ClearedLog _clearedLog;   // 构造函数里 new ClearedLog(directory)

private void ArchiveCleared(TaskItem task)
{
    try { _clearedLog.Append(new ClearedEntry { Id = task.Id, Text = task.Text, Due = task.DueAt, Done = task.IsCompleted, ClearedAt = DateTime.Now }); }
    catch (Exception error)
    {
        if (!IsStorageError(error)) throw;
        SetToast(Strings.T(_language, "toast.restoreArchiveFailed"));
    }
}
```

- `Delete(TaskItem task)`：在 `_undo.Clear()` 之前调用 `ArchiveCleared(task)`。
- `ClearCompleted()`：在 `_undo.Clear()` 之前，对每个将清任务先归档，然后照旧。
- `IsStorageError`：`IOException || UnauthorizedAccessException || SecurityException`。
- 归档失败不影响删除继续（同 CompletionToggled 哲学：辅助日志失败不阻塞主操作）。

### 3.2 托盘菜单项

`SetupTray()` 中「显示」之后：

```csharp
_trayRestoreItem = new ToolStripMenuItem();
menu.Items.Insert(1, _trayRestoreItem);
menu.Opening += (s, e) => { SyncTrayRestoreState(); };   // 动态计数
_trayRestoreItem.Click += delegate { OpenRestoreWindow(); };
```

`SyncTrayRestoreState()`：
- 读 `_clearedLog.ReadAll()` → `RestoreLogic.Restorable(entries, currentIds, now)` 的条数 n；
- 文本 = `Strings.T(_language, "tray.restore")` + (n > 0 ? $" ({n})" : "")；
- Enabled = n > 0；
- 全程 try/catch（IO 失败 → Enabled = false）。
- `ApplyTrayLanguage()` 里补 `_trayRestoreItem.Text` 刷新（计数下次 Opening 再补）。

### 3. Opening 挂接位置

`_trayMenu.Opening` 事件挂 `SyncTrayRestoreState`。注意：`_syncingTrayMenu` 只约束 CheckChanged 类回调，不影响 Opening。

### 3.3 预览窗（代码构建，仿 ShowReminderWindow）

`OpenRestoreWindow()` 流程：
1. `ShowFromTray()`（若主窗隐藏先唤回）；
2. 读归档 + Restorable → 空则 toast（`restore.window.emptyToast`）并 return；
3. 构建模态 Window（仿 ShowReminderWindow 的主题化方式，但主题需在 ThemeManager.Applied 事件里同步刷新——预览窗打开期间切主题的概率极低，**v1 不做实时跟随**，打开时定格当前 palette，README/蓝图注明）。

窗口结构（全部代码构建，宽 560，SizeToContent=Height，MaxHeight 限 ~640）：

```
┌──────────────────────────────────────────┐
│ 标题：恢复待办                              │
│ [时间范围 ComboBox] [DatePicker(条件显示)] [搜索 TextBox] │
│ ┌──────────────────────────────────────┐ │
│ │ ☑ [已完成] 买牛奶        9/20 05:10 清除 │ │
│ │ ☑ [未完成] 写周报 …      9/19 22:01 清除 │ │
│ │ …（清除时间倒序）                       │ │
│ └──────────────────────────────────────┘ │
│        [全选(n)] [全部不选]      [恢复] [取消] │
└──────────────────────────────────────────┘
```

- 时间范围 ComboBox：6 项（Day/Week/Month/Year/All/ExactDate），默认 **All**；选中 ExactDate 时 DatePicker.Visibility=Visible 且 SelectedDate 默认今天，否则 Collapsed。切换档位时重算列表（勾选状态重置为全选）。
- 搜索框：TextChanged 重算列表（勾选重置全选）。空白=不过滤。
- 列表：ListBox + ItemTemplate（CheckBox + 状态标签 + 文本(截断 80) + due + 清除时间）；全列表时禁用虚拟化无必要（量级小）。
- 底部左：「全选」「全部不选」两个小按钮 + 当前勾选数；底部右：「恢复」「取消」。
- 「恢复」Enabled：勾选数 > 0。
- Esc=取消，Enter=恢复（窗口 KeyDown 处理）。
- 清除时间显示：`Strings.Date(language, clearedAt.Date)` + " " + HH:mm。
- due 显示：DueLogic.Parse 后 FormatDueLabel 风格（无 due 则不显示）。
- 状态标签颜色：已完成=DoneGray，未完成=Accent。
- **恢复执行**（点「恢复」或 Enter）：
  - 对每个勾选条目：若 id 已在 Tasks（竞态兜底）跳过，否则 `Tasks.Add(new TaskItem(this, new TodoRecord { Id, Text, DueAt=due, IsCompleted=done }))`；
  - `TaskChanged()` 一次；`SetToast(Strings.T(_language, "toast.restoreDone", restoredCount, skippedCount))`；
  - 关窗。
- 窗口文案全部走 Strings（键见 §5），三语即时正确。
- 复用既有 `Truncate` helper。

### 3.4 Undo toast 的提示语补充

`toast.cleared`（"已清除 N 件已完成任务"）保持不变；无需改，因为新入口在托盘。原 10s 撤销机制不动。

## 4. 彆 i18n（Localization.cs）

三语各加（zh-Hans 示例，zh-Hant/en 同步翻译）：

```
tray.restore            恢复待办
restore.title           恢复待办
restore.range           时间范围
restore.range.day       最近一天
restore.range.week      最近一周
restore.range.month     最近一个月
restore.range.year      最近一年
restore.range.all       全部
restore.range.exact     精确日期
restore.search          搜索任务内容…
restore.selectAll       全选
restore.selectNone      全部不选
restore.restore         恢复
restore.cancel          取消
restore.empty           当前筛选条件下没有可恢复的任务
restore.state.done      已完成
restore.state.open      未完成
restore.clearedAt       清除于 {0}
restore.count           已选 {0} 件
restore.window.emptyToast  归档中没有可恢复的任务
toast.restoreDone       已恢复 {0} 件任务（跳过 {1} 件已在列表中的任务）
toast.restoreArchiveFailed 归档写入失败，本次清除不会进入恢复列表
restore.due.none        （无，due 为空则整段不显示）
```

`restore.count`（"已选 N 件"）随勾选实时更新。`toast.restoreDone` 两个数字都为 0 时也应能读到（实际不会出现：勾选>0 才能点恢复）。

## 5. 测试（StorageTests.cs / UiTests.cs）

### 5.1 console（StorageTests）

新增：
- `ClearedAppendAndRead`：追加 3 条（含多行文本、unicode、due、done）读回字段全等；原始文件 LF 检查。
- `ClearedCorruptLine`：手动 AppendAllText 一行非 JSON → ReadAll 跳过且不产生 .bad 文件。
- `RestoreLogicFunctions`：InWindow 边界（恰 24h/7d/30d/365d 前清除 → true；多 1 秒 → false）；ExactMatch（当天内/前一天/次日凌晨 → true/false/false）；Restorable（同 id 双条取最新；排除 currentTaskIds；倒序）；TextMatches（大小写、子串、null/空关键字）。
- `RestoreConcurrency`（可选）：双线程各 Append 50 条 → ReadAll 100 条。

### 5.2 UI（UiTests）

新增 `RunRestoreScenarios(directory)`：
1. 开窗、添加 3 任务（1 完成 2 未完成）→ Delete 一个未完成 + ClearCompleted → cleared.jsonl 存在且 2 行；
2. 菜单计数：`controller` internal 暴露 `RestorePreviewCount`（SyncTrayRestoreState 用同一方法）→ 2；
3. 恢复全部 → Tasks.Count 回到 3、完成态保持、cleared.jsonl 仍 2 行（append-only 断言）；
4. 再开菜单 → 计数 0、菜单禁用；
5. 桌面层面再删一次同文本任务 → 计数 1（新 id）。

依赖：`_trayMenu` 需 internal 可见或经 controller 方法暴露（UiTests 与主程序同 assembly？**不是**——UiTests 单独编译，需在 TodoController 上加 internal + InternalsVisibleTo 或改 public。看现有 UiTests 访问 controller.Tasks/AddCommand 均为 public。新增 `public int RestorePreviewCount { get; }` + `public void OpenRestoreWindowForTest()`（内部调 OpenRestoreWindow，返回弹出的 Window 或 null 供测试定位控件）。

**注意**：`restore.window.emptyToast` 分支：归档空时 OpenRestoreWindow 直接 toast，不弹窗——测试 4 之后再删一个 → 计数 1；恢复后清空勾选恢复 0 件 → 不可能（按钮禁用）。另有：预览窗打开期间主窗再删任务 → 关窗后计数变化（不在 v1 测试范围）。

## 6. 构建与验证

- `build.ps1` 无新文件（ClearedLog 进 Storage.cs，RestoreLogic 进 Logic.cs，UI 进 App.cs）——零构建脚本改动。
- 验证序列：`build.ps1` → `tests\run-storage-tests.ps1` → `tests\run-ui-tests.ps1`，三者 exit 0。
- 先编到 `test-build/` 跑测试，最后再停实例替换 dist（沿用既有流程）。

## 7. README 更新

- 「删除任务」小节加一句：托盘右键菜单「恢复待办」可按时间范围/精确日期/关键词找回已删除/清除的任务（默认恢复为清除时的状态）。
- 「数据与备份」表格加一行 `cleared.jsonl`：清除/删除任务的归档（append-only），恢复功能数据源；**注意任务文本会保留在此文件中**，不需要时可手动删除整个文件。

## 8. 里程碑与验收

| # | 里程碑 | 验收 |
| - | ------ | ---- |
| M1 | ClearedLog + RestoreLogic + console 测试 | run-storage-tests PASS |
| M2 | 写入点 + 托盘菜单 + 计数 | UI 测试 1/2/4/5 |
| M3 | 预览窗全链路 | UI 测试 3 + 手工冒烟 |
| M4 | README + dist 替换 | 三脚本 exit 0 |
