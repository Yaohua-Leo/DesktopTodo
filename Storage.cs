using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace DesktopTodo
{
    [DataContract]
    public sealed class TodoRecord
    {
        [DataMember(Order = 1)]
        public string Id { get; set; }

        [DataMember(Order = 2)]
        public string Text { get; set; }

        [DataMember(Order = 3)]
        public bool IsCompleted { get; set; }

        // Hour-precise local deadline ("yyyy-MM-ddTHH:00"); null when the task has none.
        [DataMember(Order = 4, EmitDefaultValue = false)]
        public string DueAt { get; set; }

        public TodoRecord()
        {
            Id = Guid.NewGuid().ToString("N");
            Text = String.Empty;
        }
    }

    [DataContract]
    public sealed class AppState
    {
        [DataMember(Order = 1)]
        public List<TodoRecord> Tasks { get; set; }

        [DataMember(Order = 2)]
        public bool IsPinned { get; set; }

        [IgnoreDataMember]
        public double Left { get; set; }

        [IgnoreDataMember]
        public double Top { get; set; }

        // Use JSON null for automatic placement instead of the nonstandard NaN token.
        [DataMember(Name = "Left", Order = 3)]
        private double? StoredLeft
        {
            get { return Double.IsNaN(Left) ? (double?)null : Left; }
            set { Left = value ?? Double.NaN; }
        }

        [DataMember(Name = "Top", Order = 4)]
        private double? StoredTop
        {
            get { return Double.IsNaN(Top) ? (double?)null : Top; }
            set { Top = value ?? Double.NaN; }
        }

        [DataMember(Order = 5)]
        public double Width { get; set; }

        [DataMember(Order = 6)]
        public double Height { get; set; }

        [DataMember(Order = 7, EmitDefaultValue = false)]
        public bool CalendarExpanded { get; set; }

        [DataMember(Order = 8, EmitDefaultValue = false)]
        public bool CalendarHeatMode { get; set; }

        [DataMember(Order = 9, EmitDefaultValue = false)]
        public int ReminderHour { get; set; }

        // Local calendar day ("yyyy-MM-dd") of the last reminder; null when never reminded.
        [DataMember(Order = 10, EmitDefaultValue = false)]
        public string LastRemindedDate { get; set; }

        [DataMember(Order = 11, EmitDefaultValue = false)]
        public bool RunAtStartup { get; set; }

        [DataMember(Order = 12, EmitDefaultValue = false)]
        public bool TrayHintShown { get; set; }

        // ---- Heatmap and reminder settings added with the localisation release ----

        // Inverts the heat scale: more completions paint lighter instead of darker.
        [DataMember(Order = 13, EmitDefaultValue = false)]
        public bool CalendarHeatInverted { get; set; }

        // The morning slot reports today's to-dos; the evening slot reports tomorrow's.
        [DataMember(Order = 14, EmitDefaultValue = false)]
        public int MorningReminderHour { get; set; }

        [DataMember(Order = 15, EmitDefaultValue = false)]
        public int EveningReminderHour { get; set; }

        // Per-slot dedupe keys: "morning@2026-09-20" / "evening@2026-09-20".
        [DataMember(Order = 16, EmitDefaultValue = false)]
        public string LastMorningReminded { get; set; }

        [DataMember(Order = 17, EmitDefaultValue = false)]
        public string LastEveningReminded { get; set; }

        // UI language: "zh-Hans", "zh-Hant" or "en".
        [DataMember(Order = 18, EmitDefaultValue = false)]
        public string Language { get; set; }

        // Last day the calendar window accepted a new "add" action.
        [DataMember(Order = 19, EmitDefaultValue = false)]
        public string LastDashboardDate { get; set; }

        // Theme family: "sage", "graphite", "midnight", "catppuccin" or
        // "liquidglass". Null/absent means the classic sage look.
        [DataMember(Order = 20, EmitDefaultValue = false)]
        public string ThemeFamily { get; set; }

        // Light/dark behaviour: "followSystem", "light" or "dark".
        [DataMember(Order = 21, EmitDefaultValue = false)]
        public string LightDarkMode { get; set; }

        // Not persisted. True only for a state object that was just read from a file
        // whose two-slot reminder keys were absent, i.e. one that still needs the
        // single-slot migration. Normalize() consumes the flag (sets it false) so the
        // migration runs exactly once and can never rewrite a value the user has since
        // chosen in memory.
        //
        // HasLegacyHour records whether the old single-value key was physically present.
        // The constructor pre-fills ReminderHour with 20, so without this a file that
        // never stored a reminder setting would look like a deliberate 20:00 choice.
        [IgnoreDataMember]
        internal bool NeedsSlotMigration { get; set; }

        [IgnoreDataMember]
        internal bool HasLegacyHour { get; set; }

        public AppState()
        {
            SetDefaults();
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            // DataContract deserialization does not invoke the constructor.
            SetDefaults();
        }

        private void SetDefaults()
        {
            Tasks = new List<TodoRecord>();
            IsPinned = true;
            Left = Double.NaN;
            Top = Double.NaN;
            Width = 392;
            Height = 580;
            CalendarExpanded = false;
            CalendarHeatMode = false;
            ReminderHour = 20;
            LastRemindedDate = null;
            RunAtStartup = false;
            TrayHintShown = false;
            CalendarHeatInverted = false;
            MorningReminderHour = ReminderSlot.DefaultMorningHour;
            EveningReminderHour = ReminderSlot.DefaultEveningHour;
            LastMorningReminded = null;
            LastEveningReminded = null;
            Language = Strings.CodeOf(UiLanguage.ZhHans);
            LastDashboardDate = null;
            ThemeFamily = null;
            LightDarkMode = null;
        }
    }

    // Single source for the per-directory mutex name. AppRepository and
    // CompletionLog share it so concurrent writers exclude each other across
    // both files; the two never hold the lock in nested order.
    internal static class StorageLock
    {
        internal static string MutexNameFor(string directory)
        {
            return "Local\\DesktopTodo.Storage." + Hash(Encoding.UTF8.GetBytes(
                directory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()));
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }

        internal sealed class Lock : IDisposable
        {
            private readonly Mutex mutex;
            private bool held;
            public Lock(string name)
            {
                mutex = new Mutex(false, name);
                try
                {
                    try { held = mutex.WaitOne(2000); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("另一个待办窗口正在读写数据，请稍后重试。");
                }
                catch { mutex.Dispose(); throw; }
            }
            public void Dispose()
            {
                if (held) mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }

    public sealed class AppRepository
    {
        private readonly string directory;
        private readonly string primaryPath;
        private readonly string backupPath;
        private bool loaded;
        private bool primaryIsValid;
        private string saveBlockedReason;
        private string primaryFingerprint;
        private AppState lastSavedState;
        private readonly string historyDirectory;
        private readonly string mutexName;
        private string lastSnapshotSignature;

        public string LoadWarning { get; private set; }
        public string DataDirectory { get { return directory; } }
        public bool CanSave { get { return loaded && String.IsNullOrEmpty(saveBlockedReason); } }
        public bool HasExternalChanges
        {
            get
            {
                if (!loaded) return false;
                try { return GetFingerprint(primaryPath) != primaryFingerprint; }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
                catch (SecurityException) { return true; }
            }
        }

        public AppRepository(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("请指定有效的数据目录。", "directory");

            this.directory = Path.GetFullPath(directory);
            primaryPath = Path.Combine(this.directory, "tasks.json");
            backupPath = Path.Combine(this.directory, "tasks.backup.json");
            historyDirectory = Path.Combine(this.directory, "history");
            mutexName = StorageLock.MutexNameFor(this.directory);
        }

        public AppState Load()
        {
            loaded = true;
            primaryIsValid = false;
            saveBlockedReason = null;
            primaryFingerprint = null;
            lastSavedState = null;
            lastSnapshotSignature = null;
            LoadWarning = null;

            try
            {
                using (StorageLock.Lock gate = new StorageLock.Lock(mutexName))
                    return LoadLocked();
            }
            catch (Exception error)
            {
                if (!IsFileError(error)) throw;
                saveBlockedReason = "无法读取待办数据，已停止保存以保护历史任务。";
                LoadWarning = saveBlockedReason + Environment.NewLine + error.Message;
                return new AppState();
            }
        }

        private AppState LoadLocked()
        {

            AppState result;
            Exception primaryError;
            byte[] primaryBytes;
            ReadResult primaryResult = TryRead(primaryPath, out result, out primaryError, out primaryBytes);
            primaryFingerprint = primaryResult == ReadResult.Missing ? "missing"
                : primaryBytes == null ? null : Hash(primaryBytes);
            if (primaryResult == ReadResult.Success)
            {
                primaryIsValid = true;
                return Remember(result);
            }

            List<string> warnings = new List<string>();
            if (primaryResult == ReadResult.Invalid || primaryResult == ReadResult.Unavailable)
                PreserveUnreadable(primaryPath, primaryError, primaryBytes, warnings);
            if (primaryResult == ReadResult.Unavailable)
                saveBlockedReason = "原任务文件暂时无法读取。请恢复文件访问后重新加载；当前已停止保存。";

            Exception backupError;
            byte[] backupBytes;
            ReadResult backupResult = TryRead(backupPath, out result, out backupError, out backupBytes);
            if (backupResult == ReadResult.Success)
            {
                warnings.Add("已从上一次备份恢复待办任务；最近一次修改可能需要重新添加。");
                if (!String.IsNullOrEmpty(saveBlockedReason))
                    warnings.Add("为保护原始数据，当前无法保存修改。请先修复数据目录的访问权限，再重新打开应用。");
                LoadWarning = String.Join(Environment.NewLine, warnings.ToArray());
                return Remember(result);
            }

            if (backupResult == ReadResult.Invalid || backupResult == ReadResult.Unavailable)
                PreserveUnreadable(backupPath, backupError, backupBytes, warnings);

            bool historyExists;
            if (TryReadHistory(out result, out historyExists, warnings))
            {
                warnings.Add("已从历史快照恢复待办任务。");
                LoadWarning = String.Join(Environment.NewLine, warnings.ToArray());
                return Remember(result);
            }

            if (primaryResult != ReadResult.Missing || backupResult != ReadResult.Missing || historyExists)
                saveBlockedReason = "历史任务暂时无法恢复。为避免用空列表覆盖历史数据，已停止保存；请检查数据目录。";

            if (warnings.Count > 0)
                warnings.Add("历史任务暂时无法显示，原始文件已保留。");

            if (!String.IsNullOrEmpty(saveBlockedReason))
                warnings.Add("为保护原始数据，当前无法保存修改。请先修复数据目录的访问权限，再重新打开应用。");

            if (warnings.Count > 0)
                LoadWarning = String.Join(Environment.NewLine, warnings.ToArray());

            return Remember(new AppState());
        }

        private AppState Remember(AppState state)
        {
            // Do not keep the caller's mutable task collection as the saved baseline.
            lastSavedState = Normalize(state);
            return Normalize(lastSavedState);
        }

        public void Save(AppState state)
        {
            if (state == null)
                throw new ArgumentNullException("state");

            // Also protect existing files when a caller saves before loading.
            if (!loaded)
                Load();
            if (!String.IsNullOrEmpty(saveBlockedReason))
                throw new IOException(saveBlockedReason);

            AppState normalized = Normalize(state);
            byte[] contents = Serialize(normalized);
            using (StorageLock.Lock gate = new StorageLock.Lock(mutexName))
            {
                RequireUnchangedPrimary();
                Directory.CreateDirectory(directory);
                bool tasksChanged = TaskFingerprint(normalized) != TaskFingerprint(lastSavedState);
                if (lastSavedState.Tasks.Count > 0)
                    EnsureSnapshot(lastSavedState);
                if (primaryIsValid && Hash(contents) == primaryFingerprint)
                    return;

                string temporaryPath = Path.Combine(directory, ".tasks-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    WriteNewFile(temporaryPath, contents);
                    // Recheck after disk work as well as before it. Other copies of this
                    // app use this same mutex; external editors may not use it.
                    RequireUnchangedPrimary();
                    if (primaryFingerprint != "missing")
                    {
                        // Position/size changes must never evict the last task backup.
                        File.Replace(temporaryPath, primaryPath,
                            primaryIsValid && tasksChanged ? backupPath : null);
                    }
                    else
                        File.Move(temporaryPath, primaryPath);
                    primaryIsValid = true;
                    primaryFingerprint = Hash(contents);
                    lastSavedState = Normalize(normalized);
                    // Include empty states: a deliberate deletion must remain deleted
                    // even when the primary later has to be recovered from history.
                    EnsureSnapshot(normalized);
                }
                finally
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private void RequireUnchangedPrimary()
        {
            if (GetFingerprint(primaryPath) != primaryFingerprint)
                throw new IOException("待办数据已被另一个窗口或程序修改。已停止保存以保护最新任务，请重新加载后再操作。");
        }

        private static byte[] Serialize(AppState state)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                CreateSerializer().WriteObject(stream, state);
                return stream.ToArray();
            }
        }

        private static string TaskFingerprint(AppState state)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(List<TodoRecord>)).WriteObject(stream, state.Tasks);
                return Hash(stream.ToArray());
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }

        private static string GetFingerprint(string path)
        {
            try { return Hash(File.ReadAllBytes(path)); }
            catch (FileNotFoundException) { return "missing"; }
            catch (DirectoryNotFoundException) { return "missing"; }
        }

        private static void WriteNewFile(string path, byte[] contents)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(true);
            }
        }

        private void EnsureSnapshot(AppState state)
        {
            string signature = TaskFingerprint(state);
            if (lastSnapshotSignature == signature) return;
            Directory.CreateDirectory(historyDirectory);
            string[] snapshots = Directory.GetFiles(historyDirectory, "*.json");
            Array.Sort(snapshots, StringComparer.Ordinal);
            for (int index = snapshots.Length - 1; index >= 0; index--)
            {
                AppState existing;
                Exception error;
                byte[] bytes;
                if (TryRead(snapshots[index], out existing, out error, out bytes) == ReadResult.Success)
                {
                    if (TaskFingerprint(Normalize(existing)) == signature)
                    {
                        lastSnapshotSignature = signature;
                        return;
                    }
                    break;
                }
            }
            // Windows clocks may return the same timestamp for rapid consecutive
            // writes. Keep names strictly ordered so fallback cannot choose an
            // older task state by the random GUID portion of the filename.
            DateTime timestamp = DateTime.UtcNow;
            foreach (string snapshot in snapshots)
            {
                string filename = Path.GetFileName(snapshot);
                DateTime previous;
                if (filename.Length >= 23 && DateTime.TryParseExact(filename.Substring(0, 23),
                    "yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out previous)
                    && timestamp <= previous && previous < DateTime.MaxValue)
                    timestamp = previous.AddTicks(1);
            }
            string name = timestamp.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + "-" + signature;
            string temporaryPath = Path.Combine(historyDirectory, name + ".tmp");
            try
            {
                WriteNewFile(temporaryPath, Serialize(state));
                File.Move(temporaryPath, Path.Combine(historyDirectory, name + ".json"));
                lastSnapshotSignature = signature;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private bool TryReadHistory(out AppState state, out bool historyExists, List<string> warnings)
        {
            state = null;
            historyExists = false;
            string[] files;
            try { files = Directory.GetFiles(historyDirectory, "*.json"); }
            catch (DirectoryNotFoundException) { return false; }
            catch (Exception error)
            {
                if (!IsFileError(error)) throw;
                historyExists = true;
                warnings.Add("无法读取历史快照：" + error.Message);
                return false;
            }
            historyExists = files.Length > 0;
            Array.Sort(files, StringComparer.Ordinal);
            for (int index = files.Length - 1; index >= 0; index--)
            {
                Exception error;
                byte[] bytes;
                if (TryRead(files[index], out state, out error, out bytes) == ReadResult.Success)
                    return true;
            }
            if (historyExists) warnings.Add("没有找到可读取的历史快照；所有快照均已保留。");
            return false;
        }

        private static DataContractJsonSerializer CreateSerializer()
        {
            return new DataContractJsonSerializer(typeof(AppState));
        }

        private enum ReadResult { Missing, Success, Invalid, Unavailable }

        private static ReadResult TryRead(string path, out AppState state, out Exception error, out byte[] bytes)
        {
            state = null;
            error = null;
            bytes = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    bytes = File.ReadAllBytes(path);
                    break;
                }
                catch (FileNotFoundException) { return ReadResult.Missing; }
                catch (DirectoryNotFoundException) { return ReadResult.Missing; }
                catch (Exception exception)
                {
                    if (!IsFileError(exception)) throw;
                    error = exception;
                    if (attempt == 5) return ReadResult.Unavailable;
                    Thread.Sleep(80);
                }
            }
            try
            {
                string json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                JavaScriptSerializer validator = new JavaScriptSerializer();
                validator.MaxJsonLength = Int32.MaxValue;
                Dictionary<string, object> root = validator.DeserializeObject(json) as Dictionary<string, object>;
                object tasks;
                if (root == null || !root.TryGetValue("Tasks", out tasks) || !(tasks is object[]))
                    throw new SerializationException("数据文件缺少有效的 Tasks 数组，不能当作空列表读取。");
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    state = CreateSerializer().ReadObject(stream) as AppState;
                if (state == null || state.Tasks == null)
                    throw new SerializationException("数据文件没有有效的任务列表。");
                // The two-slot migration keys off key presence, which the typed
                // deserializer cannot report because the constructor pre-fills both
                // the slots and the old single reminder hour.
                bool slotsInFile = root.ContainsKey("MorningReminderHour") || root.ContainsKey("EveningReminderHour");
                state.NeedsSlotMigration = !slotsInFile;
                state.HasLegacyHour = root.ContainsKey("ReminderHour");
                error = null;
                return ReadResult.Success;            }
            catch (Exception exception)
            {
                if (!(exception is SerializationException) && !(exception is XmlException)
                    && !(exception is FormatException) && !(exception is ArgumentException)
                    && !(exception is InvalidOperationException))
                    throw;
                error = exception;
                return ReadResult.Invalid;
            }
        }

        private static bool IsFileError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is SecurityException;
        }

        private void PreserveUnreadable(string path, Exception error, byte[] contents, List<string> warnings)
        {
            try
            {
                if (contents == null) contents = File.ReadAllBytes(path);
                string signature = Hash(contents);
                string stem = Path.GetFileNameWithoutExtension(path) + ".bad-";
                // Startup recovery can retry periodically. Preserve each distinct
                // damaged file once, including across fresh repository instances.
                foreach (string existing in Directory.GetFiles(directory, stem + "*-" + signature + ".json"))
                {
                    try
                    {
                        if (Hash(File.ReadAllBytes(existing)) != signature) continue;
                        warnings.Add("无法读取 " + Path.GetFileName(path) + "，原始副本已保留："
                            + Path.GetFileName(existing) + "。");
                        return;
                    }
                    catch (Exception readError)
                    {
                        if (!IsFileError(readError)) throw;
                    }
                }
                string preservedPath = Path.Combine(directory, stem
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + signature + ".json");
                WriteNewFile(preservedPath, contents);
                warnings.Add("无法读取 " + Path.GetFileName(path) + "，已保留原始副本："
                    + Path.GetFileName(preservedPath) + "。");
            }
            catch (Exception preserveError)
            {
                if (!(preserveError is IOException) && !(preserveError is UnauthorizedAccessException)
                    && !(preserveError is SecurityException))
                    throw;
                saveBlockedReason = "无法备份原始数据文件，因此已停止保存以避免覆盖：" + path
                    + Environment.NewLine + preserveError.Message;
                warnings.Add("无法读取或备份 " + Path.GetFileName(path) + "：" + error.Message);
            }
        }

        private static AppState Normalize(AppState source)
        {
            AppState normalized = new AppState();
            normalized.IsPinned = source.IsPinned;
            normalized.Left = Double.IsInfinity(source.Left) ? Double.NaN : source.Left;
            normalized.Top = Double.IsInfinity(source.Top) ? Double.NaN : source.Top;
            normalized.Width = IsValidSize(source.Width, 280) ? source.Width : 392;
            normalized.Height = IsValidSize(source.Height, 260) ? source.Height : 580;
            // Every persisted field must be copied here; anything forgotten is
            // silently dropped on the next save. NewFieldsRoundTrip guards this list.
            normalized.CalendarExpanded = source.CalendarExpanded;
            normalized.CalendarHeatMode = source.CalendarHeatMode;
            normalized.CalendarHeatInverted = source.CalendarHeatInverted;

            // An install that predates the two-slot scheme stores only a single
            // 18:00-22:00 hour. Its evening reminder keeps that time; the morning
            // one is new, so it takes the default without disturbing the old habit.
            //
            // The migration runs only for a state freshly read from a file that lacked
            // the slot keys; the flag is cleared here so a later save of the same
            // in-memory object can never overwrite a choice the user just made. The old
            // hour is honoured only when the file actually carried it.
            bool legacyHour = source.NeedsSlotMigration && source.HasLegacyHour
                && source.ReminderHour >= 18 && source.ReminderHour <= 22;
            int morning = source.MorningReminderHour;
            int evening = source.EveningReminderHour;
            if (source.NeedsSlotMigration)
            {
                // The pair was never written: adopt the old single hour for the
                // evening slot so an existing habit survives the upgrade.
                evening = legacyHour ? source.ReminderHour : ReminderSlot.DefaultEveningHour;
                source.NeedsSlotMigration = false;
                source.HasLegacyHour = false;
            }
            ReminderSlot.Repair(ref morning, ref evening);
            normalized.MorningReminderHour = morning;
            normalized.EveningReminderHour = evening;

            // Carry a single-slot "last reminded" day into the evening slot only when
            // the previous run actually used a late hour; otherwise it belonged to a
            // different schedule and must not suppress today's reminder.
            normalized.LastMorningReminded = ReminderLogic.IsSlotKey(source.LastMorningReminded, ReminderSlot.MorningKind)
                ? source.LastMorningReminded.Trim() : null;
            normalized.LastEveningReminded = ReminderLogic.IsSlotKey(source.LastEveningReminded, ReminderSlot.EveningKind)
                ? source.LastEveningReminded.Trim()
                : legacyHour && ReminderLogic.IsValidDayKey(source.LastRemindedDate)
                    ? ReminderSlot.EveningKind + "@" + source.LastRemindedDate.Trim()
                    : null;

            normalized.Language = Strings.CodeOf(Strings.Parse(source.Language));
            normalized.LastDashboardDate = ReminderLogic.IsValidDayKey(source.LastDashboardDate)
                ? source.LastDashboardDate.Trim() : null;
            // Unknown theme values fall back to the defaults rather than being
            // carried through to confuse a future build.
            normalized.ThemeFamily = String.IsNullOrWhiteSpace(source.ThemeFamily)
                ? null : Themes.CodeOf(Themes.ParseFamily(source.ThemeFamily));
            normalized.LightDarkMode = String.IsNullOrWhiteSpace(source.LightDarkMode)
                ? null : Themes.CodeOf(Themes.ParseMode(source.LightDarkMode));

            normalized.RunAtStartup = source.RunAtStartup;
            normalized.TrayHintShown = source.TrayHintShown;

            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            if (source.Tasks != null)
            {
                foreach (TodoRecord record in source.Tasks)
                {
                    if (record == null) continue;
                    string id = record.Id;
                    if (String.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    {
                        do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id));
                    }
                    normalized.Tasks.Add(new TodoRecord
                    {
                        Id = id,
                        Text = record.Text ?? String.Empty,
                        IsCompleted = record.IsCompleted,
                        DueAt = DueLogic.Normalize(record.DueAt)
                    });
                }
            }
            return normalized;
        }

        private static bool IsValidSize(double value, double minimum)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value)
                && value >= minimum && value <= 10000;
        }
    }

    public sealed class CompletionEntry
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public DateTime Done { get; set; }
    }

    public sealed class ClearedEntry
    {
        public string Id { get; set; }
        public string Text { get; set; }
        // DueAt exactly as the task stored it ("yyyy-MM-ddTHH:00"); null when the
        // task had no deadline or the stored value was not hour-precise valid.
        public string Due { get; set; }
        public bool Done { get; set; }
        public DateTime ClearedAt { get; set; }
    }

    // Append-only archive of cleared/deleted tasks (cleared.jsonl), independent of
    // the task list. "Restore to-do" reads it back; nothing ever rewrites it, so
    // unlike completions.jsonl there is no rewrite path to hang a .bad- copy on.
    // Lines keep a text snapshot on purpose; that privacy tradeoff mirrors the
    // completion log and is documented in the README.
    public sealed class ClearedLog
    {
        private readonly string directory;
        private readonly string path;
        private readonly string mutexName;

        public ClearedLog(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("请指定有效的数据目录。", "directory");
            this.directory = Path.GetFullPath(directory);
            path = Path.Combine(this.directory, "cleared.jsonl");
            mutexName = StorageLock.MutexNameFor(this.directory);
        }

        public void Append(ClearedEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (String.IsNullOrWhiteSpace(entry.Id)) throw new ArgumentException("清除记录缺少任务标识。", "entry");
            using (StorageLock.Lock gate = new StorageLock.Lock(mutexName))
            {
                Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(Serialize(entry) + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
        }

        public List<ClearedEntry> ReadAll()
        {
            List<ClearedEntry> entries = new List<ClearedEntry>();
            using (StorageLock.Lock gate = new StorageLock.Lock(mutexName))
            {
                foreach (string line in ReadLines())
                {
                    ClearedEntry entry = Parse(line);
                    if (entry != null) entries.Add(entry);
                }
            }
            return entries;
        }

        private string[] ReadLines()
        {
            try
            {
                string content;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    content = reader.ReadToEnd();
                return content.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch (FileNotFoundException) { return new string[0]; }
            catch (DirectoryNotFoundException) { return new string[0]; }
        }

        private static string Serialize(ClearedEntry entry)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            return serializer.Serialize(new Dictionary<string, object>
            {
                { "id", entry.Id },
                { "text", entry.Text ?? String.Empty },
                { "due", entry.Due },
                { "done", entry.Done },
                { "cleared", entry.ClearedAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) }
            });
        }

        private static ClearedEntry Parse(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return null;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null) return null;
                object idValue, clearedValue, textValue, dueValue, doneValue;
                if (!root.TryGetValue("id", out idValue) || !(idValue is string) || String.IsNullOrWhiteSpace((string)idValue)) return null;
                if (!root.TryGetValue("cleared", out clearedValue) || !(clearedValue is string)) return null;
                DateTime cleared;
                if (!DateTime.TryParseExact((string)clearedValue, "yyyy-MM-dd'T'H:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out cleared)) return null;
                root.TryGetValue("text", out textValue);
                // The due snapshot is kept only when it is hour-precise valid; anything
                // else collapses to null so the restore window never shows a bogus date.
                root.TryGetValue("due", out dueValue);
                string due = DueLogic.Normalize(dueValue as string);
                root.TryGetValue("done", out doneValue);
                return new ClearedEntry
                {
                    Id = ((string)idValue).Trim(),
                    Text = textValue as string ?? String.Empty,
                    Due = due,
                    Done = doneValue is bool && (bool)doneValue,
                    ClearedAt = cleared
                };
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is InvalidOperationException) && !(error is FormatException)) throw;
                return null;
            }
        }
    }

    // Append-only completion history (completions.jsonl), independent of the task
    // list so clearing completed tasks never erases the heatmap. Lines keep a text
    // snapshot on purpose; that privacy tradeoff is documented in the README.
    public sealed class CompletionLog
    {
        private readonly string directory;
        private readonly string path;
        private readonly string mutexName;

        public CompletionLog(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("请指定有效的数据目录。", "directory");
            this.directory = Path.GetFullPath(directory);
            path = Path.Combine(this.directory, "completions.jsonl");
            mutexName = StorageLock.MutexNameFor(this.directory);
        }

        public void Append(CompletionEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (String.IsNullOrWhiteSpace(entry.Id)) throw new ArgumentException("完成记录缺少任务标识。", "entry");
            using (StorageLock.Lock gate = new StorageLock.Lock(mutexName))
            {
                Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(Serialize(entry) + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
        }

        public List<CompletionEntry> ReadAll()
        {
            List<CompletionEntry> entries = new List<CompletionEntry>();
            using (StorageLock.Lock gate = new StorageLock.Lock(mutexName))
            {
                string[] lines = ReadLines();
                foreach (string line in lines)
                {
                    CompletionEntry entry = Parse(line);
                    if (entry != null) entries.Add(entry);
                }
            }
            return entries;
        }

        // "Restore to-do" removes the task's newest record so the heatmap stays
        // honest. Rewrites atomically; unreadable lines are preserved verbatim in
        // a completions.bad-*.jsonl copy before the rewrite, never silently dropped.
        public void RemoveLatest(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return;
            using (StorageLock.Lock gate = new StorageLock.Lock(mutexName))
            {
                string[] lines = ReadLines();
                if (lines.Length == 0) return;
                int removeAt = -1;
                List<string> badLines = new List<string>();
                List<string> good = new List<string>(lines.Length);
                for (int index = 0; index < lines.Length; index++)
                {
                    CompletionEntry entry = Parse(lines[index]);
                    if (entry == null)
                    {
                        badLines.Add(lines[index]);
                        continue;
                    }
                    if (String.Equals(entry.Id, id, StringComparison.Ordinal)) removeAt = good.Count;
                    good.Add(lines[index]);
                }
                if (removeAt < 0) return; // nothing to remove: leave the file byte-identical
                good.RemoveAt(removeAt);
                if (badLines.Count > 0) PreserveUnreadable(lines, badLines);
                string temporaryPath = Path.Combine(directory, ".completions-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    WriteNewFile(temporaryPath, new UTF8Encoding(false).GetBytes(
                        good.Count == 0 ? String.Empty : String.Join("\n", good.ToArray()) + "\n"));
                    File.Replace(temporaryPath, path, null);
                }
                finally
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private string[] ReadLines()
        {
            try
            {
                string content;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    content = reader.ReadToEnd();
                return content.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch (FileNotFoundException) { return new string[0]; }
            catch (DirectoryNotFoundException) { return new string[0]; }
        }

        private static string Serialize(CompletionEntry entry)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            return serializer.Serialize(new Dictionary<string, object>
            {
                { "id", entry.Id },
                { "text", entry.Text ?? String.Empty },
                { "done", entry.Done.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) }
            });
        }

        private static CompletionEntry Parse(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return null;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null) return null;
                object idValue, doneValue, textValue;
                if (!root.TryGetValue("id", out idValue) || !(idValue is string) || String.IsNullOrWhiteSpace((string)idValue)) return null;
                if (!root.TryGetValue("done", out doneValue) || !(doneValue is string)) return null;
                DateTime done;
                if (!DateTime.TryParseExact((string)doneValue, "yyyy-MM-dd'T'H:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out done)) return null;
                root.TryGetValue("text", out textValue);
                return new CompletionEntry
                {
                    Id = ((string)idValue).Trim(),
                    Text = textValue as string ?? String.Empty,
                    Done = done
                };
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is InvalidOperationException) && !(error is FormatException)) throw;
                return null;
            }
        }

        // The preserved copy holds the full file verbatim, but dedup keys on the
        // damaged lines only: rewriting after unrelated valid edits must not
        // preserve the same corruption again and again.
        private void PreserveUnreadable(string[] lines, List<string> badLines)
        {
            byte[] contents = new UTF8Encoding(false).GetBytes(String.Join("\n", lines) + "\n");
            byte[] badContents = new UTF8Encoding(false).GetBytes(String.Join("\n", badLines.ToArray()) + "\n");
            using (SHA256 hash = SHA256.Create())
            {
                string signature = BitConverter.ToString(hash.ComputeHash(badContents)).Replace("-", "");
                foreach (string existing in Directory.GetFiles(directory, "completions.bad-*-" + signature + ".jsonl"))
                    return; // this exact damaged revision was already preserved once
                string preservedPath = Path.Combine(directory, "completions.bad-"
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + signature + ".jsonl");
                WriteNewFile(preservedPath, contents);
            }
        }

        private static void WriteNewFile(string path, byte[] contents)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(true);
            }
        }
    }
}
