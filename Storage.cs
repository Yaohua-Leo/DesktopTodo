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
            mutexName = "Local\\DesktopTodo.Storage." + Hash(Encoding.UTF8.GetBytes(
                this.directory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()));
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
                using (RepositoryLock gate = new RepositoryLock(mutexName))
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
            using (RepositoryLock gate = new RepositoryLock(mutexName))
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
                error = null;
                return ReadResult.Success;
            }
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

        private sealed class RepositoryLock : IDisposable
        {
            private readonly Mutex mutex;
            private bool held;
            public RepositoryLock(string name)
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
                        IsCompleted = record.IsCompleted
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
}
