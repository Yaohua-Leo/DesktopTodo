using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTodo;

internal static class UiTests
{
    private static int assertions;
    private static readonly List<Window> windows = new List<Window>();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--cold-read") return RunColdRead(args[1]);
        if (args.Length != 1) { Console.Error.WriteLine("Expected the tests/data directory."); return 2; }
        string dataRoot = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
        string dataDirectory = Path.Combine(dataRoot, "UiTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Pin the OS appearance so theme resolution is deterministic: scenarios
        // below assume "follow system" means light unless they flip this probe.
        Themes.SystemLightProbe = () => true;
        StringBuilder bindingErrors = new StringBuilder();
        TextWriterTraceListener listener = new TextWriterTraceListener(new StringWriter(bindingErrors));
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        try
        {
            RunScenarios(dataDirectory);
            RunStartupReadProtection(Path.Combine(dataDirectory, "startup-read"));
            RunAutomaticRecovery(Path.Combine(dataDirectory, "automatic-recovery"));
            RunExternalChangeProtection(Path.Combine(dataDirectory, "external-change"));
            RunDataDirectoryScenarios(Path.Combine(dataDirectory, "different-working-directory"));
            RunDueScenarios(Path.Combine(dataDirectory, "due"));
            RunCompletionLogScenarios(Path.Combine(dataDirectory, "completion-log"));
            RunRestoreScenarios(Path.Combine(dataDirectory, "restore"));
            RunRestoreWindowScenarios(Path.Combine(dataDirectory, "restore-window"));
            RunCalendarScenarios(Path.Combine(dataDirectory, "calendar"));
            RunReminderScenarios(Path.Combine(dataDirectory, "reminder"));
            RunLanguageScenarios(Path.Combine(dataDirectory, "language"));
            RunThemeScenarios(Path.Combine(dataDirectory, "theme"));
            RunTrayHideShowScenarios(Path.Combine(dataDirectory, "tray-hide"));
            RunWindowCloseExitScenario(Path.Combine(dataDirectory, "window-close-exit"));
            listener.Flush();
            Assert(bindingErrors.Length == 0, "no WPF data-binding errors: " + bindingErrors.ToString());
            Console.WriteLine("PASS: " + assertions + " UI integration assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            if (bindingErrors.Length > 0) Console.Error.WriteLine("Binding trace:\n" + bindingErrors.ToString());
            return 1;
        }
        finally
        {
            foreach (Window window in windows.ToArray())
                if (window.IsLoaded) window.Close();
            application.Shutdown();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            listener.Close();
            // Only this invocation's generated directory is eligible for cleanup.
            string resolved = Path.GetFullPath(dataDirectory);
            if (resolved.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("UiTests-", StringComparison.Ordinal))
                Directory.Delete(resolved, true);
        }
    }

    private static void RunScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        TextBox input = (TextBox)window.FindName("TaskInput");
        Assert(controller.Tasks.Count == 0 && controller.EmptyVisibility == Visibility.Visible, "empty state is ready");
        Assert(controller.IsPinned && window.Topmost, "new card starts pinned");
        Assert(IsNativeTopmost(window), "new card has the Win32 WS_EX_TOPMOST style");
        Assert(!controller.AddCommand.CanExecute(null), "empty text cannot be added");
        input.Text = " \t\u3000 ";
        Pump(window);
        Assert(!controller.AddCommand.CanExecute(null), "whitespace-only input cannot be added");
        controller.AddCommand.Execute(null);
        Assert(controller.Tasks.Count == 0, "executing disabled add produces no task");

        input.Text = "  买牛奶 🥛 和 bread  ";
        Pump(window);
        Assert(controller.InputText == input.Text, "input binding updates the controller");
        controller.AddCommand.Execute(null);
        Pump(window);
        TaskItem first = controller.Tasks[0];
        Assert(first.Text == "买牛奶 🥛 和 bread" && input.Text == "", "add trims outside whitespace and clears the bound input");
        Assert(controller.EmptyVisibility == Visibility.Collapsed, "adding a task hides the empty state");
        Assert(ReadState(directory).Tasks[0].Text == first.Text, "add persists Unicode text");

        CheckBox checkbox = TaskCheckbox(window, 0);
        checkbox.IsChecked = true;
        Pump(window);
        Assert(first.IsCompleted, "checking the bound circle marks the task complete");
        Assert(ReadState(directory).Tasks[0].IsCompleted, "completion is persisted immediately");
        TextBlock label = TaskLabel(window, 0);
        Assert(label.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough), "completed task has a strikethrough decoration");
        Assert(controller.CompletedPercent == 100 && controller.RemainingLabel == Strings.T(controller.Language, "card.summary.done"),
            "completion updates progress and summary");
        checkbox.IsChecked = false;
        Pump(window);
        Assert(!first.IsCompleted && !ReadState(directory).Tasks[0].IsCompleted, "unchecking restores and persists the task");
        Assert(label.TextDecorations.Count == 0, "restoring the task removes its strikethrough");

        Add(controller, window, "第二件任务");
        Add(controller, window, "第三件任务");
        Add(controller, window, "第四件任务");
        TaskItem second = controller.Tasks[1];
        controller.InputText = "尚未添加的新任务草稿";
        controller.BeginEdit(first);
        Assert(controller.InputText == first.Text && controller.AddGlyph == "✓", "begin edit loads task text and changes action");
        controller.InputText = "第一件修改草稿";
        controller.BeginEdit(second);
        controller.InputText = "第二件修改草稿";
        controller.BeginEdit(first);
        Assert(controller.InputText == "第一件修改草稿", "switching edits preserves the first task draft");
        controller.AddCommand.Execute(null);
        Pump(window);
        Assert(first.Text == "第一件修改草稿" && controller.Tasks.Count == 4, "edit saves in place without adding a task");
        Assert(controller.InputText == "尚未添加的新任务草稿", "saving an edit restores the new-task draft");
        controller.BeginEdit(second);
        Assert(controller.InputText == "第二件修改草稿", "another task's edit draft survives saving the first edit");
        controller.AddCommand.Execute(null);
        Pump(window);
        Assert(second.Text == "第二件修改草稿" && controller.InputText == "尚未添加的新任务草稿", "saving another edit retains the new-task draft");
        controller.AddCommand.Execute(null);
        Pump(window);
        Assert(controller.Tasks.Count == 5 && controller.Tasks[4].Text == "尚未添加的新任务草稿", "restored draft can be added normally");
        Assert(ReadState(directory).Tasks[0].Text == first.Text && ReadState(directory).Tasks[1].Text == second.Text, "edited task contents are persisted");

        string[] beforeDelete = controller.Tasks.Select(t => t.Id).ToArray();
        TaskItem deleted = controller.Tasks[2];
        deleted.DeleteCommand.Execute(null);
        Assert(controller.Tasks.Count == 4 && !controller.Tasks.Contains(deleted), "delete command removes selected task");
        Assert(controller.UndoCommand.CanExecute(null) && controller.UndoVisibility == Visibility.Visible, "delete exposes undo");
        Assert(ReadState(directory).Tasks.Count == 4, "deletion persists immediately");
        controller.UndoCommand.Execute(null);
        Assert(controller.Tasks.Select(t => t.Id).SequenceEqual(beforeDelete), "delete undo restores exact task order");
        Assert(!controller.UndoCommand.CanExecute(null) && controller.UndoVisibility == Visibility.Collapsed, "undo becomes unavailable after restoring");

        controller.Tasks[1].IsCompleted = true;
        controller.Tasks[3].IsCompleted = true;
        string[] beforeClear = controller.Tasks.Select(t => t.Id).ToArray();
        string[] remaining = controller.Tasks.Where(t => !t.IsCompleted).Select(t => t.Id).ToArray();
        controller.InputText = "清除操作期间的新草稿";
        controller.BeginEdit(controller.Tasks[1]);
        controller.InputText = "尚未保存的已完成任务编辑";
        Assert(controller.ClearCompletedCommand.CanExecute(null), "clear completed is enabled when tasks are complete");
        controller.ClearCompletedCommand.Execute(null);
        Assert(controller.Tasks.Select(t => t.Id).SequenceEqual(remaining), "clear removes only completed tasks and preserves remaining order");
        Assert(controller.InputText == "清除操作期间的新草稿", "clearing the edited task restores the new-task draft");
        Assert(!controller.ClearCompletedCommand.CanExecute(null) && ReadState(directory).Tasks.Count == 3, "clear persists and disables when nothing is complete");
        controller.UndoCommand.Execute(null);
        Assert(controller.Tasks.Select(t => t.Id).SequenceEqual(beforeClear), "clear undo restores exact original order");
        Assert(controller.Tasks[1].IsCompleted && controller.Tasks[3].IsCompleted, "clear undo restores completion states");
        Assert(ReadState(directory).Tasks.Select(t => t.Id).SequenceEqual(beforeClear), "undo restoration is persisted");

        ToggleButton pin = (ToggleButton)window.FindName("PinButton");
        pin.IsChecked = false;
        Pump(window);
        Assert(!controller.IsPinned && !window.Topmost, "pin button clears the window's Topmost flag");
        Assert(!IsNativeTopmost(window), "unpinning clears the Win32 WS_EX_TOPMOST style");
        Assert(!ReadState(directory).IsPinned, "unpinning is persisted");
        pin.IsChecked = true;
        Pump(window);
        Assert(controller.IsPinned && window.Topmost && ReadState(directory).IsPinned, "pin button restores and persists Topmost");
        Assert(IsNativeTopmost(window), "pinning applies the Win32 WS_EX_TOPMOST style");
        pin.IsChecked = false;
        Pump(window);
        window.Width = 430;
        window.Height = 620;
        Pump(window);
        TodoRecord[] expected = ReadState(directory).Tasks.ToArray();
        window.Close();
        Pump(null);
        Assert(!window.IsLoaded, "closing destroys the test window");
        AppState closedState = ReadState(directory);
        Assert(closedState.Width == 430 && closedState.Height == 620, "close saves the latest window dimensions");

        Window reopenedWindow;
        TodoController reopened = OpenWindow(directory, out reopenedWindow);
        Assert(reopened.Tasks.Select(t => t.Id).SequenceEqual(expected.Select(t => t.Id)), "relaunch restores task identities and order");
        Assert(reopened.Tasks.Select(t => t.Text).SequenceEqual(expected.Select(t => t.Text)), "relaunch restores edited Unicode text");
        Assert(reopened.Tasks.Select(t => t.IsCompleted).SequenceEqual(expected.Select(t => t.IsCompleted)), "relaunch restores completion states");
        Assert(!reopened.IsPinned && !reopenedWindow.Topmost, "relaunch restores the saved pin state");
        Assert(!IsNativeTopmost(reopenedWindow), "relaunch restores the native unpinned window style");
        Assert(reopenedWindow.Width == 430 && reopenedWindow.Height == 620, "relaunch restores window dimensions");
        Assert(!reopened.SaveStatus.Contains(Strings.Get(reopened.Language, "footer.saveFailed")), "successful operations keep the saved status healthy");
        reopenedWindow.Close();
        Pump(null);

        RunResizeScenarios(Path.Combine(directory, "resize"));
    }

    private static void RunResizeScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        double width = window.ActualWidth;
        double height = window.ActualHeight;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert(NativeHitTest(window, new Point(14, height / 2)) == 10, "visible left border requests native horizontal resize");
        Assert(NativeHitTest(window, new Point(width - 14, height / 2)) == 11, "visible right border requests native horizontal resize");
        Assert(NativeHitTest(window, new Point(width / 2, 14)) == 12, "visible top border requests native vertical resize");
        Assert(NativeHitTest(window, new Point(width / 2, height - 14)) == 15, "visible bottom border requests native vertical resize");

        // These points lie on the rounded card's opaque arcs, not its transparent corner pixels.
        Assert(NativeHitTest(window, new Point(26, 14)) == 13, "top-left rounded corner requests native diagonal resize");
        Assert(NativeHitTest(window, new Point(width - 26, 14)) == 14, "top-right rounded corner requests native diagonal resize");
        Assert(NativeHitTest(window, new Point(26, height - 14)) == 16, "bottom-left rounded corner requests native diagonal resize");
        Assert(NativeHitTest(window, new Point(width - 26, height - 14)) == 17, "bottom-right rounded corner requests native diagonal resize");
        Assert(NativeHitTest(window, new Point(14, 26)) == 13, "top-left corner also resizes from the left border");
        Assert(NativeHitTest(window, new Point(width - 14, 26)) == 14, "top-right corner also resizes from the right border");
        Assert(NativeHitTest(window, new Point(14, height - 26)) == 16, "bottom-left corner also resizes from the left border");
        Assert(NativeHitTest(window, new Point(width - 14, height - 26)) == 17, "bottom-right corner also resizes from the right border");

        Assert(NativeHitTest(window, new Point(width / 2, height / 2)) == 1, "card body remains a normal client area");
        Assert(NativeHitTest(window, new Point(30, 30)) == 1, "interior near a corner remains a normal client area");
        foreach (string name in new[] { "TaskInput", "AddButton", "PinButton", "MinimizeButton", "CloseButton" })
        {
            FrameworkElement control = (FrameworkElement)window.FindName(name);
            Point center = control.TranslatePoint(new Point(control.ActualWidth / 2, control.ActualHeight / 2), window);
            Assert(NativeHitTest(window, center) == 1, name + " remains clickable instead of starting resize");
        }

        double originalLeft = window.Left;
        double originalTop = window.Top;
        window.Left = -width / 2;
        window.Top = 50;
        Pump(window);
        Point leftEdge = new Point(14, window.ActualHeight / 2);
        Assert(window.PointToScreen(leftEdge).X < 0, "negative-coordinate resize scenario reaches a negative screen X");
        Assert(NativeHitTest(window, leftEdge) == 10, "left border resize handles signed negative screen coordinates");
        window.Left = originalLeft;
        window.Top = originalTop;
        Pump(window);

        window.WindowState = WindowState.Maximized;
        Pump(window);
        Assert(NativeHitTest(window, new Point(14, window.ActualHeight / 2)) == 1, "maximized window does not expose a left resize border");
        Assert(NativeHitTest(window, new Point(window.ActualWidth / 2, 14)) == 1, "maximized window does not expose a top resize border");
        window.WindowState = WindowState.Normal;
        Pump(window);
        Assert(NativeHitTest(window, new Point(14, window.ActualHeight / 2)) == 10, "restoring the window restores native border resize");

        for (int i = 0; i < 24; i++) Add(controller, window, "待办任务 " + (i + 1));
        TaskCheckbox(window, 0).IsChecked = true;
        Pump(window);
        ScrollViewer scroller = (ScrollViewer)window.FindName("TaskScroller");
        scroller.ScrollToTop();
        Pump(window);
        double oldViewportHeight = scroller.ViewportHeight;
        int oldVisibleRows = CountFullyVisibleTasks(window, controller.Tasks.Count);
        Assert(scroller.ScrollableHeight > 0 && oldVisibleRows > 0, "many tasks initially overflow the visible list");
        string completedText = controller.Tasks[0].Text;
        string[] taskOrder = controller.Tasks.Select(t => t.Id).ToArray();

        window.Height += 180;
        Pump(window);
        Assert(scroller.ViewportHeight > oldViewportHeight + 150, "increasing card height gives the added space to the task viewport");
        Assert(CountFullyVisibleTasks(window, controller.Tasks.Count) > oldVisibleRows, "a taller card displays more complete task rows");
        Assert(controller.IsPinned && window.Topmost && IsNativeTopmost(window), "resizing preserves the pinned native topmost window");
        Assert(controller.Tasks.Select(t => t.Id).SequenceEqual(taskOrder), "resizing preserves every task and its order");
        Assert(controller.Tasks[0].IsCompleted && TaskLabel(window, 0).Text == completedText
            && TaskLabel(window, 0).TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough),
            "resizing preserves completed task text and its strikethrough");
        window.Close();
        Pump(null);
    }

    private static AppState SeedLegacyState(string directory)
    {
        Directory.CreateDirectory(directory);
        AppState state = new AppState { IsPinned = false, Width = 440, Height = 650 };
        for (int i = 0; i < 20; i++)
            state.Tasks.Add(new TodoRecord { Id = "restart-item-" + i, Text = "重启恢复测试 " + i, IsCompleted = i % 4 == 0 });
        // Reproduce an upgraded installation: the previous build has a primary
        // and one backup, but no history snapshots to mask the startup lock.
        using (FileStream stream = File.Create(Path.Combine(directory, "tasks.json")))
            new DataContractJsonSerializer(typeof(AppState)).WriteObject(stream, state);
        File.Copy(Path.Combine(directory, "tasks.json"), Path.Combine(directory, "tasks.backup.json"));
        return state;
    }

    private static void RunStartupReadProtection(string directory)
    {
        AppState expected = SeedLegacyState(directory);
        string primary = Path.Combine(directory, "tasks.json");
        string backup = Path.Combine(directory, "tasks.backup.json");
        byte[] original = File.ReadAllBytes(primary);
        byte[] originalBackup = File.ReadAllBytes(backup);
        Window window;
        TodoController controller;
        using (FileStream primaryLock = new FileStream(primary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (FileStream backupLock = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            controller = OpenWindow(directory, out window);
            Assert(controller.Tasks.Count == 0 && !controller.CanEditTasks, "a transient startup read failure does not expose an editable empty task list");
            controller.InputText = "读取期间保留的新任务草稿";
            Assert(!controller.AddCommand.CanExecute(null) && !controller.ClearCompletedCommand.CanExecute(null)
                && !controller.UndoCommand.CanExecute(null), "task mutation commands are disabled while startup data is unavailable");
            controller.AddCommand.Execute(null);
            Assert(controller.Tasks.Count == 0 && controller.InputText == "读取期间保留的新任务草稿", "disabled add preserves the input draft without creating tasks");
            Assert(controller.ReloadCommand.CanExecute(null), "manual retry is available after startup read failure");
            Assert(controller.EmptyVisibility == Visibility.Collapsed, "failed loading does not claim a fresh empty list");
            Assert(controller.SaveStatus.Contains(Strings.Get(controller.Language, "footer.blocked"))
                && !controller.SaveStatus.Contains(Strings.Get(controller.Language, "footer.saving").Replace("{0} ", "")), "startup read failure has a persistent protective status");
            // The 5s recovery tick re-attempts Load under the held locks; its retry
            // sleeps block the dispatcher for ~1.1s, so the 10s toast expiry can land
            // late. Give the assert enough slack beyond that worst case.
            WaitForDispatcher(TimeSpan.FromMilliseconds(12500));
            Assert(controller.ToastVisibility == Visibility.Collapsed && controller.SaveStatus.Contains(Strings.Get(controller.Language, "footer.blocked")), "read failure status survives expiration of the temporary toast");
        }
        Assert(controller.RefreshFromDisk(), "manual retry succeeds after transient file locks are released");
        Pump(window);
        Assert(controller.Tasks.Select(t => t.Id).SequenceEqual(expected.Tasks.Select(t => t.Id)), "retry restores all twenty task identities in order");
        Assert(controller.Tasks.Select(t => t.Text).SequenceEqual(expected.Tasks.Select(t => t.Text))
            && controller.Tasks.Select(t => t.IsCompleted).SequenceEqual(expected.Tasks.Select(t => t.IsCompleted)), "retry restores task text and completion states");
        Assert(controller.InputText == "读取期间保留的新任务草稿", "recovering historical tasks preserves the new-task input draft");
        Assert(controller.CanEditTasks && controller.AddCommand.CanExecute(null) && !controller.IsPinned && !window.Topmost, "successful retry restores editing and the saved pin state");
        Assert(controller.SaveStatus.Contains("20") && !controller.SaveStatus.Contains(Strings.Get(controller.Language, "footer.blocked")), "successful retry replaces the persistent error with the loaded count");
        Assert(File.ReadAllBytes(primary).SequenceEqual(original) && File.ReadAllBytes(backup).SequenceEqual(originalBackup), "loading failure and retry preserve both original task files byte for byte");
        window.Close();
        Pump(null);

        string closeDirectory = Path.Combine(directory, "close-while-unavailable");
        SeedLegacyState(closeDirectory);
        string closePrimary = Path.Combine(closeDirectory, "tasks.json");
        string closeBackup = Path.Combine(closeDirectory, "tasks.backup.json");
        byte[] closeOriginal = File.ReadAllBytes(closePrimary);
        byte[] closeOriginalBackup = File.ReadAllBytes(closeBackup);
        using (FileStream primaryLock = new FileStream(closePrimary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (FileStream backupLock = new FileStream(closeBackup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            TodoController failed = OpenWindow(closeDirectory, out window);
            Assert(!failed.CanEditTasks, "close-protection scenario starts with an unavailable task store");
            window.Close();
            Pump(null);
            Assert(!window.IsLoaded, "an unchanged failed startup can close without a misleading unsaved-edits dialog");
        }
        Assert(File.ReadAllBytes(closePrimary).SequenceEqual(closeOriginal)
            && File.ReadAllBytes(closeBackup).SequenceEqual(closeOriginalBackup), "closing an empty failed startup cannot overwrite either historical task file");
    }

    private static void RunAutomaticRecovery(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Assert(controller.Tasks.Count == 0, "automatic recovery scenario begins before the task files appear");
        controller.InputText = "自动恢复期间的新草稿";
        SeedLegacyState(directory);
        WaitForDispatcher(TimeSpan.FromMilliseconds(5700));
        Pump(window);
        Assert(controller.Tasks.Count == 20 && controller.CanEditTasks, "the periodic startup recovery check loads tasks that become available later");
        Assert(controller.InputText == "自动恢复期间的新草稿", "automatic recovery preserves the new-task draft");
        Assert(controller.Tasks.Count(t => t.IsCompleted) == 5 && !controller.IsPinned, "automatic recovery includes completion and pin states");
        window.Close();
        Pump(null);
    }

    private static void RunExternalChangeProtection(string directory)
    {
        SeedLegacyState(directory);
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        string primary = Path.Combine(directory, "tasks.json");
        byte[] original = File.ReadAllBytes(primary);
        controller.BeginEdit(controller.Tasks[0]);
        controller.InputText = "此窗口尚未保存的修改";
        Assert(!controller.ReloadCommand.CanExecute(null), "manual reload is disabled while a task edit draft is active");

        AppRepository other = new AppRepository(directory);
        AppState external = other.Load();
        external.Tasks.Add(new TodoRecord { Id = "external-item", Text = "另一窗口已保存的新增任务" });
        other.Save(external);
        byte[] externalBytes = File.ReadAllBytes(primary);
        Assert(!controller.RefreshFromDisk() && controller.InputText == "此窗口尚未保存的修改", "external reload cannot discard an active task edit draft");
        controller.AddCommand.Execute(null);
        Pump(window);
        Assert(controller.Tasks[0].Text == "此窗口尚未保存的修改" && controller.SaveStatus.Contains(Strings.Get(controller.Language, "footer.saveFailed")), "a conflicting save preserves local task edits and reports the failure");
        Assert(File.ReadAllBytes(primary).SequenceEqual(externalBytes), "a stale controller cannot overwrite the externally saved task list");
        Assert(!controller.ReloadCommand.CanExecute(null) && !controller.RefreshFromDisk(), "reload stays disabled while local task changes remain unsaved");
        WaitForDispatcher(TimeSpan.FromMilliseconds(5700));
        Assert(controller.Tasks.Count == 20 && controller.Tasks[0].Text == "此窗口尚未保存的修改"
            && File.ReadAllBytes(primary).SequenceEqual(externalBytes), "automatic refresh preserves both unsaved local edits and the separate on-disk changes");

        // Restore this synthetic fixture's original baseline to resolve the
        // intentional conflict without displaying a modal close confirmation.
        File.WriteAllBytes(primary, original);
        controller.TaskChanged();
        Assert(!controller.SaveStatus.Contains(Strings.Get(controller.Language, "footer.saveFailed")) && ReadState(directory).Tasks[0].Text == "此窗口尚未保存的修改", "the preserved local edit can be saved once the synthetic conflict is resolved");
        window.Close();
        Pump(null);
    }

    private static void RunDataDirectoryScenarios(string directory)
    {
        Directory.CreateDirectory(directory);
        string previousDirectory = Environment.CurrentDirectory;
        string defaultDirectory = Program.ResolveDataDirectory(new string[0]);
        try
        {
            Environment.CurrentDirectory = directory;
            Assert(Program.ResolveDataDirectory(new string[0]) == defaultDirectory && Path.IsPathRooted(defaultDirectory), "default task storage is absolute and independent of the current working directory");
            string fixture = Path.Combine(directory, "saved-state");
            SeedLegacyState(fixture);
            Assert(Program.ResolveDataDirectory(new[] { "--data-dir", fixture })
                == Program.ResolveDataDirectory(new[] { "--data-dir", fixture + Path.DirectorySeparatorChar }), "a trailing directory separator cannot split the startup single-instance identity");
            Assert(!Program.HasTrayFlag(new string[0]) && Program.HasTrayFlag(new[] { "--tray" })
                && Program.HasTrayFlag(new[] { "--data-dir", fixture, "--tray" }), "the tray flag is detected anywhere in the arguments");
            Assert(Program.ResolveDataDirectory(new[] { "--tray" }) == defaultDirectory, "the tray flag alone uses the default directory");
            Assert(Program.ResolveDataDirectory(new[] { "--data-dir", fixture, "--tray" })
                == Program.ResolveDataDirectory(new[] { "--data-dir", fixture }), "the tray flag combines with a data directory");

            ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--cold-read \"" + fixture + "\"");
            start.WorkingDirectory = directory;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (Process child = Process.Start(start))
            {
                if (!child.WaitForExit(15000)) throw new Exception("The isolated cold-start test did not finish.");
                string output = child.StandardOutput.ReadToEnd();
                string error = child.StandardError.ReadToEnd();
                Assert(child.ExitCode == 0 && output.Contains("COLD-READ PASS"), "a fresh process restores all saved tasks from an unrelated working directory: " + error);
            }
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    // A real process launched from the built exe, closed through its main window
    // (the taskbar-close / Alt+F4 path), must actually terminate: before the
    // window.Closed shutdown hook this left a windowless zombie process alive.
    private static void RunWindowCloseExitScenario(string baseDirectory)
    {
        string appBinary = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "DesktopTodo.exe");
        Assert(File.Exists(appBinary), "the app binary sits next to the test binary for the window-close exit probe");
        string directory = Path.Combine(baseDirectory, "window-close-exit");
        Directory.CreateDirectory(directory);
        ProcessStartInfo start = new ProcessStartInfo(appBinary, "--data-dir \"" + directory + "\"");
        start.WorkingDirectory = directory;
        start.UseShellExecute = true;
        using (Process child = Process.Start(start))
        {
            IntPtr handle = IntPtr.Zero;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                child.Refresh();
                if (child.HasExited) throw new Exception("The app exited before showing its window.");
                handle = child.MainWindowHandle;
                if (handle != IntPtr.Zero) break;
                WaitForDispatcher(TimeSpan.FromMilliseconds(200));
            }
            if (handle == IntPtr.Zero) { StopChildAndFail(child); return; }
            Assert(SendMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero
                || !child.HasExited, "WM_CLOSE was delivered to the main window");
            if (!child.WaitForExit(15000))
            {
                child.Kill();
                throw new Exception("FAIL: closing the main window must terminate the process (zombie regression).");
            }
            Assert(child.ExitCode == 0, "the window-close exit path returns a clean exit code");
        }
    }

    private static void StopChildAndFail(Process child)
    {
        if (!child.HasExited) child.Kill();
        throw new Exception("FAIL: the app never showed a main window handle within 15s.");
    }

    private static void RunDueScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Add(controller, window, "带截止的任务");
        TaskItem task = controller.Tasks[0];
        Assert(task.DueAt == null && task.DueVisibility == Visibility.Collapsed, "new task has no deadline");

        DateTime tomorrow18 = DateTime.Today.AddDays(1).AddHours(18);
        task.DueAt = tomorrow18;
        Pump(window);
        Assert(task.DueText == TaskItem.FormatDue(tomorrow18, false, DateTime.Now, controller.Language) && task.DueText.Contains(Strings.Get(controller.Language, "due.tomorrow").Replace(" {0}", "")), "tomorrow deadline labels itself");
        Assert(ReadState(directory).Tasks[0].DueAt == DueLogic.Format(tomorrow18), "deadline persists immediately");
        Button dueButton = Descendants<Button>(TaskPresenter(window, 0)).Single(b => b.Name == "Due");
        Assert(dueButton.Command == task.PickDueCommand, "hover calendar button wires the picker command");
        TextBlock dueLabel = Descendants<TextBlock>(TaskPresenter(window, 0)).Single(t => t.Name == "DueLabel");
        Assert(dueLabel.Visibility == Visibility.Visible && dueLabel.Text == task.DueText, "deadline label shows under the task text");

        DateTime later = DateTime.Now.AddHours(2);
        task.DueAt = later;
        // The label is built by the localised formatter; assert it agrees with the
        // shared state machine rather than duplicating the wording here.
        Assert(task.DueText == TaskItem.FormatDue(later, false, DateTime.Now, controller.Language), "same-day label matches the shared due logic");
        Assert(DueLogic.State(later, false, DateTime.Now) == DueState.Today, "a later-today deadline is in the Today state");
        task.DueAt = DateTime.Now.AddHours(-3);
        Assert(task.DueText.StartsWith(Strings.Get(controller.Language, "due.overduePrefix").Replace(" · {0}", "")), "past deadline is overdue");
        Assert(((SolidColorBrush)task.DueBrush).Color == Color.FromRgb(0xC0, 0x50, 0x4D), "overdue deadline paints red");
        task.IsCompleted = true;
        Pump(window);
        Assert(!task.DueText.StartsWith(Strings.Get(controller.Language, "due.overduePrefix").Replace(" · {0}", "")), "completed deadline drops the overdue prefix");
        Assert(dueLabel.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough), "completed deadline label is struck through");
        task.IsCompleted = false;
        Pump(window);

        controller.OpenDuePicker(task);
        Pump(window);
        Popup popup = (Popup)window.FindName("DuePickerPopup");
        Assert(popup.IsOpen, "picker popup opens for the task");
        Border popupRoot = popup.Child as Border;
        Assert(popupRoot != null && Object.ReferenceEquals(popupRoot.DataContext, controller), "popup content inherits the controller as DataContext");
        Assert(controller.PickerDays.Count == 42, "picker month renders a full grid");
        Assert(controller.PickerTitle.Contains("带截止的任务"), "picker names its task");
        CalendarDayItem tomorrowCell = controller.PickerDays.Single(c => c.Date == DateTime.Today.AddDays(1));
        tomorrowCell.SelectCommand.Execute(null);
        Pump(window);
        Assert(controller.PickerHoursEnabled && controller.SetDueHourCommand.CanExecute("9:00"), "picking a date unlocks the hour grid");
        controller.SetDueHourCommand.Execute("18:00");
        Pump(window);
        Assert(!popup.IsOpen, "choosing an hour closes the picker");
        Assert(task.DueAt == DateTime.Today.AddDays(1).AddHours(18), "picker sets date plus hour");
        Assert(ReadState(directory).Tasks[0].DueAt == DueLogic.Format(DateTime.Today.AddDays(1).AddHours(18)), "picker choice persists");

        controller.OpenDuePicker(task);
        Pump(window);
        Assert(controller.PickerCurrentDue.Contains(Strings.Get(controller.Language, "due.tomorrow").Replace(" {0}", "")), "reopening shows the current deadline");
        controller.ClearDueCommand.Execute(null);
        Pump(window);
        Assert(task.DueAt == null && !popup.IsOpen && ReadState(directory).Tasks[0].DueAt == null, "clearing removes and persists the deadline");
        window.Close();
        Pump(null);
    }

    private static void RunCompletionLogScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Add(controller, window, "第一件");
        Add(controller, window, "第二件");
        TaskItem first = controller.Tasks[0];
        CheckBox box = TaskCheckbox(window, 0);
        box.IsChecked = true;
        Pump(window);
        CompletionLog log = new CompletionLog(directory);
        List<CompletionEntry> entries = log.ReadAll();
        Assert(entries.Count == 1 && entries[0].Id == first.Id && entries[0].Text == "第一件", "completing a task appends a text-snapshot record");
        Assert((DateTime.Now - entries[0].Done).TotalMinutes < 5, "record carries the completion time");
        box.IsChecked = false;
        Pump(window);
        Assert(log.ReadAll().Count == 0, "restoring a task removes its newest record");
        controller.Tasks[0].IsCompleted = true;
        controller.Tasks[1].IsCompleted = true;
        Pump(window);
        controller.ClearCompletedCommand.Execute(null);
        Pump(window);
        Assert(controller.Tasks.Count == 0, "clear removes completed tasks");
        Assert(log.ReadAll().Count == 2, "cleared tasks keep their completion history for the heatmap");
        window.Close();
        Pump(null);
    }

    private static void RunRestoreScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Add(controller, window, "可恢复的未完成任务");
        Add(controller, window, "会被单条删除的任务");
        Add(controller, window, "可恢复的已完成任务");
        controller.Tasks[2].IsCompleted = true;
        Pump(window);

        // One single delete plus one clear: both paths must land in the archive.
        TaskItem deleted = controller.Tasks[1];
        deleted.DeleteCommand.Execute(null);
        controller.ClearCompletedCommand.Execute(null);
        Pump(window);
        Assert(controller.Tasks.Count == 1 && controller.Tasks[0].Text == "可恢复的未完成任务", "delete and clear leave one open task");
        ClearedLog archive = new ClearedLog(directory);
        List<ClearedEntry> archived = archive.ReadAll();
        Assert(archived.Count == 2, "delete and clear both append to cleared.jsonl");
        Assert(archived.TrueForAll(e => e.Id == deleted.Id || e.Text == "可恢复的已完成任务"), "archived ids and text match the removed tasks");
        Assert(archived.Find(e => e.Text == "可恢复的已完成任务").Done, "the cleared completed task archives its done flag");

        // The preview count shares the Restorable pipeline with the tray item.
        Assert(controller.RestorePreviewCount == 2, "restore preview count reflects the archive minus nothing");

        // Restoring everything: append-only archive, ids return with their state.
        controller.ApplyRestore(archive.ReadAll());
        Pump(window);
        Assert(controller.Tasks.Count == 3, "apply restore returns all three tasks");
        TaskItem restoredDone = controller.Tasks.Single(t => t.Text == "可恢复的已完成任务");
        Assert(restoredDone.IsCompleted, "restored task keeps its completed state");
        Assert(archive.ReadAll().Count == 2, "the archive itself is never consumed");
        Assert(ReadState(directory).Tasks.Count == 3, "restored tasks persist");
        Assert(controller.RestorePreviewCount == 0, "restored ids no longer count as restorable");

        // Deleting a task with the same text but a new id re-opens one candidate.
        TaskItem again = controller.Tasks.Single(t => t.Text == "会被单条删除的任务");
        again.DeleteCommand.Execute(null);
        Pump(window);
        Assert(controller.RestorePreviewCount == 1, "a fresh delete of a restored id becomes restorable again");
        window.Close();
        Pump(null);
    }

    private static void RunRestoreWindowScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Add(controller, window, "alpha 报告");
        Add(controller, window, "beta 报告");
        Add(controller, window, "gamma 琐事");
        controller.Tasks[0].IsCompleted = true;
        Pump(window);
        TaskItem beta = controller.Tasks[1];
        TaskItem gamma = controller.Tasks[2];
        beta.DeleteCommand.Execute(null);   // open task, deleted now
        gamma.DeleteCommand.Execute(null);  // stays open; also deleted now
        Pump(window);
        Assert(controller.Tasks.Count == 1 && controller.Tasks[0].Text == "alpha 报告", "two tasks left the list");
        Assert(controller.RestorePreviewCount == 2, "two archive candidates");

        // Building with an empty archive returns null instead of a window.
        controller.Tasks[0].DeleteCommand.Execute(null);
        controller.ApplyRestore(new ClearedLog(directory).ReadAll().FindAll(e => e.Text == "alpha 报告"));
        Pump(window);
        // archive: alpha(done) + beta(open) + gamma(open); alpha restored, so restorable = beta+gamma
        Assert(controller.RestorePreviewCount == 2, "restoring alpha leaves beta and gamma restorable");
        Assert(controller.Tasks.Count == 1 && controller.Tasks[0].Text == "alpha 报告" && controller.Tasks[0].IsCompleted,
            "alpha returned completed");

        RestoreWindow restore = controller.BuildRestoreWindow();
        Assert(restore != null, "restore window builds with candidates");
        restore.ShowActivated = false;
        restore.Show();
        Pump(restore);
        Assert(restore.EntryList.Items.Count == 2, "all range shows every candidate");
        Assert(restore.CurrentSelection().Count == 2, "rows start fully checked");
        Assert(restore.RestoreButton.IsEnabled, "restore enabled with a checked row");
        Assert(restore.ExactDatePicker.Visibility == Visibility.Collapsed, "date picker hidden outside exact mode");

        // Keyword search narrows to matching text only; checks reset to all.
        restore.SearchBox.Text = "报告";
        Pump(restore);
        Assert(restore.EntryList.Items.Count == 1, "search filters to matching text");
        Assert(restore.CurrentSelection().Count == 1, "visible rows are checked after filtering");

        // A keyword matching nothing empties the list and disables restore.
        restore.SearchBox.Text = "不存在的关键字";
        Pump(restore);
        Assert(restore.EntryList.Items.Count == 0 && restore.EmptyLabel.Visibility == Visibility.Visible,
            "empty state shows when nothing matches");
        Assert(!restore.RestoreButton.IsEnabled, "restore disabled without a checked row");

        // The day window keeps only clearings from the last 24 hours - both
        // clearings just happened, so both stay visible.
        restore.SearchBox.Text = "";
        restore.RangeBox.SelectedIndex = (int)RestoreRange.Day;
        Pump(restore);
        Assert(restore.EntryList.Items.Count == 2, "recent clearings stay in the day window");

        // Exact date for today finds them too; the picker becomes visible.
        restore.RangeBox.SelectedIndex = (int)RestoreRange.ExactDate;
        Pump(restore);
        Assert(restore.ExactDatePicker.Visibility == Visibility.Visible, "exact mode reveals the date picker");
        Assert(restore.EntryList.Items.Count == 2, "today's exact date matches today's clearings");
        restore.ExactDatePicker.SelectedDate = DateTime.Today.AddDays(-1);
        Pump(restore);
        Assert(restore.EntryList.Items.Count == 0, "yesterday's exact date misses today's clearings");

        // Uncheck everything, confirm is refused; check one, restore exactly it.
        restore.RangeBox.SelectedIndex = (int)RestoreRange.All;
        Pump(restore);
        List<CheckBox> boxes = Descendants<CheckBox>(restore.EntryList).ToList();
        Assert(boxes.Count == 2, "two checkbox rows rendered");
        boxes[0].IsChecked = false;
        Pump(restore);
        Assert(restore.CurrentSelection().Count == 1, "one row remains checked");
        restore.RestoreButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, restore.RestoreButton));
        Pump(restore);
        Assert(!restore.IsLoaded, "confirm closes the preview window");
        Pump(window);
        Assert(controller.Tasks.Count == 2, "one task came back from the preview window");
        // Exactly the still-checked entry returned; the unchecked one stayed archived.
        ClearedLog archive = new ClearedLog(directory);
        List<ClearedEntry> remaining = new List<ClearedEntry>();
        HashSet<string> currentIds = new HashSet<string>(controller.Tasks.Select(t => t.Id));
        foreach (ClearedEntry entry in archive.ReadAll())
            if (!currentIds.Contains(entry.Id)) remaining.Add(entry);
        Assert(controller.Tasks.Count == 2 && controller.RestorePreviewCount == remaining.Count,
            "the unchecked candidate is still restorable");
        window.Close();
        Pump(null);
    }

    private static void RunCalendarScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Assert(!controller.CalendarExpanded && controller.CalendarVisibility == Visibility.Collapsed, "calendar starts collapsed");
        controller.ToggleCalendarCommand.Execute(null);
        Pump(window);
        Assert(controller.CalendarExpanded && controller.CalendarVisibility == Visibility.Visible && controller.CalendarDays.Count == 42,
            "expanding renders the month grid");
        Assert(ReadState(directory).CalendarExpanded, "expanded state persists");

        Add(controller, window, "明天的事");
        TaskItem task = controller.Tasks[0];
        task.DueAt = DateTime.Today.AddDays(1).AddHours(18);
        Pump(window);
        CalendarDayItem tomorrowCell = controller.CalendarDays.Single(c => c.Date == DateTime.Today.AddDays(1));
        Assert(tomorrowCell.Count == 1 && tomorrowCell.DotVisibility == Visibility.Visible, "task changes refresh due dots while expanded");
        Assert(tomorrowCell.ToolTipText.Contains(Strings.CountOf(controller.Language, 1, "due")), "dot tooltip counts the day");

        tomorrowCell.SelectCommand.Execute(null);
        Pump(window);
        Assert(controller.SelectedDayVisibility == Visibility.Visible && controller.SelectedDayTasks.Count == 1
            && Object.ReferenceEquals(controller.SelectedDayTasks[0], task), "selecting a day lists its due tasks");
        Assert(controller.SelectedDayTitle.Contains(Strings.CountOf(controller.Language, 1, "due")), "day panel title counts open tasks");
        Assert(tomorrowCell.IsSelected, "selected cell is highlighted");

        controller.SelectedDayTasks[0].IsCompleted = true;
        Pump(window);
        Assert(task.IsCompleted, "checking inside the day list completes the real task");
        Assert(controller.SelectedDayTitle.Contains(Strings.Get(controller.Language, "cal.day.noDue")), "completed day no longer counts as due");
        Assert(controller.CalendarDays.Single(c => c.Date == DateTime.Today.AddDays(1)).DotVisibility == Visibility.Collapsed,
            "completed tasks lose their due dot");

        // Weekends are marked exactly like gazetted holidays. A holiday that happens to
        // fall on a weekend is skipped here so the assertion stays about plain weekends.
        CalendarDayItem aSaturday = controller.CalendarDays.First(c => c.Date.DayOfWeek == DayOfWeek.Saturday && !HolidayData.IsHoliday(c.Date));
        CalendarDayItem aMonday = controller.CalendarDays.First(c => c.Date.DayOfWeek == DayOfWeek.Monday && !HolidayData.IsHoliday(c.Date));
        Assert(aSaturday.IsWeekend && !aSaturday.IsHoliday && aSaturday.HolidayMarkVisibility == Visibility.Visible,
            "a saturday carries the red marker");
        Assert(aSaturday.ToolTipText.Contains(Strings.T(controller.Language, "holiday.weekend")),
            "the weekend cell names the weekend in its tooltip");
        Assert(aSaturday.AutomationLabel.Contains(Strings.T(controller.Language, "holiday.weekend")),
            "screen readers hear the weekend too");
        Assert(!aMonday.IsWeekend && aMonday.HolidayMarkVisibility == Visibility.Collapsed,
            "a weekday carries no marker");

        controller.ToggleCalendarModeCommand.Execute(null);
        Pump(window);
        Assert(controller.CalendarHeatMode && ReadState(directory).CalendarHeatMode, "mode switch flips to heatmap and persists");
        CalendarDayItem todayCell = controller.CalendarDays.Single(c => c.IsToday);
        Assert(todayCell.Count == 1 && todayCell.HeatLevel == 4, "today heats up from the fresh completion");
        Assert(((SolidColorBrush)todayCell.CellBrush).Color == Color.FromRgb(0x3C, 0x78, 0x65), "busiest day paints the deepest green");
        Assert(todayCell.ToolTipText.Contains(Strings.CountOf(controller.Language, 1, "done")) && todayCell.ToolTipText.Contains("明天的事"), "heat tooltip names the completed task");

        // One-click inversion: the same day now paints the lightest green, and days
        // that have not happened yet are left out of the scale entirely.
        Assert(controller.InvertVisibility == Visibility.Visible, "the invert button is offered in heat mode");
        Assert(!controller.CalendarHeatInverted, "inverted mode starts off");
        controller.ToggleLightModeCommand.Execute(null);
        Pump(window);
        Assert(controller.CalendarHeatInverted && ReadState(directory).CalendarHeatInverted, "invert toggles and persists");
        CalendarDayItem invertedToday = controller.CalendarDays.Single(c => c.IsToday);
        // Inversion walks the whole palette backwards: the day that was the deepest green
        // becomes the lightest shade, and an elapsed day with nothing completed - which is
        // every day before it - takes the deepest.
        Assert(invertedToday.HeatLevel == 0, "the busiest day drops to the lightest inverted level");
        Assert(((SolidColorBrush)invertedToday.CellBrush).Color == Color.FromRgb(0xED, 0xF1, 0xE8),
            "inverted mode paints the completed day the lightest shade");
        CalendarDayItem idleElapsed = controller.CalendarDays.First(c => c.Date < DateTime.Today.Date);
        Assert(idleElapsed.Count == 0 && idleElapsed.HeatLevel == HeatScale.Colors.Length - 1,
            "an elapsed day with no completions takes the deepest inverted level");
        Assert(((SolidColorBrush)idleElapsed.CellBrush).Color == Color.FromRgb(0x3C, 0x78, 0x65),
            "the empty day is painted the deepest green rather than left pale");
        CalendarDayItem futureCell = controller.CalendarDays.First(c => c.Date > DateTime.Today);
        // "Out of scale" is a deliberate state, not an accident of the initialiser: the
        // level says -1 and the cell is flagged, so its neutral colour outlives any
        // future change to the default brush.
        Assert(futureCell.HeatLevel == -1 && futureCell.OutOfHeatScale && futureCell.CellBrush != null
            && ((SolidColorBrush)futureCell.CellBrush).Color == Color.FromRgb(0xFF, 0xFE, 0xFB),
            "a future day is out of scale and stays on the neutral cell colour in inverted mode");
        controller.ToggleLightModeCommand.Execute(null);
        Pump(window);
        Assert(!controller.CalendarHeatInverted && ((SolidColorBrush)controller.CalendarDays.Single(c => c.IsToday).CellBrush).Color
            == Color.FromRgb(0x3C, 0x78, 0x65), "toggling back restores the normal heat colour");
        controller.ToggleCalendarModeCommand.Execute(null);
        Pump(window);
        Assert(!controller.CalendarHeatMode && controller.InvertVisibility == Visibility.Collapsed,
            "the invert button is hidden outside heat mode");
        controller.ToggleCalendarModeCommand.Execute(null);
        Pump(window);

        DateTime nextMonth = DateTime.Today.AddMonths(1);
        controller.NextMonthCommand.Execute(null);
        Pump(window);
        Assert(controller.CalendarTitle == Strings.T(controller.Language, "cal.monthTitle", nextMonth.Year, nextMonth.Month),
            "next month navigates forward");
        controller.PrevMonthCommand.Execute(null);
        Pump(window);
        Assert(controller.CalendarTitle == Strings.T(controller.Language, "cal.monthTitle", DateTime.Today.Year, DateTime.Today.Month),
            "previous month navigates back");

        // The keepsake day survives all the way to the rendered cell: 17 August shows a
        // heart, painted red, and the tooltip leads with the line.
        controller.PrevMonthCommand.Execute(null);
        Pump(window);
        DateTime keepsake = new DateTime(DateTime.Today.Year, 8, 17);
        CalendarDayItem heartCell = controller.CalendarDays.SingleOrDefault(c => c.Date == keepsake);
        Assert(heartCell != null, "17 august falls inside the august grid");
        Assert(heartCell.IsAnniversary && heartCell.DayLabel == Strings.T(controller.Language, "anniversary.glyph"),
            "17 august renders a heart in place of the date");
        Assert(((SolidColorBrush)heartCell.LabelBrush).Color == Color.FromRgb(0xD6, 0x45, 0x5F),
            "the heart is painted red");
        Assert(heartCell.ToolTipText.StartsWith(Strings.T(controller.Language, "anniversary.tip")),
            "hovering the heart day leads with the keepsake line");
        Assert(heartCell.AutomationLabel.Contains(Strings.T(controller.Language, "anniversary.tip")),
            "screen readers hear the keepsake line too");
        controller.NextMonthCommand.Execute(null);
        Pump(window);
        Assert(controller.CalendarTitle == Strings.T(controller.Language, "cal.monthTitle", DateTime.Today.Year, DateTime.Today.Month),
            "the keepsake detour returns to the current month");

        // Public holidays: the cell is flagged and its name is localised.
        controller.Language = UiLanguage.English;
        Pump(window);
        DateTime christmas = new DateTime(DateTime.Today.Year, 12, 25);
        controller.NextMonthCommand.Execute(null);
        Pump(window);
        while (controller.CalendarDays.Count > 0 && controller.CalendarDays[0].Date.Month != 12)
            controller.NextMonthCommand.Execute(null);
        CalendarDayItem holidayCell = controller.CalendarDays.SingleOrDefault(c => c.Date == christmas);
        if (holidayCell != null)
        {
            Assert(holidayCell.IsHoliday && holidayCell.HolidayMarkVisibility == Visibility.Visible,
                "a public holiday shows its marker");
            Assert(holidayCell.AutomationLabel.Contains("Christmas Day"),
                "the holiday name follows the active language (" + holidayCell.AutomationLabel + ")");
            Assert(holidayCell.ToolTipText.Contains(Strings.Get(UiLanguage.English, "holiday.label")),
                "the holiday tooltip is localised too");
        }
        controller.Language = UiLanguage.ZhHans;
        Pump(window);

        tomorrowCell.SelectCommand.Execute(null);
        Pump(window);
        Assert(controller.SelectedDayVisibility == Visibility.Collapsed, "selecting the same day again collapses the panel");

        window.Close();
        Pump(null);
        Window reopenedWindow;
        TodoController reopened = OpenWindow(directory, out reopenedWindow);
        Assert(reopened.CalendarExpanded && reopened.CalendarHeatMode && reopened.CalendarDays.Count == 42,
            "relaunch restores calendar expansion and mode");
        reopenedWindow.Close();
        Pump(null);
    }

    private static void RunReminderScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Assert(controller.LastMorningReminded == null && controller.LastEveningReminded == null, "fresh install never reminded");
        Assert(controller.MorningReminderHour == 9 && controller.EveningReminderHour == 16, "fresh install uses the 09:00 and 16:00 defaults");

        // A task due today exercises the morning slot; one due tomorrow exercises the
        // evening slot. They must be reported independently.
        Add(controller, window, "今天要交");
        Add(controller, window, "明天要交");
        controller.Tasks[0].DueAt = DateTime.Today.AddHours(18);
        controller.Tasks[1].DueAt = DateTime.Today.AddDays(1).AddHours(9);
        Pump(window);

        // Morning slot: 09:00 reports today's to-dos and takes priority over the evening one.
        DateTime morning = DateTime.Today.AddHours(9).AddMinutes(30);
        Assert(controller.TryRemind(morning, false, false), "the morning slot fires once its hour has passed");
        Assert(controller.LastMorningReminded == ReminderLogic.SlotKey(ReminderSlot.MorningKind, morning),
            "the morning dedupe key records the day");
        Assert(ReadState(directory).LastMorningReminded == ReminderLogic.SlotKey(ReminderSlot.MorningKind, morning),
            "the morning record persists");
        Assert(!controller.TryRemind(morning, false, false), "the morning slot does not fire twice in a day");

        // Evening slot on the same day still fires: the two records are independent.
        DateTime evening = DateTime.Today.AddHours(16);
        Assert(controller.TryRemind(evening, false, false), "the evening slot fires independently on the same day");
        Assert(controller.LastEveningReminded == ReminderLogic.SlotKey(ReminderSlot.EveningKind, evening),
            "the evening dedupe key records the day");
        Assert(ReadState(directory).LastEveningReminded == ReminderLogic.SlotKey(ReminderSlot.EveningKind, evening),
            "the evening record persists");
        Assert(!controller.TryRemind(evening, false, false), "the evening slot does not fire twice either");

        // A quiet day fires nothing.
        DateTime quiet = DateTime.Today.AddDays(5).AddHours(21);
        Assert(!controller.TryRemind(quiet, false, false), "a day with no dues stays quiet");

        // The launch-time catch-up stays silent for a slot that already ran today.
        Assert(!controller.TryRemind(DateTime.Today.AddHours(10), true, false), "the startup path respects the morning dedupe key");

        // Moving the duedates forward makes the next day fire both slots again.
        controller.Tasks[0].DueAt = DateTime.Today.AddDays(1).AddHours(18);
        controller.Tasks[1].DueAt = DateTime.Today.AddDays(2).AddHours(9);
        Pump(window);
        DateTime nextMorning = DateTime.Today.AddDays(1).AddHours(9).AddMinutes(15);
        Assert(controller.TryRemind(nextMorning, false, false), "a new day fires the morning slot again");
        Assert(controller.LastMorningReminded == ReminderLogic.SlotKey(ReminderSlot.MorningKind, nextMorning),
            "the morning record advances to the new day");

        // ShowReminderWindow runs a real dialog; make sure it survives a shown pass.
        Assert(controller.TryRemind(DateTime.Today.AddDays(1).AddHours(16), true, true),
            "the evening path can display its window without throwing");
        Pump(window);
        window.Close();
        Pump(null);
    }

    private static void RunLanguageScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Assert(controller.Language == UiLanguage.ZhHans, "a fresh install starts in simplified Chinese");
        Assert(controller.HeadingText == Strings.T(UiLanguage.ZhHans, "heading"), "headings use the active table");
        string simplifiedHeading = controller.HeadingText;

        controller.Language = UiLanguage.ZhHant;
        Pump(window);
        Assert(controller.HeadingText == Strings.T(UiLanguage.ZhHant, "heading") && controller.HeadingText != simplifiedHeading,
            "switching to traditional Chinese relabels the UI immediately");
        Assert(ReadState(directory).Language == "zh-Hant", "the language choice persists");
        Assert(controller.WeekHeaders.Count == 7 && controller.WeekHeaders[0] == Strings.T(UiLanguage.ZhHant, "week.0"),
            "week headers follow the language");

        controller.Language = UiLanguage.English;
        Pump(window);
        Assert(controller.HeadingText == Strings.T(UiLanguage.English, "heading"), "switching to English relabels the UI");
        Assert(ReadState(directory).Language == "en", "the English choice persists");
        Assert(controller.CalendarTitle == Strings.T(UiLanguage.English, "cal.monthTitle", DateTime.Today.Year, DateTime.Today.Month),
            "calendar titles follow the language");

        // A task and its locally-formatted deadline must both re-render. Assert against
        // the expected English wording rather than against FormatDue itself, otherwise a
        // hardcoded-language bug inside FormatDue would satisfy its own assertion.
        Add(controller, window, "Buy milk");
        controller.Tasks[0].DueAt = DateTime.Today.AddDays(1).AddHours(18);
        Pump(window);
        Assert(controller.Tasks[0].DueText == TaskItem.FormatDue(DateTime.Today.AddDays(1).AddHours(18), false, DateTime.Now, UiLanguage.English),
            "the deadline label re-renders in English");
        Assert(controller.Tasks[0].DueText.Contains("Tomorrow")
            && controller.Tasks[0].DueText == Strings.T(UiLanguage.English, "due.tomorrow",
                Strings.F(Strings.Get(UiLanguage.English, "clock.hour"), 18)),
            "the English deadline uses the English wording");

        window.Close();
        Pump(null);
        Window reopenedWindow;
        TodoController reopened = OpenWindow(directory, out reopenedWindow);
        Assert(reopened.Language == UiLanguage.English && reopened.HeadingText == Strings.T(UiLanguage.English, "heading"),
            "relaunch restores the saved language");
        reopened.Language = UiLanguage.ZhHans;
        Pump(reopenedWindow);
        reopenedWindow.Close();
        Pump(null);
    }

    private static void RunTrayHideShowScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        Assert(window.IsVisible, "window starts visible");
        controller.HideToTray();
        Pump(window);
        Assert(!window.IsVisible && window.IsLoaded, "hide-to-tray keeps the window alive but invisible");
        controller.ShowFromTray();
        Pump(window);
        Assert(window.IsVisible, "show-from-tray brings the card back");
        window.Close();
        Pump(null);
        Assert(!window.IsLoaded, "closing the window still destroys it on the real exit path");
    }

    private static int RunColdRead(string directory)
    {
        Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Window window;
            TodoController controller = OpenWindow(Program.ResolveDataDirectory(new[] { "--data-dir", directory }), out window);
            Assert(controller.Tasks.Count == 20 && controller.Tasks[0].Id == "restart-item-0"
                && controller.Tasks[19].Text == "重启恢复测试 19", "cold process restores the full saved task list");
            Assert(controller.Tasks.Count(t => t.IsCompleted) == 5 && !controller.IsPinned
                && controller.CanEditTasks, "cold process restores completion, pin, and writable state");
            window.Close();
            Console.WriteLine("COLD-READ PASS");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { application.Shutdown(); }
    }

    private static void WaitForDispatcher(TimeSpan duration)
    {
        DispatcherFrame frame = new DispatcherFrame();
        DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = duration };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static int NativeHitTest(Window window, Point position)
    {
        Point screen = window.PointToScreen(position);
        int x = (int)Math.Round(screen.X);
        int y = (int)Math.Round(screen.Y);
        // WM_NCHITTEST packs signed 16-bit screen coordinates into LPARAM.
        int packed = unchecked((x & 0xffff) | ((y & 0xffff) << 16));
        return SendMessage(new WindowInteropHelper(window).Handle, 0x0084, IntPtr.Zero, new IntPtr(packed)).ToInt32();
    }

    private static int CountFullyVisibleTasks(Window window, int taskCount)
    {
        ScrollViewer scroller = (ScrollViewer)window.FindName("TaskScroller");
        ScrollContentPresenter viewport = Descendants<ScrollContentPresenter>(scroller).Single();
        int count = 0;
        for (int i = 0; i < taskCount; i++)
        {
            ContentPresenter task = TaskPresenter(window, i);
            Point top = task.TranslatePoint(new Point(0, 0), viewport);
            if (top.Y >= -0.5 && top.Y + task.ActualHeight <= viewport.ActualHeight + 0.5) count++;
        }
        return count;
    }

    private static SolidColorBrush ResourceBrush(Window window, string key)
    {
        return window.Resources[key] as SolidColorBrush;
    }

    private static void RunThemeScenarios(string directory)
    {
        Window window;
        TodoController controller = OpenWindow(directory, out window);
        // Defaults: sage + followSystem, which resolves to light under the probe.
        Assert(controller.ThemeFamily == ThemeFamily.Sage && controller.ThemeMode == LightDarkMode.FollowSystem,
            "a fresh card starts on sage following the system");
        Assert(ReferenceEquals(ThemeManager.Current, Themes.Get(ThemeFamily.Sage, false)), "sage light is the resolved palette");
        Assert(ResourceBrush(window, "ThemeAccent").Color == Color.FromRgb(0x3C, 0x78, 0x65),
            "the sage accent is stamped into the window resources");
        Assert(window.Resources["ThemeCardShadow"] is Color, "the shadow token resolves as a Color, not a brush");

        // The gallery popup lists every family with a preview and marks the
        // current one; previews follow the active variant (light here).
        controller.OpenThemeGallery();
        Pump(window);
        Popup themePopup = (Popup)window.FindName("ThemePopup");
        Assert(themePopup.IsOpen, "theme gallery popup opens from the title-bar button");
        Assert(controller.ThemeCards.Count == Themes.FamilyCount, "the gallery lists every theme family");
        Assert(controller.ThemeCards.Single(c => c.Family == ThemeFamily.Sage).IsCurrent
            && !controller.ThemeCards.Single(c => c.Family == ThemeFamily.Midnight).IsCurrent,
            "the gallery checks the current family only");
        Assert(((SolidColorBrush)controller.ThemeCards.Single(c => c.Family == ThemeFamily.Catppuccin).PreviewAccent).Color
            == Color.FromRgb(0x88, 0x39, 0xEF), "catppuccin previews its latte mauve in light mode");
        themePopup.IsOpen = false;
        Pump(window);

        // Family switch: resources are re-stamped, the choice persists, and the
        // code-side colours (calendar cells, deadline labels) follow.
        controller.SelectThemeCommand.Execute(ThemeFamily.Midnight);
        Pump(window);
        Assert(controller.ThemeFamily == ThemeFamily.Midnight && ReadState(directory).ThemeFamily == "midnight",
            "switching family persists to tasks.json");
        Assert(ReferenceEquals(ThemeManager.Current, Themes.Get(ThemeFamily.Midnight, false)),
            "midnight resolves to its light variant while the system is light");
        Assert(ResourceBrush(window, "ThemeAccent").Color == Color.FromRgb(0x5E, 0x6A, 0xD2),
            "window resources are re-stamped with the midnight accent");
        Assert(controller.ThemeCards.Single(c => c.Family == ThemeFamily.Midnight).IsCurrent,
            "the gallery check follows the switch");
        Assert(((SolidColorBrush)controller.CalendarDays.First(c => c.IsCurrentMonth).CellBrush).Color
            == Color.FromRgb(0xF7, 0xF8, 0xF9), "calendar cells adopt the theme card colour");
        controller.InputText = "昨天就该做的事";
        controller.AddCommand.Execute(null);
        TaskItem task = controller.Tasks[0];
        task.DueAt = DateTime.Now.AddDays(-1);
        Pump(window);
        Assert(((SolidColorBrush)task.DueBrush).Color == Color.FromRgb(0xD9, 0x57, 0x57),
            "overdue red is the midnight-tuned shade, not the sage one");

        // Locking dark: another full re-stamp, semantic colours re-tuned, and the
        // gallery previews switch to the dark variants.
        controller.SetLightDarkModeCommand.Execute("dark");
        Pump(window);
        Assert(controller.ThemeMode == LightDarkMode.Dark && ReadState(directory).LightDarkMode == "dark",
            "locking dark persists to tasks.json");
        Assert(ReferenceEquals(ThemeManager.Current, Themes.Get(ThemeFamily.Midnight, true)), "midnight dark resolved");
        Assert(ResourceBrush(window, "ThemeAccent").Color == Color.FromRgb(0x8A, 0x97, 0xFF), "dark accent stamped");
        Assert(((SolidColorBrush)task.DueBrush).Color == Color.FromRgb(0xF0, 0x70, 0x70),
            "overdue red is re-tuned for the dark palette");
        Assert(((SolidColorBrush)controller.ThemeCards.Single(c => c.Family == ThemeFamily.Midnight).PreviewAccent).Color
            == Color.FromRgb(0x8A, 0x97, 0xFF), "gallery previews flip to the dark variant");

        // FollowSystem reacts when the OS appearance flips (simulated via probe).
        controller.SetLightDarkModeCommand.Execute("follow");
        Pump(window);
        Assert(ReferenceEquals(ThemeManager.Current, Themes.Get(ThemeFamily.Midnight, false)),
            "followSystem returns to light while the probe says light");
        Themes.SystemLightProbe = () => false;
        ThemeManager.RefreshFromSystem();
        Pump(window);
        Assert(ReferenceEquals(ThemeManager.Current, Themes.Get(ThemeFamily.Midnight, true)),
            "followSystem tracks the OS into dark mode");
        Themes.SystemLightProbe = () => true;
        ThemeManager.RefreshFromSystem();
        Pump(window);
        Assert(ReferenceEquals(ThemeManager.Current, Themes.Get(ThemeFamily.Midnight, false)),
            "and tracks it back into light mode");

        // The full sweep: every one of the ten palettes applies cleanly, with
        // every XAML resource key resolvable and the accent matching the palette.
        for (int familyIndex = 0; familyIndex < Themes.FamilyCount; familyIndex++)
        {
            foreach (LightDarkMode mode in new[] { LightDarkMode.Light, LightDarkMode.Dark })
            {
                controller.ThemeFamily = (ThemeFamily)familyIndex;
                controller.ThemeMode = mode;
                Pump(window);
                ThemePalette expected = Themes.Get((ThemeFamily)familyIndex, mode == LightDarkMode.Dark);
                string label = Themes.CodeOf(expected.Family) + (expected.Dark ? " dark" : " light");
                Assert(ReferenceEquals(ThemeManager.Current, expected), label + " resolves");
                foreach (FieldInfo field in typeof(ThemePalette).GetFields())
                {
                    if (field.FieldType != typeof(string) || field.Name == "CardBgEnd") continue;
                    Assert(window.Resources.Contains("Theme" + field.Name), label + " provides resource key Theme" + field.Name);
                }
                Assert(ResourceBrush(window, "ThemeAccent").Color
                    == (Color)ColorConverter.ConvertFromString("#" + expected.Accent), label + " stamps its accent");
                if (expected.IsGlass)
                {
                    Assert(window.Resources["ThemeCardBg"] is LinearGradientBrush, label + " paints the card as a gradient");
                    Assert(window.Resources["ThemeGlassHighlight"] is LinearGradientBrush, label + " shows the specular highlight");
                }
                else
                {
                    Assert(window.Resources["ThemeCardBg"] is SolidColorBrush, label + " paints the card as a solid");
                    Assert(((SolidColorBrush)window.Resources["ThemeGlassHighlight"]).Color == Colors.Transparent,
                        label + " has no glass highlight");
                }
            }
        }

        // Leave the shared static state the way later scenarios expect it.
        controller.ThemeFamily = ThemeFamily.Sage;
        controller.ThemeMode = LightDarkMode.FollowSystem;
        Pump(window);
        Assert(ReferenceEquals(ThemeManager.Current, Themes.Get(ThemeFamily.Sage, false)), "theme state resets to sage light");
    }

    private static TodoController OpenWindow(string directory, out Window window)
    {
        Assembly assembly = typeof(TodoController).Assembly;
        using (Stream stream = assembly.GetManifestResourceStream("MainWindow.xaml"))
        {
            if (stream == null) throw new Exception("The compiled app has no embedded MainWindow.xaml.");
            window = (Window)XamlReader.Load(stream);
        }
        // Exercise a real WPF visual tree without presenting an interactive window.
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Opacity = 0;
        TodoController controller = new TodoController(window, directory, enableTray: false);
        controller.StopReminderTimer(); // reminder scenarios inject times manually
        windows.Add(window);
        window.Show();
        Pump(window);
        Assert(window.IsLoaded, "embedded XAML window reaches Loaded");
        return controller;
    }

    private static void Add(TodoController controller, Window window, string text)
    {
        controller.InputText = text;
        controller.AddCommand.Execute(null);
        Pump(window);
    }

    private static AppState ReadState(string directory)
    {
        AppRepository repository = new AppRepository(directory);
        AppState state = repository.Load();
        if (repository.LoadWarning != null) throw new Exception("Unexpected storage warning: " + repository.LoadWarning);
        return state;
    }

    private static CheckBox TaskCheckbox(Window window, int index)
    {
        return Descendants<CheckBox>(TaskPresenter(window, index)).Single();
    }

    private static TextBlock TaskLabel(Window window, int index)
    {
        return Descendants<TextBlock>(TaskPresenter(window, index)).Single(t => t.Name == "TaskLabel");
    }

    private static ContentPresenter TaskPresenter(Window window, int index)
    {
        ScrollViewer scroller = (ScrollViewer)window.FindName("TaskScroller");
        ItemsControl items = (ItemsControl)scroller.Content;
        ContentPresenter presenter = items.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;
        if (presenter == null) throw new Exception("Task visual container was not generated for index " + index);
        return presenter;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            T matching = child as T;
            if (matching != null) yield return matching;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Pump(Window window)
    {
        for (int i = 0; i < 2; i++)
        {
            if (window != null) window.UpdateLayout();
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }
    }

    private static bool IsNativeTopmost(Window window)
    {
        const int extendedStyleIndex = -20;
        const int topmostStyle = 0x00000008;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) throw new Exception("Test window has no native handle.");
        return (GetWindowLong(handle, extendedStyleIndex) & topmostStyle) != 0;
    }

    private static void Assert(bool condition, string description)
    {
        assertions++;
        if (!condition) throw new Exception("FAIL: " + description);
    }
}
