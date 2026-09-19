using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DesktopTodo;

internal static class StorageTests
{
    private static int assertions;

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopTodoStorageTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RoundTrip(Path.Combine(root, "roundtrip"));
            RecoverBackup(Path.Combine(root, "recovery"));
            PreserveMalformed(Path.Combine(root, "malformed"));
            NormalizeInvalidState(Path.Combine(root, "normalize"));
            LoadMissingFields(Path.Combine(root, "missing-fields"));
            ProtectLockedFile(Path.Combine(root, "locked"));
            FailedSavePreservesPreviousState(Path.Combine(root, "failed-save"));
            SaveBeforeLoad(Path.Combine(root, "save-before-load"));
            StrictValidation(Path.Combine(root, "strict"));
            RejectStaleEmptyWriter(Path.Combine(root, "stale-empty"));
            ExternalEdits(Path.Combine(root, "external-edits"));
            GeometryKeepsTaskBackup(Path.Combine(root, "geometry"));
            RecoverHistory(Path.Combine(root, "history-recovery"));
            DeletionsRemainDeleted(Path.Combine(root, "deletions"));
            BriefLockRetries(Path.Combine(root, "transient-lock"));
            ConcurrentWriters(Path.Combine(root, "concurrent"));
            RapidHistoryOrdering(Path.Combine(root, "rapid-history"));
            RepeatedCorruptLoad(Path.Combine(root, "repeated-corrupt"));
            RetryAfterStartupLock(Path.Combine(root, "startup-retry"));
            FirstNoOpProtectsHistory(Path.Combine(root, "first-no-op"));
            NewFieldsRoundTrip(Path.Combine(root, "new-fields"));
            LegacyJsonNoNewFields(Path.Combine(root, "legacy-json"));
            InvalidDueNormalized(Path.Combine(root, "invalid-due"));
            ReminderSlotRepair(Path.Combine(root, "reminder-slot"));
            ReminderMigration(Path.Combine(root, "reminder-migration"));
            LanguageRoundTrip(Path.Combine(root, "language"));
            ThemeSettings(Path.Combine(root, "theme"));
            ThemePalettes();
            HolidayTable();
            StringsTable();
            CompletionAppendAndRead(Path.Combine(root, "completion-append"));
            CompletionRemoveMissing(Path.Combine(root, "completion-missing"));
            CompletionCorruptLine(Path.Combine(root, "completion-corrupt"));
            CompletionConcurrent(Path.Combine(root, "completion-concurrent"));
            ClearedAppendAndRead(Path.Combine(root, "cleared-append"));
            ClearedCorruptLine(Path.Combine(root, "cleared-corrupt"));
            ClearedConcurrent(Path.Combine(root, "cleared-concurrent"));
            RestoreLogicFunctions();
            LogicPureFunctions();
            Console.WriteLine("PASS: " + assertions + " assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
        finally
        {
            // This path is a unique directory created by this test process.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }

    private static void RoundTrip(string directory)
    {
        AppRepository repository = new AppRepository(directory);
        AppState initial = repository.Load();
        Assert(initial.Tasks.Count == 0 && initial.IsPinned, "new state defaults");
        Assert(Double.IsNaN(initial.Left) && Double.IsNaN(initial.Top), "new position defaults");
        Assert(repository.LoadWarning == null, "missing file is not a warning");
        Assert(repository.CanSave && !repository.HasExternalChanges, "brand-new store is writable and unchanged");
        Assert(repository.DataDirectory == Path.GetFullPath(directory), "data directory available to diagnostics");
        initial.Tasks.Add(new TodoRecord { Id = "first", Text = "买牛奶 🥛\n第二行 \"quotes\"", IsCompleted = true });
        initial.Left = -150;
        initial.Top = 48;
        initial.IsPinned = false;
        repository.Save(initial);
        AppState actual = new AppRepository(directory).Load();
        Assert(actual.Tasks.Count == 1 && actual.Tasks[0].Text == initial.Tasks[0].Text, "Unicode and newlines roundtrip");
        Assert(actual.Tasks[0].IsCompleted && actual.Tasks[0].Id == "first", "task attributes roundtrip");
        Assert(actual.Left == -150 && actual.Top == 48 && !actual.IsPinned, "window settings roundtrip");
        Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "temporary files cleaned");
        Assert(!File.ReadAllText(Path.Combine(directory, "tasks.json")).Contains("NaN"), "automatic position uses standard JSON");
    }

    private static void RecoverBackup(string directory)
    {
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        state.Tasks.Add(new TodoRecord { Id = "saved", Text = "保留的备份" });
        repository.Save(state);
        state.Tasks.Add(new TodoRecord { Id = "latest", Text = "较新的任务" });
        repository.Save(state);
        Assert(File.Exists(Path.Combine(directory, "tasks.backup.json")), "second save creates backup");
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "{broken", Encoding.UTF8);
        AppRepository recovered = new AppRepository(directory);
        AppState restored = recovered.Load();
        Assert(restored.Tasks.Count == 1 && restored.Tasks[0].Text == "保留的备份", "backup recovers last valid state");
        Assert(recovered.LoadWarning != null && recovered.LoadWarning.Contains("恢复"), "recovery warning");
        string[] preserved = Directory.GetFiles(directory, "tasks.bad-*.json");
        Assert(preserved.Length == 1 && File.ReadAllText(preserved[0], Encoding.UTF8) == "{broken", "damaged primary preserved verbatim");
        recovered.Save(restored);
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "broken again", Encoding.UTF8);
        Assert(new AppRepository(directory).Load().Tasks.Count == 1, "recovery save keeps known-good backup");
    }

    private static void PreserveMalformed(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "not JSON", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "tasks.backup.json"), "null", Encoding.UTF8);
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        Assert(state.Tasks.Count == 0 && repository.LoadWarning != null, "malformed files produce usable empty state and warning");
        Assert(Directory.GetFiles(directory, "*.bad-*.json").Length == 2, "both damaged files preserved");
        Assert(!repository.CanSave, "unrecoverable malformed data blocks saves");
        AssertSaveRefused(repository, state, "cannot overwrite malformed history with an empty list");
        Assert(File.ReadAllText(Path.Combine(directory, "tasks.json"), Encoding.UTF8) == "not JSON", "malformed primary remains intact");
    }

    private static void NormalizeInvalidState(string directory)
    {
        AppState state = new AppState();
        state.Width = -2;
        state.Height = Double.PositiveInfinity;
        state.Left = Double.PositiveInfinity;
        state.Tasks.Add(null);
        state.Tasks.Add(new TodoRecord { Id = "same", Text = " A " });
        state.Tasks.Add(new TodoRecord { Id = "same", Text = "B" });
        state.Tasks.Add(new TodoRecord { Id = " ", Text = null });
        new AppRepository(directory).Save(state);
        AppState actual = new AppRepository(directory).Load();
        Assert(actual.Tasks.Count == 3, "skip null records but retain all text records");
        Assert(actual.Tasks[0].Text == " A " && actual.Tasks[2].Text == String.Empty, "preserve text whitespace and normalize null text");
        HashSet<string> ids = new HashSet<string>();
        foreach (TodoRecord task in actual.Tasks)
            Assert(!String.IsNullOrWhiteSpace(task.Id) && ids.Add(task.Id), "task IDs are nonblank and unique");
        Assert(actual.Width == 392 && actual.Height == 580 && Double.IsNaN(actual.Left), "invalid geometry normalized");
        actual.Tasks = null;
        new AppRepository(directory).Save(actual);
        Assert(new AppRepository(directory).Load().Tasks.Count == 0, "null list normalized");
    }

    private static void LoadMissingFields(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "{\"Tasks\":[]}", Encoding.UTF8);
        AppState state = new AppRepository(directory).Load();
        Assert(state.Tasks.Count == 0 && state.IsPinned && state.Width == 392 && state.Height == 580, "missing fields retain defaults");
        Assert(Double.IsNaN(state.Left) && Double.IsNaN(state.Top), "missing position keeps automatic placement");
    }

    private static void ProtectLockedFile(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "tasks.json");
        File.WriteAllText(path, "protected bytes", Encoding.UTF8);
        using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AppRepository repository = new AppRepository(directory);
            AppState state = repository.Load();
            Assert(repository.LoadWarning != null, "unreadable file surfaces warning");
            bool refused = false;
            try { repository.Save(state); } catch (IOException) { refused = true; }
            Assert(refused, "save refuses when original cannot be preserved");
        }
        Assert(File.ReadAllText(path, Encoding.UTF8) == "protected bytes", "unreadable original never overwritten");
    }

    private static void SaveBeforeLoad(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "old damaged data", Encoding.UTF8);
        AssertSaveRefused(new AppRepository(directory), new AppState(), "save before load refuses damaged existing history");
        Assert(Directory.GetFiles(directory, "tasks.bad-*.json").Length == 1, "save-before-load protects existing damaged data");
    }

    private static void FailedSavePreservesPreviousState(string directory)
    {
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        state.Tasks.Add(new TodoRecord { Text = "已保存" });
        repository.Save(state);
        byte[] original = File.ReadAllBytes(Path.Combine(directory, "tasks.json"));
        state.Tasks.Add(new TodoRecord { Text = "无法保存" });
        using (FileStream locked = new FileStream(Path.Combine(directory, "tasks.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { repository.Save(state); } catch (IOException) { failed = true; }
            Assert(failed, "failed atomic replacement surfaces save error");
        }
        Assert(Convert.ToBase64String(original) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(directory, "tasks.json"))), "failed save preserves previous bytes");
        Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "failed save removes temporary file");
        Assert(new AppRepository(directory).Load().Tasks.Count == 1, "previous state remains readable after failed save");
    }

    private static void Assert(bool condition, string description)
    {
        assertions++;
        if (!condition) throw new Exception("FAIL: " + description);
    }

    private static void AssertSaveRefused(AppRepository repository, AppState state, string description)
    {
        bool refused = false;
        try { repository.Save(state); } catch (IOException) { refused = true; }
        Assert(refused, description);
    }

    private static AppState Seed(string directory, int count)
    {
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        for (int index = 0; index < count; index++)
            state.Tasks.Add(new TodoRecord { Id = "task-" + index, Text = "待办 " + index });
        repository.Save(state);
        return state;
    }

    private static void StrictValidation(string directory)
    {
        string[] invalid = { "{}", "{\"Tasks\":null}", "{\"Tasks\":{}}", "{\"Tasks\":\"\"}",
            "{\"Tasks\":[]", "{\"Tasks\":[", "{\"Tasks\":[{\"Text\":\"abc\"}",
            "{\"Tasks\":[]} garbage", "[]", "null", "", "{\"Tasks\":[],\"Width\":" };
        for (int index = 0; index < invalid.Length; index++)
        {
            string current = Path.Combine(directory, index.ToString());
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "tasks.backup.json"), "{\"Tasks\":[{\"Id\":\"recovery\",\"Text\":\"safe\"}]}");
            File.WriteAllText(Path.Combine(current, "tasks.json"), invalid[index]);
            AppRepository repository = new AppRepository(current);
            AppState state = repository.Load();
            Assert(state.Tasks.Count == 1 && state.Tasks[0].Text == "safe", "strict invalid JSON uses backup " + index);
            Assert(repository.LoadWarning != null && repository.CanSave, "strict invalid recovery reports warning " + index);
        }
        string noTasks = Path.Combine(directory, "no-tasks");
        Directory.CreateDirectory(noTasks);
        File.WriteAllText(Path.Combine(noTasks, "tasks.json"), "{}");
        AppRepository blocked = new AppRepository(noTasks);
        AppState empty = blocked.Load();
        Assert(!blocked.CanSave, "missing Tasks is corruption, not a fresh empty store");
        AssertSaveRefused(blocked, empty, "missing Tasks cannot be silently replaced");
    }

    private static void RejectStaleEmptyWriter(string directory)
    {
        AppRepository stale = new AppRepository(directory);
        AppState empty = stale.Load();
        Seed(directory, 16);
        Assert(stale.HasExternalChanges, "missing-at-load store detects a newly created primary");
        AssertSaveRefused(stale, empty, "stale startup empty list cannot overwrite 16 recovered tasks");
        Assert(new AppRepository(directory).Load().Tasks.Count == 16, "all 16 live records survive stale empty save");
        AppState refreshed = stale.Load();
        Assert(!stale.HasExternalChanges && refreshed.Tasks.Count == 16, "reload resets concurrency baseline");
        stale.Save(refreshed);
    }

    private static void ExternalEdits(string directory)
    {
        Seed(directory, 2);
        AppRepository first = new AppRepository(directory);
        AppState old = first.Load();
        AppRepository second = new AppRepository(directory.ToUpperInvariant());
        AppState current = second.Load();
        current.Tasks[0].Text = "external edit";
        second.Save(current);
        Assert(first.HasExternalChanges, "other writer task edit is detected");
        AssertSaveRefused(first, old, "stale writer cannot overwrite an external edit");
        Assert(new AppRepository(directory).Load().Tasks[0].Text == "external edit", "external task edit retained");
        first.Load();
        File.Delete(Path.Combine(directory, "tasks.json"));
        Assert(first.HasExternalChanges, "external primary deletion is detected");
        AssertSaveRefused(first, old, "stale writer cannot recreate externally removed primary");
    }

    private static void GeometryKeepsTaskBackup(string directory)
    {
        Seed(directory, 1);
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        state.Tasks.Add(new TodoRecord { Text = "new task" });
        repository.Save(state);
        string path = Path.Combine(directory, "tasks.backup.json");
        string backup = Convert.ToBase64String(File.ReadAllBytes(path));
        int snapshots = Directory.GetFiles(Path.Combine(directory, "history"), "*.json").Length;
        for (int index = 0; index < 3; index++)
        {
            state.Top = index * 20;
            state.Height = 650 + index * 50;
            repository.Save(state);
        }
        repository.Save(state);
        Assert(Convert.ToBase64String(File.ReadAllBytes(path)) == backup, "move/resize/close keeps previous task-content backup");
        Assert(Directory.GetFiles(Path.Combine(directory, "history"), "*.json").Length == snapshots, "geometry and no-op saves do not churn history");
        Assert(new AppRepository(directory).Load().Height == 750, "geometry still saved");
        AppRepository reopened = new AppRepository(directory);
        reopened.Save(reopened.Load());
        Assert(Directory.GetFiles(Path.Combine(directory, "history"), "*.json").Length == snapshots, "reopening unchanged state does not duplicate snapshot");
    }

    private static void RecoverHistory(string directory)
    {
        Seed(directory, 3);
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        state.Tasks.Add(new TodoRecord { Text = "latest fourth" });
        repository.Save(state);
        string history = Path.Combine(directory, "history");
        Assert(Directory.GetFiles(history, "*.json").Length == 2, "both previous and new task states have immutable snapshots");
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "tasks.backup.json"), "invalid");
        File.WriteAllText(Path.Combine(history, "99999999-invalid.json"), "{\"Tasks\":null}");
        AppRepository recovered = new AppRepository(directory);
        AppState latest = recovered.Load();
        Assert(latest.Tasks.Count == 4 && recovered.CanSave, "newest valid history recovers after corrupt primary and backup");
        Assert(recovered.LoadWarning.Contains("历史快照"), "history recovery is explained");
        recovered.Save(latest);
        File.Delete(Path.Combine(directory, "tasks.json"));
        File.Delete(Path.Combine(directory, "tasks.backup.json"));
        Assert(new AppRepository(directory).Load().Tasks.Count == 4, "missing primary and backup recover existing history");
        string broken = Path.Combine(directory, "only-broken");
        Directory.CreateDirectory(Path.Combine(broken, "history"));
        File.WriteAllText(Path.Combine(broken, "history", "bad.json"), "broken");
        AppRepository unavailable = new AppRepository(broken);
        unavailable.Load();
        Assert(!unavailable.CanSave, "unreadable history prevents treating data store as brand new");
    }

    private static void DeletionsRemainDeleted(string directory)
    {
        Seed(directory, 2);
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        state.Tasks.Clear();
        repository.Save(state);
        AppRepository fresh = new AppRepository(directory);
        Assert(fresh.Load().Tasks.Count == 0 && fresh.LoadWarning == null, "valid explicit empty primary never revives deleted tasks");
        state.Tasks.Add(new TodoRecord { Id = "temporary", Text = "temporary" });
        repository.Save(state);
        state.Tasks.Clear();
        repository.Save(state);
        File.Delete(Path.Combine(directory, "tasks.json"));
        File.Delete(Path.Combine(directory, "tasks.backup.json"));
        Assert(new AppRepository(directory).Load().Tasks.Count == 0, "repeated deletion creates latest empty history snapshot");
    }

    private static void BriefLockRetries(string directory)
    {
        Seed(directory, 2);
        FileStream locked = new FileStream(Path.Combine(directory, "tasks.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        Thread release = new Thread(delegate() { Thread.Sleep(150); locked.Dispose(); });
        release.Start();
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        release.Join();
        Assert(state.Tasks.Count == 2 && repository.LoadWarning == null && repository.CanSave, "brief startup file lock retries instead of showing empty list");
    }

    private static void ConcurrentWriters(string directory)
    {
        Seed(directory, 2);
        AppRepository[] repositories = { new AppRepository(directory), new AppRepository(directory) };
        AppState[] states = { repositories[0].Load(), repositories[1].Load() };
        states[0].Tasks.Add(new TodoRecord { Text = "writer one" });
        states[1].Tasks.Add(new TodoRecord { Text = "writer two" });
        int saved = 0;
        int refused = 0;
        Exception unexpected = null;
        using (ManualResetEvent start = new ManualResetEvent(false))
        {
            Thread[] writers = new Thread[2];
            for (int index = 0; index < writers.Length; index++)
            {
                int writer = index;
                writers[index] = new Thread(delegate()
                {
                    start.WaitOne();
                    try { repositories[writer].Save(states[writer]); Interlocked.Increment(ref saved); }
                    catch (IOException) { Interlocked.Increment(ref refused); }
                    catch (Exception error) { unexpected = error; }
                });
                writers[index].Start();
            }
            start.Set();
            foreach (Thread writer in writers) writer.Join();
        }
        Assert(unexpected == null && saved == 1 && refused == 1, "directory mutex admits one writer and refuses stale concurrent state");
        Assert(new AppRepository(directory).Load().Tasks.Count == 3, "concurrent save leaves a complete winning task list");
    }

    private static void RapidHistoryOrdering(string directory)
    {
        Seed(directory, 1);
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        for (int index = 0; index < 10; index++)
        {
            state.Tasks[0].Text = "version " + index;
            repository.Save(state);
        }
        File.Delete(Path.Combine(directory, "tasks.json"));
        File.Delete(Path.Combine(directory, "tasks.backup.json"));
        Assert(new AppRepository(directory).Load().Tasks[0].Text == "version 9", "rapid snapshots always recover the newest task state");
    }

    private static void RepeatedCorruptLoad(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "damaged primary");
        File.WriteAllText(Path.Combine(directory, "tasks.backup.json"), "damaged backup");
        AppRepository repository = new AppRepository(directory);
        for (int index = 0; index < 4; index++) repository.Load();
        new AppRepository(directory).Load();
        Assert(Directory.GetFiles(directory, "*.bad-*.json").Length == 2, "retry and restart preserve each unchanged corrupt file only once");
        Assert(!repository.CanSave, "repeated corrupt load remains protected");
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "differently damaged primary");
        repository.Load();
        Assert(Directory.GetFiles(directory, "*.bad-*.json").Length == 3, "a distinct corrupt revision gets its own preserved copy");
    }

    private static void RetryAfterStartupLock(string directory)
    {
        Seed(directory, 2);
        AppRepository writer = new AppRepository(directory);
        AppState written = writer.Load();
        written.Tasks.Add(new TodoRecord { Text = "latest" });
        writer.Save(written);
        string primary = Path.Combine(directory, "tasks.json");
        AppRepository repository = new AppRepository(directory);
        using (FileStream locked = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AppState fallback = repository.Load();
            Assert(fallback.Tasks.Count == 2 && !repository.CanSave, "locked primary can show backup but cannot overwrite unknown current data");
        }
        AppState restored = repository.Load();
        Assert(restored.Tasks.Count == 3 && repository.CanSave && !repository.HasExternalChanges,
            "later load after lock release gets latest primary and becomes writable");
        string backup = Path.Combine(directory, "tasks.backup.json");
        using (FileStream primaryLock = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.None))
        using (FileStream backupLock = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AppState fallback = repository.Load();
            Assert(fallback.Tasks.Count == 3 && !repository.CanSave, "history recovery is read-only while primary and backup are locked");
        }
        restored = repository.Load();
        Assert(restored.Tasks.Count == 3 && repository.CanSave, "release of both startup locks permits recovery on the same repository");
    }

    private static void FirstNoOpProtectsHistory(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tasks.json"), "{\"Tasks\":[{\"Id\":\"legacy\",\"Text\":\"original task\"}]}");
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        repository.Save(state);
        repository.Save(state);
        Assert(Directory.GetFiles(Path.Combine(directory, "history"), "*.json").Length == 1,
            "first no-op save of existing nonempty store makes exactly one history snapshot");
        Assert(!File.Exists(Path.Combine(directory, "tasks.backup.json")), "no-op legacy normalization does not rotate task backup");
    }

    private static void NewFieldsRoundTrip(string directory)
    {
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        state.Tasks.Add(new TodoRecord { Id = "due-task", Text = "有截止的任务", DueAt = "2026-09-20T18:00" });
        state.Tasks.Add(new TodoRecord { Id = "plain-task", Text = "无截止的任务" });
        state.CalendarExpanded = true;
        state.CalendarHeatMode = true;
        state.CalendarHeatInverted = true;
        state.MorningReminderHour = 8;
        state.EveningReminderHour = 17;
        state.LastMorningReminded = "morning@2026-09-19";
        state.LastEveningReminded = "evening@2026-09-19";
        state.Language = Strings.CodeOf(UiLanguage.English);
        state.LastDashboardDate = "2026-09-19";
        state.RunAtStartup = true;
        state.TrayHintShown = true;
        repository.Save(state);
        AppState actual = new AppRepository(directory).Load();
        Assert(actual.Tasks[0].DueAt == "2026-09-20T18:00", "due date roundtrips");
        Assert(actual.Tasks[1].DueAt == null, "missing due date stays null");
        Assert(actual.CalendarExpanded && actual.CalendarHeatMode && actual.CalendarHeatInverted, "calendar preferences roundtrip");
        Assert(actual.MorningReminderHour == 8 && actual.EveningReminderHour == 17, "both reminder hours roundtrip");
        Assert(actual.LastMorningReminded == "morning@2026-09-19" && actual.LastEveningReminded == "evening@2026-09-19",
            "per-slot reminder dedupe keys roundtrip");
        Assert(actual.Language == "en", "language roundtrips");
        Assert(actual.LastDashboardDate == "2026-09-19", "last dashboard date roundtrips");
        Assert(actual.RunAtStartup && actual.TrayHintShown, "startup and tray flags roundtrip");
    }

    private static void LegacyJsonNoNewFields(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tasks.json"),
            "{\"Tasks\":[{\"Id\":\"a\",\"Text\":\"旧版任务\",\"IsCompleted\":true}],\"IsPinned\":false,\"Width\":400,\"Height\":500}", Encoding.UTF8);
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        Assert(repository.LoadWarning == null && state.Tasks.Count == 1 && state.Tasks[0].IsCompleted, "legacy json loads cleanly");
        Assert(state.Tasks[0].DueAt == null, "legacy task has no due date");
        Assert(!state.CalendarExpanded && !state.CalendarHeatMode && !state.CalendarHeatInverted, "new calendar flags default to false");
        Assert(!state.RunAtStartup && !state.TrayHintShown, "new tray flags default to false");
        // This file has neither slot keys nor a reminder hour, so nothing is carried
        // over: the constructor's ReminderHour value must not be mistaken for a choice.
        Assert(state.MorningReminderHour == 9 && state.EveningReminderHour == 16, "reminder slots default to 09:00 and 16:00");
        Assert(state.LastMorningReminded == null && state.LastEveningReminded == null, "slot dedupe keys default to null");
        Assert(state.Language == "zh-Hans" && state.LastDashboardDate == null, "language defaults to simplified Chinese");
        repository.Save(state);
        AppState reloaded = new AppRepository(directory).Load();
        Assert(reloaded.Tasks.Count == 1 && reloaded.Tasks[0].Text == "旧版任务" && reloaded.MorningReminderHour == 9,
            "saving a legacy state keeps tasks and persists new defaults");
    }

    private static void InvalidDueNormalized(string directory)
    {
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        state.Tasks.Add(new TodoRecord { Id = "bad-1", Text = "x", DueAt = "not-a-date" });
        state.Tasks.Add(new TodoRecord { Id = "bad-2", Text = "x", DueAt = "2026-13-40T99:00" });
        state.Tasks.Add(new TodoRecord { Id = "ok", Text = "x", DueAt = "2026-9-5T8:15" });
        state.Tasks.Add(new TodoRecord { Id = "blank", Text = "x", DueAt = "   " });
        repository.Save(state);
        AppState actual = new AppRepository(directory).Load();
        Assert(actual.Tasks[0].DueAt == null && actual.Tasks[1].DueAt == null && actual.Tasks[3].DueAt == null,
            "invalid or blank due dates normalize to null");
        Assert(actual.Tasks[2].DueAt == "2026-09-05T08:00", "lenient due input canonicalizes and drops minutes");
    }

    private static void ReminderSlotRepair(string directory)
    {
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        state.MorningReminderHour = 3;
        repository.Save(state);
        Assert(new AppRepository(directory).Load().MorningReminderHour == 9, "too-early morning hour falls back to default 09:00");

        state = repository.Load();
        state.EveningReminderHour = 99;
        repository.Save(state);
        Assert(new AppRepository(directory).Load().EveningReminderHour == 16, "too-late evening hour falls back to default 16:00");

        state = repository.Load();
        state.MorningReminderHour = 6;
        state.EveningReminderHour = 22;
        repository.Save(state);
        AppState boundaries = new AppRepository(directory).Load();
        Assert(boundaries.MorningReminderHour == 6 && boundaries.EveningReminderHour == 22, "boundary hours 6 and 22 are kept");

        state = repository.Load();
        state.MorningReminderHour = 14;
        state.EveningReminderHour = 14;
        repository.Save(state);
        AppState identical = new AppRepository(directory).Load();
        Assert(identical.MorningReminderHour == 14 && identical.EveningReminderHour == 16,
            "two slots on the same hour push the evening slot back to its default");

        state = repository.Load();
        state.LastMorningReminded = "2026年9月19日";
        state.LastEveningReminded = "evening@2026-9-19";
        repository.Save(state);
        AppState keys = new AppRepository(directory).Load();
        Assert(keys.LastMorningReminded == null && keys.LastEveningReminded == null,
            "malformed slot keys normalize to null");
    }

    private static void ReminderMigration(string directory)
    {
        // A pre-two-slot store: ReminderHour 20 (in the 18-22 evening window) and a
        // day-key LastRemindedDate. The evening slot must inherit the hour and the
        // day key, while the morning slot takes its default.
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tasks.json"),
            "{\"Tasks\":[],\"ReminderHour\":20,\"LastRemindedDate\":\"2026-09-19\"}", Encoding.UTF8);
        AppState migrated = new AppRepository(directory).Load();
        Assert(migrated.MorningReminderHour == 9 && migrated.EveningReminderHour == 20,
            "legacy 20:00 reminder becomes the evening slot; morning takes the default");
        Assert(migrated.LastEveningReminded == "evening@2026-09-19", "legacy day key migrates into the evening slot key");
        Assert(migrated.LastMorningReminded == null, "morning slot starts with no dedupe record");

        // A legacy hour outside 18-22 was the old invalid value; it falls back to the
        // evening default rather than being carried over.
        string early = Path.Combine(directory, "early");
        Directory.CreateDirectory(early);
        File.WriteAllText(Path.Combine(early, "tasks.json"),
            "{\"Tasks\":[],\"ReminderHour\":3}", Encoding.UTF8);
        Assert(new AppRepository(early).Load().EveningReminderHour == 16, "invalid legacy hour falls back to the evening default");

        // Once two slots are stored, a stale legacy field must not win.
        string twoSlot = Path.Combine(directory, "two-slot");
        Directory.CreateDirectory(twoSlot);
        File.WriteAllText(Path.Combine(twoSlot, "tasks.json"),
            "{\"Tasks\":[],\"ReminderHour\":20,\"LastRemindedDate\":\"2026-09-19\",\"MorningReminderHour\":7,\"EveningReminderHour\":19}", Encoding.UTF8);
        AppState kept = new AppRepository(twoSlot).Load();
        Assert(kept.MorningReminderHour == 7 && kept.EveningReminderHour == 19, "stored two-slot values beat the legacy field");
        Assert(String.IsNullOrEmpty(kept.LastEveningReminded), "legacy day key is not copied when slot keys already exist");

        // A file with no reminder keys at all must not invent a 20:00 evening slot
        // just because the constructor's ReminderHour default sits in the legacy window.
        string noReminder = Path.Combine(directory, "no-reminder");
        Directory.CreateDirectory(noReminder);
        File.WriteAllText(Path.Combine(noReminder, "tasks.json"), "{\"Tasks\":[]}", Encoding.UTF8);
        AppState bare = new AppRepository(noReminder).Load();
        Assert(bare.MorningReminderHour == 9 && bare.EveningReminderHour == 16,
            "a file without any reminder key keeps both slot defaults");

        // A user's in-memory choice must survive a save of an object that still carries
        // the migration marker from the load that produced it.
        string choose = Path.Combine(directory, "choose");
        Directory.CreateDirectory(choose);
        File.WriteAllText(Path.Combine(choose, "tasks.json"), "{\"Tasks\":[],\"ReminderHour\":19}", Encoding.UTF8);
        AppRepository chooser = new AppRepository(choose);
        AppState editing = chooser.Load();
        Assert(editing.EveningReminderHour == 19, "legacy hour seeds the evening slot on load");
        editing.MorningReminderHour = 8;
        editing.EveningReminderHour = 21;
        chooser.Save(editing);
        AppState chosen = new AppRepository(choose).Load();
        Assert(chosen.MorningReminderHour == 8 && chosen.EveningReminderHour == 21,
            "a reminder hour chosen after the migration sticks across a reload");
    }

    private static void ThemeSettings(string directory)
    {
        // Theme choice persists as two plain strings; absent keys mean the
        // classic sage look with the system deciding light vs dark.
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        Assert(state.ThemeFamily == null && state.LightDarkMode == null, "theme fields default to null for a fresh store");
        state.ThemeFamily = "midnight";
        state.LightDarkMode = "dark";
        repository.Save(state);
        AppState actual = new AppRepository(directory).Load();
        Assert(actual.ThemeFamily == "midnight" && actual.LightDarkMode == "dark", "theme family and mode roundtrip");
        state = repository.Load();
        state.ThemeFamily = "polka-dots";
        state.LightDarkMode = "dim";
        repository.Save(state);
        actual = new AppRepository(directory).Load();
        Assert(actual.ThemeFamily == "sage" && actual.LightDarkMode == "followSystem",
            "unknown theme values normalize to the defaults");
    }

    private static void ThemePalettes()
    {
        // Every registered palette must be structurally complete: all tokens
        // filled with valid hex colours and a five-stop heat scale.
        Assert(Themes.Registered.Length == Themes.FamilyCount * 2, "exactly ten palettes are registered (5 families x 2)");
        foreach (ThemePalette palette in Themes.Registered)
        {
            List<string> problems = Themes.Validate(palette);
            Assert(problems.Count == 0, Themes.CodeOf(palette.Family) + (palette.Dark ? " dark" : " light")
                + " palette is complete" + (problems.Count == 0 ? "" : ": " + String.Join("; ", problems.ToArray())));
        }
        for (int index = 0; index < Themes.FamilyCount; index++)
        {
            ThemeFamily family = (ThemeFamily)index;
            Assert(Themes.Get(family, false).Family == family && !Themes.Get(family, false).Dark
                && Themes.Get(family, true).Family == family && Themes.Get(family, true).Dark,
                "family " + Themes.CodeOf(family) + " has both light and dark variants");
        }
        Assert(Themes.ParseFamily(null) == ThemeFamily.Sage && Themes.ParseFamily("MIDNIGHT") == ThemeFamily.Midnight
            && Themes.ParseFamily("???") == ThemeFamily.Sage, "theme family parsing falls back to sage");
        Assert(Themes.ParseMode(null) == LightDarkMode.FollowSystem && Themes.ParseMode("Light") == LightDarkMode.Light
            && Themes.ParseMode("???") == LightDarkMode.FollowSystem, "light/dark mode parsing falls back to followSystem");
        Assert(ReferenceEquals(Themes.Resolve(ThemeFamily.Midnight, LightDarkMode.Light, false), Themes.Get(ThemeFamily.Midnight, false)),
            "locked light ignores the system appearance");
        Assert(ReferenceEquals(Themes.Resolve(ThemeFamily.Midnight, LightDarkMode.Dark, true), Themes.Get(ThemeFamily.Midnight, true)),
            "locked dark ignores the system appearance");
        Assert(ReferenceEquals(Themes.Resolve(ThemeFamily.Midnight, LightDarkMode.FollowSystem, false), Themes.Get(ThemeFamily.Midnight, true))
            && ReferenceEquals(Themes.Resolve(ThemeFamily.Midnight, LightDarkMode.FollowSystem, true), Themes.Get(ThemeFamily.Midnight, false)),
            "followSystem resolves through the system appearance probe");
        // Sage Light is the classic card: its values must stay byte-identical to
        // the original hard-coded colours the UI tests assert on.
        ThemePalette sage = Themes.Get(ThemeFamily.Sage, false);
        Assert(sage.CardBg == "FFFEFB" && sage.Accent == "3C7865" && sage.Overdue == "C0504D"
            && sage.SelectedCell == "D5E8DA" && sage.Heat[0] == "EDF1E8" && sage.Heat[4] == "3C7865",
            "sage light keeps the original card colours");
        // The heart day is deliberately not themeable.
        Assert(Themes.Registered.All(p => p.Family != ThemeFamily.LiquidGlass || p.IsGlass),
            "only liquid glass palettes are marked as glass");
    }

    private static void LanguageRoundTrip(string directory)
    {
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        string[] codes = { "zh-Hans", "zh-Hant", "en" };
        for (int index = 0; index < codes.Length; index++)
        {
            state.Language = codes[index];
            repository.Save(state);
            Assert(new AppRepository(directory).Load().Language == codes[index], "language " + codes[index] + " roundtrips");
        }
        state = repository.Load();
        state.Language = "klingon";
        repository.Save(state);
        Assert(new AppRepository(directory).Load().Language == "zh-Hans", "unknown language falls back to simplified Chinese");
        state = repository.Load();
        state.Language = null;
        repository.Save(state);
        Assert(new AppRepository(directory).Load().Language == "zh-Hans", "missing language falls back to simplified Chinese");
        Assert(Strings.Parse("zh-Hant") == UiLanguage.ZhHant && Strings.Parse("EN") == UiLanguage.English,
            "language codes parse case-insensitively and accept the hant alias");
    }

    private static void HolidayTable()
    {
        Assert(HolidayData.FirstYear == 2025 && HolidayData.LastYear == 2028, "holiday table spans 2025 through 2028");
        Assert(HolidayData.DayCount == 69, "holiday table holds all 69 published days (was " + HolidayData.DayCount + ")");
        // 17 statutory days a year, plus one extra in 2028 where a holiday lands on a
        // Sunday and the following weekday is observed instead.
        for (int year = HolidayData.FirstYear; year <= HolidayData.LastYear; year++)
        {
            int count = 0;
            for (DateTime day = new DateTime(year, 1, 1); day.Year == year; day = day.AddDays(1))
                if (HolidayData.IsHoliday(day)) count++;
            Assert(count == (year == 2028 ? 18 : 17), "year " + year + " has the expected holiday count (was " + count + ")");
        }
        Assert(HolidayData.IsHoliday(new DateTime(2026, 1, 1)), "2026-01-01 is a public holiday");
        Assert(HolidayData.IsHoliday(new DateTime(2026, 2, 17)), "2026 lunar new year day one is a public holiday");
        Assert(HolidayData.IsHoliday(new DateTime(2026, 12, 25)), "2026 christmas day is a public holiday");
        Assert(!HolidayData.IsHoliday(new DateTime(2026, 3, 4)), "an ordinary wednesday is not a public holiday");
        Assert(Strings.Get(UiLanguage.ZhHans, "holiday.label") != null, "holiday label is localised");

        string simplified = HolidayData.Name(new DateTime(2026, 12, 25), UiLanguage.ZhHans);
        string traditional = HolidayData.Name(new DateTime(2026, 12, 25), UiLanguage.ZhHant);
        string english = HolidayData.Name(new DateTime(2026, 12, 25), UiLanguage.English);
        Assert(simplified == "圣诞节" && traditional == "聖誕節" && english == "Christmas Day",
            "holiday names localise per language");
        // An English name containing spaces must survive the row split.
        Assert(HolidayData.Name(new DateTime(2026, 2, 17), UiLanguage.English) == "Lunar New Year’s Day",
            "multi-word holiday names are parsed whole");

        Assert(HolidayData.Covers(new DateTime(2026, 6, 1)) && !HolidayData.Covers(new DateTime(2030, 6, 1)),
            "coverage stops at the published range");
        Assert(HolidayData.Name(new DateTime(2030, 6, 1), UiLanguage.ZhHans) == null,
            "a date outside the table has no holiday name");
        // Every stored row must agree with the real calendar, so a mis-typed weekday
        // would have been dropped at parse time.
        foreach (int year in new[] { 2025, 2026, 2027, 2028 })
            for (DateTime day = new DateTime(year, 1, 1); day.Year == year; day = day.AddDays(1))
                if (HolidayData.IsHoliday(day))
                    Assert(day.DayOfWeek != DayOfWeek.Sunday || year == 2028,
                        "no holiday is silently parked on a non-observed Sunday (" + day.ToString("yyyy-MM-dd") + ")");
    }

    private static void StringsTable()
    {
        UiLanguage[] languages = { UiLanguage.ZhHans, UiLanguage.ZhHant, UiLanguage.English };
        Assert(Strings.CodeOf(UiLanguage.ZhHans) == "zh-Hans" && Strings.CodeOf(UiLanguage.ZhHant) == "zh-Hant"
            && Strings.CodeOf(UiLanguage.English) == "en", "language codes are the canonical identifiers");

        // Every table must expose exactly the same key set, or a switch would leave
        // a control untranslated at runtime.
        Dictionary<string, string> baseline = Strings.Table(UiLanguage.ZhHans).Values;
        Assert(baseline.Count > 150, "the text table is substantially populated (" + baseline.Count + ")");
        for (int index = 1; index < languages.Length; index++)
        {
            Dictionary<string, string> other = Strings.Table(languages[index]).Values;
            Assert(other.Count == baseline.Count, "table " + languages[index] + " has the same key count");
            foreach (KeyValuePair<string, string> pair in baseline)
                Assert(other.ContainsKey(pair.Key), "table " + languages[index] + " is missing key " + pair.Key);
            foreach (KeyValuePair<string, string> pair in other)
                Assert(baseline.ContainsKey(pair.Key), "table " + languages[index] + " has an extra key " + pair.Key);
        }

        // Placeholder arity must agree, otherwise F() would throw at runtime.
        foreach (KeyValuePair<string, string> pair in baseline)
            for (int index = 1; index < languages.Length; index++)
                Assert(PlaceholderCount(pair.Value) == PlaceholderCount(Strings.Get(languages[index], pair.Key)),
                    "placeholder count matches for " + pair.Key);
        Assert(PlaceholderCount("a {0} b {1}") == 2 && PlaceholderCount("no slots") == 0 && PlaceholderCount("{{0}}") == 0,
            "placeholder counter understands escapes");

        Assert(Strings.Get(UiLanguage.English, "no.such.key") == Strings.Get(UiLanguage.ZhHans, "no.such.key"),
            "missing keys fall back to simplified Chinese");
        Assert(Strings.T(UiLanguage.English, "week.0") == "Mo" || Strings.T(UiLanguage.English, "week.0").Length > 0,
            "week headers resolve");
        Assert(Strings.F("{0} 件", 3) == "3 件", "F substitutes with the invariant culture");
        Assert(Strings.CountOf(UiLanguage.English, 2, "due") == "2 due", "count classifiers resolve per language");
        Assert(Strings.Date(UiLanguage.ZhHans, new DateTime(2026, 9, 18)) != Strings.Date(UiLanguage.English, new DateTime(2026, 9, 18)),
            "date labels differ per language");
    }

    // Counts real substitution slots, ignoring escaped braces.
    private static int PlaceholderCount(string template)
    {
        if (template == null) return 0;
        int count = 0;
        for (int index = 0; index < template.Length; index++)
        {
            if (template[index] != '{') continue;
            if (index + 1 < template.Length && template[index + 1] == '{') { index++; continue; }
            if (index + 1 < template.Length && Char.IsDigit(template[index + 1])) count++;
        }
        return count;
    }

    private static void CompletionAppendAndRead(string directory)
    {
        CompletionLog log = new CompletionLog(directory);
        DateTime first = new DateTime(2026, 9, 18, 18, 32, 11);
        DateTime second = new DateTime(2026, 9, 19, 9, 5, 0);
        DateTime third = new DateTime(2026, 9, 19, 21, 47, 30);
        log.Append(new CompletionEntry { Id = "a", Text = "买牛奶 🥛\n第二行 \"quotes\"", Done = first });
        log.Append(new CompletionEntry { Id = "b", Text = "另一件", Done = second });
        log.Append(new CompletionEntry { Id = "a", Text = "再次完成 a", Done = third });
        List<CompletionEntry> entries = log.ReadAll();
        Assert(entries.Count == 3, "appended entries are all read back");
        Assert(entries[0].Id == "a" && entries[0].Text == "买牛奶 🥛\n第二行 \"quotes\"" && entries[0].Done == first,
            "unicode and newline text snapshot roundtrips with timestamp");
        Assert(entries[2].Id == "a" && entries[2].Done == third, "append order is preserved");
        log.RemoveLatest("a");
        entries = log.ReadAll();
        Assert(entries.Count == 2 && entries[0].Id == "a" && entries[0].Done == first && entries[1].Id == "b",
            "removing latest deletes only the newest record of that task");
        string raw = File.ReadAllText(Path.Combine(directory, "completions.jsonl"), Encoding.UTF8);
        Assert(raw.IndexOf("\r") < 0, "log lines use plain LF separators");
    }

    private static void CompletionRemoveMissing(string directory)
    {
        CompletionLog log = new CompletionLog(directory);
        log.Append(new CompletionEntry { Id = "a", Text = "保留", Done = new DateTime(2026, 9, 18, 8, 0, 0) });
        string path = Path.Combine(directory, "completions.jsonl");
        byte[] before = File.ReadAllBytes(path);
        log.RemoveLatest("no-such-task");
        Assert(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(path)),
            "removing an unknown task leaves the log byte-identical");
        new CompletionLog(Path.Combine(directory, "empty-sub")).RemoveLatest("anything");
        Assert(!File.Exists(Path.Combine(directory, "empty-sub", "completions.jsonl")),
            "removing from a missing log does not create the file");
    }

    private static void CompletionCorruptLine(string directory)
    {
        CompletionLog log = new CompletionLog(directory);
        log.Append(new CompletionEntry { Id = "keep-1", Text = "第一条", Done = new DateTime(2026, 9, 17, 10, 0, 0) });
        log.Append(new CompletionEntry { Id = "keep-2", Text = "第二条", Done = new DateTime(2026, 9, 18, 11, 0, 0) });
        File.AppendAllText(Path.Combine(directory, "completions.jsonl"), "this is not json\n", Encoding.UTF8);
        Assert(log.ReadAll().Count == 2, "unreadable lines are skipped on read");
        log.RemoveLatest("keep-1");
        List<CompletionEntry> entries = log.ReadAll();
        Assert(entries.Count == 1 && entries[0].Id == "keep-2", "rewrite keeps the remaining valid entries");
        string[] preserved = Directory.GetFiles(directory, "completions.bad-*.jsonl");
        Assert(preserved.Length == 1 && File.ReadAllText(preserved[0], Encoding.UTF8).Contains("this is not json"),
            "damaged lines are preserved verbatim before the rewrite");
        File.AppendAllText(Path.Combine(directory, "completions.jsonl"), "this is not json\n", Encoding.UTF8);
        log.Append(new CompletionEntry { Id = "keep-3", Text = "第三条", Done = new DateTime(2026, 9, 19, 12, 0, 0) });
        log.RemoveLatest("keep-3");
        Assert(Directory.GetFiles(directory, "completions.bad-*.jsonl").Length == 1,
            "the same damaged revision is preserved only once");
    }

    private static void CompletionConcurrent(string directory)
    {
        CompletionLog[] logs = { new CompletionLog(directory), new CompletionLog(directory) };
        Exception unexpected = null;
        using (ManualResetEvent start = new ManualResetEvent(false))
        {
            Thread[] writers = new Thread[2];
            for (int index = 0; index < writers.Length; index++)
            {
                int writer = index;
                writers[index] = new Thread(delegate ()
                {
                    try
                    {
                        start.WaitOne();
                        for (int n = 0; n < 50; n++)
                            logs[writer].Append(new CompletionEntry { Id = "w" + writer + "-" + n, Text = "并发", Done = new DateTime(2026, 9, 19, 10, 0, 0) });
                    }
                    catch (Exception error) { unexpected = error; }
                });
                writers[index].Start();
            }
            start.Set();
            foreach (Thread writer in writers) writer.Join();
        }
        Assert(unexpected == null, "concurrent appends never throw: " + unexpected);
        List<CompletionEntry> entries = new CompletionLog(directory).ReadAll();
        Assert(entries.Count == 100, "directory mutex keeps every concurrent append");
        HashSet<string> ids = new HashSet<string>();
        foreach (CompletionEntry entry in entries) Assert(ids.Add(entry.Id), "no appended line was torn or duplicated");
    }

    private static void ClearedAppendAndRead(string directory)
    {
        ClearedLog log = new ClearedLog(directory);
        DateTime first = new DateTime(2026, 9, 18, 18, 32, 11);
        DateTime second = new DateTime(2026, 9, 19, 9, 5, 0);
        log.Append(new ClearedEntry { Id = "a", Text = "买牛奶 🥛\n第二行 \"quotes\"", Due = "2026-09-21T18:00", Done = true, ClearedAt = first });
        log.Append(new ClearedEntry { Id = "b", Text = "没有截止的", Due = null, Done = false, ClearedAt = second });
        List<ClearedEntry> entries = log.ReadAll();
        Assert(entries.Count == 2, "appended cleared entries are all read back");
        Assert(entries[0].Id == "a" && entries[0].Text == "买牛奶 🥛\n第二行 \"quotes\""
            && entries[0].Due == "2026-09-21T18:00" && entries[0].Done && entries[0].ClearedAt == first,
            "cleared entry roundtrips text, due, done and timestamp");
        Assert(entries[1].Id == "b" && entries[1].Due == null && !entries[1].Done && entries[1].ClearedAt == second,
            "a task without deadline archives a null due");
        string raw = File.ReadAllText(Path.Combine(directory, "cleared.jsonl"), Encoding.UTF8);
        Assert(raw.IndexOf("\r") < 0, "cleared log lines use plain LF separators");
        Assert(raw.Contains("\"cleared\":\"2026-09-18T18:32:11\""), "clearing timestamps are stored second-precise");
        Assert(raw.Contains("\"due\":null") && raw.Contains("\"done\":false"), "null due and false flags serialize explicitly");
    }

    private static void ClearedCorruptLine(string directory)
    {
        ClearedLog log = new ClearedLog(directory);
        log.Append(new ClearedEntry { Id = "keep", Text = "保留", Due = null, Done = false, ClearedAt = new DateTime(2026, 9, 17, 10, 0, 0) });
        File.AppendAllText(Path.Combine(directory, "cleared.jsonl"), "this is not json\n", Encoding.UTF8);
        File.AppendAllText(Path.Combine(directory, "cleared.jsonl"),
            "{\"id\":\"bad-date\",\"text\":\"坏时间\",\"cleared\":\"not-a-date\"}\n", Encoding.UTF8);
        List<ClearedEntry> entries = log.ReadAll();
        Assert(entries.Count == 1 && entries[0].Id == "keep", "unreadable and invalid lines are skipped on read");
        // The archive is append-only: there is no rewrite path, so unlike the
        // completion log there is also no .bad- copy to expect.
        Assert(Directory.GetFiles(directory, "cleared.bad-*.jsonl").Length == 0, "append-only archive never writes a bad copy");
        // A due that is not hour-precise collapses to null instead of surviving.
        File.AppendAllText(Path.Combine(directory, "cleared.jsonl"),
            "{\"id\":\"odd-due\",\"text\":\"奇怪截止\",\"due\":\"2026-09-21T18:30\",\"cleared\":\"2026-09-18T09:00:00\"}\n", Encoding.UTF8);
        File.AppendAllText(Path.Combine(directory, "cleared.jsonl"),
            "{\"id\":\"bad-due\",\"text\":\"坏截止\",\"due\":\"not-a-date\",\"cleared\":\"2026-09-18T10:00:00\"}\n", Encoding.UTF8);
        List<ClearedEntry> after = log.ReadAll();
        Assert(after.Count == 3 && after[1].Id == "odd-due" && after[1].Due == "2026-09-21T18:00",
            "a hand-edited due truncates to the hour like every other due reader");
        Assert(after[2].Id == "bad-due" && after[2].Due == null,
            "an unparseable due archives as null");
    }

    private static void ClearedConcurrent(string directory)
    {
        ClearedLog[] logs = { new ClearedLog(directory), new ClearedLog(directory) };
        Exception unexpected = null;
        using (ManualResetEvent start = new ManualResetEvent(false))
        {
            Thread[] writers = new Thread[2];
            for (int index = 0; index < writers.Length; index++)
            {
                int writer = index;
                writers[index] = new Thread(delegate ()
                {
                    try
                    {
                        start.WaitOne();
                        for (int n = 0; n < 50; n++)
                            logs[writer].Append(new ClearedEntry { Id = "w" + writer + "-" + n, Text = "并发清除", Due = null, Done = n % 2 == 0, ClearedAt = new DateTime(2026, 9, 19, 10, 0, 0) });
                    }
                    catch (Exception error) { unexpected = error; }
                });
                writers[index].Start();
            }
            start.Set();
            foreach (Thread writer in writers) writer.Join();
        }
        Assert(unexpected == null, "concurrent cleared appends never throw: " + unexpected);
        List<ClearedEntry> entries = new ClearedLog(directory).ReadAll();
        Assert(entries.Count == 100, "directory mutex keeps every concurrent cleared append");
    }

    private static void RestoreLogicFunctions()
    {
        DateTime now = new DateTime(2026, 9, 20, 12, 0, 0);

        // Rolling windows include the boundary and exclude one second past it.
        Assert(RestoreLogic.InWindow(RestoreRange.Day, now.AddHours(-24), now), "exactly 24h ago is inside last day");
        Assert(!RestoreLogic.InWindow(RestoreRange.Day, now.AddHours(-24).AddSeconds(-1), now), "one second older leaves last day");
        Assert(RestoreLogic.InWindow(RestoreRange.Week, now.AddDays(-7), now), "exactly 7 days ago is inside last week");
        Assert(!RestoreLogic.InWindow(RestoreRange.Week, now.AddDays(-7).AddSeconds(-1), now), "one second older leaves last week");
        Assert(RestoreLogic.InWindow(RestoreRange.Month, now.AddDays(-30), now), "exactly 30 days ago is inside last month");
        Assert(!RestoreLogic.InWindow(RestoreRange.Month, now.AddDays(-30).AddSeconds(-1), now), "one second older leaves last month");
        Assert(RestoreLogic.InWindow(RestoreRange.Year, now.AddDays(-365), now), "exactly 365 days ago is inside last year");
        Assert(!RestoreLogic.InWindow(RestoreRange.Year, now.AddDays(-365).AddSeconds(-1), now), "one second older leaves last year");
        Assert(RestoreLogic.InWindow(RestoreRange.All, now.AddYears(-50), now), "all keeps any age");
        Assert(!RestoreLogic.InWindow(RestoreRange.ExactDate, now, now), "exact date is judged by ExactMatch, not InWindow");

        // Exact date means the local calendar day [00:00, next 00:00).
        DateTime picked = new DateTime(2026, 9, 15);
        Assert(RestoreLogic.ExactMatch(new DateTime(2026, 9, 15, 0, 0, 0), picked), "midnight of the picked day matches");
        Assert(RestoreLogic.ExactMatch(new DateTime(2026, 9, 15, 23, 59, 59), picked), "the last second of the picked day matches");
        Assert(!RestoreLogic.ExactMatch(new DateTime(2026, 9, 14, 23, 59, 59), picked), "the second before the picked day does not");
        Assert(!RestoreLogic.ExactMatch(new DateTime(2026, 9, 16, 0, 0, 0), picked), "midnight after the picked day does not");

        // Restorable: latest record per id, minus ids back in the list, newest first.
        List<ClearedEntry> archive = new List<ClearedEntry>
        {
            new ClearedEntry { Id = "same", Text = "旧文本", ClearedAt = new DateTime(2026, 9, 10, 8, 0, 0) },
            new ClearedEntry { Id = "old", Text = "更早清除", ClearedAt = new DateTime(2026, 9, 1, 8, 0, 0) },
            new ClearedEntry { Id = "same", Text = "新文本", ClearedAt = new DateTime(2026, 9, 18, 9, 0, 0) },
            new ClearedEntry { Id = "fresh", Text = "刚清除", ClearedAt = new DateTime(2026, 9, 19, 21, 0, 0) },
            new ClearedEntry { Id = "back", Text = "已在列表", ClearedAt = new DateTime(2026, 9, 17, 9, 0, 0) }
        };
        HashSet<string> current = new HashSet<string> { "back" };
        List<ClearedEntry> restorable = RestoreLogic.Restorable(archive, current, now);
        Assert(restorable.Count == 3, "one record per id and none of the current ids");
        Assert(restorable[0].Id == "fresh" && restorable[1].Id == "same" && restorable[2].Id == "old",
            "restorable entries are ordered newest clearing first");
        Assert(restorable[1].Text == "新文本", "the latest snapshot of a repeated id wins");
        Assert(RestoreLogic.Restorable(new List<ClearedEntry>(), null, now).Count == 0, "empty archive restores nothing");
        Assert(RestoreLogic.Restorable(null, new HashSet<string>(), now).Count == 0, "null archive restores nothing");

        // Text filter: case-insensitive substring over the task text.
        Assert(RestoreLogic.TextMatches("买牛奶和 BREAD", "bread"), "search ignores case");
        Assert(RestoreLogic.TextMatches("买牛奶", "牛奶"), "search matches a substring");
        Assert(!RestoreLogic.TextMatches("买牛奶", "酸奶"), "unrelated keyword misses");
        Assert(RestoreLogic.TextMatches("任何文本", ""), "empty keyword matches everything");
        Assert(RestoreLogic.TextMatches("任何文本", null), "null keyword matches everything");
        Assert(!RestoreLogic.TextMatches(null, "任何"), "null text never matches a keyword");
    }

    private static void LogicPureFunctions()
    {
        Assert(DueLogic.Normalize("not-a-date") == null && DueLogic.Normalize(null) == null, "invalid due strings normalize to null");
        Assert(DueLogic.Normalize(" 2026-09-20T18:00 ") == "2026-09-20T18:00", "canonical due string survives normalization");
        Assert(DueLogic.Normalize("2026-9-5T8:15") == "2026-09-05T08:00", "lenient input canonicalizes and truncates to the hour");

        // The pure layer returns enum state plus the parsed value; the wording now
        // belongs to Strings, so these assertions stay language-independent.
        DateTime now = new DateTime(2026, 9, 19, 22, 0, 0);
        Assert(DueLogic.State(null, false, now) == DueState.None, "no due date has no state");
        Assert(DueLogic.State(new DateTime(2026, 9, 19, 23, 0, 0), false, now) == DueState.Today, "later today is Today");
        Assert(DueLogic.State(new DateTime(2026, 9, 20, 18, 0, 0), false, now) == DueState.Future, "tomorrow is Future");
        Assert(DueLogic.State(new DateTime(2026, 9, 19, 18, 0, 0), false, now) == DueState.Overdue, "past time today is Overdue");
        Assert(DueLogic.State(new DateTime(2026, 9, 18, 18, 0, 0), false, now) == DueState.Overdue, "yesterday is Overdue");
        Assert(DueLogic.State(new DateTime(2026, 9, 18, 18, 0, 0), true, now) == DueState.Completed, "completed wins over overdue");
        Assert(DueLogic.State(new DateTime(2026, 9, 20, 18, 0, 0), true, now) == DueState.Completed, "completed future task is Completed");

        Assert(DueLogic.Day(new DateTime(2026, 9, 19, 8, 0, 0), now) == DueDay.Today, "same day is Today");
        Assert(DueLogic.Day(new DateTime(2026, 9, 20, 8, 0, 0), now) == DueDay.Tomorrow, "next day is Tomorrow");
        Assert(DueLogic.Day(new DateTime(2026, 12, 1, 8, 0, 0), now) == DueDay.ThisYear, "later this year is ThisYear");
        Assert(DueLogic.Day(new DateTime(2027, 1, 5, 8, 0, 0), now) == DueDay.OtherYear, "another year is OtherYear");

        // The label wording is a Strings concern; assert it once per language to make
        // sure the UI-facing composition still reads correctly.
        Assert(DueLabel(new DateTime(2026, 9, 19, 23, 0, 0), false, now, UiLanguage.ZhHans) == "今天 23:00", "zh-Hans today label");
        Assert(DueLabel(new DateTime(2026, 9, 20, 18, 0, 0), false, now, UiLanguage.ZhHans) == "明天 18:00", "zh-Hans tomorrow label");
        Assert(DueLabel(new DateTime(2026, 12, 1, 9, 0, 0), false, now, UiLanguage.ZhHans) == "12月1日 9:00", "zh-Hans this-year label omits the year");
        Assert(DueLabel(new DateTime(2027, 1, 5, 9, 0, 0), false, now, UiLanguage.ZhHans) == "2027年1月5日 9:00", "zh-Hans other-year label includes the year");
        Assert(DueLabel(new DateTime(2026, 9, 19, 18, 0, 0), false, now, UiLanguage.ZhHans) == "已过期 · 今天 18:00", "zh-Hans overdue label");
        Assert(DueLabel(new DateTime(2026, 9, 18, 18, 0, 0), false, now, UiLanguage.ZhHans) == "已过期 · 9月18日 18:00", "zh-Hans overdue yesterday label");
        Assert(DueLabel(new DateTime(2026, 9, 20, 18, 0, 0), true, now, UiLanguage.ZhHans) == "明天 18:00", "completed keeps a plain label");
        Assert(DueLabel(new DateTime(2026, 9, 18, 18, 0, 0), true, now, UiLanguage.ZhHans) == "9月18日 18:00", "completed past task is not marked overdue");
        Assert(DueLabel(new DateTime(2026, 9, 19, 23, 0, 0), false, now, UiLanguage.English) == "Today 23:00", "en today label");
        Assert(DueLabel(new DateTime(2026, 9, 19, 18, 0, 0), false, now, UiLanguage.English) == "Overdue · Today 18:00", "en overdue label");
        Assert(DueLabel(new DateTime(2026, 9, 20, 18, 0, 0), false, now, UiLanguage.English) == "Tomorrow 18:00", "en tomorrow label");
        Assert(DueLabel(new DateTime(2026, 12, 1, 9, 0, 0), false, now, UiLanguage.English) == "12/1 9:00", "en this-year label omits the year");
        Assert(DueLabel(new DateTime(2027, 1, 5, 9, 0, 0), false, now, UiLanguage.English) == "2027/1/5 9:00", "en other-year label includes the year");
        Assert(DueLabel(new DateTime(2026, 9, 18, 18, 0, 0), false, now, UiLanguage.English) == "Overdue · 9/18 18:00", "en overdue last-month label");
        Assert(DueLabel(new DateTime(2026, 9, 20, 18, 0, 0), false, now, UiLanguage.ZhHant) == "明天 18:00", "zh-Hant tomorrow label");
        Assert(DueLabel(new DateTime(2027, 1, 5, 9, 0, 0), false, now, UiLanguage.ZhHant) == "2027年1月5日 9:00", "zh-Hant other-year label");
        Assert(DueLabel(new DateTime(2026, 9, 18, 18, 0, 0), false, now, UiLanguage.ZhHant) == "已過期 · 9月18日 18:00", "zh-Hant overdue label uses the traditional wording");
        // The three languages must not collapse onto one another: the same instant has
        // to read differently in each of them, which is what a hardcoded-language
        // formatter would break.
        DateTime sample = new DateTime(2026, 9, 20, 18, 0, 0);
        Assert(DueLabel(sample, false, now, UiLanguage.ZhHans) != DueLabel(sample, false, now, UiLanguage.English),
            "the deadline label actually depends on the language");
        Assert(DueLabel(new DateTime(2026, 9, 18, 18, 0, 0), false, now, UiLanguage.ZhHans)
            != DueLabel(new DateTime(2026, 9, 18, 18, 0, 0), false, now, UiLanguage.ZhHant),
            "simplified and traditional deadlines differ");

        Dictionary<DateTime, int> dueCounts = new Dictionary<DateTime, int> { { new DateTime(2026, 9, 20), 2 } };
        Dictionary<DateTime, int> doneCounts = new Dictionary<DateTime, int> { { new DateTime(2026, 9, 18), 4 }, { new DateTime(2026, 9, 19), 1 } };
        Dictionary<DateTime, List<string>> doneTexts = new Dictionary<DateTime, List<string>>
        {
            { new DateTime(2026, 9, 18), new List<string> { "任务甲", "任务乙", "任务丙", "任务丁" } }
        };
        DateTime todayKey = new DateTime(2026, 9, 19);
        List<CalendarCell> cells = CalendarBuilder.BuildCells(2026, 9, todayKey, dueCounts, doneCounts, doneTexts, false, false, UiLanguage.ZhHans);
        Assert(cells.Count == 42 && cells[0].Date.DayOfWeek == DayOfWeek.Monday, "calendar grid has six weeks starting on Monday");
        Assert(cells[1].Date == new DateTime(2026, 9, 1) && cells[1].IsCurrentMonth && !cells[0].IsCurrentMonth,
            "september 2026 starts on tuesday with august filler before it");
        CalendarCell today = cells.Find(c => c.IsToday);
        Assert(today != null && today.Date == new DateTime(2026, 9, 19), "today is marked");
        CalendarCell dueDay = cells.Find(c => c.Date == new DateTime(2026, 9, 20));
        Assert(dueDay.Count == 2, "due counts land on the right cell");
        // 20 September 2026 is a Sunday, so the marker names the weekend before the dues.
        Assert(dueDay.IsWeekend && dueDay.HasMarker && dueDay.ToolTipText == "9月20日 · 周末 · 2 件到期",
            "a weekend cell names the weekend alongside its dues");
        CalendarCell plain = cells.Find(c => c.Date == new DateTime(2026, 9, 21));
        // 21 September 2026 is a Monday: no marker, no dues, so the tooltip stays bare.
        Assert(plain.Count == 0 && !plain.HasMarker && plain.MarkerName == null && plain.ToolTipText == "9月21日",
            "a plain weekday keeps a bare tooltip");

        // Weekends carry the same red marker as gazetted holidays, and a holiday that
        // lands on a weekend keeps its own name rather than being flattened to "weekend".
        CalendarCell saturday = cells.Find(c => c.Date == new DateTime(2026, 9, 19));
        Assert(saturday.IsWeekend && !saturday.IsHoliday && saturday.MarkerName == "周末",
            "saturday is marked as a weekend without pretending to be a holiday");
        CalendarCell holidayWeekend = cells.Find(c => c.Date == new DateTime(2026, 9, 26));
        Assert(holidayWeekend.IsHoliday && holidayWeekend.IsWeekend && holidayWeekend.MarkerName == "中秋节翌日",
            "a holiday on a weekend keeps its own name instead of being called a weekend");
        Assert(holidayWeekend.ToolTipText == "9月26日 · 中秋节翌日", "the holiday tooltip names the holiday");
        List<CalendarCell> weekendEn = CalendarBuilder.BuildCells(2026, 9, todayKey, null, null, null, false, false, UiLanguage.English);
        Assert(weekendEn.Find(c => c.Date == new DateTime(2026, 9, 20)).MarkerName == "Weekend",
            "the weekend marker follows the language");
        Assert(weekendEn.Find(c => c.Date == new DateTime(2026, 9, 26)).MarkerName == "The day following the Chinese Mid-Autumn Festival",
            "the holiday name follows the language too");

        // Both directions stop counting at today, so a later day's completions can never
        // become the ceiling that the elapsed days are measured against.
        Dictionary<DateTime, int> withFuture = new Dictionary<DateTime, int>
        {
            { new DateTime(2026, 9, 18), 4 },
            { new DateTime(2026, 9, 25), 9 }
        };
        Assert(CalendarBuilder.BuildCells(2026, 9, todayKey, null, withFuture, null, true, false, UiLanguage.ZhHans)
                .Find(c => c.Date == new DateTime(2026, 9, 18)).HeatLevel == 4,
            "forward heat ignores completions dated after today");
        Assert(CalendarBuilder.BuildCells(2026, 9, todayKey, null, withFuture, null, true, true, UiLanguage.ZhHans)
                .Find(c => c.Date == new DateTime(2026, 9, 18)).HeatLevel == 0,
            "inverted heat ignores completions dated after today as well");

        List<CalendarCell> heat = CalendarBuilder.BuildCells(2026, 9, todayKey, dueCounts, doneCounts, doneTexts, true, false, UiLanguage.ZhHans);
        CalendarCell busiest = heat.Find(c => c.Date == new DateTime(2026, 9, 18));
        CalendarCell quiet = heat.Find(c => c.Date == new DateTime(2026, 9, 19));
        Assert(busiest.Count == 4 && busiest.HeatLevel == 4 && quiet.Count == 1 && quiet.HeatLevel == 1,
            "heat levels scale against the busiest day in view");
        Assert(busiest.ToolTipText == "9月18日 · 完成 4 件\n· 任务甲\n· 任务乙\n· 任务丙\n等 1 件",
            "heat tooltip caps task names and summarizes the rest");
        Assert(quiet.ToolTipText == "9月19日 · 周末 · 完成 1 件",
            "the heat tooltip names the weekend as well");
        Assert(heat.Find(c => c.Date == new DateTime(2026, 9, 17)).ToolTipText == "9月17日 · 没有完成记录",
            "quiet days say so in heat mode");

        // Inverted heat: only today and earlier take a colour, because in this direction
        // "nothing completed" is the deepest shade and a day that has not arrived yet must
        // not be accused of it.
        List<CalendarCell> inverted = CalendarBuilder.BuildCells(2026, 9, todayKey, dueCounts, doneCounts, doneTexts, true, true, UiLanguage.ZhHans);
        CalendarCell busiestInverted = inverted.Find(c => c.Date == new DateTime(2026, 9, 18));
        CalendarCell quietInverted = inverted.Find(c => c.Date == new DateTime(2026, 9, 19));
        CalendarCell idleInverted = inverted.Find(c => c.Date == new DateTime(2026, 9, 17));
        CalendarCell future = inverted.Find(c => c.Date == new DateTime(2026, 9, 25));
        Assert(busiestInverted.HeatLevel == 0 && quietInverted.HeatLevel == 3,
            "inverted heat walks the palette backwards so more completions means a lighter colour");
        Assert(idleInverted.HeatLevel == HeatScale.Colors.Length - 1,
            "a day with nothing completed takes the deepest shade when inverted");
        Assert(future.HeatLevel == -1, "future days are out of scale in inverted mode");
        Assert(HeatScale.Level(0, 4, true) == 4, "zero completions maps to the deepest inverted level");
        Assert(HeatScale.Level(4, 4, true) == 0 && HeatScale.Level(3, 4, true) == 1
            && HeatScale.Level(2, 4, true) == 2 && HeatScale.Level(1, 4, true) == 3,
            "inverted levels mirror every forward step across the whole palette");
        Assert(HeatScale.Level(2, 4, true) == HeatScale.Colors.Length - 1 - HeatScale.Level(2, 4, false),
            "inversion is an exact mirror of the forward scale");
        Assert(HeatScale.Level(4, 4, false) == 4, "forward heat is unaffected by the inversion flag");
        Assert(CalendarBuilder.HeatFor(new DateTime(2026, 9, 25), todayKey, 0, 4, false).InScale,
            "future days stay in scale when not inverted");
        Assert(!CalendarBuilder.HeatFor(new DateTime(2026, 9, 25), todayKey, 0, 4, true).InScale,
            "HeatFor reports out-of-scale for a future day when inverted");
        Assert(CalendarBuilder.HeatFor(todayKey, todayKey, 0, 4, true).InScale,
            "today itself stays in scale when inverted");

        // 17 August hides the number behind a heart. The keepsake line leads the tooltip
        // in every language while the real date stays spelled out underneath it.
        foreach (UiLanguage language in new[] { UiLanguage.ZhHans, UiLanguage.ZhHant, UiLanguage.English })
        {
            List<CalendarCell> august = CalendarBuilder.BuildCells(2026, 8, todayKey, null, null, null, false, false, language);
            CalendarCell heart = august.Find(c => c.Date == new DateTime(2026, 8, 17));
            Assert(heart.IsAnniversary && heart.DayLabel == Strings.T(language, "anniversary.glyph"),
                "17 august shows a heart instead of the date");
            Assert(heart.ToolTipText.StartsWith(Strings.T(language, "anniversary.tip")),
                "the heart day leads its tooltip with the keepsake line");
            Assert(heart.ToolTipText.Contains(Strings.Date(language, new DateTime(2026, 8, 17))),
                "the heart day still spells out the real date below the line");
            Assert(august.Find(c => c.Date == new DateTime(2026, 8, 16)).DayLabel == "16"
                && august.Find(c => c.Date == new DateTime(2026, 8, 18)).DayLabel == "18",
                "only 17 august turns into a heart");
        }
        Assert(Strings.T(UiLanguage.ZhHans, "anniversary.tip") == "I will love you forever"
            && Strings.T(UiLanguage.ZhHant, "anniversary.tip") == Strings.T(UiLanguage.English, "anniversary.tip"),
            "the keepsake line is deliberately the same sentence in every language");

        // Holidays surface on the calendar cell with a stable code and a name in the
        // active language.
        List<CalendarCell> holidays = CalendarBuilder.BuildCells(2026, 12, todayKey, null, null, null, false, false, UiLanguage.ZhHant);
        CalendarCell christmas = holidays.Find(c => c.Date == new DateTime(2026, 12, 25));
        Assert(christmas.IsHoliday && christmas.HolidayCode == "20261225" && christmas.HolidayName == "聖誕節",
            "holiday cells carry a stable code and a traditional-chinese name");
        Assert(CalendarBuilder.HolidayDate("20261225") == new DateTime(2026, 12, 25) && CalendarBuilder.HolidayDate("bad") == DateTime.MinValue,
            "holiday codes parse back to dates");
        List<CalendarCell> holidaysEn = CalendarBuilder.BuildCells(2026, 12, todayKey, null, null, null, false, false, UiLanguage.English);
        Assert(holidaysEn.Find(c => c.Date == new DateTime(2026, 12, 25)).HolidayName == "Christmas Day",
            "switching the language re-derives the holiday name");

        List<CalendarCell> january = CalendarBuilder.BuildCells(2027, 1, todayKey, null, null, null, false, false, UiLanguage.ZhHans);
        Assert(january[0].Date == new DateTime(2026, 12, 28) && !january[0].IsCurrentMonth,
            "january 2027 backfills december 2026 days");
        Assert(CalendarBuilder.BuildCells(2026, 9, todayKey, null, null, null, false, false, UiLanguage.ZhHans)[18].Count == 0,
            "null count dictionaries are tolerated");

        Assert(HeatScale.Level(0, 5) == 0 && HeatScale.Level(5, 0) == 0, "heat scale is zero without data");
        Assert(HeatScale.Level(1, 4) == 1 && HeatScale.Level(2, 4) == 2 && HeatScale.Level(3, 4) == 3 && HeatScale.Level(4, 4) == 4,
            "heat scale splits the range into quartiles");
        Assert(HeatScale.Colors.Length == 5, "heat palette has a color per level");

        List<TodoRecord> tasks = new List<TodoRecord>
        {
            new TodoRecord { Id = "t1", Text = "明早", DueAt = "2026-09-20T09:00" },
            new TodoRecord { Id = "t2", Text = "明晚", DueAt = "2026-09-20T21:00" },
            new TodoRecord { Id = "t3", Text = "已完成", IsCompleted = true, DueAt = "2026-09-20T10:00" },
            new TodoRecord { Id = "t4", Text = "今天", DueAt = "2026-09-19T23:00" },
            new TodoRecord { Id = "t5", Text = "昨天", DueAt = "2026-09-18T10:00" },
            new TodoRecord { Id = "t6", Text = "无截止" }
        };
        List<TodoRecord> tomorrow = ReminderLogic.TomorrowDues(tasks, now);
        Assert(tomorrow.Count == 2 && tomorrow[0].Id == "t1" && tomorrow[1].Id == "t2",
            "tomorrow dues exclude completed, today and undated tasks, sorted by hour");
        List<TodoRecord> todayDues = ReminderLogic.TodayDues(tasks, now);
        Assert(todayDues.Count == 1 && todayDues[0].Id == "t4", "today dues list only unfinished tasks due today");
        List<TodoRecord> overdue = ReminderLogic.Overdue(tasks, now);
        Assert(overdue.Count == 1 && overdue[0].Id == "t5", "overdue only lists unfinished past-due tasks");
        Assert(ReminderLogic.DuesOn(tasks, new DateTime(2026, 9, 20)).Count == 2, "DuesOn addresses any calendar day");
        Assert(ReminderLogic.DuesOn(tasks, new DateTime(2026, 9, 17)).Count == 0, "DuesOn returns nothing for a quiet day");
        Assert(ReminderLogic.TomorrowDues(tasks, new DateTime(2026, 9, 19, 23, 59, 59)).Count == 2,
            "late-night checks still see tomorrow");
        Assert(ReminderLogic.TomorrowDues(tasks, new DateTime(2026, 9, 20, 0, 0, 0)).Count == 0,
            "after midnight yesterday's tomorrow is gone");

        // Per-slot dedupe: the morning and the afternoon slots never block each other.
        DateTime morningNow = new DateTime(2026, 9, 19, 9, 30, 0);
        Assert(ReminderLogic.ShouldFireSlot(null, ReminderSlot.MorningKind, 9, morningNow, true), "first morning reminder fires");
        Assert(ReminderLogic.ShouldFireSlot(null, ReminderSlot.MorningKind, 9, new DateTime(2026, 9, 19, 11, 0, 0), true),
            "an app started after 09:00 still fires the morning slot that day");
        Assert(!ReminderLogic.ShouldFireSlot(null, ReminderSlot.MorningKind, 9, new DateTime(2026, 9, 19, 8, 59, 0), true),
            "no morning reminder before the hour");
        Assert(!ReminderLogic.ShouldFireSlot("morning@2026-09-19", ReminderSlot.MorningKind, 9, morningNow, true),
            "the morning slot never fires twice in a day");
        Assert(ReminderLogic.ShouldFireSlot("morning@2026-09-18", ReminderSlot.MorningKind, 9, morningNow, true),
            "a new day fires the morning slot again");
        Assert(!ReminderLogic.ShouldFireSlot(null, ReminderSlot.MorningKind, 9, morningNow, false),
            "no dues today means no morning reminder");
        Assert(ReminderLogic.ShouldFireSlot("evening@2026-09-18", ReminderSlot.EveningKind, 16, new DateTime(2026, 9, 19, 16, 0, 0), true),
            "the evening slot has its own dedupe record");
        Assert(ReminderLogic.ShouldFireSlot("morning@2026-09-19", ReminderSlot.EveningKind, 16, new DateTime(2026, 9, 19, 16, 0, 0), true),
            "a morning record does not suppress the evening slot");

        Assert(ReminderLogic.SlotKey(ReminderSlot.MorningKind, new DateTime(2026, 9, 20)) == "morning@2026-09-20",
            "morning slot keys are canonical");
        Assert(ReminderLogic.SlotKey(ReminderSlot.EveningKind, new DateTime(2026, 9, 20)) == "evening@2026-09-20",
            "evening slot keys are canonical");
        Assert(ReminderLogic.IsSlotKey("morning@2026-09-20", ReminderSlot.MorningKind)
            && !ReminderLogic.IsSlotKey("evening@2026-09-20", ReminderSlot.MorningKind)
            && !ReminderLogic.IsSlotKey("morning@2026-9-20", ReminderSlot.MorningKind)
            && !ReminderLogic.IsSlotKey("2026-09-20", ReminderSlot.MorningKind),
            "slot keys are strict and kind-specific");
        Assert(ReminderLogic.SlotDay("evening@2026-09-20", ReminderSlot.EveningKind) == new DateTime(2026, 9, 20),
            "slot keys parse back to their day");
        Assert(ReminderLogic.SlotDay("garbage", ReminderSlot.EveningKind) == DateTime.MinValue,
            "malformed slot keys yield the minimum day");

        Assert(ReminderSlot.IsValidHour(6) && ReminderSlot.IsValidHour(22) && !ReminderSlot.IsValidHour(5) && !ReminderSlot.IsValidHour(23),
            "reminder hours are limited to 06:00-22:00");
        int morningHour = 9, eveningHour = 16;
        ReminderSlot.Repair(ref morningHour, ref eveningHour);
        Assert(morningHour == 9 && eveningHour == 16, "valid slot pair is left alone");
        int badMorning = 2, badEvening = 30;
        ReminderSlot.Repair(ref badMorning, ref badEvening);
        Assert(badMorning == 9 && badEvening == 16, "out-of-range slots fall back to their defaults");
        int clashMorning = 15, clashEvening = 15;
        ReminderSlot.Repair(ref clashMorning, ref clashEvening);
        Assert(clashMorning == 15 && clashEvening == 16, "a clash pushes the evening slot to its default");

        Assert(ReminderLogic.IsValidDayKey("2026-09-19") && !ReminderLogic.IsValidDayKey("2026-9-19") && !ReminderLogic.IsValidDayKey("x"),
            "day keys are strict canonical dates");
    }

    // Mirrors TaskItem.FormatDue so the wording can be asserted without WPF.
    private static string DueLabel(DateTime? due, bool completed, DateTime now, UiLanguage language)
    {
        if (!due.HasValue) return String.Empty;
        DateTime value = due.Value;
        DueState state = DueLogic.State(due, completed, now);
        string hour = Strings.F(Strings.Get(language, "clock.hour"), value.Hour);
        string day;
        switch (DueLogic.Day(value, now))
        {
            case DueDay.Today: day = Strings.T(language, "due.today", hour); break;
            case DueDay.Tomorrow: day = Strings.T(language, "due.tomorrow", hour); break;
            case DueDay.ThisYear: day = Strings.T(language, "due.monthDay", value.Month, value.Day, hour); break;
            default: day = Strings.T(language, "due.fullDate", value.Year, value.Month, value.Day, hour); break;
        }
        return state == DueState.Overdue ? Strings.T(language, "due.overduePrefix", day) : day;
    }
}
