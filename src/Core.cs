using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexScheduler
{
    // ============================================================ 数据模型

    public class Schedule
    {
        public string Type { get; set; }
        public string At { get; set; }
        public string Time { get; set; }
        public int[] Weekdays { get; set; }
        public int EveryMinutes { get; set; }
        public string StartAt { get; set; }
        public string EndAt { get; set; }
    }

    public class Job
    {
        public string Id { get; set; }
        public string Title { get; set; }
        /// <summary>任务所属的 SSH 主机；老数据为空时使用当前设置的主机</summary>
        public string Host { get; set; }
        public string[] Threads { get; set; }
        public string Message { get; set; }
        public Schedule Schedule { get; set; }
        public bool Enabled { get; set; }
        /// <summary>已经没有下一次（单次已发 / 到达结束条件 / 已错过）——和“手动暂停”区分开</summary>
        public bool Finished { get; set; }
        public int MaxRuns { get; set; }
        public int Runs { get; set; }
        /// <summary>连接失败后的自动重试次数</summary>
        public int Retry { get; set; }
        public string LastRunTs { get; set; }
        public int LastOk { get; set; }
        public int LastTotal { get; set; }
        public string LastNote { get; set; }
        public string NextRun { get; set; }
        public string CreatedAt { get; set; }
    }

    public class ThreadInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long UpdatedAt { get; set; }
        public string Source { get; set; }
        public long Tokens { get; set; }
    }

    public class ThreadResult
    {
        public string ThreadId { get; set; }
        public bool Ok { get; set; }
        public bool RateLimited { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
    }

    public class RunEntry
    {
        public string Ts { get; set; }
        public string JobId { get; set; }
        public string JobTitle { get; set; }
        public string Host { get; set; }
        public string Trigger { get; set; }
        public string Message { get; set; }
        public int OkCount { get; set; }
        public int Total { get; set; }
        public bool ConnFailed { get; set; }
        public bool RateLimited { get; set; }
        public string Detail { get; set; }
    }

    // ============================================================ 路径 / 存储

    public static class Paths
    {
        static string _dir;

        public static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                // 优先 exe 旁边的 data 目录（便携）；不可写（比如放在 Program Files）再退到 %APPDATA%
                string local = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "data");
                if (CanWrite(local)) { _dir = local; return _dir; }

                string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexScheduler");
                try { if (!Directory.Exists(roaming)) Directory.CreateDirectory(roaming); } catch { }
                _dir = roaming;
                return _dir;
            }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string JobsFile { get { return Path.Combine(Dir, "jobs.json"); } }
        public static string LogFile { get { return Path.Combine(Dir, "run-log.jsonl"); } }
        public static string AppLogFile { get { return Path.Combine(Dir, "app.log"); } }
        public static string SettingsFile { get { return Path.Combine(Dir, "settings.json"); } }
        public static string TmpDir { get { return Path.Combine(Dir, "tmp"); } }
    }

    public class Settings
    {
        public static readonly string[] DefaultPresets = new string[] { "继续", "接着做", "继续，出结论或到达停止条件就汇报。" };

        public string SshHost { get; set; }
        public bool AutoStart { get; set; }
        /// <summary>全局暂停：不触发任何定时任务（手动执行不受影响）</summary>
        public bool Paused { get; set; }
        public bool TrayHintShown { get; set; }
        public string[] Presets { get; set; }

        public Settings()
        {
            SshHost = "cad-3-5090-fgy";
            AutoStart = false;
            Presets = (string[])DefaultPresets.Clone();
        }
    }

    public static class Store
    {
        public static readonly object Sync = new object();
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();
        static Store() { Ser.MaxJsonLength = int.MaxValue; }

        public static JavaScriptSerializer Serializer { get { return Ser; } }

        /// <summary>JavaScriptSerializer 解析出的数组可能是 object[] 或 ArrayList，统一成 object[]</summary>
        public static object[] AsArray(object o)
        {
            if (o == null) return new object[0];
            var arr = o as object[];
            if (arr != null) return arr;
            var al = o as ArrayList;
            if (al != null) return al.ToArray();
            return new object[0];
        }

        /// <summary>
        /// 读取任务。文件不存在返回空列表；文件存在但读不出来（损坏 / 被占用 / 正在被外部写）返回 null，
        /// 调用方必须保留内存中的任务，绝不能拿空列表去覆盖文件。
        /// </summary>
        public static List<Job> LoadJobs(out string error)
        {
            error = null;
            lock (Sync)
            {
                if (!File.Exists(Paths.JobsFile)) return new List<Job>();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        string txt = File.ReadAllText(Paths.JobsFile, Encoding.UTF8);
                        if (string.IsNullOrWhiteSpace(txt))
                        {
                            // 可能是外部程序刚截断、还没写完：稍等再读
                            if (attempt < 2) { Thread.Sleep(200); continue; }
                            return new List<Job>();
                        }
                        var list = Ser.Deserialize<List<Job>>(txt) ?? new List<Job>();
                        return Normalize(list);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        Thread.Sleep(200);
                    }
                }
                return null;
            }
        }

        static List<Job> Normalize(List<Job> list)
        {
            var res = new List<Job>();
            var seen = new HashSet<string>();
            foreach (var j in list)
            {
                if (j == null) continue;
                if (string.IsNullOrEmpty(j.Id) || seen.Contains(j.Id)) j.Id = NewId();
                seen.Add(j.Id);
                if (j.Threads == null) j.Threads = new string[0];
                if (j.Schedule == null) j.Schedule = new Schedule();
                if (string.IsNullOrEmpty(j.Title)) j.Title = "未命名任务";
                res.Add(j);
            }
            return res;
        }

        public static string NewId()
        {
            return DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        /// <summary>把读不出来的 jobs.json 另存一份，避免被后续保存覆盖后无法找回</summary>
        public static string BackupBrokenJobs()
        {
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(Paths.JobsFile)) return null;
                    string dst = Paths.JobsFile + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    File.Copy(Paths.JobsFile, dst, true);
                    return dst;
                }
                catch { return null; }
            }
        }

        public static void SaveJobs(List<Job> jobs)
        {
            lock (Sync)
            {
                string tmp = Paths.JobsFile + ".tmp";
                File.WriteAllText(tmp, Ser.Serialize(jobs), new UTF8Encoding(false));
                if (File.Exists(Paths.JobsFile))
                {
                    // 原子替换，顺带留一份 .bak；个别文件系统不支持 Replace 时退回覆盖复制
                    try { File.Replace(tmp, Paths.JobsFile, Paths.JobsFile + ".bak", true); return; }
                    catch { }
                    File.Copy(tmp, Paths.JobsFile, true);
                    try { File.Delete(tmp); } catch { }
                    return;
                }
                File.Move(tmp, Paths.JobsFile);
            }
        }

        public static DateTime JobsMtime()
        {
            try { return File.Exists(Paths.JobsFile) ? File.GetLastWriteTimeUtc(Paths.JobsFile) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        public static void AppendLog(RunEntry e)
        {
            lock (Sync)
            {
                try
                {
                    File.AppendAllText(Paths.LogFile, Ser.Serialize(e) + "\r\n", new UTF8Encoding(false));
                    var info = new FileInfo(Paths.LogFile);
                    if (info.Length > 2 * 1024 * 1024)
                    {
                        var lines = File.ReadAllLines(Paths.LogFile, Encoding.UTF8);
                        int keep = Math.Max(200, lines.Length / 2);
                        File.WriteAllLines(Paths.LogFile, lines.Skip(Math.Max(0, lines.Length - keep)).ToArray(), new UTF8Encoding(false));
                    }
                }
                catch { }
            }
        }

        public static List<RunEntry> ReadLog(int limit)
        {
            lock (Sync)
            {
                var res = new List<RunEntry>();
                try
                {
                    if (!File.Exists(Paths.LogFile)) return res;
                    var lines = File.ReadAllLines(Paths.LogFile, Encoding.UTF8);
                    foreach (var line in lines.Skip(Math.Max(0, lines.Length - limit)))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var e = Ser.Deserialize<RunEntry>(line);
                            if (e != null) res.Add(e);
                        }
                        catch { }
                    }
                }
                catch { }
                res.Reverse();
                return res;
            }
        }

        public static void ClearLog()
        {
            lock (Sync)
            {
                try { if (File.Exists(Paths.LogFile)) File.Delete(Paths.LogFile); } catch { }
            }
        }

        public static Settings LoadSettings()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    var s = Ser.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile, Encoding.UTF8));
                    if (s != null)
                    {
                        if (string.IsNullOrWhiteSpace(s.SshHost)) s.SshHost = new Settings().SshHost;
                        if (s.Presets == null) s.Presets = (string[])Settings.DefaultPresets.Clone();
                        s.Presets = s.Presets.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                        return s;
                    }
                }
            }
            catch { }
            return new Settings();
        }

        public static void SaveSettings(Settings s)
        {
            try { File.WriteAllText(Paths.SettingsFile, Ser.Serialize(s), new UTF8Encoding(false)); } catch { }
        }

        /// <summary>轻量运行日志，方便排查“为什么没发”；超过 1 MB 轮换一次</summary>
        public static void Log(string msg)
        {
            lock (Sync)
            {
                try
                {
                    string f = Paths.AppLogFile;
                    if (File.Exists(f) && new FileInfo(f).Length > 1024 * 1024)
                    {
                        string old = f + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(f, old);
                    }
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + "\r\n", new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }

    // ============================================================ SSH

    public static class Ssh
    {
        /// <summary>当前设置的主机（界面上显示/新建任务使用）</summary>
        public static string Host = "cad-3-5090-fgy";

        static readonly Regex HostRe = new Regex(@"^[A-Za-z0-9._@:\[\]\-]+$");
        static readonly Regex ThreadRe = new Regex(@"^[A-Za-z0-9_\-]{1,128}$");

        public static bool IsValidHost(string h)
        {
            return !string.IsNullOrEmpty(h) && h.Length <= 255 && !h.StartsWith("-") && HostRe.IsMatch(h);
        }

        public static bool IsValidThreadId(string id)
        {
            return !string.IsNullOrEmpty(id) && ThreadRe.IsMatch(id);
        }

        public class Result
        {
            public int Code;
            public bool TimedOut;
            public string Out = "";
            public string Err = "";
            public bool Ok { get { return Code == 0; } }
        }

        /// <summary>
        /// 在远端执行一段 bash。脚本写到临时文件，通过 stdin 重定向交给远端 `bash -s`：
        /// 不受 cmd 8191 字符命令行上限影响（以前长消息会直接失败），也不需要管道。
        /// </summary>
        public static Result Run(string host, string bash, int timeoutMs)
        {
            var res = new Result();
            if (!IsValidHost(host))
            {
                res.Code = 2;
                res.Err = "SSH 主机名不合法：" + host;
                return res;
            }

            string stamp = Guid.NewGuid().ToString("N");
            string tmp = Paths.TmpDir;
            try { if (!Directory.Exists(tmp)) Directory.CreateDirectory(tmp); } catch { tmp = Path.GetTempPath(); }
            string inFile = Path.Combine(tmp, "cs-" + stamp + ".sh");
            string outFile = Path.Combine(tmp, "cs-" + stamp + ".out");
            string errFile = Path.Combine(tmp, "cs-" + stamp + ".err");

            try
            {
                // 整段包进函数：bash 会先读完整个函数再执行，远端命令读 stdin 也不会吃掉后面的脚本
                string body = (bash ?? "").Replace("\r\n", "\n");
                string script = "__cs_main() {\n" + body + "\n}\n__cs_main < /dev/null\n";
                File.WriteAllText(inFile, script, new UTF8Encoding(false));

                string cmd = "ssh -o BatchMode=yes -o ConnectTimeout=15 -o ServerAliveInterval=30 -o ServerAliveCountMax=4 "
                           + host + " \"bash -s\" < \"" + inFile + "\" 1> \"" + outFile + "\" 2> \"" + errFile + "\"";

                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
                // /s：只去掉最外层引号，内部引号原样保留；参数走 Unicode，不再受 bat 文件代码页影响
                psi.Arguments = "/d /s /c \"" + cmd + "\"";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        KillTree(p.Id);
                        try { p.WaitForExit(5000); } catch { }
                        res.Code = 124;
                        res.TimedOut = true;
                        res.Err = "超时（" + (timeoutMs / 1000) + " 秒）";
                    }
                    else
                    {
                        res.Code = p.ExitCode;
                    }
                }

                res.Out = ReadShared(outFile);
                string err = ReadShared(errFile);
                if (!string.IsNullOrEmpty(err)) res.Err = string.IsNullOrEmpty(res.Err) ? err : (res.Err + "\n" + err);
            }
            catch (Exception ex)
            {
                res.Code = 1;
                res.Err = ex.Message;
            }
            finally
            {
                TryDelete(inFile);
                TryDelete(outFile);
                TryDelete(errFile);
            }
            return res;
        }

        /// <summary>超时时连同 ssh.exe 一起结束，避免留下孤儿进程占着输出文件</summary>
        static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + pid + " /T /F");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var k = Process.Start(psi)) k.WaitForExit(5000);
            }
            catch { }
        }

        static string ReadShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, new UTF8Encoding(false)))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        static void TryDelete(string path)
        {
            for (int i = 0; i < 3; i++)
            {
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch { Thread.Sleep(100); }
            }
        }
    }

    // ============================================================ 远端脚本

    public static class RemoteScripts
    {
        const string PathPrefix = "export PATH=\"$HOME/.local/bin:/usr/local/bin:$PATH\"\n";

        public static string ListThreads()
        {
            return PathPrefix + @"
python3 - <<'PYEOF'
import sqlite3, os, json, sys
base = os.path.expanduser('~/.codex')
out = []
src = None
try:
    con = sqlite3.connect('file:%s/state_5.sqlite?mode=ro' % base, uri=True)
    cur = con.cursor()
    rows = cur.execute(""select id, coalesce(nullif(name,''),''), coalesce(title,''), coalesce(updated_at,0), coalesce(source,''), coalesce(tokens_used,0) from threads where archived=0 order by updated_at desc"").fetchall()
    for r in rows:
        title = ' '.join((r[2] or '').split())
        out.append({'id': r[0], 'name': (r[1] or '').strip() or title[:48] or r[0][:8],
                    'updatedAt': int(r[3] or 0), 'source': r[4] or '', 'tokens': int(r[5] or 0)})
    src = 'state_5.sqlite'
except Exception as e:
    sys.stderr.write('state db failed: %s\n' % e)
print(json.dumps({'source': src, 'threads': out}, ensure_ascii=False))
PYEOF
";
        }

        /// <summary>threadId 必须先经过 Ssh.IsValidThreadId 校验（只含字母数字 _ -），这里才能安全地放进单引号</summary>
        public static string Queue(string threadId, string messageBase64)
        {
            return PathPrefix
                + "command -v codex >/dev/null 2>&1 || { echo NO_CODEX_IN_PATH; exit 127; }\n"
                + "msg=$(printf '%s' '" + messageBase64 + "' | base64 -d)\n"
                + "codex queue --thread '" + threadId + "' --message \"$msg\" --resume\n"
                + "rc=$?\n"
                + "echo QUEUE_EXIT=$rc\n"
                + "exit $rc\n";
        }

        /// <summary>解析 ListThreads 的输出；登录横幅等杂项会出现在 JSON 前面，取最后一行 JSON</summary>
        public static List<ThreadInfo> ParseThreads(string stdout, out string source)
        {
            source = null;
            var list = new List<ThreadInfo>();
            string json = null;
            var lines = (stdout ?? "").Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.StartsWith("{")) { json = l; break; }
            }
            if (json == null) throw new Exception("远端没有返回会话数据");

            var dict = Store.Serializer.Deserialize<Dictionary<string, object>>(json);
            if (dict.ContainsKey("source") && dict["source"] != null) source = Convert.ToString(dict["source"]);
            var arr = Store.AsArray(dict.ContainsKey("threads") ? dict["threads"] : null);
            foreach (var item in arr)
            {
                var o = item as Dictionary<string, object>;
                if (o == null) continue;
                var t = new ThreadInfo();
                t.Id = o.ContainsKey("id") ? Convert.ToString(o["id"]) : "";
                if (string.IsNullOrEmpty(t.Id)) continue;
                t.Name = o.ContainsKey("name") ? Convert.ToString(o["name"]) : "";
                t.Source = o.ContainsKey("source") ? Convert.ToString(o["source"]) : "";
                t.UpdatedAt = o.ContainsKey("updatedAt") ? Convert.ToInt64(o["updatedAt"]) : 0;
                t.Tokens = o.ContainsKey("tokens") ? Convert.ToInt64(o["tokens"]) : 0;
                list.Add(t);
            }
            return list;
        }
    }

    // ============================================================ 调度核心

    public static class Scheduler
    {
        public const int MaxRetry = 3;
        public const int RetryMinutes = 5;
        /// <summary>单次任务错过多久以内仍补发</summary>
        public const int OnceGraceHours = 6;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] WeekNames = new string[] { "日", "一", "二", "三", "四", "五", "六" };

        public static string TypeOf(Job job)
        {
            var s = job.Schedule;
            return (s == null || string.IsNullOrEmpty(s.Type)) ? "once" : s.Type;
        }

        public static DateTime? ComputeNextRun(Job job, DateTime now)
        {
            var s = job.Schedule ?? new Schedule();
            string type = TypeOf(job);

            if (type == "once")
            {
                DateTime at;
                if (!TryParseLocal(s.At, out at)) return null;
                if (job.Runs > 0) return null;
                if (at <= now.AddHours(-OnceGraceHours)) return null;
                return at;
            }

            DateTime end;
            bool hasEnd = TryParseLocal(s.EndAt, out end);
            if (hasEnd && now > end) return null;
            if (job.MaxRuns > 0 && job.Runs >= job.MaxRuns) return null;

            DateTime? cand = null;
            if (type == "daily")
            {
                DateTime c = AtTime(now, s.Time);
                if (c <= now) c = AtTime(now.AddDays(1), s.Time);
                cand = c;
            }
            else if (type == "weekly")
            {
                int[] days = (s.Weekdays != null && s.Weekdays.Length > 0) ? s.Weekdays : new int[] { 1 };
                for (int i = 0; i <= 7; i++)
                {
                    DateTime day = now.AddDays(i);
                    if (!days.Contains((int)day.DayOfWeek)) continue;
                    DateTime c = AtTime(day, s.Time);
                    if (c > now) { cand = c; break; }
                }
            }
            else if (type == "interval")
            {
                int mins = s.EveryMinutes > 0 ? s.EveryMinutes : 60;
                long step = (long)mins * 60000L;
                DateTime anchor;
                if (!TryParseLocal(s.StartAt, out anchor))
                    if (!TryParseLocal(job.CreatedAt, out anchor)) anchor = now;

                long nowMs = ToMs(now);
                long anchorMs = ToMs(anchor);
                long candMs = anchorMs;
                if (candMs <= nowMs)
                {
                    long k = (nowMs - anchorMs) / step + 1;
                    candMs = anchorMs + k * step;
                }
                cand = FromMs(candMs);
            }

            // 以前只在“现在”检查截止日期，导致截止后还会多发最后一次
            if (cand.HasValue && hasEnd && cand.Value > end) return null;
            return cand;
        }

        /// <summary>到点时再确认一次是否还该执行；返回 null 表示可以执行，否则是跳过原因</summary>
        public static string SkipReason(Job job, DateTime due, DateTime now)
        {
            var s = job.Schedule ?? new Schedule();
            if (TypeOf(job) == "once")
            {
                if (job.Runs > 0) return "已经执行过";
                if (due <= now.AddHours(-OnceGraceHours)) return "错过超过 " + OnceGraceHours + " 小时，不再补发";
                return null;
            }
            DateTime end;
            if (TryParseLocal(s.EndAt, out end) && due > end) return "已过截止日期";
            if (job.MaxRuns > 0 && job.Runs >= job.MaxRuns) return "已达到最多执行次数";
            return null;
        }

        static long ToMs(DateTime d) { return (d.ToUniversalTime().Ticks - 621355968000000000L) / 10000L; }
        static DateTime FromMs(long ms) { return new DateTime(621355968000000000L + ms * 10000L, DateTimeKind.Utc).ToLocalTime(); }

        public static DateTime AtTime(DateTime day, string hhmm)
        {
            int h = 2, m = 0;
            if (!string.IsNullOrEmpty(hhmm))
            {
                var parts = hhmm.Split(':');
                if (parts.Length >= 1) int.TryParse(parts[0], out h);
                if (parts.Length >= 2) int.TryParse(parts[1], out m);
            }
            h = Math.Min(23, Math.Max(0, h));
            m = Math.Min(59, Math.Max(0, m));
            return new DateTime(day.Year, day.Month, day.Day, h, m, 0, DateTimeKind.Local);
        }

        public static bool TryParseLocal(string iso, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (string.IsNullOrEmpty(iso)) return false;
            return DateTime.TryParse(iso, Inv, DateTimeStyles.AssumeLocal, out dt);
        }

        /// <summary>所有写进 jobs.json 的时间都用固定格式 + InvariantCulture（以前部分地方跟随系统区域设置）</summary>
        public static string Iso(DateTime? d)
        {
            if (!d.HasValue) return null;
            return d.Value.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
        }

        public static string IsoMinute(DateTime d) { return d.ToString("yyyy-MM-ddTHH:mm", Inv); }
        public static string HhMm(DateTime d) { return d.ToString("HH:mm", Inv); }

        public static string Friendly(DateTime d)
        {
            var today = DateTime.Today;
            string day;
            if (d.Date == today) day = "今天";
            else if (d.Date == today.AddDays(1)) day = "明天";
            else if (d.Date == today.AddDays(-1)) day = "昨天";
            else day = d.ToString("MM-dd", Inv) + " 周" + WeekNames[(int)d.DayOfWeek];
            return day + " " + d.ToString("HH:mm", Inv);
        }

        public static string RelFuture(DateTime d)
        {
            var span = d - DateTime.Now;
            if (span.TotalSeconds <= 30) return "即将执行";
            if (span.TotalMinutes < 60) return "还有 " + Math.Max(1, (int)Math.Ceiling(span.TotalMinutes)) + " 分钟";
            if (span.TotalHours < 24)
            {
                int h = (int)span.TotalHours;
                int m = span.Minutes;
                return "还有 " + h + " 小时" + (m > 0 ? (" " + m + " 分") : "");
            }
            return "还有 " + (int)span.TotalDays + " 天";
        }

        public static string Describe(Job job)
        {
            var s = job.Schedule ?? new Schedule();
            string type = TypeOf(job);
            string res;
            if (type == "once")
            {
                DateTime at;
                res = TryParseLocal(s.At, out at) ? ("单次 · " + at.ToString("MM-dd HH:mm", Inv)) : "单次";
                return res;
            }
            string time = string.IsNullOrEmpty(s.Time) ? "02:00" : s.Time;
            if (type == "daily") res = "每天 " + time;
            else if (type == "weekly")
            {
                var days = (s.Weekdays ?? new int[0]).Distinct().OrderBy(d => d == 0 ? 7 : d).ToArray();
                string ds;
                if (days.Length == 7) ds = "每天";
                else if (days.Length == 5 && !days.Contains(0) && !days.Contains(6)) ds = "工作日";
                else if (days.Length == 2 && days.Contains(0) && days.Contains(6)) ds = "周末";
                else ds = "周" + string.Join("", days.Select(d => WeekNames[Math.Max(0, Math.Min(6, d))]).ToArray());
                res = ds + " " + time;
            }
            else if (type == "interval")
            {
                int m = s.EveryMinutes > 0 ? s.EveryMinutes : 60;
                res = m % 60 == 0 ? ("每 " + (m / 60) + " 小时") : ("每 " + m + " 分钟");
            }
            else res = "—";

            DateTime end;
            if (TryParseLocal(s.EndAt, out end)) res += " · 至 " + end.ToString("MM-dd", Inv);
            if (job.MaxRuns > 0) res += " · " + job.Runs + "/" + job.MaxRuns + " 次";
            return res;
        }

        public static RunEntry Execute(Job job, string trigger, string host, Action<int, int> progress)
        {
            var started = DateTime.Now;
            var results = new List<ThreadResult>();
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(job.Message ?? ""));
            var tids = job.Threads ?? new string[0];

            for (int i = 0; i < tids.Length; i++)
            {
                string tid = tids[i] ?? "";
                if (progress != null) { try { progress(i, tids.Length); } catch { } }

                var tr = new ThreadResult();
                tr.ThreadId = tid;
                if (!Ssh.IsValidThreadId(tid))
                {
                    tr.ExitCode = 2;
                    tr.Output = "会话 ID 含非法字符，已跳过";
                    results.Add(tr);
                    continue;
                }
                try
                {
                    var r = Ssh.Run(host, RemoteScripts.Queue(tid, b64), 180000);
                    string all = (r.Out ?? "") + (string.IsNullOrEmpty(r.Err) ? "" : "\n" + r.Err);
                    tr.ExitCode = r.Code;
                    tr.Ok = r.Ok && all.IndexOf("Queued message", StringComparison.OrdinalIgnoreCase) >= 0;
                    tr.RateLimited = all.IndexOf("usage limit", StringComparison.OrdinalIgnoreCase) >= 0;
                    tr.Output = all.Trim();
                    if (tr.Output.Length > 1200) tr.Output = tr.Output.Substring(0, 1200) + " …";
                }
                catch (Exception ex)
                {
                    tr.ExitCode = 1;
                    tr.Output = ex.Message;
                }
                results.Add(tr);
                if (i < tids.Length - 1) Thread.Sleep(1200);
            }
            if (progress != null) { try { progress(tids.Length, tids.Length); } catch { } }

            var entry = new RunEntry();
            entry.Ts = started.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
            entry.JobId = job.Id;
            entry.JobTitle = job.Title;
            entry.Host = host;
            entry.Trigger = trigger;
            entry.Message = job.Message;
            entry.OkCount = results.Count(x => x.Ok);
            entry.Total = results.Count;
            entry.RateLimited = results.Any(x => x.RateLimited);
            // 只有 ssh 本身连不上（退出码 255）才算“连接失败”，才会自动重试——避免消息其实已入队却被重复发送
            entry.ConnFailed = results.Count > 0 && results.All(x => !x.Ok && x.ExitCode == 255);

            var sb = new StringBuilder();
            sb.AppendLine("主机: " + host + "    触发: " + (trigger == "manual" ? "手动" : "定时") + "    时间: " + entry.Ts);
            sb.AppendLine();
            foreach (var r in results)
            {
                sb.AppendLine(Short(r.ThreadId) + "  [" + (r.Ok ? "OK" : (r.RateLimited ? "限流" : "失败")) + "]  exit=" + r.ExitCode);
                sb.AppendLine(r.Output);
                sb.AppendLine();
            }
            entry.Detail = sb.ToString();
            Store.AppendLog(entry);
            return entry;
        }

        public static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }
    }
}
