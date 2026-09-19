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
using Microsoft.Win32;

// The UI integration suite compiles against this exe and drives internals directly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UiTests")]

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
                // No data directory means no way to read the saved language; the
                // startup failure is reported in every shipped language instead.
                MessageBox.Show(Strings.T("msgbox.noDir") + "\n" + ex.Message,
                    Strings.T("msgbox.title"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                        // Explicit shutdown: the card hides to the tray instead of closing,
                        // so the process only exits via the tray menu's Exit path.
                        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        Window window;
                        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
                        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("App.ico"))
                            if (stream != null) window.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var controller = new TodoController(window, directory);
                        app.MainWindow = window;
                        var registration = ThreadPool.RegisterWaitForSingleObject(signal, delegate
                        {
                            app.Dispatcher.BeginInvoke(new Action(delegate { controller.ShowFromTray(); }));
                        }, null, Timeout.Infinite, false);
                        try
                        {
                            if (HasTrayFlag(args))
                            {
                                // Boot auto-start: load fully, then tuck away without showing a frame.
                                window.Show();
                                controller.HideToTray();
                                app.Run();
                            }
                            else app.Run(window);
                        }
                        finally { registration.Unregister(null); GC.KeepAlive(controller); }
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Strings.T("msgbox.noStart") + "\n" + ex.GetBaseException().Message,
                        Strings.T("msgbox.title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        // "--tray" starts the card hidden in the tray (used by boot auto-start).
        // It may appear anywhere in the argument list and combines with --data-dir.
        public static bool HasTrayFlag(string[] args)
        {
            return args != null && Array.IndexOf(args, "--tray") >= 0;
        }

        public static string ResolveDataDirectory(string[] args)
        {
            if (args == null) throw new ArgumentException(Strings.T(UiLanguage.ZhHans, "msgbox.badArg"));
            List<string> rest = new List<string>();
            foreach (string arg in args)
            {
                if (arg == "--tray") continue;
                rest.Add(arg);
            }
            string directory;
            if (rest.Count == 2 && rest[0] == "--data-dir" && !String.IsNullOrWhiteSpace(rest[1])) directory = rest[1];
            else if (rest.Count == 0)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
                if (String.IsNullOrWhiteSpace(local)) local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (String.IsNullOrWhiteSpace(local) || !Path.IsPathRooted(local) || Path.GetPathRoot(local).Length < 3) throw new IOException(Strings.T(UiLanguage.ZhHans, "msgbox.noDataDir"));
                directory = Path.Combine(local, "DesktopTodo");
            }
            else throw new ArgumentException(Strings.T(UiLanguage.ZhHans, "msgbox.badArg"));
            string full = Path.GetFullPath(directory);
            string root = Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
    }

    // Boot auto-start lives in the per-user Run key; no admin rights needed.
    internal static class StartupRegistry
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DesktopTodo";

        internal static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    return key != null && key.GetValue(ValueName) is string;
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException)) throw;
                return false;
            }
        }

        internal static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    if (enabled) key.SetValue(ValueName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray");
                    else key.DeleteValue(ValueName, false);
                }
                return IsEnabled() == enabled;
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException)) throw;
                return false;
            }
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

    // RelayCommand variant that forwards the CommandParameter (used by the hour grid).
    public sealed class ParamCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<bool> _canExecute;
        public ParamCommand(Action<object> execute, Func<bool> canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object parameter) { return _canExecute == null || _canExecute(); }
        public void Execute(object parameter) { if (CanExecute(parameter)) _execute(parameter); }
        public event EventHandler CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
    }

    public abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed(string name) { var handler = PropertyChanged; if (handler != null) handler(this, new PropertyChangedEventArgs(name)); }
    }

    // WPF side of the theme system: keeps the resolved palette, turns its hex
    // tokens into cached brushes and stamps them into each open window's resource
    // dictionary, where the XAML picks them up through DynamicResource. Pure
    // palette data lives in Theme.cs so the storage tests can compile it.
    internal static class ThemeManager
    {
        internal static ThemeFamily Family { get; private set; }
        internal static LightDarkMode Mode { get; private set; }
        internal static ThemePalette Current { get; private set; }

        // Fired after a different palette has been applied to every window.
        internal static event Action Applied;

        private static readonly Dictionary<string, SolidColorBrush> BrushCache =
            new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Window> Windows = new List<Window>();

        static ThemeManager()
        {
            Family = ThemeFamily.Sage;
            Mode = LightDarkMode.FollowSystem;
            Current = Themes.Resolve(Family, Mode, Themes.SystemIsLight());
        }

        internal static SolidColorBrush Brush(string hex)
        {
            SolidColorBrush brush;
            if (!BrushCache.TryGetValue(hex, out brush))
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + hex));
                brush.Freeze();
                BrushCache.Add(hex, brush);
            }
            return brush;
        }

        private static Color Colour(string hex)
        {
            return (Color)ColorConverter.ConvertFromString("#" + hex);
        }

        internal static void Register(Window window)
        {
            if (!Windows.Contains(window)) Windows.Add(window);
            Apply(window);
        }

        internal static void Unregister(Window window)
        {
            Windows.Remove(window);
        }

        // Resolves and applies a (family, mode) pair. Fires Applied only when the
        // resolved palette actually changed, so re-applying the same pair is cheap.
        internal static void SetAndApply(ThemeFamily family, LightDarkMode mode)
        {
            Family = family;
            Mode = mode;
            ThemePalette resolved = Themes.Resolve(family, mode, Themes.SystemIsLight());
            if (ReferenceEquals(resolved, Current)) return;
            Current = resolved;
            ApplyAll();
        }

        // The OS appearance flipped (or a test flipped the probe). Only the
        // FollowSystem mode reacts; locked modes keep their variant.
        internal static void RefreshFromSystem()
        {
            if (Mode != LightDarkMode.FollowSystem) return;
            ThemePalette resolved = Themes.Resolve(Family, Mode, Themes.SystemIsLight());
            if (ReferenceEquals(resolved, Current)) return;
            Current = resolved;
            ApplyAll();
        }

        private static void ApplyAll()
        {
            foreach (Window window in Windows.ToArray()) Apply(window);
            Action applied = Applied;
            if (applied != null) applied();
        }

        internal static void Apply(Window window)
        {
            ThemePalette palette = Current;
            foreach (FieldInfo field in typeof(ThemePalette).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType != typeof(string)) continue;
                string key = "Theme" + field.Name;
                string value = (string)field.GetValue(palette);
                if (field.Name == "CardShadow")
                {
                    window.Resources[key] = Colour(value);
                }
                else if (field.Name == "CardBg")
                {
                    // Glass themes paint the card as a vertical gradient; everyone
                    // else gets a plain solid (CardBgEnd stays empty for them).
                    window.Resources[key] = palette.IsGlass && !String.IsNullOrEmpty(palette.CardBgEnd)
                        ? (Brush)new LinearGradientBrush(Colour(value), Colour(palette.CardBgEnd), 90.0)
                        : (Brush)Brush(value);
                }
                else if (field.Name == "GlassHighlight")
                {
                    window.Resources[key] = String.IsNullOrEmpty(value)
                        ? Brushes.Transparent
                        : (Brush)new LinearGradientBrush(Colour(value), Colors.Transparent, 90.0);
                }
                else if (field.Name == "CardBgEnd")
                {
                    // Folded into the CardBg gradient above; no resource of its own.
                }
                else
                {
                    window.Resources[key] = Brush(value);
                }
            }
        }
    }

    // One row of the theme gallery popup: a miniature preview of a family in the
    // currently active light/dark variant, plus the localised name and a check.
    public sealed class ThemeCardItem : Bindable
    {
        private bool _current;
        public ThemeFamily Family { get; set; }
        public string Name { get; set; }
        public Brush PreviewBg { get; set; }
        public Brush PreviewBorder { get; set; }
        public Brush PreviewAccent { get; set; }
        public Brush PreviewHeat { get; set; }
        public bool IsCurrent
        {
            get { return _current; }
            set { if (_current == value) return; _current = value; Changed("IsCurrent"); Changed("CheckVisibility"); }
        }
        public Visibility CheckVisibility { get { return _current ? Visibility.Visible : Visibility.Collapsed; } }
    }


    public sealed class TaskItem : Bindable
    {
        private readonly TodoController _owner;
        private string _text;
        private bool _completed;
        private DateTime? _dueAt;
        public string Id { get; private set; }
        public string Text { get { return _text; } set { _text = value; Changed("Text"); Changed("CheckLabel"); Changed("DeleteLabel"); Changed("A11yLabel"); } }
        public bool IsCompleted
        {
            get { return _completed; }
            set
            {
                if (_completed == value) return;
                _completed = value;
                Changed("IsCompleted"); Changed("CheckLabel"); Changed("A11yLabel");
                RefreshDue();
                _owner.CompletionToggled(this, value);
            }
        }
        public DateTime? DueAt
        {
            get { return _dueAt; }
            set { if (_dueAt == value) return; _dueAt = value; RefreshDue(); _owner.TaskChanged(); }
        }
        public string DueText { get { return TaskItem.FormatDue(_dueAt, _completed, DateTime.Now, _owner.Language); } }
        public Brush DueBrush
        {
            get
            {
                // Semantic colours come from the active palette: red stays red and
                // green stays green, but the exact shade is tuned per theme.
                ThemePalette palette = ThemeManager.Current;
                switch (DueLogic.State(_dueAt, _completed, DateTime.Now))
                {
                    case DueState.Today: return ThemeManager.Brush(palette.DueToday);
                    case DueState.Overdue: return ThemeManager.Brush(palette.Overdue);
                    case DueState.Completed: return ThemeManager.Brush(palette.DoneGray);
                    default: return ThemeManager.Brush(palette.DueFuture);
                }
            }
        }
        public Visibility DueVisibility { get { return _dueAt.HasValue ? Visibility.Visible : Visibility.Collapsed; } }
        public string CheckLabel { get { return Strings.T(_owner.Language, _completed ? "task.restore" : "task.complete") + Strings.T(_owner.Language, "label.sep") + _text; } }
        public string DeleteLabel { get { return Strings.T(_owner.Language, "task.delete") + Strings.T(_owner.Language, "label.sep") + _text; } }
        public string DuePickerLabel { get { return Strings.T(_owner.Language, "task.due") + Strings.T(_owner.Language, "label.sep") + _text; } }
        // Screen readers get one stable label per row.
        public string A11yLabel { get { return Strings.T(_owner.Language, _completed ? "task.restore" : "task.complete") + " " + _text; } }
        public ICommand EditCommand { get; private set; }
        public ICommand DeleteCommand { get; private set; }
        public ICommand PickDueCommand { get; private set; }
        public TaskItem(TodoController owner, TodoRecord record)
        {
            _owner = owner; Id = record.Id; _text = record.Text; _completed = record.IsCompleted;
            _dueAt = DueLogic.Parse(record.DueAt);
            EditCommand = new RelayCommand(delegate { owner.BeginEdit(this); });
            DeleteCommand = new RelayCommand(delegate { owner.Delete(this); });
            PickDueCommand = new RelayCommand(delegate { owner.OpenDuePicker(this); }, delegate { return owner.CanEditTasks; });
        }
        // Recomputes time-relative labels (today/overdue flip at midnight and as time passes).
        public void RefreshDue()
        {
            Changed("DueText"); Changed("DueBrush"); Changed("DueVisibility"); Changed("A11yLabel");
        }
        public TodoRecord ToRecord() { return new TodoRecord { Id = Id, Text = _text, IsCompleted = _completed, DueAt = DueLogic.FormatOrNull(_dueAt) }; }

        internal static string FormatDue(DateTime? due, bool completed, DateTime now)
        {
            return FormatDue(due, completed, now, UiLanguage.ZhHans);
        }

        // Language-aware deadline label. Every caller that has a controller must pass
        // its language through, otherwise the label silently stays Chinese.
        internal static string FormatDue(DateTime? due, bool completed, DateTime now, UiLanguage language)
        {
            if (!due.HasValue) return "";
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

        // Re-renders the label in a different language after a language switch.
        internal void LanguageChanged()
        {
            RefreshDue();
            Changed("CheckLabel"); Changed("DeleteLabel"); Changed("DuePickerLabel"); Changed("A11yLabel");
        }
    }

    // One month-grid cell, prepared for binding by the controller from CalendarCell data.
    public sealed class CalendarDayItem : Bindable
    {
        private bool _selected;
        public DateTime Date { get; set; }
        public string DayLabel { get; set; }
        public bool IsCurrentMonth { get; set; }
        public bool IsToday { get; set; }
        public int Count { get; set; }
        public int HeatLevel { get; set; }
        public string CountLabel { get; set; }
        public string ToolTipText { get; set; }
        public string AutomationLabel { get; set; }
        public Brush CellBrush { get; set; }
        public Brush LabelBrush { get; set; }
        public Brush TodayBorderBrush { get; set; }
        public Visibility DotVisibility { get; set; }
        public Visibility HolidayMarkVisibility { get; set; }
        public bool IsHoliday { get; set; }
        public bool IsWeekend { get; set; }
        public bool IsAnniversary { get; set; }
        public bool InHeatScale { get; set; }
        // True when heat mode is on but this day is deliberately out of the scale
        // (a future day under inverted heat), so the UI paints it neutrally.
        public bool OutOfHeatScale { get; set; }
        public bool IsSelected
        {
            get { return _selected; }
            set { if (_selected == value) return; _selected = value; Changed("IsSelected"); Changed("SelectionBrush"); }
        }
        public Brush SelectionBrush { get { return _selected ? ThemeManager.Brush(ThemeManager.Current.SelectedCell) : CellBrush; } }
        public ICommand SelectCommand { get; set; }
    }

    public sealed class TodoController : Bindable
    {
        // The heart day keeps the same red in every theme - it is a keepsake,
        // not a themeable accent.
        private static readonly Brush CalendarAnniversaryBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x5F));
        private static readonly Brush TransparentBrush = Brushes.Transparent;

        private readonly Window _window;
        private readonly AppRepository _repository;
        private readonly CompletionLog _completionLog;
        private readonly ClearedLog _clearedLog;
        private readonly TextBox _input;
        private readonly ScrollViewer _scroller;
        private readonly System.Windows.Controls.Primitives.Popup _duePickerPopup;
        private readonly System.Windows.Controls.Primitives.Popup _themePopup;
        private readonly DispatcherTimer _geometryTimer;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _dateTimer;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _reminderTimer;
        private readonly HwndSourceHook _resizeHook;
        private readonly bool _trayEnabled;
        private HwndSource _windowSource;
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private System.Windows.Forms.ContextMenuStrip _trayMenu;
        private System.Windows.Forms.ToolStripMenuItem _trayShowItem;
        private System.Windows.Forms.ToolStripMenuItem _trayRestoreItem;
        private System.Windows.Forms.ToolStripMenuItem _trayStartupItem;
        private System.Windows.Forms.ToolStripMenuItem _trayReminderItem;
        private System.Windows.Forms.ToolStripMenuItem _trayMorningMenu;
        private System.Windows.Forms.ToolStripMenuItem _trayEveningMenu;
        private System.Windows.Forms.ToolStripMenuItem _trayLanguageItem;
        private System.Windows.Forms.ToolStripMenuItem _trayThemeItem;
        private System.Windows.Forms.ToolStripMenuItem _trayThemeModeItem;
        private System.Windows.Forms.ToolStripMenuItem _trayExitItem;
        private readonly List<System.Windows.Forms.ToolStripMenuItem> _morningHourItems = new List<System.Windows.Forms.ToolStripMenuItem>();
        private readonly List<System.Windows.Forms.ToolStripMenuItem> _eveningHourItems = new List<System.Windows.Forms.ToolStripMenuItem>();
        private readonly List<System.Windows.Forms.ToolStripMenuItem> _languageItems = new List<System.Windows.Forms.ToolStripMenuItem>();
        private readonly List<System.Windows.Forms.ToolStripMenuItem> _themeFamilyItems = new List<System.Windows.Forms.ToolStripMenuItem>();
        private readonly List<System.Windows.Forms.ToolStripMenuItem> _themeModeItems = new List<System.Windows.Forms.ToolStripMenuItem>();
        private bool _syncingTrayMenu;
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
        private bool _calendarExpanded;
        private bool _calendarHeatMode;
        private bool _calendarHeatInverted;
        private int _calendarYear;
        private int _calendarMonth;
        private DateTime? _selectedDay;
        private int _reminderHour = 20;
        private int _morningReminderHour = ReminderSlot.DefaultMorningHour;
        private int _eveningReminderHour = ReminderSlot.DefaultEveningHour;
        private string _lastMorningReminded;
        private string _lastEveningReminded;
        private UiLanguage _language = UiLanguage.ZhHans;
        private ThemeFamily _themeFamily = ThemeFamily.Sage;
        private LightDarkMode _lightDarkMode = LightDarkMode.FollowSystem;
        private string _lastDashboardDate;
        private bool _runAtStartup;
        private bool _trayHintShown;
        private TaskItem _duePickerTask;
        private int _pickerYear;
        private int _pickerMonth;
        private DateTime? _pickerDate;
        private DateTime _lastTickDay = DateTime.Today;
        private Dictionary<DateTime, int> _dueCountsCache = new Dictionary<DateTime, int>();
        private int _calendarMaxDone;
        private Dictionary<DateTime, int> _doneCounts = new Dictionary<DateTime, int>();
        private Dictionary<DateTime, List<string>> _doneTexts = new Dictionary<DateTime, List<string>>();
        private Window _reminderWindow;
        private object _aboutWindow;
        public ObservableCollection<TaskItem> Tasks { get; private set; }
        public ObservableCollection<CalendarDayItem> CalendarDays { get; private set; }
        public ObservableCollection<TaskItem> SelectedDayTasks { get; private set; }
        public ObservableCollection<CalendarDayItem> PickerDays { get; private set; }
        public ObservableCollection<string> PickerHours { get; private set; }
        public ObservableCollection<string> WeekHeaders { get; private set; }
        public ObservableCollection<ThemeCardItem> ThemeCards { get; private set; }
        public ICommand AddCommand { get; private set; }
        public ICommand ClearCompletedCommand { get; private set; }
        public ICommand UndoCommand { get; private set; }
        public ICommand ReloadCommand { get; private set; }
        public ICommand ToggleCalendarCommand { get; private set; }
        public ICommand PrevMonthCommand { get; private set; }
        public ICommand NextMonthCommand { get; private set; }
        public ICommand ToggleCalendarModeCommand { get; private set; }
        public ICommand ToggleLightModeCommand { get; private set; }
        public ICommand PickerPrevMonthCommand { get; private set; }
        public ICommand PickerNextMonthCommand { get; private set; }
        public ICommand ClearDueCommand { get; private set; }
        public ICommand SetDueHourCommand { get; private set; }
        public ICommand SelectThemeCommand { get; private set; }
        public ICommand SetLightDarkModeCommand { get; private set; }
        public bool CanEditTasks { get { return _repository.CanSave && String.IsNullOrEmpty(_dataError); } }
        public string DataLocationHint
        {
            get
            {
                return Strings.T(_language, "data.location", _repository.DataDirectory) + "\n"
                    + (String.IsNullOrEmpty(_dataError) ? Strings.T(_language, "data.backup") : _dataError);
            }
        }
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

        // ---- Language -----------------------------------------------------------------

        public UiLanguage Language
        {
            get { return _language; }
            set
            {
                if (_language == value) return;
                _language = value;
                ApplyLanguage();
                if (_ready) Save();
            }
        }
        public string LanguageCode { get { return Strings.CodeOf(_language); } }

        // ---- Theme ------------------------------------------------------------------

        // The persisted choice is a family plus a light/dark mode; the resolved
        // palette (which of the ten concrete variants) lives in ThemeManager.
        public ThemeFamily ThemeFamily
        {
            get { return _themeFamily; }
            set
            {
                if (_themeFamily == value) return;
                _themeFamily = value;
                ThemeManager.SetAndApply(_themeFamily, _lightDarkMode);
                if (_ready) Save();
            }
        }
        public LightDarkMode ThemeMode
        {
            get { return _lightDarkMode; }
            set
            {
                if (_lightDarkMode == value) return;
                _lightDarkMode = value;
                ThemeManager.SetAndApply(_themeFamily, _lightDarkMode);
                if (_ready) Save();
            }
        }
        public string ThemeHint { get { return Strings.T(_language, "theme.hint"); } }
        public string ThemeGalleryTitle { get { return Strings.T(_language, "theme.title"); } }
        public string ModeFollowLabel { get { return Strings.T(_language, "theme.mode.follow"); } }
        public string ModeLightLabel { get { return Strings.T(_language, "theme.mode.light"); } }
        public string ModeDarkLabel { get { return Strings.T(_language, "theme.mode.dark"); } }
        // The active mode button in the gallery gets a soft accent wash.
        public Brush ModeFollowBg { get { return ModeButtonBrush(LightDarkMode.FollowSystem); } }
        public Brush ModeLightBg { get { return ModeButtonBrush(LightDarkMode.Light); } }
        public Brush ModeDarkBg { get { return ModeButtonBrush(LightDarkMode.Dark); } }
        private Brush ModeButtonBrush(LightDarkMode mode)
        {
            return _lightDarkMode == mode
                ? ThemeManager.Brush(ThemeManager.Current.AccentSoft)
                : Brushes.Transparent;
        }
        internal string ThemeName(ThemeFamily family)
        {
            return Strings.T(_language, "theme." + Themes.CodeOf(family));
        }

        public string AppTitle { get { return Strings.T(_language, "msgbox.title"); } }
        public string HeadingText { get { return Strings.T(_language, "heading"); } }
        public string SubtitleText { get { return Strings.T(_language, "subtitle"); } }
        public string PinLabel { get { return Strings.T(_language, "pin.label"); } }
        public string PinHint { get { return Strings.T(_language, _pinned ? "tip.pin.on" : "tip.pin.off"); } }
        public string DateLabel
        {
            get
            {
                DateTime now = DateTime.Now;
                return Strings.T(_language, "date.line", Strings.Date(_language, now), Strings.WeekFull(_language, now));
            }
        }
        public string RemainingLabel
        {
            get
            {
                if (!CanEditTasks) return Strings.T(_language, "card.summary.broken");
                int open = Tasks.Count(t => !t.IsCompleted);
                if (Tasks.Count == 0) return Strings.T(_language, "card.summary.new");
                if (open == 0) return Strings.T(_language, "card.summary.done");
                int overdue = Tasks.Count(t => !t.IsCompleted && t.DueAt.HasValue && t.DueAt.Value < DateTime.Now);
                return overdue > 0
                    ? Strings.T(_language, "card.summary.overdue", open, overdue)
                    : Strings.T(_language, "card.summary.todo", open);
            }
        }
        public string ProgressLabel
        {
            get
            {
                return !CanEditTasks
                    ? Strings.T(_language, "card.progress.broken")
                    : Strings.T(_language, "card.progress", Tasks.Count(t => t.IsCompleted), Tasks.Count);
            }
        }
        public double CompletedPercent { get { return Tasks.Count == 0 ? 0 : 100.0 * Tasks.Count(t => t.IsCompleted) / Tasks.Count; } }
        public Visibility EmptyVisibility { get { return Tasks.Count == 0 && CanEditTasks ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility PlaceholderVisibility { get { return String.IsNullOrEmpty(InputText) ? Visibility.Visible : Visibility.Collapsed; } }
        public string InputHint { get { return Strings.T(_language, _editing == null ? "input.add" : "input.edit"); } }
        public string AddHint { get { return Strings.T(_language, _editing == null ? "input.addHint" : "input.editHint"); } }
        public string AddGlyph { get { return _editing == null ? "+" : "✓"; } }
        public string ToastText { get { return _toastText; } }
        public Visibility ToastVisibility { get { return String.IsNullOrEmpty(_toastText) ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility UndoVisibility { get { return _undo.Count > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public string UndoLabel { get { return Strings.T(_language, "toast.undo"); } }
        public string ClearCompletedLabel { get { return Strings.T(_language, "footer.clearCompleted"); } }
        public string ReloadLabel { get { return Strings.T(_language, "footer.reload"); } }
        public string EmptyTitle { get { return Strings.T(_language, "empty.title"); } }
        public string EmptyHint { get { return Strings.T(_language, "empty.hint"); } }
        public string MinimizeHint { get { return Strings.T(_language, "window.minimize"); } }
        public string CloseHint { get { return Strings.T(_language, "window.close"); } }
        public string ResizeHint { get { return Strings.T(_language, "window.resize"); } }
        public string DragHint { get { return Strings.T(_language, "window.drag"); } }
        public string PrevMonthHint { get { return Strings.T(_language, "cal.prev"); } }
        public string NextMonthHint { get { return Strings.T(_language, "cal.next"); } }
        public string CalendarBarTooltip { get { return Strings.T(_language, "cal.barTooltip"); } }
        public string CalendarModeTooltip { get { return Strings.T(_language, "cal.mode.toggle"); } }
        public string ClearDueLabel { get { return Strings.T(_language, "picker.clear"); } }
        public string TaskDoneHint { get { return Strings.T(_language, "task.doneToggle"); } }
        public string TaskTextHint { get { return Strings.T(_language, "task.text.tip"); } }
        public string TaskDueHint { get { return Strings.T(_language, "task.due"); } }
        public string TaskDeleteHint { get { return Strings.T(_language, "task.delete"); } }
        public string TaskMenuEdit { get { return Strings.T(_language, "task.menu.edit"); } }
        public string TaskMenuDue { get { return Strings.T(_language, "task.menu.due"); } }
        public string TaskMenuDelete { get { return Strings.T(_language, "task.menu.delete"); } }
        public string SaveStatus
        {
            get
            {
                if (!String.IsNullOrEmpty(_dataError)) return Strings.T(_language, "footer.blocked");
                if (!_saveOk) return Strings.T(_language, "footer.saveFailed");
                if (!String.IsNullOrEmpty(_loadNotice)) return _loadNotice;
                return _editing == null
                    ? Strings.T(_language, "footer.saving", Tasks.Count)
                    : Strings.T(_language, "footer.editing");
            }
        }
        public Brush SaveStatusColor { get { return _saveOk ? ThemeManager.Brush(ThemeManager.Current.TextMuted) : ThemeManager.Brush(ThemeManager.Current.Overdue); } }

        public bool CalendarExpanded
        {
            get { return _calendarExpanded; }
            set
            {
                if (_calendarExpanded == value) return;
                _calendarExpanded = value;
                Changed("CalendarExpanded"); Changed("CalendarVisibility"); Changed("CalendarChevron");
                if (_ready) Save();
            }
        }
        public Visibility CalendarVisibility { get { return _calendarExpanded ? Visibility.Visible : Visibility.Collapsed; } }
        public string CalendarChevron { get { return _calendarExpanded ? "▾" : "▸"; } }
        public string CalendarTitle { get { return Strings.T(_language, "cal.monthTitle", _calendarYear, _calendarMonth); } }
        public string CalendarBarTitle { get { return Strings.CalendarBar(_language, DateTime.Now); } }
        public bool CalendarHeatMode
        {
            get { return _calendarHeatMode; }
            set
            {
                if (_calendarHeatMode == value) return;
                _calendarHeatMode = value;
                Changed("CalendarHeatMode"); Changed("CalendarModeSwitchLabel"); Changed("InvertVisibility");
                RebuildCalendar();
                if (_ready) Save();
            }
        }

        // "Light mode": the same heat scale walked from the other end, so a day with
        // more completions is paler and a day with fewer is deeper.
        public bool CalendarHeatInverted
        {
            get { return _calendarHeatInverted; }
            set
            {
                if (_calendarHeatInverted == value) return;
                _calendarHeatInverted = value;
                Changed("CalendarHeatInverted"); Changed("InvertLabel"); Changed("InvertTooltip");
                RebuildCalendar();
                if (_ready) Save();
            }
        }
        public string CalendarModeSwitchLabel { get { return Strings.T(_language, _calendarHeatMode ? "cal.mode.toDue" : "cal.mode.toHeat"); } }
        public string InvertLabel { get { return Strings.T(_language, _calendarHeatInverted ? "cal.invert.on" : "cal.invert.off"); } }
        public string InvertTooltip { get { return Strings.T(_language, "cal.invert.tooltip"); } }
        public Visibility InvertVisibility { get { return _calendarHeatMode ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility SelectedDayVisibility { get { return _selectedDay.HasValue && _calendarExpanded ? Visibility.Visible : Visibility.Collapsed; } }
        public string SelectedDayTitle
        {
            get
            {
                if (!_selectedDay.HasValue) return "";
                int due = SelectedDayTasks.Count(t => !t.IsCompleted);
                string day = Strings.Date(_language, _selectedDay.Value);
                return due > 0
                    ? day + " · " + Strings.CountOf(_language, due, "due")
                    : Strings.T(_language, "cal.day.due.none", day);
            }
        }
        public string PickerTitle { get { return _duePickerTask == null ? "" : Strings.T(_language, "picker.title", Truncate(_duePickerTask.Text, 12)); } }
        public string PickerMonthTitle { get { return Strings.T(_language, "cal.monthTitle", _pickerYear, _pickerMonth); } }
        public string PickerCurrentDue
        {
            get
            {
                if (_duePickerTask == null) return "";
                bool pickedNewDate = _pickerDate.HasValue
                    && (!_duePickerTask.DueAt.HasValue || _pickerDate.Value != _duePickerTask.DueAt.Value.Date);
                if (pickedNewDate) return Strings.T(_language, "picker.flowPicked", Strings.Date(_language, _pickerDate.Value));
                string current = _duePickerTask.DueAt.HasValue
                    ? FormatDueLabel(_duePickerTask.DueAt, false, DateTime.Now) : "";
                return String.IsNullOrEmpty(current) ? Strings.T(_language, "picker.flowEmpty") : Strings.T(_language, "picker.flowCurrent", current);
            }
        }
        public bool PickerHoursEnabled { get { return _pickerDate.HasValue; } }
        // The morning slot reports today's to-dos; the evening slot reports tomorrow's.
        public int MorningReminderHour
        {
            get { return _morningReminderHour; }
            set
            {
                if (!ReminderSlot.IsValidHour(value)) return;
                int evening = _eveningReminderHour;
                if (value == evening) return; // the two slots never share an hour
                _morningReminderHour = value;
                SyncTrayReminderChecks();
                if (_ready) Save();
            }
        }
        public int EveningReminderHour
        {
            get { return _eveningReminderHour; }
            set
            {
                if (!ReminderSlot.IsValidHour(value)) return;
                if (value == _morningReminderHour) return;
                _eveningReminderHour = value;
                _reminderHour = value;
                SyncTrayReminderChecks();
                if (_ready) Save();
            }
        }
        internal string LastMorningReminded { get { return _lastMorningReminded; } }
        internal string LastEveningReminded { get { return _lastEveningReminded; } }
        // Test hook: the integration suite drives reminders manually with injected times.
        internal void StopReminderTimer() { _reminderTimer.Stop(); }
        private static string Truncate(string text, int length)
        {
            if (String.IsNullOrEmpty(text)) return "";
            string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= length ? flat : flat.Substring(0, length) + "…";
        }

        public TodoController(Window window, string directory, bool enableTray = true)
        {
            _window = window;
            _trayEnabled = enableTray;
            _resizeHook = ResizeHitTest;
            _repository = new AppRepository(directory);
            _completionLog = new CompletionLog(directory);
            _clearedLog = new ClearedLog(directory);
            StorageDiagnostics.Write(directory, "startup-before-load", -1, "primaryExists=" + File.Exists(Path.Combine(directory, "tasks.json")) + "; backupExists=" + File.Exists(Path.Combine(directory, "tasks.backup.json")));
            var state = _repository.Load();
            _saveOk = _repository.CanSave;
            _dataError = _repository.CanSave ? "" : (_repository.LoadWarning ?? Strings.T(_language, "footer.blocked"));
            _loadNotice = _repository.CanSave && !String.IsNullOrEmpty(_repository.LoadWarning) ? Strings.T(_language, "footer.recovered") : "";
            StorageDiagnostics.Write(directory, "startup-loaded", state.Tasks.Count, "canSave=" + _repository.CanSave + "; hasWarning=" + !String.IsNullOrEmpty(_repository.LoadWarning));
            Tasks = new ObservableCollection<TaskItem>(state.Tasks.Select(t => new TaskItem(this, t)));
            _pinned = state.IsPinned;
            _calendarExpanded = state.CalendarExpanded;
            _calendarHeatMode = state.CalendarHeatMode;
            _calendarHeatInverted = state.CalendarHeatInverted;
            _morningReminderHour = state.MorningReminderHour;
            _eveningReminderHour = state.EveningReminderHour;
            _lastMorningReminded = state.LastMorningReminded;
            _lastEveningReminded = state.LastEveningReminded;
            _language = Strings.Parse(state.Language);
            _themeFamily = Themes.ParseFamily(state.ThemeFamily);
            _lightDarkMode = Themes.ParseMode(state.LightDarkMode);
            _lastDashboardDate = state.LastDashboardDate;
            _reminderHour = _eveningReminderHour;
            _runAtStartup = state.RunAtStartup;
            _trayHintShown = state.TrayHintShown;
            _calendarYear = DateTime.Today.Year;
            _calendarMonth = DateTime.Today.Month;
            _pickerYear = _calendarYear;
            _pickerMonth = _calendarMonth;
            CalendarDays = new ObservableCollection<CalendarDayItem>();
            SelectedDayTasks = new ObservableCollection<TaskItem>();
            PickerDays = new ObservableCollection<CalendarDayItem>();
            PickerHours = new ObservableCollection<string>(Enumerable.Range(0, 24).Select(h => h.ToString("D2") + ":00"));
            WeekHeaders = new ObservableCollection<string>(Enumerable.Range(0, 7).Select(i => Strings.WeekHead(_language, i)));
            _input = (TextBox)window.FindName("TaskInput");
            _scroller = (ScrollViewer)window.FindName("TaskScroller");
            _duePickerPopup = (System.Windows.Controls.Primitives.Popup)window.FindName("DuePickerPopup");
            _themePopup = (System.Windows.Controls.Primitives.Popup)window.FindName("ThemePopup");
            AddCommand = new RelayCommand(Add, delegate { return CanEditTasks && !String.IsNullOrWhiteSpace(InputText); });
            ClearCompletedCommand = new RelayCommand(ClearCompleted, delegate { return CanEditTasks && Tasks.Any(t => t.IsCompleted); });
            UndoCommand = new RelayCommand(Undo, delegate { return CanEditTasks && _undo.Count > 0; });
            ReloadCommand = new RelayCommand(delegate { RefreshFromDisk(); }, delegate { return !_hasUnsavedTaskChanges && _editing == null; });
            ToggleCalendarCommand = new RelayCommand(delegate { CalendarExpanded = !CalendarExpanded; });
            PrevMonthCommand = new RelayCommand(delegate { ShiftMonth(-1); });
            NextMonthCommand = new RelayCommand(delegate { ShiftMonth(1); });
            ToggleCalendarModeCommand = new RelayCommand(delegate { CalendarHeatMode = !CalendarHeatMode; });
            ToggleLightModeCommand = new RelayCommand(delegate
            {
                CalendarHeatInverted = !CalendarHeatInverted;
                SetToast(Strings.T(_language, _calendarHeatInverted ? "toast.invertOn" : "toast.invertOff"));
            });
            PickerPrevMonthCommand = new RelayCommand(delegate { ShiftPickerMonth(-1); });
            PickerNextMonthCommand = new RelayCommand(delegate { ShiftPickerMonth(1); });
            ClearDueCommand = new RelayCommand(ClearDue, delegate { return _duePickerTask != null; });
            SetDueHourCommand = new ParamCommand(SetDueHour, delegate { return _duePickerTask != null && _pickerDate.HasValue; });
            SelectThemeCommand = new ParamCommand(SelectTheme);
            SetLightDarkModeCommand = new ParamCommand(SetLightDarkMode);
            ThemeCards = new ObservableCollection<ThemeCardItem>();
            // Register first so the window's resources match the saved theme before
            // the first frame; SetAndApply then resolves the persisted pair (and
            // fires OnThemeApplied only when it differs from the static default).
            ThemeManager.Register(window);
            ThemeManager.Applied += OnThemeApplied;
            ThemeManager.SetAndApply(_themeFamily, _lightDarkMode);
            RebuildThemeCards();
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            ((Button)window.FindName("ThemeButton")).Click += delegate { OpenThemeGallery(); };
            window.DataContext = this;
            window.Topmost = _pinned;
            RebuildDoneCache();
            RebuildCalendar();
            RebuildPickerDays();
            _geometryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _geometryTimer.Tick += delegate { _geometryTimer.Stop(); Save(); };
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _toastTimer.Tick += delegate { _toastTimer.Stop(); _undo.Clear(); SetToast(""); };
            _dateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _dateTimer.Tick += delegate
            {
                Changed("DateLabel"); Changed("CalendarBarTitle");
                // Labels like "today"/"overdue" and the calendar grid flip at midnight.
                if (DateTime.Today != _lastTickDay)
                {
                    _lastTickDay = DateTime.Today;
                    foreach (TaskItem task in Tasks) task.RefreshDue();
                    RebuildCalendar();
                }
            };
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += delegate { CheckForRecoveredData(); };
            _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _reminderTimer.Tick += delegate { TryRemind(DateTime.Now, false, true); };
            ((Button)window.FindName("CloseButton")).Click += delegate { HideToTray(); };
            _duePickerPopup.Closed += delegate
            {
                _duePickerTask = null;
                _pickerDate = null;
                Changed("PickerHoursEnabled");
                CommandManager.InvalidateRequerySuggested();
            };
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
                _ready = true; _dateTimer.Start(); _refreshTimer.Start(); _reminderTimer.Start(); _input.Focus();
                CheckForRecoveredData();
                if (!String.IsNullOrEmpty(_repository.LoadWarning)) SetToast(_repository.LoadWarning);
                TryRemind(DateTime.Now, true, true);
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
                if (!saved && _hasUnsavedTaskChanges && MessageBox.Show(window, Strings.T(_language, "msgbox.unsaved"), Strings.T(_language, "msgbox.unsavedTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
            };
            window.Closed += delegate
            {
                _ready = false; _geometryTimer.Stop(); _toastTimer.Stop(); _dateTimer.Stop(); _refreshTimer.Stop(); _reminderTimer.Stop();
                SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
                ThemeManager.Applied -= OnThemeApplied;
                ThemeManager.Unregister(window);
                if (_windowSource != null && !_windowSource.IsDisposed) _windowSource.RemoveHook(_resizeHook);
                if (_reminderWindow != null) { _reminderWindow.Close(); _reminderWindow = null; }
                // Never leave a ghost tray icon behind, whatever the exit path was.
                if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
            };
            if (_trayEnabled) SetupTray();
        }

        private void SetupTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon { Text = Strings.T(_language, "msgbox.title"), Visible = true };
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("App.ico"))
                if (stream != null) _trayIcon.Icon = new System.Drawing.Icon(stream);
            _trayIcon.MouseUp += delegate (object sender, System.Windows.Forms.MouseEventArgs e)
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray();
            };
            System.Windows.Forms.ContextMenuStrip menu = new System.Windows.Forms.ContextMenuStrip();
            _trayMenu = menu;
            _trayShowItem = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.show"));
            _trayShowItem.Name = "show";
            _trayShowItem.Click += delegate { ShowFromTray(); };
            menu.Items.Add(_trayShowItem);

            // "Restore to-do" is the permanent entrance the 10-second undo toast
            // never had. It always shows; the count and enabled state are computed
            // every time the menu opens so they can never go stale.
            _trayRestoreItem = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.restore"));
            _trayRestoreItem.Name = "restore";
            _trayRestoreItem.Enabled = false;
            _trayRestoreItem.Click += delegate { OpenRestoreWindow(); };
            menu.Items.Add(_trayRestoreItem);
            menu.Opening += delegate { SyncTrayRestoreState(); };

            // The registry is the source of truth for auto-start; the saved flag only
            // records intent. Reconcile on startup so manual registry edits win.
            System.Windows.Forms.ToolStripMenuItem startupItem = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.autostart")) { CheckOnClick = true };
            _trayStartupItem = startupItem;
            _syncingTrayMenu = true;
            startupItem.Checked = StartupRegistry.IsEnabled();
            _syncingTrayMenu = false;
            _runAtStartup = startupItem.Checked;
            startupItem.CheckedChanged += delegate
            {
                if (_syncingTrayMenu) return;
                if (!StartupRegistry.SetEnabled(startupItem.Checked))
                {
                    _syncingTrayMenu = true;
                    startupItem.Checked = StartupRegistry.IsEnabled();
                    _syncingTrayMenu = false;
                    SetToast(Strings.T(_language, "tray.autostartFailed"));
                }
                _runAtStartup = startupItem.Checked;
                if (_ready) Save();
            };
            menu.Items.Add(startupItem);

            // Two reminder slots, each offering its own hour range. The ranges are
            // disjoint, which also guarantees the slots can never collide.
            System.Windows.Forms.ToolStripMenuItem reminderMenu = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.reminder"));
            _trayReminderItem = reminderMenu;
            _trayMorningMenu = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.morning"));
            _trayEveningMenu = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.evening"));
            for (int hour = ReminderSlot.FloorHour; hour <= 11; hour++)
            {
                int captured = hour;
                System.Windows.Forms.ToolStripMenuItem hourItem = new System.Windows.Forms.ToolStripMenuItem(FormatHour(hour)) { CheckOnClick = true };
                hourItem.Click += delegate { MorningReminderHour = captured; };
                _morningHourItems.Add(hourItem);
                _trayMorningMenu.DropDownItems.Add(hourItem);
            }
            for (int hour = 12; hour <= ReminderSlot.CeilingHour; hour++)
            {
                int captured = hour;
                System.Windows.Forms.ToolStripMenuItem hourItem = new System.Windows.Forms.ToolStripMenuItem(FormatHour(hour)) { CheckOnClick = true };
                hourItem.Click += delegate { EveningReminderHour = captured; };
                _eveningHourItems.Add(hourItem);
                _trayEveningMenu.DropDownItems.Add(hourItem);
            }
            reminderMenu.DropDownItems.Add(_trayMorningMenu);
            reminderMenu.DropDownItems.Add(_trayEveningMenu);
            menu.Items.Add(reminderMenu);

            System.Windows.Forms.ToolStripMenuItem languageMenu = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.language"));
            _trayLanguageItem = languageMenu;
            for (int index = 0; index < 3; index++)
            {
                UiLanguage captured = (UiLanguage)index;
                System.Windows.Forms.ToolStripMenuItem languageItem = new System.Windows.Forms.ToolStripMenuItem(Strings.Table(captured).LanguageName) { CheckOnClick = true };
                languageItem.Click += delegate { Language = captured; };
                _languageItems.Add(languageItem);
                languageMenu.DropDownItems.Add(languageItem);
            }
            menu.Items.Add(languageMenu);

            // Theme: five families as a radio list, and the light/dark mode as a
            // second radio list. Both mirror the gallery popup on the card.
            System.Windows.Forms.ToolStripMenuItem themeMenu = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.theme"));
            _trayThemeItem = themeMenu;
            for (int index = 0; index < Themes.FamilyCount; index++)
            {
                ThemeFamily captured = (ThemeFamily)index;
                System.Windows.Forms.ToolStripMenuItem themeItem = new System.Windows.Forms.ToolStripMenuItem(ThemeName(captured)) { CheckOnClick = true };
                themeItem.Click += delegate { ThemeFamily = captured; };
                _themeFamilyItems.Add(themeItem);
                themeMenu.DropDownItems.Add(themeItem);
            }
            menu.Items.Add(themeMenu);

            System.Windows.Forms.ToolStripMenuItem themeModeMenu = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.themeMode"));
            _trayThemeModeItem = themeModeMenu;
            string[] trayModeKeys = { "theme.mode.follow", "theme.mode.light", "theme.mode.dark" };
            for (int index = 0; index < Themes.ModeCount; index++)
            {
                LightDarkMode captured = (LightDarkMode)index;
                System.Windows.Forms.ToolStripMenuItem modeItem = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, trayModeKeys[index])) { CheckOnClick = true };
                modeItem.Click += delegate { ThemeMode = captured; };
                _themeModeItems.Add(modeItem);
                themeModeMenu.DropDownItems.Add(modeItem);
            }
            menu.Items.Add(themeModeMenu);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _trayExitItem = new System.Windows.Forms.ToolStripMenuItem(Strings.T(_language, "tray.exit"));
            _trayExitItem.Name = "exit";
            _trayExitItem.Click += delegate { ExitApplication(); };
            menu.Items.Add(_trayExitItem);
            _trayIcon.ContextMenuStrip = menu;
            SyncTrayReminderChecks();
            SyncTrayLanguageChecks();
            SyncTrayThemeChecks();
        }

        private static string FormatHour(int hour)
        {
            return hour.ToString("D2", CultureInfo.InvariantCulture) + ":00";
        }

        private void SyncTrayReminderChecks()
        {
            _syncingTrayMenu = true;
            foreach (System.Windows.Forms.ToolStripMenuItem item in _morningHourItems)
                item.Checked = item.Text == FormatHour(_morningReminderHour);
            foreach (System.Windows.Forms.ToolStripMenuItem item in _eveningHourItems)
                item.Checked = item.Text == FormatHour(_eveningReminderHour);
            _syncingTrayMenu = false;
        }

        private void SyncTrayLanguageChecks()
        {
            _syncingTrayMenu = true;
            for (int index = 0; index < _languageItems.Count; index++)
                _languageItems[index].Checked = index == (int)_language;
            _syncingTrayMenu = false;
        }

        private void SyncTrayThemeChecks()
        {
            _syncingTrayMenu = true;
            for (int index = 0; index < _themeFamilyItems.Count; index++)
                _themeFamilyItems[index].Checked = index == (int)_themeFamily;
            for (int index = 0; index < _themeModeItems.Count; index++)
                _themeModeItems[index].Checked = index == (int)_lightDarkMode;
            _syncingTrayMenu = false;
        }

        // ---- Theme switching ----------------------------------------------------

        // A new palette has been stamped into the window's resources. Everything
        // the XAML reaches through DynamicResource is already fresh; what remains
        // is the code-side colouring: calendar cells, deadline labels and the
        // save-status text.
        private void OnThemeApplied()
        {
            RebuildCalendar();
            RebuildPickerDays();
            foreach (TaskItem task in Tasks) task.RefreshDue();
            Changed("SaveStatusColor");
            RebuildThemeCards();
            Changed("ModeFollowBg"); Changed("ModeLightBg"); Changed("ModeDarkBg");
            SyncTrayThemeChecks();
        }

        private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.BeginInvoke(new Action(RefreshThemeFromSystem));
                return;
            }
            RefreshThemeFromSystem();
        }

        private void RefreshThemeFromSystem()
        {
            if (_lightDarkMode == LightDarkMode.FollowSystem) ThemeManager.RefreshFromSystem();
        }

        // Test hook: the suite drives the gallery without synthesising clicks.
        internal void OpenThemeGallery()
        {
            RebuildThemeCards();
            _themePopup.IsOpen = true;
        }

        // Gallery rows preview each family in the variant the user would get if
        // they picked it right now (dark families preview dark when the mode
        // resolves dark).
        private void RebuildThemeCards()
        {
            bool dark = ThemeManager.Current.Dark;
            ThemeCards.Clear();
            for (int index = 0; index < Themes.FamilyCount; index++)
            {
                ThemeFamily family = (ThemeFamily)index;
                ThemePalette preview = Themes.Get(family, dark);
                ThemeCards.Add(new ThemeCardItem
                {
                    Family = family,
                    Name = ThemeName(family),
                    PreviewBg = ThemeManager.Brush(preview.CardBg),
                    PreviewBorder = ThemeManager.Brush(preview.CardBorder),
                    PreviewAccent = ThemeManager.Brush(preview.Accent),
                    PreviewHeat = ThemeManager.Brush(preview.Heat[preview.Heat.Length - 1]),
                    IsCurrent = family == _themeFamily
                });
            }
        }

        private void SelectTheme(object parameter)
        {
            if (parameter is ThemeFamily) ThemeFamily = (ThemeFamily)parameter;
            else if (parameter is string) ThemeFamily = Themes.ParseFamily((string)parameter);
            else return;
            RebuildThemeCards();
        }

        private void SetLightDarkMode(object parameter)
        {
            string value = Convert.ToString(parameter, CultureInfo.InvariantCulture);
            if (String.IsNullOrEmpty(value)) return;
            // XAML passes short tokens ("follow"/"light"/"dark"); the persisted
            // code for followSystem is longer, so map it explicitly.
            ThemeMode = value == "follow" ? LightDarkMode.FollowSystem : Themes.ParseMode(value);
        }

        private void ApplyLanguage()
        {
            foreach (TaskItem task in Tasks) task.LanguageChanged();
            WeekHeaders.Clear();
            for (int index = 0; index < 7; index++) WeekHeaders.Add(Strings.WeekHead(_language, index));
            _loadNotice = String.IsNullOrEmpty(_loadNotice) ? _loadNotice : Strings.T(_language, "footer.recovered");
            RebuildCalendar();
            RebuildPickerDays();
            Changed("LanguageCode"); Changed("AppTitle"); Changed("HeadingText"); Changed("SubtitleText");
            Changed("PinLabel"); Changed("PinHint"); Changed("DateLabel"); Changed("RemainingLabel");
            Changed("ProgressLabel"); Changed("InputHint"); Changed("AddHint"); Changed("UndoLabel");
            Changed("ClearCompletedLabel"); Changed("ReloadLabel"); Changed("EmptyTitle"); Changed("EmptyHint");
            Changed("MinimizeHint"); Changed("CloseHint"); Changed("ResizeHint"); Changed("DragHint");
            Changed("PrevMonthHint"); Changed("NextMonthHint"); Changed("CalendarBarTooltip");
            Changed("CalendarModeTooltip"); Changed("CalendarModeSwitchLabel"); Changed("InvertLabel");
            Changed("InvertTooltip"); Changed("CalendarBarTitle"); Changed("CalendarTitle");
            Changed("SelectedDayTitle"); Changed("PickerCurrentDue"); Changed("PickerMonthTitle");
            Changed("ClearDueLabel"); Changed("TaskDoneHint"); Changed("TaskTextHint"); Changed("TaskDueHint");
            Changed("TaskDeleteHint"); Changed("TaskMenuEdit"); Changed("TaskMenuDue"); Changed("TaskMenuDelete");
            Changed("SaveStatus"); Changed("DataLocationHint");
            Changed("ThemeHint"); Changed("ThemeGalleryTitle");
            Changed("ModeFollowLabel"); Changed("ModeLightLabel"); Changed("ModeDarkLabel");
            RebuildThemeCards();
            ApplyTrayLanguage();
        }

        private void ApplyTrayLanguage()
        {
            if (_trayMenu == null) return;
            if (_trayShowItem != null) _trayShowItem.Text = Strings.T(_language, "tray.show");
            if (_trayRestoreItem != null) _trayRestoreItem.Text = Strings.T(_language, "tray.restore"); // count returns on the next menu open
            if (_trayStartupItem != null) _trayStartupItem.Text = Strings.T(_language, "tray.autostart");
            if (_trayReminderItem != null) _trayReminderItem.Text = Strings.T(_language, "tray.reminder");
            if (_trayMorningMenu != null) _trayMorningMenu.Text = Strings.T(_language, "tray.morning");
            if (_trayEveningMenu != null) _trayEveningMenu.Text = Strings.T(_language, "tray.evening");
            if (_trayLanguageItem != null) _trayLanguageItem.Text = Strings.T(_language, "tray.language");
            if (_trayThemeItem != null)
            {
                _trayThemeItem.Text = Strings.T(_language, "tray.theme");
                for (int index = 0; index < _themeFamilyItems.Count; index++)
                    _themeFamilyItems[index].Text = ThemeName((ThemeFamily)index);
            }
            if (_trayThemeModeItem != null)
            {
                _trayThemeModeItem.Text = Strings.T(_language, "tray.themeMode");
                string[] modeKeys = { "theme.mode.follow", "theme.mode.light", "theme.mode.dark" };
                for (int index = 0; index < _themeModeItems.Count && index < modeKeys.Length; index++)
                    _themeModeItems[index].Text = Strings.T(_language, modeKeys[index]);
            }
            if (_trayExitItem != null) _trayExitItem.Text = Strings.T(_language, "tray.exit");
            if (_trayIcon != null)
            {
                _trayIcon.Text = Strings.T(_language, "msgbox.title");
                if (_trayHintShown) _trayIcon.Text = Strings.T(_language, "msgbox.title");
            }
            SyncTrayLanguageChecks();
            // The reminder window is generated text; refresh it so a switch is visible.
            if (_reminderWindow != null) { _reminderWindow.Close(); _reminderWindow = null; }
            if (_aboutWindow is Window) { ((Window)_aboutWindow).Close(); _aboutWindow = null; }
        }

        public void HideToTray()
        {
            Save();
            _window.Hide();
            if (!_trayEnabled || _trayIcon == null) return; // tray-less sessions (tests) simply hide
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                try { _trayIcon.ShowBalloonTip(3000, Strings.T(_language, "tray.hintTitle"), Strings.T(_language, "tray.hintText"), System.Windows.Forms.ToolTipIcon.Info); }
                catch (Exception error) { if (!(error is InvalidOperationException) && !(error is System.ComponentModel.Win32Exception)) throw; }
                if (_ready) Save();
            }
        }

        public void ShowFromTray()
        {
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            // Hiding drops the topmost flag; re-apply the saved pin state.
            _window.Topmost = _pinned;
        }

        public void ExitApplication()
        {
            _window.Close(); // runs the existing unsaved-changes guard
            if (_window.IsLoaded) return; // the user cancelled that prompt
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
            if (Application.Current != null) Application.Current.Shutdown();
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
                _repository.Save(new AppState
                {
                    Tasks = Tasks.Select(t => t.ToRecord()).ToList(),
                    IsPinned = _pinned,
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    CalendarExpanded = _calendarExpanded,
                    CalendarHeatMode = _calendarHeatMode,
                    CalendarHeatInverted = _calendarHeatInverted,
                    ReminderHour = _eveningReminderHour,
                    MorningReminderHour = _morningReminderHour,
                    EveningReminderHour = _eveningReminderHour,
                    LastMorningReminded = _lastMorningReminded,
                    LastEveningReminded = _lastEveningReminded,
                    Language = Strings.CodeOf(_language),
                    ThemeFamily = Themes.CodeOf(_themeFamily),
                    LightDarkMode = Themes.CodeOf(_lightDarkMode),
                    LastDashboardDate = _lastDashboardDate,
                    RunAtStartup = _runAtStartup,
                    TrayHintShown = _trayHintShown
                });
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
            _dataError = _repository.CanSave ? "" : (_repository.LoadWarning ?? Strings.T(_language, "footer.blocked"));
            if (!_repository.CanSave) { UpdateDataStatus(); return false; }
            bool wasReady = _ready;
            _ready = false;
            try
            {
                Tasks.Clear();
                foreach (var record in recovered.Tasks) Tasks.Add(new TaskItem(this, record));
                IsPinned = recovered.IsPinned;
                _calendarExpanded = recovered.CalendarExpanded;
                _calendarHeatMode = recovered.CalendarHeatMode;
                _calendarHeatInverted = recovered.CalendarHeatInverted;
                _morningReminderHour = recovered.MorningReminderHour;
                _eveningReminderHour = recovered.EveningReminderHour;
                _reminderHour = _eveningReminderHour;
                _lastMorningReminded = recovered.LastMorningReminded;
                _lastEveningReminded = recovered.LastEveningReminded;
                _lastDashboardDate = recovered.LastDashboardDate;
                _runAtStartup = recovered.RunAtStartup;
                _trayHintShown = recovered.TrayHintShown;
                SyncTrayReminderChecks();
                _undo.Clear(); _editDrafts.Clear();
                _loadNotice = !String.IsNullOrEmpty(_repository.LoadWarning) ? Strings.T(_language, "footer.recovered") : "";
                RebuildDoneCache();
                RebuildCalendar();
                Changed("CalendarExpanded"); Changed("CalendarVisibility"); Changed("CalendarChevron");
                Changed("CalendarHeatMode"); Changed("CalendarModeSwitchLabel"); Changed("InvertVisibility");
                Changed("CalendarHeatInverted"); Changed("InvertLabel");
                SetToast(Strings.T(_language, "toast.reread", Tasks.Count));
                if (recovered.Language != Strings.CodeOf(_language))
                {
                    _language = Strings.Parse(recovered.Language);
                    ApplyLanguage();
                }
                // Another instance may have switched theme; adopt it wholesale.
                ThemeFamily recoveredFamily = Themes.ParseFamily(recovered.ThemeFamily);
                LightDarkMode recoveredMode = Themes.ParseMode(recovered.LightDarkMode);
                if (recoveredFamily != _themeFamily || recoveredMode != _lightDarkMode)
                {
                    _themeFamily = recoveredFamily;
                    _lightDarkMode = recoveredMode;
                    ThemeManager.SetAndApply(_themeFamily, _lightDarkMode);
                }
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
            // Due dots follow every task change; a collapsed calendar skips the work.
            if (_calendarExpanded) RebuildCalendar();
            else RefreshSelectedDay();
            CommandManager.InvalidateRequerySuggested();
        }

        internal void CompletionToggled(TaskItem task, bool completed)
        {
            // Log first, then save: the completion log and the repository share
            // the directory mutex and must never hold it in nested order.
            try
            {
                if (completed) _completionLog.Append(new CompletionEntry { Id = task.Id, Text = task.Text, Done = DateTime.Now });
                else _completionLog.RemoveLatest(task.Id);
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException)) throw;
                SetToast(Strings.T(_language, "toast.completionNotSaved"));
            }
            RebuildDoneCache();
            TaskChanged(); // UpdateTaskSummary refreshes the calendar when expanded
            Changed("SelectedDayTitle");
        }

        private void RebuildDoneCache()
        {
            Dictionary<DateTime, int> counts = new Dictionary<DateTime, int>();
            Dictionary<DateTime, List<string>> texts = new Dictionary<DateTime, List<string>>();
            try
            {
                foreach (CompletionEntry entry in _completionLog.ReadAll())
                {
                    DateTime day = entry.Done.Date;
                    counts[day] = counts.ContainsKey(day) ? counts[day] + 1 : 1;
                    List<string> list;
                    if (!texts.TryGetValue(day, out list)) { list = new List<string>(); texts[day] = list; }
                    list.Add(entry.Text);
                }
                _doneCounts = counts;
                _doneTexts = texts;
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException)) throw;
                // Keep the last good cache; the heatmap just lags one change behind.
            }
        }

        private void ShiftMonth(int delta)
        {
            DateTime shifted = new DateTime(_calendarYear, _calendarMonth, 1).AddMonths(delta);
            _calendarYear = shifted.Year;
            _calendarMonth = shifted.Month;
            RebuildCalendar();
        }

        private void RebuildCalendar()
        {
            Dictionary<DateTime, int> dueCounts = new Dictionary<DateTime, int>();
            foreach (TaskItem task in Tasks)
            {
                if (task.IsCompleted || !task.DueAt.HasValue) continue;
                DateTime day = task.DueAt.Value.Date;
                dueCounts[day] = dueCounts.ContainsKey(day) ? dueCounts[day] + 1 : 1;
            }
            _dueCountsCache = dueCounts;
            List<CalendarCell> cells = CalendarBuilder.BuildCells(_calendarYear, _calendarMonth, DateTime.Today,
                dueCounts, _doneCounts, _doneTexts, _calendarHeatMode, _calendarHeatInverted, _language);
            _calendarMaxDone = 0;
            foreach (CalendarCell cell in cells)
                if (cell.Count > _calendarMaxDone) _calendarMaxDone = cell.Count;
            CalendarDays.Clear();
            foreach (CalendarCell cell in cells) CalendarDays.Add(MakeCalendarItem(cell));
            Changed("CalendarTitle");
            RefreshSelectedDay();
        }

        private CalendarDayItem MakeCalendarItem(CalendarCell cell)
        {
            ThemePalette palette = ThemeManager.Current;
            bool outOfScale = _calendarHeatMode && cell.HeatLevel < 0;
            Brush cellBrush = ThemeManager.Brush(palette.CardBg);
            if (_calendarHeatMode && !outOfScale)
                cellBrush = ThemeManager.Brush(palette.Heat[cell.HeatLevel]);
            // The heart day paints its glyph in the same keepsake red in every
            // theme, so it reads as a keepsake rather than a stray character.
            Brush labelBrush = cell.IsAnniversary
                ? CalendarAnniversaryBrush
                : (cell.IsCurrentMonth ? ThemeManager.Brush(palette.TextPrimary) : ThemeManager.Brush(palette.FillerText));
            // Public holiday name, or "weekend" when the day is only a weekend. Screen
            // readers get the same marker the dot conveys visually, plus the keepsake
            // line on the heart day.
            string marker = cell.MarkerName == null ? "" : " · " + cell.MarkerName;
            string keepsake = cell.IsAnniversary ? " · " + Strings.T(_language, "anniversary.tip") : "";
            CalendarDayItem item = new CalendarDayItem
            {
                Date = cell.Date,
                DayLabel = cell.DayLabel,
                IsCurrentMonth = cell.IsCurrentMonth,
                IsToday = cell.IsToday,
                Count = cell.Count,
                HeatLevel = cell.HeatLevel,
                CountLabel = !_calendarHeatMode && cell.Count > 1 ? cell.Count.ToString(CultureInfo.InvariantCulture) : "",
                ToolTipText = cell.ToolTipText,
                AutomationLabel = Strings.Date(_language, cell.Date) + marker + keepsake,
                CellBrush = cellBrush,
                LabelBrush = labelBrush,
                TodayBorderBrush = cell.IsToday ? ThemeManager.Brush(palette.Accent) : TransparentBrush,
                DotVisibility = !_calendarHeatMode && cell.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                HolidayMarkVisibility = !_calendarHeatMode && cell.HasMarker ? Visibility.Visible : Visibility.Collapsed,
                IsHoliday = cell.IsHoliday,
                IsWeekend = cell.IsWeekend,
                IsAnniversary = cell.IsAnniversary,
                InHeatScale = cell.HeatLevel >= 0,
                // Out of scale (a future day in inverted mode) keeps the neutral card
                // colour rather than being dragged into the palette.
                OutOfHeatScale = outOfScale,
                IsSelected = _selectedDay.HasValue && _selectedDay.Value == cell.Date
            };
            DateTime captured = cell.Date;
            item.SelectCommand = new RelayCommand(delegate { SelectDay(captured); });
            return item;
        }

        private void SelectDay(DateTime date)
        {
            if (_selectedDay.HasValue && _selectedDay.Value == date) _selectedDay = null;
            else _selectedDay = date;
            foreach (CalendarDayItem item in CalendarDays)
                item.IsSelected = _selectedDay.HasValue && item.Date == _selectedDay.Value;
            RefreshSelectedDay();
        }

        private void RefreshSelectedDay()
        {
            if (SelectedDayTasks == null) return; // collection is built after the first calendar rebuild
            SelectedDayTasks.Clear();
            if (_selectedDay.HasValue)
            {
                foreach (TaskItem task in Tasks.Where(t => t.DueAt.HasValue && t.DueAt.Value.Date == _selectedDay.Value).OrderBy(t => t.DueAt.Value))
                    SelectedDayTasks.Add(task);
            }
            Changed("SelectedDayTitle"); Changed("SelectedDayVisibility");
        }

        public void OpenDuePicker(TaskItem task)
        {
            if (!CanEditTasks || task == null) return;
            _duePickerTask = task;
            DateTime basis = task.DueAt ?? DateTime.Now.AddDays(1);
            _pickerYear = basis.Year;
            _pickerMonth = basis.Month;
            _pickerDate = task.DueAt.HasValue ? (DateTime?)task.DueAt.Value.Date : null;
            RebuildPickerDays();
            Changed("PickerTitle"); Changed("PickerCurrentDue"); Changed("PickerHoursEnabled");
            CommandManager.InvalidateRequerySuggested();
            _duePickerPopup.IsOpen = true;
        }

        private void RebuildPickerDays()
        {
            ThemePalette palette = ThemeManager.Current;
            List<CalendarCell> cells = CalendarBuilder.BuildCells(_pickerYear, _pickerMonth, DateTime.Today, null, null, null, false, false, _language);
            PickerDays.Clear();
            foreach (CalendarCell cell in cells)
            {
                CalendarDayItem item = new CalendarDayItem
                {
                    Date = cell.Date,
                    DayLabel = cell.DayLabel,
                    IsCurrentMonth = cell.IsCurrentMonth,
                    IsToday = cell.IsToday,
                    Count = 0,
                    CountLabel = "",
                    ToolTipText = cell.ToolTipText,
                    AutomationLabel = Strings.Date(_language, cell.Date)
                        + (cell.MarkerName == null ? "" : " · " + cell.MarkerName)
                        + (cell.IsAnniversary ? " · " + Strings.T(_language, "anniversary.tip") : ""),
                    CellBrush = ThemeManager.Brush(palette.CardBg),
                    LabelBrush = cell.IsAnniversary
                        ? CalendarAnniversaryBrush
                        : (cell.IsCurrentMonth ? ThemeManager.Brush(palette.TextPrimary) : ThemeManager.Brush(palette.FillerText)),
                    TodayBorderBrush = cell.IsToday ? ThemeManager.Brush(palette.Accent) : TransparentBrush,
                    DotVisibility = Visibility.Collapsed,
                    HolidayMarkVisibility = cell.HasMarker ? Visibility.Visible : Visibility.Collapsed,
                    IsHoliday = cell.IsHoliday,
                    IsWeekend = cell.IsWeekend,
                    IsAnniversary = cell.IsAnniversary,
                    InHeatScale = true,
                    IsSelected = _pickerDate.HasValue && _pickerDate.Value == cell.Date
                };
                DateTime captured = cell.Date;
                item.SelectCommand = new RelayCommand(delegate { SelectPickerDay(captured); });
                PickerDays.Add(item);
            }
            Changed("PickerMonthTitle");
        }

        private void SelectPickerDay(DateTime date)
        {
            _pickerDate = date;
            if (date.Year != _pickerYear || date.Month != _pickerMonth) { _pickerYear = date.Year; _pickerMonth = date.Month; }
            RebuildPickerDays();
            Changed("PickerCurrentDue"); Changed("PickerHoursEnabled");
            CommandManager.InvalidateRequerySuggested();
        }

        private void ShiftPickerMonth(int delta)
        {
            DateTime shifted = new DateTime(_pickerYear, _pickerMonth, 1).AddMonths(delta);
            _pickerYear = shifted.Year;
            _pickerMonth = shifted.Month;
            RebuildPickerDays();
        }

        private void SetDueHour(object parameter)
        {
            if (_duePickerTask == null || !_pickerDate.HasValue) return;
            string text = Convert.ToString(parameter, CultureInfo.InvariantCulture);
            int colon = text == null ? -1 : text.IndexOf(':');
            if (colon > 0) text = text.Substring(0, colon);
            int hour;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour) || hour < 0 || hour > 23) return;
            DateTime date = _pickerDate.Value;
            TaskItem task = _duePickerTask;
            bool hadDue = task.DueAt.HasValue;
            task.DueAt = new DateTime(date.Year, date.Month, date.Day, hour, 0, 0);
            _duePickerPopup.IsOpen = false; // the Closed handler clears picker state
            SetToast(Strings.T(_language, hadDue ? "toast.dueUpdated" : "toast.dueSet",
                FormatDueLabel(task.DueAt, false, DateTime.Now)));
        }

        private void ClearDue()
        {
            if (_duePickerTask == null) return;
            _duePickerTask.DueAt = null;
            _duePickerPopup.IsOpen = false;
            SetToast(Strings.T(_language, "due.pickerCleared"));
        }

        // Localised "tomorrow 14:00" / "已过期 · 9月18日 18:00" label for any due value.
        internal string FormatDueLabel(DateTime? due, bool completed, DateTime now)
        {
            return TaskItem.FormatDue(due, completed, now, _language);
        }

        // Two reminder slots. `startup` runs the launch-time catch-up: it fires any
        // slot whose hour has already passed today and which has not fired yet.
        internal bool TryRemind(DateTime now, bool startup, bool showWindow)
        {
            List<TodoRecord> records = Tasks.Select(t => t.ToRecord()).ToList();
            List<TodoRecord> today = ReminderLogic.TodayDues(records, now);
            List<TodoRecord> tomorrow = ReminderLogic.TomorrowDues(records, now);

            // The 09:00 slot reports today's to-dos and takes priority: if both slots
            // are due the same run, the morning one is the more urgent news.
            bool morningDue = startup
                ? today.Count > 0 && _morningReminderHour <= now.Hour
                    && ReminderLogic.SlotDay(_lastMorningReminded, ReminderSlot.MorningKind) != now.Date
                : ReminderLogic.ShouldFireSlot(_lastMorningReminded, ReminderSlot.MorningKind, _morningReminderHour, now, today.Count > 0);
            if (morningDue)
            {
                _lastMorningReminded = ReminderLogic.SlotKey(ReminderSlot.MorningKind, now);
                if (_ready) Save();
                if (showWindow) ShowReminderWindow(_morningReminderHour, today, ReminderLogic.Overdue(records, now));
                return true;
            }

            bool eveningDue = startup
                ? tomorrow.Count > 0 && _eveningReminderHour <= now.Hour
                    && ReminderLogic.SlotDay(_lastEveningReminded, ReminderSlot.EveningKind) != now.Date
                : ReminderLogic.ShouldFireSlot(_lastEveningReminded, ReminderSlot.EveningKind, _eveningReminderHour, now, tomorrow.Count > 0);
            if (!eveningDue) return false;
            _lastEveningReminded = ReminderLogic.SlotKey(ReminderSlot.EveningKind, now);
            if (_ready) Save();
            if (showWindow) ShowReminderWindow(_eveningReminderHour, tomorrow, ReminderLogic.Overdue(records, now));
            return true;
        }

        // hour < 12 selects the morning wording ("today"), otherwise "tomorrow".
        private void ShowReminderWindow(int hour, List<TodoRecord> dues, List<TodoRecord> overdue)
        {
            bool morning = hour < 12;
            if (_reminderWindow != null) { _reminderWindow.Close(); _reminderWindow = null; }
            // The reminder window follows the card's theme.
            ThemePalette palette = ThemeManager.Current;
            Brush ink = ThemeManager.Brush(palette.TextPrimary);
            Brush accent = ThemeManager.Brush(palette.Accent);
            Brush red = ThemeManager.Brush(palette.Overdue);
            Window dialog = new Window
            {
                Title = Strings.T(_language, morning ? "reminder.morning.title" : "reminder.evening.title"),
                Width = 340,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13,
                Foreground = ink,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFE, 0xFB))
            };
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("App.ico"))
                if (stream != null) dialog.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            StackPanel stack = new StackPanel { Margin = new Thickness(20, 16, 20, 14) };
            string headline = dues.Count > 0
                ? Strings.T(_language, morning ? "reminder.morning.head" : "reminder.evening.head", dues.Count)
                : Strings.T(_language, morning ? "reminder.morning.none" : "reminder.evening.none");
            stack.Children.Add(new TextBlock { Text = headline, FontSize = 15, FontWeight = FontWeights.SemiBold });
            foreach (TodoRecord record in dues)
            {
                DateTime? due = DueLogic.Parse(record.DueAt);
                DockPanel row = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
                TextBlock clock = new TextBlock { Text = due.HasValue ? Strings.F(Strings.Get(_language, "clock.hourPadded"), due.Value.Hour) : "", Foreground = accent, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(clock, Dock.Right);
                row.Children.Add(clock);
                row.Children.Add(new TextBlock { Text = Truncate(record.Text, 22), VerticalAlignment = VerticalAlignment.Center });
                stack.Children.Add(row);
            }
            // The morning window already lists today, so it only needs one overdue line.
            int overdueShown = morning ? Math.Min(overdue.Count, 3) : overdue.Count;
            if (overdueShown > 0)
            {
                stack.Children.Add(new TextBlock { Text = String.Format(CultureInfo.InvariantCulture, "{0} · {1}",
                    Strings.T(_language, "reminder.overdue"), Strings.CountOf(_language, overdue.Count, "overdue")),
                    Foreground = red, FontSize = 11, Margin = new Thickness(0, 14, 0, 0) });
                for (int index = 0; index < overdueShown; index++)
                {
                    TodoRecord record = overdue[index];
                    DateTime? due = DueLogic.Parse(record.DueAt);
                    DockPanel row = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
                    TextBlock when = new TextBlock { Text = FormatDueLabel(due, false, DateTime.Now), Foreground = red, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(when, Dock.Right);
                    row.Children.Add(when);
                    row.Children.Add(new TextBlock { Text = Truncate(record.Text, 22), Foreground = red, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                    stack.Children.Add(row);
                }
                if (overdue.Count > overdueShown)
                    stack.Children.Add(new TextBlock { Text = Strings.T(_language, "reminder.overdueTail", overdue.Count - overdueShown), Foreground = red, FontSize = 11, Margin = new Thickness(0, 5, 0, 0) });
            }
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            Button show = new Button { Content = Strings.T(_language, "reminder.show"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
            show.Click += delegate { ShowFromTray(); dialog.Close(); };
            Button ok = new Button { Content = Strings.T(_language, "reminder.ok"), Padding = new Thickness(12, 5, 12, 5), Background = accent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            ok.Click += delegate { dialog.Close(); };
            buttons.Children.Add(show);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);
            dialog.Content = stack;
            dialog.Closed += delegate { _reminderWindow = null; };
            _reminderWindow = dialog;
            dialog.Show();
        }

        // ---- Restore to-do ------------------------------------------------------

        // Writes the about-to-disappear task into the append-only archive before
        // it leaves the list, mirroring CompletionToggled's log-first ordering:
        // the cleared log and the repository share the directory mutex and must
        // never hold it in nested order. An archive failure never blocks the
        // delete itself; the user just loses this one restore candidate.
        private void ArchiveCleared(TaskItem task)
        {
            try
            {
                _clearedLog.Append(new ClearedEntry
                {
                    Id = task.Id,
                    Text = task.Text,
                    Due = DueLogic.FormatOrNull(task.DueAt),
                    Done = task.IsCompleted,
                    ClearedAt = DateTime.Now
                });
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException)) throw;
                SetToast(Strings.T(_language, "toast.restoreArchiveFailed"));
            }
        }

        // Entries the archive could still bring back: latest record per id, minus
        // the ids already sitting in the list. Shared by the tray count and the
        // preview window so the two can never disagree.
        internal List<ClearedEntry> RestorableArchive()
        {
            HashSet<string> currentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (TaskItem task in Tasks) currentIds.Add(task.Id);
            return RestoreLogic.Restorable(_clearedLog.ReadAll(), currentIds, DateTime.Now);
        }

        // Test hook: the UI suite asserts the same number the tray menu shows.
        public int RestorePreviewCount { get { return RestorableArchive().Count; } }

        // Recomputes the tray item on every menu open: text carries the count and
        // the item disables itself when nothing is restorable. A read failure just
        // disables the entry rather than blocking the menu.
        private void SyncTrayRestoreState()
        {
            if (_trayRestoreItem == null) return;
            int count;
            try { count = RestorableArchive().Count; }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException)) throw;
                count = 0;
            }
            _trayRestoreItem.Text = Strings.T(_language, "tray.restore") + (count > 0 ? " (" + count.ToString(CultureInfo.InvariantCulture) + ")" : String.Empty);
            _trayRestoreItem.Enabled = count > 0;
        }

        private void OpenRestoreWindow()
        {
            // Bring the card back first so the restored tasks are seen appearing.
            ShowFromTray();
            RestoreWindow window = BuildRestoreWindow();
            if (window == null) return;
            window.Owner = _window;
            window.ShowDialog();
        }

        // Returns null (with a toast) when nothing is restorable. Split from
        // OpenRestoreWindow so the UI suite can exercise the built window without
        // entering a blocking ShowDialog.
        internal RestoreWindow BuildRestoreWindow()
        {
            List<ClearedEntry> candidates;
            try { candidates = RestorableArchive(); }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException)) throw;
                SetToast(Strings.T(_language, "toast.restoreArchiveFailed"));
                return null;
            }
            if (candidates.Count == 0)
            {
                SetToast(Strings.T(_language, "restore.window.emptyToast"));
                return null;
            }
            return new RestoreWindow(this, candidates);
        }

        // Runs the actual restore after the preview window confirmed a selection.
        // Ids that snuck back into the list while the window was open are skipped,
        // the archive itself is never touched, and one TaskChanged persists it all.
        internal void ApplyRestore(List<ClearedEntry> selection)
        {
            HashSet<string> currentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (TaskItem task in Tasks) currentIds.Add(task.Id);
            int restored = 0, skipped = 0;
            foreach (ClearedEntry entry in selection)
            {
                if (String.IsNullOrEmpty(entry.Id) || currentIds.Contains(entry.Id)) { skipped++; continue; }
                Tasks.Add(new TaskItem(this, new TodoRecord
                {
                    Id = entry.Id,
                    Text = entry.Text ?? String.Empty,
                    IsCompleted = entry.Done,
                    DueAt = entry.Due
                }));
                currentIds.Add(entry.Id);
                restored++;
            }
            if (restored > 0)
            {
                TaskChanged();
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { _scroller.ScrollToEnd(); }));
            }
            SetToast(Strings.T(_language, "toast.restoreDone", restored, skipped));
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
            ArchiveCleared(task);
            _undo.Clear(); _undo.Add(new KeyValuePair<int, TaskItem>(index, task));
            Tasks.Remove(task);
            if (_editing == task) CancelEdit();
            TaskChanged(); SetToast(Strings.T(_language, "toast.deleted"));
        }

        private void ClearCompleted()
        {
            _undo.Clear();
            for (int i = 0; i < Tasks.Count; i++) if (Tasks[i].IsCompleted) _undo.Add(new KeyValuePair<int, TaskItem>(i, Tasks[i]));
            foreach (var entry in _undo) ArchiveCleared(entry.Value);
            foreach (var entry in _undo) { Tasks.Remove(entry.Value); if (_editing == entry.Value) CancelEdit(); }
            TaskChanged(); SetToast(Strings.T(_language, "toast.cleared", _undo.Count));
        }

        private void Undo()
        {
            foreach (var entry in _undo) Tasks.Insert(Math.Min(entry.Key, Tasks.Count), entry.Value);
            _undo.Clear(); TaskChanged(); SetToast(Strings.T(_language, "toast.restored")); _input.Focus();
        }

        private void SetToast(string text)
        {
            _toastText = text; Changed("ToastText"); Changed("ToastVisibility"); Changed("UndoVisibility");
            _toastTimer.Stop(); if (!String.IsNullOrEmpty(text)) _toastTimer.Start();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // Modal preview for "restore to-do". Built entirely in code and frozen onto
    // the palette that was current when it opened - the card can be re-themed
    // while this window is up, and the restore window simply keeps its colours
    // for the few seconds it lives. Filtering is local (range + exact date +
    // keyword); the checked set resets to all-selected whenever the filter
    // changes, because a half-checked list under a new filter is meaningless.
    public sealed class RestoreWindow : Window
    {
        private sealed class Row
        {
            public ClearedEntry Entry;
            public CheckBox Box;
        }

        private readonly TodoController _controller;
        private readonly UiLanguage _language;
        private readonly List<Row> _rows = new List<Row>();
        private readonly List<ClearedEntry> _candidates;
        private readonly ComboBox _rangeBox;
        private readonly DatePicker _datePicker;
        private readonly TextBox _search;
        private readonly ListBox _list;
        private readonly TextBlock _count;
        private readonly TextBlock _empty;
        private readonly Button _restoreButton;
        private readonly Button _selectAll;
        private readonly Button _selectNone;

        // Test hooks: the UI suite drives the real controls (range, date picker,
        // search, checkboxes) instead of re-deriving the filtering.
        internal ComboBox RangeBox { get { return _rangeBox; } }
        internal DatePicker ExactDatePicker { get { return _datePicker; } }
        internal TextBox SearchBox { get { return _search; } }
        internal ListBox EntryList { get { return _list; } }
        internal Button RestoreButton { get { return _restoreButton; } }
        internal TextBlock EmptyLabel { get { return _empty; } }
        internal List<ClearedEntry> CurrentSelection()
        {
            List<ClearedEntry> selection = new List<ClearedEntry>();
            foreach (Row row in _rows) if (row.Box.IsChecked == true) selection.Add(row.Entry);
            return selection;
        }

        public RestoreWindow(TodoController controller, List<ClearedEntry> candidates)
        {
            _controller = controller;
            _candidates = candidates;
            _language = controller.Language;

            ThemePalette palette = ThemeManager.Current;
            Brush ink = ThemeManager.Brush(palette.TextPrimary);
            Brush soft = ThemeManager.Brush(palette.TextSecondary);
            Brush accent = ThemeManager.Brush(palette.Accent);
            Brush accentForeground = ThemeManager.Brush(palette.AccentForeground);
            Brush inputBg = ThemeManager.Brush(palette.InputBg);
            Brush separator = ThemeManager.Brush(palette.Separator);
            Brush cardBg = ThemeManager.Brush(palette.CardBg);
            Brush doneGray = ThemeManager.Brush(palette.DoneGray);

            Title = Strings.T(_language, "restore.title");
            Width = 560;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            FontSize = 13;
            Foreground = ink;
            Background = cardBg;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("App.ico"))
                if (stream != null) Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            DockPanel root = new DockPanel();

            DockPanel bottom = new DockPanel { Margin = new Thickness(16, 10, 16, 14) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _restoreButton = new Button { Content = Strings.T(_language, "restore.restore"), Padding = new Thickness(16, 5, 16, 5), Background = accent, Foreground = accentForeground, BorderThickness = new Thickness(0) };
            _restoreButton.Click += delegate { Confirm(); };
            DockPanel.SetDock(_restoreButton, Dock.Right);
            Button cancel = new Button { Content = Strings.T(_language, "restore.cancel"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
            cancel.Click += delegate { Close(); };
            DockPanel.SetDock(cancel, Dock.Right);
            _selectAll = new Button { Content = Strings.T(_language, "restore.selectAll"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
            _selectAll.Click += delegate { SetAllChecked(true); };
            DockPanel.SetDock(_selectAll, Dock.Left);
            _selectNone = new Button { Content = Strings.T(_language, "restore.selectNone"), Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(10, 4, 10, 4) };
            _selectNone.Click += delegate { SetAllChecked(false); };
            DockPanel.SetDock(_selectNone, Dock.Left);
            _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = soft, FontSize = 12 };
            DockPanel.SetDock(_count, Dock.Left);
            bottom.Children.Add(_selectAll);
            bottom.Children.Add(_selectNone);
            bottom.Children.Add(_count);
            bottom.Children.Add(cancel);
            bottom.Children.Add(_restoreButton);
            bottom.Children.Add(new Border()); // spring between the two groups
            root.Children.Add(bottom);

            StackPanel top = new StackPanel { Margin = new Thickness(16, 14, 16, 10) };
            DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(new TextBlock { Text = Strings.T(_language, "restore.title"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            StackPanel filters = new StackPanel { Orientation = Orientation.Horizontal };
            _rangeBox = new ComboBox { Width = 140, VerticalContentAlignment = VerticalAlignment.Center };
            string[] rangeKeys = { "restore.range.day", "restore.range.week", "restore.range.month", "restore.range.year", "restore.range.all", "restore.range.exact" };
            for (int index = 0; index < rangeKeys.Length; index++)
            {
                ComboBoxItem item = new ComboBoxItem { Content = Strings.T(_language, rangeKeys[index]), Tag = index };
                _rangeBox.Items.Add(item);
            }
            _rangeBox.SelectedIndex = (int)RestoreRange.All;
            _rangeBox.SelectionChanged += delegate { RefreshList(); };
            _datePicker = new DatePicker { Width = 130, Margin = new Thickness(8, 0, 0, 0), SelectedDateFormat = DatePickerFormat.Short, Visibility = Visibility.Collapsed };
            _datePicker.SelectedDate = DateTime.Today;
            _datePicker.SelectedDateChanged += delegate { RefreshList(); };
            _search = new TextBox { Width = 180, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 4, 6, 4), Background = inputBg, BorderBrush = separator };
            _search.TextChanged += delegate { RefreshList(); };
            filters.Children.Add(_rangeBox);
            filters.Children.Add(_datePicker);
            filters.Children.Add(_search);
            top.Children.Add(filters);
            root.Children.Add(top);

            Border listBorder = new Border { BorderBrush = separator, BorderThickness = new Thickness(0, 1, 0, 1), Margin = new Thickness(16, 0, 16, 0), Background = inputBg };
            DockPanel.SetDock(listBorder, Dock.Bottom);
            ScrollViewer scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 380, Padding = new Thickness(4) };
            _list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            _empty = new TextBlock { Text = Strings.T(_language, "restore.empty"), Foreground = soft, FontSize = 12, Margin = new Thickness(8, 10, 8, 10), Visibility = Visibility.Collapsed };
            StackPanel listHost = new StackPanel();
            listHost.Children.Add(_list);
            listHost.Children.Add(_empty);
            scroller.Content = listHost;
            listBorder.Child = scroller;
            root.Children.Add(listBorder);

            Content = root;
            PreviewKeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
                else if (e.Key == Key.Enter) { if (_restoreButton.IsEnabled) { Confirm(); e.Handled = true; } }
            };

            RefreshList();
        }

        private void RefreshList()
        {
            RestoreRange range = (RestoreRange)_rangeBox.SelectedIndex;
            bool exact = range == RestoreRange.ExactDate;
            _datePicker.Visibility = exact ? Visibility.Visible : Visibility.Collapsed;
            if (exact && !_datePicker.SelectedDate.HasValue) { ShowEmpty(); return; }
            string keyword = _search.Text;
            _rows.Clear();
            _list.Items.Clear();
            foreach (ClearedEntry entry in _candidates)
            {
                if (exact)
                {
                    if (!RestoreLogic.ExactMatch(entry.ClearedAt, _datePicker.SelectedDate.Value)) continue;
                }
                else if (!RestoreLogic.InWindow(range, entry.ClearedAt, DateTime.Now)) continue;
                if (!RestoreLogic.TextMatches(entry.Text, keyword)) continue;
                _rows.Add(new Row { Entry = entry, Box = BuildRow(entry) });
                _list.Items.Add(_rows[_rows.Count - 1].Box);
            }
            if (_rows.Count == 0) ShowEmpty();
            else
            {
                _empty.Visibility = Visibility.Collapsed;
                _list.Visibility = Visibility.Visible;
                SetAllChecked(true);
            }
        }

        private void ShowEmpty()
        {
            _list.Visibility = Visibility.Collapsed;
            _empty.Visibility = Visibility.Visible;
            UpdateCount();
        }

        private CheckBox BuildRow(ClearedEntry entry)
        {
            ThemePalette palette = ThemeManager.Current;
            Brush soft = ThemeManager.Brush(palette.TextSecondary);
            Brush stateBrush = ThemeManager.Brush(entry.Done ? palette.DoneGray : palette.Accent);

            CheckBox box = new CheckBox();
            DockPanel line = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };

            TextBlock cleared = new TextBlock
            {
                Text = Strings.T(_language, "restore.clearedAt", FormatCleared(entry.ClearedAt)),
                Foreground = soft,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(cleared, Dock.Right);
            line.Children.Add(cleared);

            TextBlock state = new TextBlock
            {
                Text = Strings.T(_language, entry.Done ? "restore.state.done" : "restore.state.open"),
                Foreground = stateBrush,
                FontSize = 11,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(state, Dock.Left);
            line.Children.Add(state);

            StackPanel body = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock text = new TextBlock { Text = TruncateText(entry.Text, 80), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            body.Children.Add(text);
            DateTime? due = DueLogic.Parse(entry.Due);
            if (due.HasValue)
            {
                TextBlock dueLabel = new TextBlock
                {
                    Text = " · " + Strings.T(_language, "task.menu.due") + " " + due.Value.ToString("yyyy-M-d HH:mm", CultureInfo.InvariantCulture),
                    Foreground = soft,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                body.Children.Add(dueLabel);
            }
            line.Children.Add(body);

            box.Content = line;
            box.Checked += delegate { UpdateCount(); };
            box.Unchecked += delegate { UpdateCount(); };
            return box;
        }

        private void SetAllChecked(bool value)
        {
            foreach (Row row in _rows) row.Box.IsChecked = value;
            UpdateCount();
        }

        private void UpdateCount()
        {
            int checkedCount = 0;
            foreach (Row row in _rows) if (row.Box.IsChecked == true) checkedCount++;
            _count.Text = Strings.T(_language, "restore.count", checkedCount);
            _restoreButton.IsEnabled = checkedCount > 0;
        }

        private void Confirm()
        {
            List<ClearedEntry> selection = new List<ClearedEntry>();
            foreach (Row row in _rows) if (row.Box.IsChecked == true) selection.Add(row.Entry);
            if (selection.Count == 0) return;
            Close();
            _controller.ApplyRestore(selection);
        }

        private string FormatCleared(DateTime value)
        {
            return Strings.Date(_language, value) + " " + value.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private static string TruncateText(string text, int length)
        {
            if (String.IsNullOrEmpty(text)) return "";
            string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= length ? flat : flat.Substring(0, length) + "…";
        }
    }
}
