using System;
using System.Collections.Generic;
using System.IO;
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
}
