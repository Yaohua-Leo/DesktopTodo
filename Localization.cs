using System;
using System.Collections.Generic;

namespace DesktopTodo
{
    // The three shipped UI languages. Stored values are the ones written to
    // tasks.json: "zh-Hans", "zh-Hant" and "en".
    public enum UiLanguage { ZhHans = 0, ZhHant = 1, English = 2 }

    // Every user-visible string in the application, as format templates so
    // ordering can follow the language (Chinese counts read "4 件到期", English
    // reads "4 due"). Placeholders are positional: {0}, {1}...
    public sealed class TextTable
    {
        public string LanguageName;
        // ICU culture used for weekday names and month names: "zh-CN", "zh-TW", "en-US".
        public string CultureName;
        public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);

        public TextTable(string languageName, string cultureName)
        {
            LanguageName = languageName;
            CultureName = cultureName;
        }
    }

    public static class Strings
    {
        private static readonly Dictionary<string, TextTable> Tables = BuildTables();

        private static readonly string[] LanguageCodes = { "zh-Hans", "zh-Hant", "en" };

        public static string CodeOf(UiLanguage language) { return LanguageCodes[(int)language]; }

        public static UiLanguage Parse(string stored)
        {
            if (!String.IsNullOrWhiteSpace(stored))
            {
                string value = stored.Trim();
                for (int i = 0; i < LanguageCodes.Length; i++)
                    if (String.Equals(LanguageCodes[i], value, StringComparison.OrdinalIgnoreCase)) return (UiLanguage)i;
            }
            return UiLanguage.ZhHans;
        }

        public static TextTable Table(UiLanguage language)
        {
            TextTable table;
            return Tables.TryGetValue(CodeOf(language), out table) ? table : Tables["zh-Hans"];
        }

        public static string Get(UiLanguage language, string key)
        {
            TextTable table = Table(language);
            string value;
            if (table.Values.TryGetValue(key, out value)) return value;
            // A missing key must never show a raw identifier; fall back to the
            // simplified table and, failing that, an empty string.
            return Tables["zh-Hans"].Values.TryGetValue(key, out value) ? value : "";
        }

        // Fills positional {0}, {1}... placeholders. Formatting is culture-invariant
        // on purpose: numbers stay in Latin digits and the arrows.
        public static string F(string template, params object[] args)
        {
            if (String.IsNullOrEmpty(template)) return template ?? "";
            if (args == null || args.Length == 0) return template;
            return String.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);
        }

        public static string T(string key, params object[] args)
        {
            return F(Get(UiLanguage.ZhHans, key), args);
        }

        public static string T(UiLanguage language, string key, params object[] args)
        {
            return F(Get(language, key), args);
        }

        // Localised "3 件待办" / "3 to-dos" style counting clause.
        public static string Count(UiLanguage language, int count)
        {
            return F(Get(language, "count"), count);
        }

        // Chinese counts pair the number with a classifier ("3 件"), English just
        // uses the plural noun; both live in the table so nothing is hard-coded.
        public static string CountOf(UiLanguage language, int count, string classifier)
        {
            return F(Get(language, "count." + classifier), count);
        }

        // Dates the human way: never a padded zero, never an ambiguous slash order.
        public static string Date(UiLanguage language, DateTime value)
        {
            return F(Get(language, "date.monthDay"), value.Month, value.Day);
        }

        public static string DateFull(UiLanguage language, DateTime value)
        {
            return F(Get(language, "date.full"), value.Year, value.Month, value.Day);
        }

        public static string Time(DateTime value)
        {
            return F(Get(UiLanguage.ZhHans, "clock.hour"), value.Hour);
        }

        // Weekday name for the calendar header. Index follows CalendarBuilder's
        // Monday-first layout: 0 = Monday .. 6 = Sunday.
        public static string WeekHead(UiLanguage language, int index)
        {
            return Get(language, "week." + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Full weekday name for a date label ("M月d日 · dddd").
        public static string WeekFull(UiLanguage language, DateTime value)
        {
            int index = ((int)value.DayOfWeek + 6) % 7; // Monday -> 0
            return Get(language, "week.full." + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Calendar bar title, e.g. "9月日历" / "Calendar 9".
        public static string CalendarBar(UiLanguage language, DateTime value)
        {
            return F(Get(language, "cal.bar"), value.Month);
        }

        // Wraps every occurrence of a phrase in U+2060 WORD JOINER pairs so a
        // tooltip never breaks a date or a holiday name across lines.
        public static string KeepTogether(string text, string phrase)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(phrase)) return text;
            return text.Replace(phrase, "\u2060" + phrase + "\u2060").Replace("\u2060\u2060", "\u2060");
        }

        private static Dictionary<string, TextTable> BuildTables()
        {
            Dictionary<string, TextTable> tables = new Dictionary<string, TextTable>(StringComparer.Ordinal);
            tables["zh-Hans"] = Simplified();
            tables["zh-Hant"] = Traditional();
            tables["en"] = English();
            return tables;
        }

        private static void Put(TextTable table, string key, string value) { table.Values[key] = value; }

        private static TextTable Simplified()
        {
            TextTable t = new TextTable("简体中文", "zh-CN");
            Put(t, "week.0", "一");
            Put(t, "week.1", "二");
            Put(t, "week.2", "三");
            Put(t, "week.3", "四");
            Put(t, "week.4", "五");
            Put(t, "week.5", "六");
            Put(t, "week.6", "日");
            Put(t, "week.full.0", "周一");
            Put(t, "week.full.1", "周二");
            Put(t, "week.full.2", "周三");
            Put(t, "week.full.3", "周四");
            Put(t, "week.full.4", "周五");
            Put(t, "week.full.5", "周六");
            Put(t, "week.full.6", "周日");
            Put(t, "toast.snap", "已吸附到{0}");
            Put(t, "toast.language", "界面语言已切换为{0}");
            Put(t, "toast.invertOn", "热力图已反色：完成越多颜色越浅");
            Put(t, "toast.invertOff", "热力图已恢复：完成越多颜色越深");
            Put(t, "toast.alreadyTop", "卡片已在屏幕顶部");
            Put(t, "tray.light", "浅色模式");
            Put(t, "tray.normal", "正常模式");
            Put(t, "tray.autostartFailed", "无法更新开机自启设置");
            Put(t, "about.body", "单文件桌面待办卡片。任务保存在：\n{0}\n\n提醒时间、开机自启和界面语言都可以在托盘右键菜单中调整。");
            Put(t, "about.close", "关闭");
            Put(t, "heading", "我的待办");
            Put(t, "subtitle", "一件一件，慢慢完成");
            Put(t, "pin.label", "固定到桌面");
            Put(t, "date.line", "{0} · {1}");
            Put(t, "cal.monthTitle", "{0}年{1}月");
            Put(t, "window.minimize", "最小化");
            Put(t, "window.close", "收进托盘（托盘菜单可退出）");
            Put(t, "window.resize", "拖动调整卡片大小");
            Put(t, "window.drag", "按住这里拖动卡片");
            Put(t, "count", "{0} 件");
            Put(t, "count.due", "{0} 件到期");
            Put(t, "count.done", "完成 {0} 件");
            Put(t, "count.overdue", "{0} 件已过期");
            Put(t, "count.open", "{0} 件待办");
            Put(t, "count.task", "{0} 件");
            Put(t, "date.monthDay", "{0}月{1}日");
            Put(t, "date.full", "{0}年{1}月{2}日");
            Put(t, "card.summary.new", "新的开始");
            Put(t, "card.summary.todo", "{0} 件待办");
            Put(t, "card.summary.overdue", "{0} 件待办 · {1} 件过期");
            Put(t, "card.summary.done", "全部完成 ✓");
            Put(t, "card.summary.broken", "读取异常");
            Put(t, "card.progress", "已完成 {0} / {1}");
            Put(t, "card.progress.broken", "历史任务暂时无法读取");
            Put(t, "empty.title", "给今天一个小小的开始");
            Put(t, "empty.hint", "在下方写下你的第一件待办");
            Put(t, "input.add", "添加一件待办…");
            Put(t, "input.edit", "修改任务内容…");
            Put(t, "input.addHint", "添加任务（Enter）");
            Put(t, "input.editHint", "保存修改（Enter），Esc 取消");
            Put(t, "toast.undo", "撤销");
            Put(t, "footer.clearCompleted", "清除已完成");
            Put(t, "footer.reload", "重新读取");
            Put(t, "footer.saving", "已读取 {0} 条 · 自动保存");
            Put(t, "footer.editing", "正在编辑 · Enter 保存 · Esc 取消");
            Put(t, "footer.saveFailed", "保存失败，修改仍保留在当前窗口");
            Put(t, "footer.recovered", "已从备份恢复 · 原文件已保留");
            Put(t, "footer.blocked", "读取异常，已暂停保存 · 请点击重新读取");
            Put(t, "cal.title", "展开 / 折叠日历");
            Put(t, "cal.bar", "{0}月日历");
            Put(t, "cal.barTooltip", "展开 / 折叠日历");
            Put(t, "cal.prev", "上个月");
            Put(t, "cal.next", "下个月");
            Put(t, "cal.mode.toHeat", "完成热力图");
            Put(t, "cal.mode.toDue", "看截止时间");
            Put(t, "cal.mode.toggle", "在截止圆点与完成热力图之间切换");
            Put(t, "cal.invert.on", "反色：已开");
            Put(t, "cal.invert.off", "反色");
            Put(t, "cal.invert.tooltip", "切换热力图色阶方向：完成越多颜色越深 / 越浅");
            Put(t, "cal.day.title", "{0}月{1}日");
            Put(t, "cal.day.due", "{0} 件到期");
            Put(t, "cal.day.noDue", "无到期任务");
            Put(t, "cal.day.due.none", "{0} · 无到期任务");
            Put(t, "cal.tip.monthDay", "{0}月{1}日");
            Put(t, "cal.tip.dues", "{0} 件到期");
            Put(t, "cal.tip.noDue", "{0} · 没有截止任务");
            Put(t, "cal.tip.holiday", "{0} · {1}");
            Put(t, "cal.tip.done", "{0} · 完成 {1} 件");
            Put(t, "cal.tip.noDone", "{0} · 没有完成记录");
            Put(t, "cal.tip.more", "等 {0} 件");
            Put(t, "cal.tip.future", "未来");
            Put(t, "cal.tip.custom", "补记 {0}");
            Put(t, "cal.tip.total", "共 {0} 件");
            Put(t, "task.due", "设置截止时间");
            Put(t, "task.delete", "删除任务");
            Put(t, "task.complete", "完成任务");
            Put(t, "task.restore", "恢复待办");
            Put(t, "task.doneToggle", "完成 / 恢复待办");
            Put(t, "task.text.tip", "双击编辑；右键可编辑或删除");
            Put(t, "task.menu.edit", "编辑任务");
            Put(t, "task.menu.due", "设置截止时间");
            Put(t, "task.menu.delete", "删除任务");
            Put(t, "picker.title", "截止时间：{0}");
            Put(t, "picker.flowPicked", "已选 {0}，再选小时");
            Put(t, "picker.flowCurrent", "当前：{0}");
            Put(t, "picker.flowEmpty", "未设置截止时间，先选日期");
            Put(t, "picker.clear", "清除截止时间");
            Put(t, "picker.date", "{0}月{1}日");
            Put(t, "picker.dateFull", "{0}年{1}月{2}日");
            Put(t, "due.today", "今天 {0}");
            Put(t, "due.tomorrow", "明天 {0}");
            Put(t, "due.monthDay", "{0}月{1}日 {2}");
            Put(t, "due.fullDate", "{0}年{1}月{2}日 {3}");
            Put(t, "due.overdue", "已过期");
            Put(t, "due.overduePrefix", "已过期 · {0}");
            Put(t, "clock.hour", "{0}:00");
            Put(t, "clock.hourPadded", "{0:D2}:00");
            Put(t, "clock.afterMidnight", "次日 {0}");
            Put(t, "due.pickerCleared", "已清除截止时间");
            Put(t, "toast.added", "已添加 1 件待办");
            Put(t, "toast.deleted", "已删除 1 件任务");
            Put(t, "toast.cleared", "已清除 {0} 件已完成任务");
            Put(t, "toast.restored", "已恢复任务");
            Put(t, "toast.editPlaceholder", "修改任务内容后按 Enter 保存");
            Put(t, "toast.dueSet", "已设置 {0}");
            Put(t, "toast.dueUpdated", "已更新 {0}");
            Put(t, "toast.defaultDone", "已完成");
            Put(t, "toast.reread", "已重新读取 {0} 条任务");
            Put(t, "toast.loadFailed", "读取失败，已停止保存以保护历史任务");
            Put(t, "toast.completionNotSaved", "完成记录未能写入，热力图可能缺少这一次");
            Put(t, "toast.startupFailed", "无法更新开机自启设置");
            Put(t, "toast.undoExpired", "撤销时间已过");
            Put(t, "toast.calendarAdded", "{0}有 1 件待办到期");
            Put(t, "toast.calendarFreed", "{0}已无到期任务");
            Put(t, "toast.noOverdue", "当前没有过期任务");
            Put(t, "toast.createFailed", "创建文件失败");
            Put(t, "quick.tip", "今天到期 {0} · 过期 {1} · 明日 {2}");
            Put(t, "quick.none", "暂无到期任务");
            Put(t, "quick.header", "快速预览");
            Put(t, "reminder.morning.title", "今日待办提醒");
            Put(t, "reminder.morning.head", "今天有 {0} 件待办");
            Put(t, "reminder.morning.none", "今天没有到期的待办");
            Put(t, "reminder.evening.title", "明日待办提醒");
            Put(t, "reminder.evening.head", "明天有 {0} 件待办到期");
            Put(t, "reminder.evening.none", "明天没有待办到期");
            Put(t, "reminder.overdue", "已过期未完成");
            Put(t, "reminder.overdueTail", "另有 {0} 件已过期");
            Put(t, "reminder.show", "查看卡片");
            Put(t, "reminder.ok", "知道了");
            Put(t, "reminder.about", "关于");
            Put(t, "reminder.aboutTitle", "关于桌面待办");
            Put(t, "reminder.aboutHead", "{0} · 版本 {1}");
            Put(t, "reminder.dupTip", "同一时间只显示一个提醒窗口");
            Put(t, "restore.title", "恢复待办");
            Put(t, "restore.range.day", "最近一天");
            Put(t, "restore.range.week", "最近一周");
            Put(t, "restore.range.month", "最近一个月");
            Put(t, "restore.range.year", "最近一年");
            Put(t, "restore.range.all", "全部");
            Put(t, "restore.range.exact", "精确日期");
            Put(t, "restore.search", "搜索任务内容…");
            Put(t, "restore.selectAll", "全选");
            Put(t, "restore.selectNone", "全部不选");
            Put(t, "restore.restore", "恢复");
            Put(t, "restore.cancel", "取消");
            Put(t, "restore.empty", "当前筛选条件下没有可恢复的任务");
            Put(t, "restore.state.done", "已完成");
            Put(t, "restore.state.open", "未完成");
            Put(t, "restore.clearedAt", "清除于 {0}");
            Put(t, "restore.count", "已选 {0} 件");
            Put(t, "restore.window.emptyToast", "归档中没有可恢复的任务");
            Put(t, "toast.restoreDone", "已恢复 {0} 件任务（跳过 {1} 件已在列表中的任务）");
            Put(t, "toast.restoreArchiveFailed", "归档写入失败，本次清除不会进入恢复列表");
            Put(t, "tray.show", "显示卡片");
            Put(t, "tray.restore", "恢复待办");
            Put(t, "tray.autostart", "开机自启");
            Put(t, "tray.reminder", "提醒时间");
            Put(t, "tray.morning", "早上");
            Put(t, "tray.evening", "下午");
            Put(t, "tray.language", "语言 / Language");
            Put(t, "tray.exit", "退出");
            Put(t, "tray.hintTitle", "桌面待办");
            Put(t, "tray.hintText", "程序仍在托盘运行，单击图标可唤回卡片。");
            Put(t, "tip.pin.on", "已固定：保持在其他窗口上方。点击取消置顶。");
            Put(t, "tip.pin.off", "点击后保持在其他窗口上方。");
            Put(t, "hook.shortcut", "按住此区域拖拽，将当前卡片吸附到屏幕边缘");
            Put(t, "hook.edgeLeft", "左边缘");
            Put(t, "hook.edgeRight", "右边缘");
            Put(t, "hook.edgeTop", "上边缘");
            Put(t, "hook.edgeBottom", "下边缘");
            Put(t, "data.location", "任务目录：{0}");
            Put(t, "data.backup", "任务变更会额外保留历史备份。");
            Put(t, "msgbox.title", "桌面待办");
            Put(t, "msgbox.noDir", "无法确定待办数据目录，已停止启动以保护历史任务。");
            Put(t, "msgbox.noStart", "桌面待办未能启动：");
            Put(t, "msgbox.unsaved", "本次更改尚未保存。继续关闭会丢失这些更改。\n\n是否仍然关闭？");
            Put(t, "msgbox.unsavedTitle", "保存未完成");
            Put(t, "msgbox.badArg", "启动参数无效。");
            Put(t, "msgbox.noDataDir", "本地应用数据目录不可用，请检查当前 Windows 用户配置。");
            Put(t, "label.sep", "：");
            Put(t, "msgbox.viewTheme", "查看主题");
            Put(t, "hook.done", "已吸附到{0}");
            Put(t, "holiday.label", "公众假期");
            Put(t, "holiday.weekend", "周末");
            // The heart day is a keepsake, not a translated holiday - the note is
            // deliberately the same sentence in every language.
            Put(t, "anniversary.glyph", "♥");
            Put(t, "anniversary.tip", "I will love you forever");
            Put(t, "theme.title", "主题");
            Put(t, "theme.sage", "鼠尾草");
            Put(t, "theme.graphite", "石墨");
            Put(t, "theme.midnight", "午夜");
            Put(t, "theme.catppuccin", "猫布奇诺");
            Put(t, "theme.liquidglass", "液态玻璃");
            Put(t, "theme.hint", "切换主题");
            Put(t, "theme.mode.follow", "跟随系统");
            Put(t, "theme.mode.light", "浅色");
            Put(t, "theme.mode.dark", "深色");
            Put(t, "tray.theme", "主题");
            Put(t, "tray.themeMode", "深浅外观");
            Put(t, "settings.languageHint", "切换后立即生效。");
            return t;
        }

        private static TextTable Traditional()
        {
            TextTable t = new TextTable("繁體中文", "zh-TW");
            Put(t, "week.0", "一");
            Put(t, "week.1", "二");
            Put(t, "week.2", "三");
            Put(t, "week.3", "四");
            Put(t, "week.4", "五");
            Put(t, "week.5", "六");
            Put(t, "week.6", "日");
            Put(t, "week.full.0", "週一");
            Put(t, "week.full.1", "週二");
            Put(t, "week.full.2", "週三");
            Put(t, "week.full.3", "週四");
            Put(t, "week.full.4", "週五");
            Put(t, "week.full.5", "週六");
            Put(t, "week.full.6", "週日");
            Put(t, "toast.snap", "已吸附到{0}");
            Put(t, "toast.language", "介面語言已切換為{0}");
            Put(t, "toast.invertOn", "熱力圖已反色：完成越多顏色越淺");
            Put(t, "toast.invertOff", "熱力圖已還原：完成越多顏色越深");
            Put(t, "toast.alreadyTop", "卡片已在螢幕頂端");
            Put(t, "tray.light", "淺色模式");
            Put(t, "tray.normal", "正常模式");
            Put(t, "tray.autostartFailed", "無法更新開機自啟設定");
            Put(t, "about.body", "單檔桌面待辦卡片。任務儲存在：\n{0}\n\n提醒時間、開機自啟和介面語言都可以在系統匣右鍵選單中調整。");
            Put(t, "about.close", "關閉");
            Put(t, "heading", "我的待辦");
            Put(t, "subtitle", "一件一件，慢慢完成");
            Put(t, "pin.label", "固定到桌面");
            Put(t, "date.line", "{0} · {1}");
            Put(t, "cal.monthTitle", "{0}年{1}月");
            Put(t, "window.minimize", "最小化");
            Put(t, "window.close", "收進系統匣（系統匣選單可結束）");
            Put(t, "window.resize", "拖曳調整卡片大小");
            Put(t, "window.drag", "按住這裡拖曳卡片");
            Put(t, "count", "{0} 件");
            Put(t, "count.due", "{0} 件到期");
            Put(t, "count.done", "完成 {0} 件");
            Put(t, "count.overdue", "{0} 件已過期");
            Put(t, "count.open", "{0} 件待辦");
            Put(t, "count.task", "{0} 件");
            Put(t, "date.monthDay", "{0}月{1}日");
            Put(t, "date.full", "{0}年{1}月{2}日");
            Put(t, "card.summary.new", "新的開始");
            Put(t, "card.summary.todo", "{0} 件待辦");
            Put(t, "card.summary.overdue", "{0} 件待辦 · {1} 件過期");
            Put(t, "card.summary.done", "全部完成 ✓");
            Put(t, "card.summary.broken", "讀取異常");
            Put(t, "card.progress", "已完成 {0} / {1}");
            Put(t, "card.progress.broken", "歷史任務暫時無法讀取");
            Put(t, "empty.title", "給今天一個小小的開始");
            Put(t, "empty.hint", "在下方寫下你的第一件待辦");
            Put(t, "input.add", "新增一件待辦…");
            Put(t, "input.edit", "修改任務內容…");
            Put(t, "input.addHint", "新增任務（Enter）");
            Put(t, "input.editHint", "儲存修改（Enter），Esc 取消");
            Put(t, "toast.undo", "復原");
            Put(t, "footer.clearCompleted", "清除已完成");
            Put(t, "footer.reload", "重新讀取");
            Put(t, "footer.saving", "已讀取 {0} 條 · 自動儲存");
            Put(t, "footer.editing", "正在編輯 · Enter 儲存 · Esc 取消");
            Put(t, "footer.saveFailed", "儲存失敗，修改仍保留在目前的視窗");
            Put(t, "footer.recovered", "已從備份還原 · 原始檔案已保留");
            Put(t, "footer.blocked", "讀取異常，已暫停儲存 · 請點選重新讀取");
            Put(t, "cal.title", "展開 / 收合日曆");
            Put(t, "cal.bar", "{0}月日曆");
            Put(t, "cal.barTooltip", "展開 / 收合日曆");
            Put(t, "cal.prev", "上個月");
            Put(t, "cal.next", "下個月");
            Put(t, "cal.mode.toHeat", "完成熱力圖");
            Put(t, "cal.mode.toDue", "看截止時間");
            Put(t, "cal.mode.toggle", "在截止圓點與完成熱力圖之間切換");
            Put(t, "cal.invert.on", "反色：已開");
            Put(t, "cal.invert.off", "反色");
            Put(t, "cal.invert.tooltip", "切換熱力圖色階方向：完成越多顏色越深 / 越淺");
            Put(t, "cal.day.title", "{0}月{1}日");
            Put(t, "cal.day.due", "{0} 件到期");
            Put(t, "cal.day.noDue", "無到期任務");
            Put(t, "cal.day.due.none", "{0} · 無到期任務");
            Put(t, "cal.tip.monthDay", "{0}月{1}日");
            Put(t, "cal.tip.dues", "{0} 件到期");
            Put(t, "cal.tip.noDue", "{0} · 沒有截止任務");
            Put(t, "cal.tip.holiday", "{0} · {1}");
            Put(t, "cal.tip.done", "{0} · 完成 {1} 件");
            Put(t, "cal.tip.noDone", "{0} · 沒有完成記錄");
            Put(t, "cal.tip.more", "等 {0} 件");
            Put(t, "cal.tip.future", "未來");
            Put(t, "cal.tip.custom", "補記 {0}");
            Put(t, "cal.tip.total", "共 {0} 件");
            Put(t, "task.due", "設定截止時間");
            Put(t, "task.delete", "刪除任務");
            Put(t, "task.complete", "完成任務");
            Put(t, "task.restore", "還原待辦");
            Put(t, "task.doneToggle", "完成 / 還原待辦");
            Put(t, "task.text.tip", "按兩下編輯；右鍵可編輯或刪除");
            Put(t, "task.menu.edit", "編輯任務");
            Put(t, "task.menu.due", "設定截止時間");
            Put(t, "task.menu.delete", "刪除任務");
            Put(t, "picker.title", "截止時間：{0}");
            Put(t, "picker.flowPicked", "已選 {0}，再選小時");
            Put(t, "picker.flowCurrent", "目前：{0}");
            Put(t, "picker.flowEmpty", "未設定截止時間，先選日期");
            Put(t, "picker.clear", "清除截止時間");
            Put(t, "picker.date", "{0}月{1}日");
            Put(t, "picker.dateFull", "{0}年{1}月{2}日");
            Put(t, "due.today", "今天 {0}");
            Put(t, "due.tomorrow", "明天 {0}");
            Put(t, "due.monthDay", "{0}月{1}日 {2}");
            Put(t, "due.fullDate", "{0}年{1}月{2}日 {3}");
            Put(t, "due.overdue", "已過期");
            Put(t, "due.overduePrefix", "已過期 · {0}");
            Put(t, "clock.hour", "{0}:00");
            Put(t, "clock.hourPadded", "{0:D2}:00");
            Put(t, "clock.afterMidnight", "次日 {0}");
            Put(t, "due.pickerCleared", "已清除截止時間");
            Put(t, "toast.added", "已新增 1 件待辦");
            Put(t, "toast.deleted", "已刪除 1 件任務");
            Put(t, "toast.cleared", "已清除 {0} 件已完成任務");
            Put(t, "toast.restored", "已還原任務");
            Put(t, "toast.editPlaceholder", "修改任務內容後按 Enter 儲存");
            Put(t, "toast.dueSet", "已設定 {0}");
            Put(t, "toast.dueUpdated", "已更新 {0}");
            Put(t, "toast.defaultDone", "已完成");
            Put(t, "toast.reread", "已重新讀取 {0} 條任務");
            Put(t, "toast.loadFailed", "讀取失敗，已停止儲存以保護歷史任務");
            Put(t, "toast.completionNotSaved", "完成記錄未能寫入，熱力圖可能缺少這一次");
            Put(t, "toast.startupFailed", "無法更新開機自啟設定");
            Put(t, "toast.undoExpired", "復原時間已過");
            Put(t, "toast.calendarAdded", "{0}有 1 件待辦到期");
            Put(t, "toast.calendarFreed", "{0}已無到期任務");
            Put(t, "toast.noOverdue", "目前沒有過期任務");
            Put(t, "toast.createFailed", "建立檔案失敗");
            Put(t, "quick.tip", "今天到期 {0} · 過期 {1} · 明日 {2}");
            Put(t, "quick.none", "暫無到期任務");
            Put(t, "quick.header", "快速預覽");
            Put(t, "reminder.morning.title", "今日待辦提醒");
            Put(t, "reminder.morning.head", "今天有 {0} 件待辦");
            Put(t, "reminder.morning.none", "今天沒有到期的待辦");
            Put(t, "reminder.evening.title", "明日待辦提醒");
            Put(t, "reminder.evening.head", "明天有 {0} 件待辦到期");
            Put(t, "reminder.evening.none", "明天沒有待辦到期");
            Put(t, "reminder.overdue", "已過期未完成");
            Put(t, "reminder.overdueTail", "另有 {0} 件已過期");
            Put(t, "reminder.show", "檢視卡片");
            Put(t, "reminder.ok", "知道了");
            Put(t, "reminder.about", "關於");
            Put(t, "reminder.aboutTitle", "關於桌面待辦");
            Put(t, "reminder.aboutHead", "{0} · 版本 {1}");
            Put(t, "reminder.dupTip", "同一時間只顯示一個提醒視窗");
            Put(t, "restore.title", "恢復待辦");
            Put(t, "restore.range.day", "最近一天");
            Put(t, "restore.range.week", "最近一週");
            Put(t, "restore.range.month", "最近一個月");
            Put(t, "restore.range.year", "最近一年");
            Put(t, "restore.range.all", "全部");
            Put(t, "restore.range.exact", "精確日期");
            Put(t, "restore.search", "搜尋任務內容…");
            Put(t, "restore.selectAll", "全選");
            Put(t, "restore.selectNone", "全部不選");
            Put(t, "restore.restore", "恢復");
            Put(t, "restore.cancel", "取消");
            Put(t, "restore.empty", "目前篩選條件下沒有可恢復的任務");
            Put(t, "restore.state.done", "已完成");
            Put(t, "restore.state.open", "未完成");
            Put(t, "restore.clearedAt", "清除於 {0}");
            Put(t, "restore.count", "已選 {0} 件");
            Put(t, "restore.window.emptyToast", "封存中沒有可恢復的任務");
            Put(t, "toast.restoreDone", "已恢復 {0} 件任務（略過 {1} 件已在清單中的任務）");
            Put(t, "toast.restoreArchiveFailed", "封存寫入失敗，本次清除不會進入恢復清單");
            Put(t, "tray.show", "顯示卡片");
            Put(t, "tray.restore", "恢復待辦");
            Put(t, "tray.autostart", "開機自啟");
            Put(t, "tray.reminder", "提醒時間");
            Put(t, "tray.morning", "早上");
            Put(t, "tray.evening", "下午");
            Put(t, "tray.language", "語言 / Language");
            Put(t, "tray.exit", "結束");
            Put(t, "tray.hintTitle", "桌面待辦");
            Put(t, "tray.hintText", "程式仍在系統匣執行，點選圖示可喚回卡片。");
            Put(t, "tip.pin.on", "已固定：保持在其他視窗上方。點選取消置頂。");
            Put(t, "tip.pin.off", "點選後保持在其他視窗上方。");
            Put(t, "hook.shortcut", "按住此區域拖曳，將目前卡片吸附到螢幕邊緣");
            Put(t, "hook.edgeLeft", "左邊緣");
            Put(t, "hook.edgeRight", "右邊緣");
            Put(t, "hook.edgeTop", "上邊緣");
            Put(t, "hook.edgeBottom", "下邊緣");
            Put(t, "data.location", "任務目錄：{0}");
            Put(t, "data.backup", "任務變更會額外保留歷史備份。");
            Put(t, "msgbox.title", "桌面待辦");
            Put(t, "msgbox.noDir", "無法確定待辦資料目錄，已停止啟動以保護歷史任務。");
            Put(t, "msgbox.noStart", "桌面待辦未能啟動：");
            Put(t, "msgbox.unsaved", "本次變更尚未儲存。繼續關閉會遺失這些變更。\n\n是否仍然關閉？");
            Put(t, "msgbox.unsavedTitle", "儲存未完成");
            Put(t, "msgbox.badArg", "啟動參數無效。");
            Put(t, "msgbox.noDataDir", "本機應用程式資料目錄無法使用，請檢查目前的 Windows 使用者設定。");
            Put(t, "label.sep", "：");
            Put(t, "msgbox.viewTheme", "檢視主題");
            Put(t, "hook.done", "已吸附到{0}");
            Put(t, "holiday.label", "公眾假期");
            Put(t, "holiday.weekend", "週末");
            Put(t, "anniversary.glyph", "♥");
            Put(t, "anniversary.tip", "I will love you forever");
            Put(t, "theme.title", "主題");
            Put(t, "theme.sage", "鼠尾草");
            Put(t, "theme.graphite", "石墨");
            Put(t, "theme.midnight", "午夜");
            Put(t, "theme.catppuccin", "貓布奇諾");
            Put(t, "theme.liquidglass", "液態玻璃");
            Put(t, "theme.hint", "切換主題");
            Put(t, "theme.mode.follow", "跟隨系統");
            Put(t, "theme.mode.light", "淺色");
            Put(t, "theme.mode.dark", "深色");
            Put(t, "tray.theme", "主題");
            Put(t, "tray.themeMode", "深淺外觀");
            Put(t, "settings.languageHint", "切換後立即生效。");
            return t;
        }

        private static TextTable English()
        {
            TextTable t = new TextTable("English", "en-US");
            Put(t, "week.0", "M");
            Put(t, "week.1", "T");
            Put(t, "week.2", "W");
            Put(t, "week.3", "T");
            Put(t, "week.4", "F");
            Put(t, "week.5", "S");
            Put(t, "week.6", "S");
            Put(t, "week.full.0", "Monday");
            Put(t, "week.full.1", "Tuesday");
            Put(t, "week.full.2", "Wednesday");
            Put(t, "week.full.3", "Thursday");
            Put(t, "week.full.4", "Friday");
            Put(t, "week.full.5", "Saturday");
            Put(t, "week.full.6", "Sunday");
            Put(t, "toast.snap", "Snapped to {0}");
            Put(t, "toast.language", "Interface language switched to {0}");
            Put(t, "toast.invertOn", "Heatmap inverted: more completions are paler");
            Put(t, "toast.invertOff", "Heatmap restored: more completions are darker");
            Put(t, "toast.alreadyTop", "The card is already at the top of the screen");
            Put(t, "tray.light", "Light mode");
            Put(t, "tray.normal", "Normal mode");
            Put(t, "tray.autostartFailed", "Could not update the start-at-login setting");
            Put(t, "about.body", "A single-file desktop to-do card. Tasks are stored in:/n{0}/n/nReminder times, start-at-login and the interface language are all in the tray menu.");
            Put(t, "about.close", "Close");
            Put(t, "heading", "My to-dos");
            Put(t, "subtitle", "One at a time");
            Put(t, "pin.label", "Pin to desktop");
            Put(t, "date.line", "{0} · {1}");
            Put(t, "cal.monthTitle", "{0}-{1}");
            Put(t, "window.minimize", "Minimise");
            Put(t, "window.close", "Hide to tray (exit from the tray menu)");
            Put(t, "window.resize", "Drag to resize the card");
            Put(t, "window.drag", "Drag here to move the card");
            Put(t, "count", "{0}");
            Put(t, "count.due", "{0} due");
            Put(t, "count.done", "{0} done");
            Put(t, "count.overdue", "{0} overdue");
            Put(t, "count.open", "{0} to-dos");
            Put(t, "count.task", "{0}");
            Put(t, "date.monthDay", "{0}/{1}");
            Put(t, "date.full", "{0}/{1}/{2}");
            Put(t, "card.summary.new", "Fresh start");
            Put(t, "card.summary.todo", "{0} to-dos");
            Put(t, "card.summary.overdue", "{0} to-dos · {1} overdue");
            Put(t, "card.summary.done", "All done ✓");
            Put(t, "card.summary.broken", "Read error");
            Put(t, "card.progress", "{0} of {1} done");
            Put(t, "card.progress.broken", "History is temporarily unreadable");
            Put(t, "empty.title", "Start with one small thing");
            Put(t, "empty.hint", "Write your first to-do below");
            Put(t, "input.add", "Add a to-do…");
            Put(t, "input.edit", "Edit the task…");
            Put(t, "input.addHint", "Add task (Enter)");
            Put(t, "input.editHint", "Save (Enter), Esc to cancel");
            Put(t, "toast.undo", "Undo");
            Put(t, "footer.clearCompleted", "Clear completed");
            Put(t, "footer.reload", "Reload");
            Put(t, "footer.saving", "{0} loaded · autosaved");
            Put(t, "footer.editing", "Editing · Enter saves · Esc cancels");
            Put(t, "footer.saveFailed", "Save failed; changes stay in this window");
            Put(t, "footer.recovered", "Restored from backup · original kept");
            Put(t, "footer.blocked", "Read error; saving paused · click Reload");
            Put(t, "cal.title", "Expand / collapse calendar");
            Put(t, "cal.bar", "Calendar {0}");
            Put(t, "cal.barTooltip", "Expand / collapse calendar");
            Put(t, "cal.prev", "Previous month");
            Put(t, "cal.next", "Next month");
            Put(t, "cal.mode.toHeat", "Completion heatmap");
            Put(t, "cal.mode.toDue", "Show due dates");
            Put(t, "cal.mode.toggle", "Switch between due dots and the completion heatmap");
            Put(t, "cal.invert.on", "Invert: on");
            Put(t, "cal.invert.off", "Invert");
            Put(t, "cal.invert.tooltip", "Flip the heat scale: more completions paint darker, or lighter");
            Put(t, "cal.day.title", "{0}/{1}");
            Put(t, "cal.day.due", "{0} due");
            Put(t, "cal.day.noDue", "Nothing due");
            Put(t, "cal.day.due.none", "{0} · nothing due");
            Put(t, "cal.tip.monthDay", "{0}/{1}");
            Put(t, "cal.tip.dues", "{0} due");
            Put(t, "cal.tip.noDue", "{0} · nothing due");
            Put(t, "cal.tip.holiday", "{0} · {1}");
            Put(t, "cal.tip.done", "{0} · {1} done");
            Put(t, "cal.tip.noDone", "{0} · nothing completed");
            Put(t, "cal.tip.more", "+{0} more");
            Put(t, "cal.tip.future", "Upcoming");
            Put(t, "cal.tip.custom", "+{0} logged");
            Put(t, "cal.tip.total", "{0} total");
            Put(t, "task.due", "Set due date");
            Put(t, "task.delete", "Delete task");
            Put(t, "task.complete", "Complete task");
            Put(t, "task.restore", "Restore task");
            Put(t, "task.doneToggle", "Complete / restore task");
            Put(t, "task.text.tip", "Double-click to edit; right-click for more");
            Put(t, "task.menu.edit", "Edit task");
            Put(t, "task.menu.due", "Set due date");
            Put(t, "task.menu.delete", "Delete task");
            Put(t, "picker.title", "Due: {0}");
            Put(t, "picker.flowPicked", "{0} selected — now pick an hour");
            Put(t, "picker.flowCurrent", "Current: {0}");
            Put(t, "picker.flowEmpty", "No due date yet — pick a date first");
            Put(t, "picker.clear", "Clear due date");
            Put(t, "picker.date", "{0}/{1}");
            Put(t, "picker.dateFull", "{0}/{1}/{2}");
            Put(t, "due.today", "Today {0}");
            Put(t, "due.tomorrow", "Tomorrow {0}");
            Put(t, "due.monthDay", "{0}/{1} {2}");
            Put(t, "due.fullDate", "{0}/{1}/{2} {3}");
            Put(t, "due.overdue", "Overdue");
            Put(t, "due.overduePrefix", "Overdue · {0}");
            Put(t, "clock.hour", "{0}:00");
            Put(t, "clock.hourPadded", "{0:D2}:00");
            Put(t, "clock.afterMidnight", "Next day {0}");
            Put(t, "due.pickerCleared", "Due date cleared");
            Put(t, "toast.added", "Added 1 to-do");
            Put(t, "toast.deleted", "Deleted 1 task");
            Put(t, "toast.cleared", "Cleared {0} completed tasks");
            Put(t, "toast.restored", "Task restored");
            Put(t, "toast.editPlaceholder", "Press Enter to save your edit");
            Put(t, "toast.dueSet", "Set {0}");
            Put(t, "toast.dueUpdated", "Updated {0}");
            Put(t, "toast.defaultDone", "Done");
            Put(t, "toast.reread", "Reloaded {0} tasks");
            Put(t, "toast.loadFailed", "Could not read the file; saving paused to protect your history");
            Put(t, "toast.completionNotSaved", "The completion record could not be written; the heatmap may miss this one");
            Put(t, "toast.startupFailed", "Could not update the start-at-login setting");
            Put(t, "toast.undoExpired", "Undo is no longer available");
            Put(t, "toast.calendarAdded", "1 to-do due {0}");
            Put(t, "toast.calendarFreed", "Nothing due {0}");
            Put(t, "toast.noOverdue", "Nothing is overdue");
            Put(t, "toast.createFailed", "Could not create the file");
            Put(t, "quick.tip", "Today {0} · Overdue {1} · Tomorrow {2}");
            Put(t, "quick.none", "Nothing due");
            Put(t, "quick.header", "Quick preview");
            Put(t, "reminder.morning.title", "Today's to-dos");
            Put(t, "reminder.morning.head", "{0} to-dos due today");
            Put(t, "reminder.morning.none", "Nothing due today");
            Put(t, "reminder.evening.title", "Tomorrow's to-dos");
            Put(t, "reminder.evening.head", "{0} to-dos due tomorrow");
            Put(t, "reminder.evening.none", "Nothing due tomorrow");
            Put(t, "reminder.overdue", "Overdue");
            Put(t, "reminder.overdueTail", "+{0} more overdue");
            Put(t, "reminder.show", "Show card");
            Put(t, "reminder.ok", "Got it");
            Put(t, "reminder.about", "About");
            Put(t, "reminder.aboutTitle", "About Desktop To-do");
            Put(t, "reminder.aboutHead", "{0} · version {1}");
            Put(t, "reminder.dupTip", "Only one reminder window shows at a time");
            Put(t, "restore.title", "Restore to-dos");
            Put(t, "restore.range.day", "Last day");
            Put(t, "restore.range.week", "Last week");
            Put(t, "restore.range.month", "Last month");
            Put(t, "restore.range.year", "Last year");
            Put(t, "restore.range.all", "All");
            Put(t, "restore.range.exact", "Exact date");
            Put(t, "restore.search", "Search task text…");
            Put(t, "restore.selectAll", "Select all");
            Put(t, "restore.selectNone", "Select none");
            Put(t, "restore.restore", "Restore");
            Put(t, "restore.cancel", "Cancel");
            Put(t, "restore.empty", "Nothing restorable under the current filters");
            Put(t, "restore.state.done", "Done");
            Put(t, "restore.state.open", "Open");
            Put(t, "restore.clearedAt", "Cleared {0}");
            Put(t, "restore.count", "{0} selected");
            Put(t, "restore.window.emptyToast", "The archive has no restorable tasks");
            Put(t, "toast.restoreDone", "Restored {0} tasks (skipped {1} already in the list)");
            Put(t, "toast.restoreArchiveFailed", "Could not write the archive; this clearing cannot be restored");
            Put(t, "tray.show", "Show card");
            Put(t, "tray.restore", "Restore to-dos");
            Put(t, "tray.autostart", "Start at login");
            Put(t, "tray.reminder", "Reminder times");
            Put(t, "tray.morning", "Morning");
            Put(t, "tray.evening", "Afternoon");
            Put(t, "tray.language", "Language / 语言");
            Put(t, "tray.exit", "Exit");
            Put(t, "tray.hintTitle", "Desktop To-do");
            Put(t, "tray.hintText", "Still running in the tray; click the icon to bring the card back.");
            Put(t, "tip.pin.on", "Pinned on top. Click to unpin.");
            Put(t, "tip.pin.off", "Click to keep the card above other windows.");
            Put(t, "hook.shortcut", "Drag this area to snap the card to a screen edge");
            Put(t, "hook.edgeLeft", "the left edge");
            Put(t, "hook.edgeRight", "the right edge");
            Put(t, "hook.edgeTop", "the top edge");
            Put(t, "hook.edgeBottom", "the bottom edge");
            Put(t, "data.location", "Task folder: {0}");
            Put(t, "data.backup", "Every change also keeps a history snapshot.");
            Put(t, "msgbox.title", "Desktop To-do");
            Put(t, "msgbox.noDir", "The to-do data folder could not be resolved; startup stopped to protect your history.");
            Put(t, "msgbox.noStart", "Desktop To-do could not start:");
            Put(t, "msgbox.unsaved", "Your changes are not saved yet. Closing now loses them.\n\nClose anyway?");
            Put(t, "msgbox.unsavedTitle", "Save incomplete");
            Put(t, "msgbox.badArg", "Invalid startup arguments.");
            Put(t, "msgbox.noDataDir", "The local application data folder is unavailable; check the current Windows user profile.");
            Put(t, "label.sep", ": ");
            Put(t, "msgbox.viewTheme", "View theme");
            Put(t, "hook.done", "Snapped to {0}");
            Put(t, "holiday.label", "Public holiday");
            Put(t, "holiday.weekend", "Weekend");
            Put(t, "anniversary.glyph", "♥");
            Put(t, "anniversary.tip", "I will love you forever");
            Put(t, "theme.title", "Themes");
            Put(t, "theme.sage", "Sage");
            Put(t, "theme.graphite", "Graphite");
            Put(t, "theme.midnight", "Midnight");
            Put(t, "theme.catppuccin", "Catppuccin");
            Put(t, "theme.liquidglass", "Liquid Glass");
            Put(t, "theme.hint", "Switch theme");
            Put(t, "theme.mode.follow", "System");
            Put(t, "theme.mode.light", "Light");
            Put(t, "theme.mode.dark", "Dark");
            Put(t, "tray.theme", "Theme");
            Put(t, "tray.themeMode", "Appearance");
            Put(t, "settings.languageHint", "Takes effect immediately.");
            return t;
        }
    }
}
