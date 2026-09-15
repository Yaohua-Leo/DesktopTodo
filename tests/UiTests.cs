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
        Assert(controller.CompletedPercent == 100 && controller.RemainingLabel == "全部完成 ✓", "completion updates progress and summary");
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
        Assert(!reopened.SaveStatus.Contains("失败"), "successful operations keep the saved status healthy");
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
            Assert(controller.SaveStatus.Contains("暂停保存") && !controller.SaveStatus.Contains("自动保存"), "startup read failure has a persistent protective status");
            WaitForDispatcher(TimeSpan.FromMilliseconds(10800));
            Assert(controller.ToastVisibility == Visibility.Collapsed && controller.SaveStatus.Contains("暂停保存"), "read failure status survives expiration of the temporary toast");
        }
        Assert(controller.RefreshFromDisk(), "manual retry succeeds after transient file locks are released");
        Pump(window);
        Assert(controller.Tasks.Select(t => t.Id).SequenceEqual(expected.Tasks.Select(t => t.Id)), "retry restores all twenty task identities in order");
        Assert(controller.Tasks.Select(t => t.Text).SequenceEqual(expected.Tasks.Select(t => t.Text))
            && controller.Tasks.Select(t => t.IsCompleted).SequenceEqual(expected.Tasks.Select(t => t.IsCompleted)), "retry restores task text and completion states");
        Assert(controller.InputText == "读取期间保留的新任务草稿", "recovering historical tasks preserves the new-task input draft");
        Assert(controller.CanEditTasks && controller.AddCommand.CanExecute(null) && !controller.IsPinned && !window.Topmost, "successful retry restores editing and the saved pin state");
        Assert(controller.SaveStatus.Contains("20") && !controller.SaveStatus.Contains("暂停保存"), "successful retry replaces the persistent error with the loaded count");
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
        Assert(controller.Tasks[0].Text == "此窗口尚未保存的修改" && controller.SaveStatus.Contains("保存失败"), "a conflicting save preserves local task edits and reports the failure");
        Assert(File.ReadAllBytes(primary).SequenceEqual(externalBytes), "a stale controller cannot overwrite the externally saved task list");
        Assert(!controller.ReloadCommand.CanExecute(null) && !controller.RefreshFromDisk(), "reload stays disabled while local task changes remain unsaved");
        WaitForDispatcher(TimeSpan.FromMilliseconds(5700));
        Assert(controller.Tasks.Count == 20 && controller.Tasks[0].Text == "此窗口尚未保存的修改"
            && File.ReadAllBytes(primary).SequenceEqual(externalBytes), "automatic refresh preserves both unsaved local edits and the separate on-disk changes");

        // Restore this synthetic fixture's original baseline to resolve the
        // intentional conflict without displaying a modal close confirmation.
        File.WriteAllBytes(primary, original);
        controller.TaskChanged();
        Assert(!controller.SaveStatus.Contains("保存失败") && ReadState(directory).Tasks[0].Text == "此窗口尚未保存的修改", "the preserved local edit can be saved once the synthetic conflict is resolved");
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
        TodoController controller = new TodoController(window, directory);
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
