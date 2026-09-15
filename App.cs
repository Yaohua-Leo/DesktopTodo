using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopTodo
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string directory;
            try { directory = ResolveDataDirectory(args); }
            catch (Exception ex)
            {
                MessageBox.Show("无法确定待办数据目录，已停止启动以保护历史任务。\n" + ex.Message, "桌面待办", MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "").Substring(0, 24);
            bool created;
            using (var mutex = new Mutex(true, "Local\\DesktopTodo-" + key, out created))
            {
                if (!created)
                {
                    try { using (var signal = EventWaitHandle.OpenExisting("Local\\DesktopTodoWake-" + key)) signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                try
                {
                    using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\DesktopTodoWake-" + key))
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        Window window;
                        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
                        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("App.ico"))
                            if (stream != null) window.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var controller = new TodoController(window, directory);
                        app.MainWindow = window;
                        var registration = ThreadPool.RegisterWaitForSingleObject(signal, delegate
                        {
                            app.Dispatcher.BeginInvoke(new Action(delegate
                            {
                                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                                window.Show();
                                window.Activate();
                            }));
                        }, null, Timeout.Infinite, false);
                        try { app.Run(window); }
                        finally { registration.Unregister(null); GC.KeepAlive(controller); }
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("桌面待办未能启动：\n" + ex.GetBaseException().Message, "桌面待办", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        public static string ResolveDataDirectory(string[] args)
        {
            string directory;
            if (args.Length == 2 && args[0] == "--data-dir" && !String.IsNullOrWhiteSpace(args[1])) directory = args[1];
            else if (args.Length == 0)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
                if (String.IsNullOrWhiteSpace(local)) local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (String.IsNullOrWhiteSpace(local) || !Path.IsPathRooted(local) || Path.GetPathRoot(local).Length < 3) throw new IOException("本地应用数据目录不可用，请检查当前 Windows 用户配置。");
                directory = Path.Combine(local, "DesktopTodo");
            }
            else throw new ArgumentException("启动参数无效。");
            string full = Path.GetFullPath(directory);
            string root = Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
    }

    internal static class StorageDiagnostics
    {
        public static void Write(string directory, string outcome, int count, string detail)
        {
            // Record only loading/saving metadata; never include task text or input drafts.
            try
            {
                Directory.CreateDirectory(directory);
                string line = DateTimeOffset.Now.ToString("o") + " | " + outcome + " | tasks=" + count
                    + " | directory=" + directory + " | " + (detail ?? "").Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;
                File.AppendAllText(Path.Combine(directory, "startup.log"), line, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        public RelayCommand(Action execute, Func<bool> canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object parameter) { return _canExecute == null || _canExecute(); }
        public void Execute(object parameter) { if (CanExecute(parameter)) _execute(); }
        public event EventHandler CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
    }

    public abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed(string name) { var handler = PropertyChanged; if (handler != null) handler(this, new PropertyChangedEventArgs(name)); }
    }

    public sealed class TaskItem : Bindable
    {
        private readonly TodoController _owner;
        private string _text;
        private bool _completed;
        public string Id { get; private set; }
        public string Text { get { return _text; } set { _text = value; Changed("Text"); Changed("CheckLabel"); Changed("DeleteLabel"); } }
        public bool IsCompleted
        {
            get { return _completed; }
            set { if (_completed == value) return; _completed = value; Changed("IsCompleted"); Changed("CheckLabel"); _owner.TaskChanged(); }
        }
        public string CheckLabel { get { return (_completed ? "恢复待办：" : "完成任务：") + _text; } }
        public string DeleteLabel { get { return "删除任务：" + _text; } }
        public ICommand EditCommand { get; private set; }
        public ICommand DeleteCommand { get; private set; }
        public TaskItem(TodoController owner, TodoRecord record)
        {
            _owner = owner; Id = record.Id; _text = record.Text; _completed = record.IsCompleted;
            EditCommand = new RelayCommand(delegate { owner.BeginEdit(this); });
            DeleteCommand = new RelayCommand(delegate { owner.Delete(this); });
        }
        public TodoRecord ToRecord() { return new TodoRecord { Id = Id, Text = _text, IsCompleted = _completed }; }
    }

    public sealed class TodoController : Bindable
    {
        private readonly Window _window;
        private readonly AppRepository _repository;
        private readonly TextBox _input;
        private readonly ScrollViewer _scroller;
        private readonly DispatcherTimer _geometryTimer;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _dateTimer;
        private readonly DispatcherTimer _refreshTimer;
        private readonly HwndSourceHook _resizeHook;
        private HwndSource _windowSource;
        private readonly List<KeyValuePair<int, TaskItem>> _undo = new List<KeyValuePair<int, TaskItem>>();
        private bool _ready;
        private bool _pinned;
        private bool _saveOk = true;
        private bool _hasUnsavedTaskChanges;
        private string _dataError = "";
        private string _loadNotice = "";
        private bool _isComposing;
        private string _inputText = "";
        private string _toastText = "";
        private TaskItem _editing;
        private string _newTaskDraft = "";
        private readonly Dictionary<string, string> _editDrafts = new Dictionary<string, string>();
        public ObservableCollection<TaskItem> Tasks { get; private set; }
        public ICommand AddCommand { get; private set; }
        public ICommand ClearCompletedCommand { get; private set; }
        public ICommand UndoCommand { get; private set; }
        public ICommand ReloadCommand { get; private set; }
        public bool CanEditTasks { get { return _repository.CanSave && String.IsNullOrEmpty(_dataError); } }
        public string DataLocationHint { get { return "任务目录：" + _repository.DataDirectory + "\n" + (String.IsNullOrEmpty(_dataError) ? "任务变更会额外保留历史备份。" : _dataError); } }
        public string InputText
        {
            get { return _inputText; }
            set { _inputText = value ?? ""; Changed("InputText"); Changed("PlaceholderVisibility"); CommandManager.InvalidateRequerySuggested(); }
        }
        public bool IsPinned
        {
            get { return _pinned; }
            set { if (_pinned == value) return; _pinned = value; _window.Topmost = value; Changed("IsPinned"); Changed("PinHint"); if (_ready) Save(); }
        }
        public string PinHint { get { return _pinned ? "已固定：保持在其他窗口上方。点击取消置顶。" : "点击后保持在其他窗口上方。"; } }
        public string DateLabel { get { return DateTime.Now.ToString("M月d日 · dddd", CultureInfo.GetCultureInfo("zh-CN")); } }
        public string RemainingLabel { get { return !CanEditTasks ? "读取异常" : Tasks.Count == 0 ? "新的开始" : Tasks.Any(t => !t.IsCompleted) ? (Tasks.Count(t => !t.IsCompleted) + " 件待办") : "全部完成 ✓"; } }
        public string ProgressLabel { get { return !CanEditTasks ? "历史任务暂时无法读取" : "已完成 " + Tasks.Count(t => t.IsCompleted) + " / " + Tasks.Count; } }
        public double CompletedPercent { get { return Tasks.Count == 0 ? 0 : 100.0 * Tasks.Count(t => t.IsCompleted) / Tasks.Count; } }
        public Visibility EmptyVisibility { get { return Tasks.Count == 0 && CanEditTasks ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility PlaceholderVisibility { get { return String.IsNullOrEmpty(InputText) ? Visibility.Visible : Visibility.Collapsed; } }
        public string InputHint { get { return _editing == null ? "添加一件待办…" : "修改任务内容…"; } }
        public string AddHint { get { return _editing == null ? "添加任务（Enter）" : "保存修改（Enter），Esc 取消"; } }
        public string AddGlyph { get { return _editing == null ? "+" : "✓"; } }
        public string ToastText { get { return _toastText; } }
        public Visibility ToastVisibility { get { return String.IsNullOrEmpty(_toastText) ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility UndoVisibility { get { return _undo.Count > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public string SaveStatus { get { return !String.IsNullOrEmpty(_dataError) ? "读取异常，已暂停保存 · 请点击重新读取" : !_saveOk ? "保存失败，修改仍保留在当前窗口" : !String.IsNullOrEmpty(_loadNotice) ? _loadNotice : (_editing == null ? "已读取 " + Tasks.Count + " 条 · 自动保存" : "正在编辑 · Enter 保存 · Esc 取消"); } }
        public Brush SaveStatusColor { get { return _saveOk ? new SolidColorBrush(Color.FromRgb(155, 165, 149)) : Brushes.Firebrick; } }

        public TodoController(Window window, string directory)
        {
            _window = window;
            _resizeHook = ResizeHitTest;
            _repository = new AppRepository(directory);
            StorageDiagnostics.Write(directory, "startup-before-load", -1, "primaryExists=" + File.Exists(Path.Combine(directory, "tasks.json")) + "; backupExists=" + File.Exists(Path.Combine(directory, "tasks.backup.json")));
            var state = _repository.Load();
            _saveOk = _repository.CanSave;
            _dataError = _repository.CanSave ? "" : (_repository.LoadWarning ?? "历史任务暂时无法读取。");
            _loadNotice = _repository.CanSave && !String.IsNullOrEmpty(_repository.LoadWarning) ? "已从备份恢复 · 原文件已保留" : "";
            StorageDiagnostics.Write(directory, "startup-loaded", state.Tasks.Count, "canSave=" + _repository.CanSave + "; hasWarning=" + !String.IsNullOrEmpty(_repository.LoadWarning));
            Tasks = new ObservableCollection<TaskItem>(state.Tasks.Select(t => new TaskItem(this, t)));
            _pinned = state.IsPinned;
            _input = (TextBox)window.FindName("TaskInput");
            _scroller = (ScrollViewer)window.FindName("TaskScroller");
            AddCommand = new RelayCommand(Add, delegate { return CanEditTasks && !String.IsNullOrWhiteSpace(InputText); });
            ClearCompletedCommand = new RelayCommand(ClearCompleted, delegate { return CanEditTasks && Tasks.Any(t => t.IsCompleted); });
            UndoCommand = new RelayCommand(Undo, delegate { return CanEditTasks && _undo.Count > 0; });
            ReloadCommand = new RelayCommand(delegate { RefreshFromDisk(); }, delegate { return !_hasUnsavedTaskChanges && _editing == null; });
            window.DataContext = this;
            window.Topmost = _pinned;
            _geometryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _geometryTimer.Tick += delegate { _geometryTimer.Stop(); Save(); };
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _toastTimer.Tick += delegate { _toastTimer.Stop(); _undo.Clear(); SetToast(""); };
            _dateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _dateTimer.Tick += delegate { Changed("DateLabel"); };
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += delegate { CheckForRecoveredData(); };
            ((Button)window.FindName("CloseButton")).Click += delegate { window.Close(); };
            ((Button)window.FindName("MinimizeButton")).Click += delegate { window.WindowState = WindowState.Minimized; };
            ((System.Windows.Controls.Primitives.Thumb)window.FindName("ResizeHandle")).DragDelta += delegate(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
            {
                window.Width = Math.Max(window.MinWidth, window.Width + e.HorizontalChange);
                window.Height = Math.Max(window.MinHeight, window.Height + e.VerticalChange);
            };
            ((Grid)window.FindName("DragHeader")).MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                // Window controls handle their own input; only the header background drags.
                if (e.OriginalSource is Border || e.OriginalSource is Grid)
                {
                    if (Mouse.LeftButton == MouseButtonState.Pressed) window.DragMove();
                }
            };
            TextCompositionManager.AddPreviewTextInputStartHandler(_input, delegate { _isComposing = true; });
            TextCompositionManager.AddPreviewTextInputHandler(_input, delegate { _isComposing = false; });
            _input.LostKeyboardFocus += delegate { _isComposing = false; };
            _input.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                // Leave Enter to the Chinese IME while a candidate is being composed.
                if (e.Key == Key.Enter && !_isComposing) { if (AddCommand.CanExecute(null)) Add(); e.Handled = true; }
                if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
            };
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { CancelEdit(); _input.Focus(); e.Handled = true; }
            };
            window.SourceInitialized += delegate
            {
                RestoreBounds(state);
                _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                _windowSource.AddHook(_resizeHook);
            };
            window.Loaded += delegate
            {
                _ready = true; _dateTimer.Start(); _refreshTimer.Start(); _input.Focus();
                CheckForRecoveredData();
                if (!String.IsNullOrEmpty(_repository.LoadWarning)) SetToast(_repository.LoadWarning);
            };
            window.LocationChanged += delegate { ScheduleGeometrySave(); };
            window.SizeChanged += delegate { ScheduleGeometrySave(); };
            ((Grid)window.FindName("ListRegion")).SizeChanged += delegate(object sender, SizeChangedEventArgs e)
            {
                ((Grid)window.FindName("EmptyArt")).Visibility = e.NewSize.Height >= 190 ? Visibility.Visible : Visibility.Collapsed;
            };
            window.Closing += delegate(object sender, CancelEventArgs e)
            {
                _geometryTimer.Stop();
                // A failed/empty startup must never save an empty list merely because
                // the window closes. Only warn when this session has actual task edits.
                bool saved = !_hasUnsavedTaskChanges && (!_repository.CanSave || _repository.HasExternalChanges) ? false : Save();
                if (!saved && _hasUnsavedTaskChanges && MessageBox.Show(window, "本次更改尚未保存。继续关闭会丢失这些更改。\n\n是否仍然关闭？", "保存未完成", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
            };
            window.Closed += delegate
            {
                _ready = false; _geometryTimer.Stop(); _toastTimer.Stop(); _dateTimer.Stop(); _refreshTimer.Stop();
                if (_windowSource != null && !_windowSource.IsDisposed) _windowSource.RemoveHook(_resizeHook);
            };
        }

        private IntPtr ResizeHitTest(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int wmNcHitTest = 0x0084;
            if (message != wmNcHitTest || _window.WindowState != WindowState.Normal
                || (_window.ResizeMode != ResizeMode.CanResize && _window.ResizeMode != ResizeMode.CanResizeWithGrip))
                return IntPtr.Zero;

            // The visible card is inset 12 DIPs from the transparent HWND. Include
            // its border and a little of the shadow, without taking input controls.
            // Signed coordinates also work on monitors left/above the main screen.
            long coordinates = lParam.ToInt64();
            Point point = _window.PointFromScreen(new Point(unchecked((short)(coordinates & 0xffff)),
                unchecked((short)((coordinates >> 16) & 0xffff))));
            double width = _window.ActualWidth;
            double height = _window.ActualHeight;
            if (point.X < 5 || point.Y < 5 || point.X > width - 5 || point.Y > height - 5)
                return IntPtr.Zero;

            bool left = point.X <= 20;
            bool right = point.X >= width - 20;
            bool top = point.Y <= 20;
            bool bottom = point.Y >= height - 20;
            int hit = 0;
            // Extend corner targets along the opaque rounded arc. A tiny square
            // in the transparent corner alone would let real clicks pass through.
            if ((top && point.X <= 36) || (left && point.Y <= 36)) hit = 13; // HTTOPLEFT
            else if ((top && point.X >= width - 36) || (right && point.Y <= 36)) hit = 14; // HTTOPRIGHT
            else if ((bottom && point.X <= 36) || (left && point.Y >= height - 36)) hit = 16; // HTBOTTOMLEFT
            else if ((bottom && point.X >= width - 36) || (right && point.Y >= height - 36)) hit = 17; // HTBOTTOMRIGHT
            else if (left) hit = 10; // HTLEFT
            else if (right) hit = 11; // HTRIGHT
            else if (top) hit = 12; // HTTOP
            else if (bottom) hit = 15; // HTBOTTOM

            if (hit == 0) return IntPtr.Zero;
            handled = true;
            return new IntPtr(hit);
        }

        private void RestoreBounds(AppState state)
        {
            var source = PresentationSource.FromVisual(_window);
            Matrix transform = source == null ? Matrix.Identity : source.CompositionTarget.TransformFromDevice;
            var workAreas = System.Windows.Forms.Screen.AllScreens.Select(s =>
            {
                var r = s.WorkingArea;
                return new Rect(transform.Transform(new Point(r.Left, r.Top)), transform.Transform(new Point(r.Right, r.Bottom)));
            }).ToList();
            Rect target = workAreas.FirstOrDefault(r => r.Contains(new Point(state.Left + 50, state.Top + 50)));
            if (target.IsEmpty || target.Width == 0) target = SystemParameters.WorkArea;
            _window.Width = Math.Min(Math.Max(340, state.Width), target.Width);
            _window.Height = Math.Min(Math.Max(410, state.Height), target.Height);
            double left = Double.IsNaN(state.Left) ? target.Right - _window.Width - 30 : state.Left;
            double top = Double.IsNaN(state.Top) ? target.Top + 50 : state.Top;
            _window.Left = Math.Max(target.Left, Math.Min(left, target.Right - _window.Width));
            _window.Top = Math.Max(target.Top, Math.Min(top, target.Bottom - _window.Height));
        }

        private void ScheduleGeometrySave()
        {
            if (!_ready || _window.WindowState != WindowState.Normal) return;
            _geometryTimer.Stop(); _geometryTimer.Start();
        }

        private bool Save()
        {
            try
            {
                if (!_repository.CanSave) { UpdateDataStatus(); return false; }
                Rect bounds = _window.WindowState == WindowState.Normal ? new Rect(_window.Left, _window.Top, _window.Width, _window.Height) : _window.RestoreBounds;
                _repository.Save(new AppState { Tasks = Tasks.Select(t => t.ToRecord()).ToList(), IsPinned = _pinned, Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height });
                _saveOk = true; _hasUnsavedTaskChanges = false;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException || ex is System.Security.SecurityException || ex is InvalidOperationException)) throw;
                _saveOk = false;
                StorageDiagnostics.Write(_repository.DataDirectory, "save-failed", Tasks.Count, ex.GetType().Name + "; hresult=" + ex.HResult.ToString("X8"));
            }
            UpdateDataStatus();
            return _saveOk;
        }

        private void UpdateDataStatus()
        {
            Changed("SaveStatus"); Changed("SaveStatusColor"); Changed("DataLocationHint");
            Changed("CanEditTasks"); Changed("EmptyVisibility");
            Changed("RemainingLabel"); Changed("ProgressLabel");
            CommandManager.InvalidateRequerySuggested();
        }

        private void CheckForRecoveredData()
        {
            if (!_ready || _hasUnsavedTaskChanges || _editing != null) return;
            try { if (!_repository.CanSave || _repository.HasExternalChanges) RefreshFromDisk(); }
            catch (IOException ex) { _saveOk = false; _dataError = ex.Message; UpdateDataStatus(); }
            catch (UnauthorizedAccessException ex) { _saveOk = false; _dataError = ex.Message; UpdateDataStatus(); }
        }

        public bool RefreshFromDisk()
        {
            if (_hasUnsavedTaskChanges || _editing != null) return false;
            var recovered = _repository.Load();
            _saveOk = _repository.CanSave;
            _dataError = _repository.CanSave ? "" : (_repository.LoadWarning ?? "暂时无法读取历史任务。");
            if (!_repository.CanSave) { UpdateDataStatus(); return false; }
            bool wasReady = _ready;
            _ready = false;
            try
            {
                Tasks.Clear();
                foreach (var record in recovered.Tasks) Tasks.Add(new TaskItem(this, record));
                IsPinned = recovered.IsPinned;
                _undo.Clear(); _editDrafts.Clear();
                _loadNotice = !String.IsNullOrEmpty(_repository.LoadWarning) ? "已从备份恢复 · 原文件已保留" : "";
                SetToast("已重新读取 " + Tasks.Count + " 条任务");
                UpdateTaskSummary(); UpdateDataStatus();
            }
            finally { _ready = wasReady; }
            StorageDiagnostics.Write(_repository.DataDirectory, "reloaded", Tasks.Count, "hasWarning=" + !String.IsNullOrEmpty(_repository.LoadWarning));
            return true;
        }

        public void TaskChanged()
        {
            _hasUnsavedTaskChanges = true;
            UpdateTaskSummary();
            if (_ready) Save();
        }

        private void UpdateTaskSummary()
        {
            Changed("RemainingLabel"); Changed("ProgressLabel"); Changed("CompletedPercent"); Changed("EmptyVisibility");
            CommandManager.InvalidateRequerySuggested();
        }

        private void Add()
        {
            string text = InputText.Trim();
            if (text.Length == 0) return;
            if (_editing != null)
            {
                _editing.Text = text; _editDrafts.Remove(_editing.Id); _editing = null;
                InputText = _newTaskDraft; _newTaskDraft = ""; UpdateEditing();
            }
            else
            {
                Tasks.Add(new TaskItem(this, new TodoRecord { Id = Guid.NewGuid().ToString("N"), Text = text }));
                InputText = "";
            }
            TaskChanged();
            _input.Focus();
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { _scroller.ScrollToEnd(); }));
        }

        public void BeginEdit(TaskItem task)
        {
            if (!CanEditTasks) return;
            if (_editing == null) _newTaskDraft = InputText;
            else _editDrafts[_editing.Id] = InputText;
            string draft;
            _editing = task; InputText = _editDrafts.TryGetValue(task.Id, out draft) ? draft : task.Text;
            UpdateEditing(); _input.Focus(); _input.SelectAll();
        }

        private void CancelEdit()
        {
            if (_editing != null)
            {
                _editDrafts.Remove(_editing.Id); _editing = null;
                InputText = _newTaskDraft; _newTaskDraft = ""; UpdateEditing();
            }
        }

        private void UpdateEditing()
        {
            Changed("InputHint"); Changed("AddHint"); Changed("AddGlyph"); Changed("SaveStatus");
        }

        public void Delete(TaskItem task)
        {
            if (!CanEditTasks) return;
            int index = Tasks.IndexOf(task);
            if (index < 0) return;
            _undo.Clear(); _undo.Add(new KeyValuePair<int, TaskItem>(index, task));
            Tasks.Remove(task);
            if (_editing == task) CancelEdit();
            TaskChanged(); SetToast("已删除 1 件任务");
        }

        private void ClearCompleted()
        {
            _undo.Clear();
            for (int i = 0; i < Tasks.Count; i++) if (Tasks[i].IsCompleted) _undo.Add(new KeyValuePair<int, TaskItem>(i, Tasks[i]));
            foreach (var entry in _undo) { Tasks.Remove(entry.Value); if (_editing == entry.Value) CancelEdit(); }
            TaskChanged(); SetToast("已清除 " + _undo.Count + " 件已完成任务");
        }

        private void Undo()
        {
            foreach (var entry in _undo) Tasks.Insert(Math.Min(entry.Key, Tasks.Count), entry.Value);
            _undo.Clear(); TaskChanged(); SetToast("已恢复任务"); _input.Focus();
        }

        private void SetToast(string text)
        {
            _toastText = text; Changed("ToastText"); Changed("ToastVisibility"); Changed("UndoVisibility");
            _toastTimer.Stop(); if (!String.IsNullOrEmpty(text)) _toastTimer.Start();
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
