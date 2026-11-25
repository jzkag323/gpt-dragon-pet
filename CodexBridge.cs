// ============================================================================
// CodexBridge.cs - 本机 OpenAI Codex CLI 状态与额度读取桥接
// 核心原理：
//   1. 优先拉起 `codex app-server` 通过标准输入输出交换 JSON-RPC 请求，
//      获取实时线程状态（Running/Completed/NeedsApproval）与账户限额百分比；
//   2. 若后台服务暂不可达，自动降级扫描 %USERPROFILE%\.codex\sessions 下的
//      rollout-*.jsonl 最近会话事件，保证状态识别稳定不卡主线程。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal enum CodexTaskStatus { Unknown, Running, Completed, Interrupted, Failed, Waiting, NeedsApproval }

internal sealed class CodexTask
{
    internal string Id;
    internal string Title;
    internal CodexTaskStatus Status;
    internal DateTime EventTime;
    internal DateTime LastWrite;
}

internal sealed class CodexQuota
{
    internal string Bucket;
    internal double Used;
    internal int Minutes;
    internal DateTime? Reset;
}

internal sealed class CodexBridge
{
    private readonly string sessions;
    private readonly SessionReader reader = new SessionReader();
    private string foundExecutable;

    internal CodexBridge() : this(GetCodexHome()) { }
    internal CodexBridge(string home) { sessions = Path.GetFullPath(Path.Combine(home, "sessions")); }

    private static string GetCodexHome()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !String.IsNullOrWhiteSpace(home) ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    internal CodexTask[] ReadLocalTasks(CancellationToken token)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in Enumerable.Range(0, 4).Select(i => DateTime.Today.AddDays(-i)))
        {
            token.ThrowIfCancellationRequested();
            var directory = Path.Combine(sessions, day.ToString("yyyy", CultureInfo.InvariantCulture), day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
            if (!Directory.Exists(directory)) continue;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("rollout-*.jsonl")) files[file.FullName] = file;
        }
        return files.Values.OrderByDescending(f => f.LastWriteTimeUtc).Take(60).Select(file =>
        {
            token.ThrowIfCancellationRequested();
            return reader.Read(file);
        }).Where(task => task != null).OrderByDescending(task => task.LastWrite).Take(12).ToArray();
    }

    internal CodexTask[] ReadTasks(CancellationToken token)
    {
        try
        {
            using (var rpc = Start(token))
            {
                var result = rpc.Call("thread/list", new { limit = 12, sortKey = "updated_at", archived = false });
                return JsonValue.Array(result, "data").Select(value => GetTask(JsonValue.Map(value))).Where(t => t != null).ToArray();
            }
        }
        catch (InvalidOperationException)
        {
            var local = ReadLocalTasks(token);
            if (local.Length == 0) throw;
            return local;
        }
    }

    private CodexTask GetTask(IDictionary<string, object> data)
    {
        string id = JsonValue.String(data, "id");
        if (String.IsNullOrEmpty(id)) return null;
        string title = JsonValue.String(data, "name");
        if (String.IsNullOrWhiteSpace(title)) title = JsonValue.String(data, "preview");
        title = String.Join(" ", (title ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (title.Length > 90) title = title.Substring(0, 89) + "…";
        if (String.IsNullOrWhiteSpace(title)) title = "Codex 会话";

        CodexTask marker = null;
        string path = JsonValue.String(data, "path");
        if (!String.IsNullOrEmpty(path) && Path.IsPathRooted(path))
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(sessions + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) marker = reader.Read(new FileInfo(full));
        }

        var status = JsonValue.Map(JsonValue.Get(data, "status"));
        string state = JsonValue.String(status, "type");
        CodexTaskStatus taskStatus = marker != null ? marker.Status : CodexTaskStatus.Unknown;
        if (state == "systemError") taskStatus = CodexTaskStatus.Failed;
        if (state == "active")
        {
            var flags = JsonValue.Array(status, "activeFlags");
            taskStatus = flags.Contains("waitingOnApproval") ? CodexTaskStatus.NeedsApproval :
                flags.Contains("waitingOnUserInput") ? CodexTaskStatus.Waiting : CodexTaskStatus.Running;
        }
        DateTime updated = JsonValue.Time(data, "updatedAt") ?? DateTime.MinValue;
        return new CodexTask { Id = id, Title = title, Status = taskStatus, EventTime = marker != null ? marker.EventTime : updated, LastWrite = updated };
    }

    internal CodexQuota[] ReadQuotas(CancellationToken token)
    {
        using (var rpc = Start(token))
        {
            var result = rpc.Call("account/rateLimits/read", null);
            var buckets = JsonValue.Map(JsonValue.Get(result, "rateLimitsByLimitId"));
            var quotas = new List<CodexQuota>();
            if (buckets != null && buckets.Count != 0)
            {
                foreach (var bucket in buckets.OrderBy(p => p.Key != "codex").ThenBy(p => p.Key))
                    AddQuotas(quotas, JsonValue.Map(bucket.Value), bucket.Key);
            }
            else AddQuotas(quotas, JsonValue.Map(JsonValue.Get(result, "rateLimits")), "codex");
            if (quotas.Count == 0) throw new InvalidOperationException("当前 Codex 登录没有返回可显示的订阅额度。");
            return quotas.ToArray();
        }
    }

    private static void AddQuotas(List<CodexQuota> quotas, IDictionary<string, object> bucket, string fallback)
    {
        var name = JsonValue.String(bucket, "limitName") ?? JsonValue.String(bucket, "limitId") ?? fallback;
        foreach (var key in new[] { "primary", "secondary" })
        {
            var window = JsonValue.Map(JsonValue.Get(bucket, key));
            var used = JsonValue.Number(window, "usedPercent");
            if (!used.HasValue) continue;
            quotas.Add(new CodexQuota { Bucket = name == "codex" ? "Codex" : name,
                Used = Math.Min(100, Math.Max(0, used.Value)), Minutes = (int)(JsonValue.Number(window, "windowDurationMins") ?? 0),
                Reset = JsonValue.Time(window, "resetsAt") });
        }
    }

    internal string LocateExecutable(CancellationToken token)
    {
        if (String.IsNullOrEmpty(foundExecutable) || !File.Exists(foundExecutable))
            foundExecutable = FindExecutable(token);
        return foundExecutable;
    }

    internal bool HasLocalCodex()
    {
        if (!String.IsNullOrEmpty(foundExecutable) && File.Exists(foundExecutable)) return true;
        string configured = Environment.GetEnvironmentVariable("CODEX_PET_EXE");
        if (Path.IsPathRooted(configured ?? "") && File.Exists(configured)) return true;
        foreach (var process in Process.GetProcessesByName("Codex"))
        using (process)
        {
            try
            {
                string bundled = Path.Combine(Path.GetDirectoryName(process.MainModule.FileName), "resources", "codex.exe");
                if (File.Exists(bundled)) { foundExecutable = bundled; return true; }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = entry.Trim().Trim('"');
            if (!Path.IsPathRooted(folder) || folder.EndsWith(@"Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate = Path.Combine(folder, "codex.exe");
            if (File.Exists(candidate)) { foundExecutable = candidate; return true; }
        }
        return false;
    }

    private CodexRpc Start(CancellationToken token)
    {
        string executable = LocateExecutable(token);
        if (String.IsNullOrEmpty(executable)) throw new InvalidOperationException("找不到本机 Codex。请安装并登录 Codex 后重试。");
        try
        {
            var connection = new CodexRpc(executable, token);
            foundExecutable = executable;
            return connection;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            foundExecutable = null;
            throw new InvalidOperationException("检测到了 Codex，但暂时无法启动本机连接。");
        }
    }

    private static string FindExecutable(CancellationToken token)
    {
        string configured = Environment.GetEnvironmentVariable("CODEX_PET_EXE");
        if (Path.IsPathRooted(configured ?? "") && File.Exists(configured)) return configured;
        foreach (var process in Process.GetProcessesByName("Codex"))
        using (process)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string bundled = Path.Combine(Path.GetDirectoryName(process.MainModule.FileName), "resources", "codex.exe");
                if (File.Exists(bundled)) return bundled;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = entry.Trim().Trim('"');
            if (!Path.IsPathRooted(folder) || folder.EndsWith(@"Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate = Path.Combine(folder, "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell)) return null;
        var commands = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $p=Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1; if($p){[IO.Path]::Combine($p.InstallLocation,'app','resources','codex.exe')}";
        try
        {
            using (var process = Process.Start(CodexRpc.StartInfo(powershell, "-NoProfile -NonInteractive -Command \"" + commands + "\"")))
            {
                var read = process.StandardOutput.ReadToEndAsync();
                try
                {
                    if (!read.Wait(4000, token)) return null;
                    string path = read.Result.Trim();
                    return File.Exists(path) ? path : null;
                }
                finally { CodexRpc.Stop(process); }
            }
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (AggregateException) { return null; }
    }
}

internal sealed class SessionReader
{
    private const int TailBytes = 2 * 1024 * 1024;
    private readonly object cacheLock = new object();
    private readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        internal long Length;
        internal DateTime LastWrite;
        internal DateTime Activity;
        internal CodexTask Marker;
    }

    internal CodexTask Read(FileInfo file)
    {
        lock (cacheLock)
        {
            try
            {
                long length = file.Length;
                DateTime modified = file.LastWriteTimeUtc;
                Entry entry;
                if (!cache.TryGetValue(file.FullName, out entry) || entry.Length != length || entry.LastWrite != modified)
                {
                    DateTime activity;
                    var marker = ReadTail(file, entry, out activity);
                    entry = new Entry { Length = length, LastWrite = modified, Marker = marker, Activity = activity };
                    if (cache.Count >= 80 && !cache.ContainsKey(file.FullName)) cache.Remove(cache.Keys.First());
                    cache[file.FullName] = entry;
                }
                if (entry.Marker == null) return null;
                var m = entry.Marker;
                var status = m.Status;
                var activityTime = entry.Activity > modified ? entry.Activity : modified;
                if (status == CodexTaskStatus.Running && (DateTime.UtcNow - activityTime).TotalMinutes > 45) status = CodexTaskStatus.Unknown;
                return new CodexTask { Id = m.Id, Title = m.Title, Status = status, EventTime = m.EventTime, LastWrite = activityTime };
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    private static CodexTask ReadTail(FileInfo file, Entry previous, out DateTime activity)
    {
        activity = DateTime.MinValue;
        string id = Path.GetFileNameWithoutExtension(file.Name);
        Guid parsed;
        if (id.Length < 36 || !Guid.TryParse(id.Substring(id.Length - 36), out parsed)) return null;
        id = parsed.ToString();
        var serializer = JsonValue.CreateSerializer();
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bool hasSameHistory = previous != null && previous.Length < stream.Length;
            long earliest = Math.Max(0, stream.Length - TailBytes);
            long start = hasSameHistory && previous.Marker != null && previous.Length >= earliest
                ? Math.Max(earliest, previous.Length - 4096) : earliest;
            if (hasSameHistory) activity = previous.Activity;
            stream.Position = start;
            using (var lines = new StreamReader(stream, Encoding.UTF8, false, 8192))
            {
                if (start > 0) lines.ReadLine();
                CodexTask latest = hasSameHistory ? previous.Marker : null;
                string line;
                while ((line = lines.ReadLine()) != null)
                {
                    DateTime when = Timestamp(line);
                    if (when > activity) activity = when;
                    if (line.IndexOf("\"event_msg\"", StringComparison.Ordinal) < 0 ||
                        (line.IndexOf("\"task_started\"", StringComparison.Ordinal) < 0 &&
                        line.IndexOf("\"task_complete\"", StringComparison.Ordinal) < 0 &&
                        line.IndexOf("\"turn_aborted\"", StringComparison.Ordinal) < 0)) continue;
                    var envelope = JsonValue.Parse(serializer, line);
                    if (JsonValue.String(envelope, "type") != "event_msg") continue;
                    var payload = JsonValue.Map(JsonValue.Get(envelope, "payload"));
                    var type = JsonValue.String(payload, "type");
                    if (when == DateTime.MinValue) continue;
                    if (latest != null && latest.EventTime > when) continue;
                    CodexTaskStatus status;
                    switch (type)
                    {
                        case "task_started": status = CodexTaskStatus.Running; break;
                        case "task_complete": status = CodexTaskStatus.Completed; break;
                        case "turn_aborted": status = CodexTaskStatus.Interrupted; break;
                        default: continue;
                    }
                    latest = new CodexTask { Id = id, Title = "本机 Codex 会话 · " + id.Substring(0, 8), Status = status, EventTime = when };
                }
                return latest;
            }
        }
    }

    private static DateTime Timestamp(string line)
    {
        const string key = "\"timestamp\"";
        var index = line.IndexOf(key, 0, Math.Min(100, line.Length), StringComparison.Ordinal);
        if (index < 0) return DateTime.MinValue;
        int first = line.IndexOf('"', index + key.Length);
        if (first < 0) return DateTime.MinValue;
        int last = line.IndexOf('"', first + 1);
        DateTime stamp;
        return last > first && last - first < 50 && DateTime.TryParse(line.Substring(first + 1, last - first - 1),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out stamp) ? stamp : DateTime.MinValue;
    }
}

internal sealed class CodexRpc : IDisposable
{
    private readonly Process process;
    private readonly CancellationToken token;
    private readonly JavaScriptSerializer serializer = JsonValue.CreateSerializer();
    private int id;

    internal CodexRpc(string executable, CancellationToken token)
    {
        this.token = token;
        token.ThrowIfCancellationRequested();
        var info = StartInfo(executable, "app-server --stdio");
        info.RedirectStandardInput = true;
        info.RedirectStandardError = true;
        process = Process.Start(info);
        try
        {
            process.BeginErrorReadLine();
            Call("initialize", new { clientInfo = new { name = "gpt_dragon_pet", title = "GPT Dragon Pet", version = "2.4.1" }, capabilities = new { experimentalApi = true } });
            Write(new { method = "initialized" });
        }
        catch { Dispose(); throw; }
    }

    internal static ProcessStartInfo StartInfo(string executable, string arguments)
    {
        return new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false), WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
    }

    internal IDictionary<string, object> Call(string method, object parameters)
    {
        int request = ++id;
        if (parameters == null) Write(new { method = method, id = request });
        else Write(new { method = method, id = request, @params = parameters });
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 12000)
        {
            var read = process.StandardOutput.ReadLineAsync();
            string line;
            try
            {
                if (!read.Wait(Math.Max(1, 12000 - (int)clock.ElapsedMilliseconds), token)) break;
                line = read.Result;
            }
            catch (AggregateException) { break; }
            if (line == null) throw new InvalidOperationException("本机 Codex 连接提前关闭。请确认 Codex 可正常启动。");
            var response = JsonValue.Parse(serializer, line);
            if (JsonValue.Number(response, "id") != request) continue;
            var result = JsonValue.Map(JsonValue.Get(response, "result"));
            if (result != null) return result;
            if (method == "account/rateLimits/read") throw new InvalidOperationException("Codex 暂未返回额度。请确认已用 ChatGPT 登录，并检查网络后刷新。");
            throw new InvalidOperationException("本机 Codex 暂未返回会话数据。");
        }
        throw new InvalidOperationException("连接 Codex 超时，可稍后刷新。");
    }

    private void Write(object value)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(serializer.Serialize(value) + "\n");
            process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            process.StandardInput.BaseStream.Flush();
        }
        catch (IOException) { throw new InvalidOperationException("本机 Codex 连接已关闭。"); }
    }

    internal static void Stop(Process process)
    {
        try { if (!process.HasExited && !process.WaitForExit(300)) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        try { process.StandardInput.Close(); } catch (IOException) { }
        Stop(process);
        process.Dispose();
    }
}

internal static class JsonValue
{
    internal static JavaScriptSerializer CreateSerializer() { return new JavaScriptSerializer { MaxJsonLength = 4194304, RecursionLimit = 40 }; }
    internal static IDictionary<string, object> Map(object value) { return value as IDictionary<string, object>; }
    internal static object Get(IDictionary<string, object> value, string key)
    {
        object found;
        return value != null && value.TryGetValue(key, out found) ? found : null;
    }
    internal static string String(IDictionary<string, object> value, string key) { return Get(value, key) as string; }
    internal static object[] Array(IDictionary<string, object> value, string key) { return Get(value, key) as object[] ?? new object[0]; }
    internal static double? Number(IDictionary<string, object> value, string key)
    {
        object raw = Get(value, key);
        double number;
        return raw != null && Double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && !Double.IsNaN(number) && !Double.IsInfinity(number) ? (double?)number : null;
    }
    internal static DateTime? Time(IDictionary<string, object> value, string key)
    {
        double? seconds = Number(value, key);
        if (!seconds.HasValue || seconds.Value < 0 || seconds.Value > 253402300799) return null;
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds.Value);
    }
    internal static IDictionary<string, object> Parse(JavaScriptSerializer serializer, string input)
    {
        try { return Map(serializer.DeserializeObject(input)); }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
