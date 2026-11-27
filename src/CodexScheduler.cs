using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

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
        public string[] Threads { get; set; }
        public string Message { get; set; }
        public Schedule Schedule { get; set; }
        public bool Enabled { get; set; }
        public int MaxRuns { get; set; }
        public int Runs { get; set; }
        public string LastRunTs { get; set; }
        public int LastOk { get; set; }
        public int LastTotal { get; set; }
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
        public string Trigger { get; set; }
        public string Message { get; set; }
        public int OkCount { get; set; }
        public int Total { get; set; }
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
                // 优先用 exe 旁边的 data 目录（便携、不受用户目录 ACL 影响），不行再退到 %APPDATA%
                string local = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "data");
                try
                {
                    if (!Directory.Exists(local)) Directory.CreateDirectory(local);
                    _dir = local;
                    return _dir;
                }
                catch { }

                string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexScheduler");
                if (!Directory.Exists(roaming)) Directory.CreateDirectory(roaming);
                _dir = roaming;
                return _dir;
            }
        }
        public static string JobsFile { get { return Path.Combine(Dir, "jobs.json"); } }
        public static string LogFile { get { return Path.Combine(Dir, "run-log.jsonl"); } }
        public static string SettingsFile { get { return Path.Combine(Dir, "settings.json"); } }
    }

    public class Settings
    {
        public string SshHost { get; set; }
        public bool AutoStart { get; set; }
        public Settings() { SshHost = "cad-3-5090-fgy"; AutoStart = false; }
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

        public static List<Job> LoadJobs()
        {
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(Paths.JobsFile)) return new List<Job>();
                    string txt = File.ReadAllText(Paths.JobsFile, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(txt)) return new List<Job>();
                    var list = Ser.Deserialize<List<Job>>(txt);
                    return list ?? new List<Job>();
                }
                catch { return new List<Job>(); }
            }
        }

        public static void SaveJobs(List<Job> jobs)
        {
            lock (Sync)
            {
                string tmp = Paths.JobsFile + ".tmp";
                File.WriteAllText(tmp, Ser.Serialize(jobs), new UTF8Encoding(false));
                if (File.Exists(Paths.JobsFile)) File.Delete(Paths.JobsFile);
                File.Move(tmp, Paths.JobsFile);
            }
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
                        File.WriteAllLines(Paths.LogFile, lines.Skip(lines.Length - keep).ToArray(), new UTF8Encoding(false));
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
                        try { res.Add(Ser.Deserialize<RunEntry>(line)); } catch { }
                    }
                }
                catch { }
                res.Reverse();
                return res;
            }
        }

        public static Settings LoadSettings()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    var s = Ser.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile, Encoding.UTF8));
                    if (s != null && !string.IsNullOrEmpty(s.SshHost)) return s;
                }
            }
            catch { }
            return new Settings();
        }

        public static void SaveSettings(Settings s)
        {
            try { File.WriteAllText(Paths.SettingsFile, Ser.Serialize(s), new UTF8Encoding(false)); } catch { }
        }

        /// <summary>轻量运行日志，方便排查“为什么没发”</summary>
        public static void Log(string msg)
        {
            lock (Sync)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Paths.Dir, "app.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n", new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }

    // ============================================================ SSH

    public static class Ssh
    {
        public static string Host = "cad-3-5090-fgy";

        public class Result
        {
            public int Code;
            public string Out = "";
            public string Err = "";
            public bool Ok { get { return Code == 0; } }
        }

        public static Result Run(string bash, int timeoutMs)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(bash));
            string stamp = Guid.NewGuid().ToString("N");
            string tmp = Path.Combine(Paths.Dir, "tmp");
            try { if (!Directory.Exists(tmp)) Directory.CreateDirectory(tmp); } catch { tmp = Path.GetTempPath(); }
            string bat = Path.Combine(tmp, "cs-" + stamp + ".bat");
            string outFile = Path.Combine(tmp, "cs-" + stamp + ".out");
            string errFile = Path.Combine(tmp, "cs-" + stamp + ".err");

            var res = new Result();
            try
            {
                // 用 bat + 文件重定向，而不是管道：管道在部分受限环境下不可用
                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("ssh -o BatchMode=yes -o ConnectTimeout=15 -o ServerAliveInterval=30 -o ServerAliveCountMax=4 "
                            + Host + " \"echo " + b64 + " | base64 -d | bash\" 1> \"" + outFile + "\" 2> \"" + errFile + "\"");
                sb.AppendLine("exit /b %errorlevel%");
                File.WriteAllText(bat, sb.ToString(), Encoding.Default);

                var psi = new ProcessStartInfo("cmd.exe");
                psi.Arguments = "/c \"" + bat + "\"";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        res.Code = 124;
                        res.Err = "timeout after " + timeoutMs + " ms";
                    }
                    else
                    {
                        res.Code = p.ExitCode;
                    }
                }

                if (File.Exists(outFile)) res.Out = File.ReadAllText(outFile, new UTF8Encoding(false));
                if (File.Exists(errFile)) res.Err = res.Err + File.ReadAllText(errFile, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                res.Code = 1;
                res.Err = ex.Message;
            }
            finally
            {
                try { if (File.Exists(bat)) File.Delete(bat); } catch { }
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                try { if (File.Exists(errFile)) File.Delete(errFile); } catch { }
            }
            return res;
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
    }

    // ============================================================ 调度核心

    public static class Scheduler
    {
        public static DateTime? ComputeNextRun(Job job, DateTime now)
        {
            var s = job.Schedule ?? new Schedule();
            string type = string.IsNullOrEmpty(s.Type) ? "once" : s.Type;

            if (type == "once")
            {
                DateTime at;
                if (!TryParseLocal(s.At, out at)) return null;
                if (job.Runs > 0) return null;
                if (at <= now.AddHours(-6)) return null;
                return at;
            }

            if (!string.IsNullOrEmpty(s.EndAt))
            {
                DateTime end;
                if (TryParseLocal(s.EndAt, out end) && now > end) return null;
            }
            if (job.MaxRuns > 0 && job.Runs >= job.MaxRuns) return null;

            if (type == "daily")
            {
                DateTime cand = AtTime(now, s.Time);
                if (cand <= now) cand = AtTime(now.AddDays(1), s.Time);
                return cand;
            }

            if (type == "weekly")
            {
                int[] days = (s.Weekdays != null && s.Weekdays.Length > 0) ? s.Weekdays : new int[] { 1 };
                for (int i = 0; i < 8; i++)
                {
                    DateTime day = now.AddDays(i);
                    if (!days.Contains((int)day.DayOfWeek)) continue;
                    DateTime cand = AtTime(day, s.Time);
                    if (cand > now) return cand;
                }
                return null;
            }

            if (type == "interval")
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
                return FromMs(candMs);
            }

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
            return DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out dt);
        }

        public static string Iso(DateTime? d)
        {
            if (!d.HasValue) return null;
            return d.Value.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string Describe(Job job)
        {
            var s = job.Schedule ?? new Schedule();
            if (s.Type == "once")
            {
                DateTime at;
                if (TryParseLocal(s.At, out at)) return "单次 · " + at.ToString("MM-dd HH:mm");
                return "单次";
            }
            if (s.Type == "daily") return "每天 " + (string.IsNullOrEmpty(s.Time) ? "02:00" : s.Time);
            if (s.Type == "weekly")
            {
                string[] names = new string[] { "日", "一", "二", "三", "四", "五", "六" };
                var ds = (s.Weekdays ?? new int[0]).Select(d => names[Math.Max(0, Math.Min(6, d))]);
                return "每周" + string.Join("、", ds.ToArray()) + " " + (string.IsNullOrEmpty(s.Time) ? "02:00" : s.Time);
            }
            if (s.Type == "interval")
            {
                int m = s.EveryMinutes > 0 ? s.EveryMinutes : 60;
                return m % 60 == 0 ? ("每 " + (m / 60) + " 小时") : ("每 " + m + " 分钟");
            }
            return "—";
        }

        public static RunEntry Execute(Job job, string trigger)
        {
            var started = DateTime.Now;
            var results = new List<ThreadResult>();
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(job.Message ?? "继续"));

            foreach (string tid in job.Threads ?? new string[0])
            {
                var r = Ssh.Run(RemoteScripts.Queue(tid, b64), 180000);
                string all = (r.Out ?? "") + (string.IsNullOrEmpty(r.Err) ? "" : "\n" + r.Err);
                var tr = new ThreadResult();
                tr.ThreadId = tid;
                tr.ExitCode = r.Code;
                tr.Ok = r.Ok && all.IndexOf("Queued message", StringComparison.OrdinalIgnoreCase) >= 0;
                tr.RateLimited = all.IndexOf("usage limit", StringComparison.OrdinalIgnoreCase) >= 0;
                tr.Output = all.Trim();
                if (tr.Output.Length > 1200) tr.Output = tr.Output.Substring(0, 1200);
                results.Add(tr);
                Thread.Sleep(1200);
            }

            var entry = new RunEntry();
            entry.Ts = started.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            entry.JobId = job.Id;
            entry.JobTitle = job.Title;
            entry.Trigger = trigger;
            entry.Message = job.Message;
            entry.OkCount = results.Count(x => x.Ok);
            entry.Total = results.Count;
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                sb.AppendLine(r.ThreadId.Substring(0, Math.Min(8, r.ThreadId.Length)) + "  [" + (r.Ok ? "OK" : (r.RateLimited ? "限流" : "失败")) + "]  exit=" + r.ExitCode);
                sb.AppendLine(r.Output);
            }
            entry.Detail = sb.ToString();
            Store.AppendLog(entry);
            return entry;
        }
    }

    // ============================================================ 主窗体

    public class MainForm : Form
    {
        static readonly Color C_BG = Color.FromArgb(13, 17, 23);
        static readonly Color C_PANEL = Color.FromArgb(21, 27, 36);
        static readonly Color C_PANEL2 = Color.FromArgb(26, 33, 44);
        static readonly Color C_INPUT = Color.FromArgb(15, 20, 28);
        static readonly Color C_LINE = Color.FromArgb(36, 44, 58);
        static readonly Color C_TEXT = Color.FromArgb(231, 236, 243);
        static readonly Color C_MUTED = Color.FromArgb(137, 148, 167);
        static readonly Color C_ACCENT = Color.FromArgb(76, 159, 254);
        static readonly Color C_BAD = Color.FromArgb(255, 107, 107);

        Settings _settings;
        List<ThreadInfo> _threads = new List<ThreadInfo>();
        List<Job> _jobs = new List<Job>();
        DateTime _jobsMtime = DateTime.MinValue;
        string _editingId = null;
        bool _exiting = false;
        bool _running = false;

        TextBox txtHost;
        Label lblHostCap;
        Label lblStatus;
        SplitContainer _split;
        ListView lvThreads;
        TextBox txtSearch;
        Label lblSel;

        TabControl tabs;
        TextBox txtTitle, txtMessage, txtPreview;
        ComboBox cbType;
        Panel pOnce, pDaily, pWeekly, pInterval;
        DateTimePicker dtOnce, dtDailyTime, dtWeeklyTime, dtEnd, dtIntervalStart;
        CheckBox[] chkDays = new CheckBox[7];
        NumericUpDown numEvery, numMaxRuns;
        Button btnSave, btnCancelEdit, btnPreview;

        DataGridView grid;
        Label lblJobHint;

        ListView lvLogs;
        NotifyIcon tray;
        System.Windows.Forms.Timer tickTimer;

        public MainForm()
        {
            _settings = Store.LoadSettings();
            Ssh.Host = _settings.SshHost;

            Text = "Codex 定时发送台";
            BackColor = C_BG;
            ForeColor = C_TEXT;
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;

            var wa = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(1380, wa.Width - 30);
            Height = Math.Min(830, wa.Height - 30);
            MinimumSize = new Size(1020, 660);

            BuildAll();
            Load += delegate { OnInit(); };
            FormClosing += OnClosing;
        }

        void BuildAll()
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.Padding = new Padding(0);
            root.Margin = new Padding(0);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.BackColor = C_BG;
            Controls.Add(root);

            var head = BuildHeader();
            head.Margin = new Padding(0);
            var body = BuildBody();
            body.Margin = new Padding(0);
            root.Controls.Add(head, 0, 0);
            root.Controls.Add(body, 0, 1);
        }

        // ---------------------------------------------------------- 顶栏

        Control BuildHeader()
        {
            var head = new Panel();
            head.Dock = DockStyle.Fill;
            head.BackColor = C_PANEL;
            head.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(C_LINE)) e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1);
            };

            var logo = new Panel();
            logo.SetBounds(18, 13, 36, 36);
            logo.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 36, 36), Color.FromArgb(43, 110, 246), Color.FromArgb(36, 200, 169), 45f))
                    g.FillEllipse(br, 0, 0, 35, 35);
                using (var f = new Font("Segoe UI", 13F, FontStyle.Bold))
                using (var b = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("C", f, b, new RectangleF(0, 0, 35, 35), sf);
                }
            };
            head.Controls.Add(logo);

            var title = new Label();
            title.Text = "Codex 定时发送台";
            title.Font = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold);
            title.ForeColor = C_TEXT;
            title.SetBounds(66, 9, 340, 24);
            head.Controls.Add(title);

            var sub = new Label();
            sub.Text = "按计划把消息投给指定会话，让它们自己继续跑";
            sub.ForeColor = C_MUTED;
            sub.Font = new Font("Microsoft YaHei UI", 8.5F);
            sub.SetBounds(67, 33, 420, 18);
            head.Controls.Add(sub);

            var lblH = new Label();
            lblH.Text = "SSH 主机";
            lblH.ForeColor = C_MUTED;
            lblH.SetBounds(880, 12, 70, 18);
            head.Controls.Add(lblH);
            lblHostCap = lblH;

            txtHost = new TextBox();
            txtHost.Text = _settings.SshHost;
            txtHost.BackColor = C_INPUT;
            txtHost.ForeColor = C_TEXT;
            txtHost.BorderStyle = BorderStyle.FixedSingle;
            txtHost.SetBounds(880, 31, 170, 24);
            txtHost.Leave += delegate
            {
                string h = txtHost.Text.Trim();
                if (h.Length > 0 && h != Ssh.Host)
                {
                    Ssh.Host = h;
                    _settings.SshHost = h;
                    Store.SaveSettings(_settings);
                    SetStatus("已切换主机 " + h + "，正在刷新会话…", false);
                    RefreshThreads(true);
                }
            };
            head.Controls.Add(txtHost);

            lblStatus = new Label();
            lblStatus.Text = "正在连接远端…";
            lblStatus.ForeColor = C_MUTED;
            lblStatus.TextAlign = ContentAlignment.MiddleRight;
            lblStatus.SetBounds(560, 27, 300, 22);
            head.Controls.Add(lblStatus);

            head.Resize += delegate { PlaceHeaderRight(head); };
            return head;
        }

        void PlaceHeaderRight(Panel head)
        {
            int w = head.ClientSize.Width;
            if (w <= 0) return;
            const int right = 18;
            const int statusW = 330;
            const int hostW = 180;
            lblStatus.SetBounds(w - right - statusW, 28, statusW, 22);
            txtHost.SetBounds(w - right - statusW - 12 - hostW, 31, hostW, 24);
            lblHostCap.SetBounds(txtHost.Left, 12, 100, 18);
        }

        // ---------------------------------------------------------- 主体

        Control BuildBody()
        {
            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.BackColor = C_BG;
            split.SplitterWidth = 1;
            split.FixedPanel = FixedPanel.Panel1;
            split.Panel1.BackColor = C_BG;
            split.Panel2.BackColor = C_BG;
            split.SplitterDistance = 350;
            _split = split;

            split.Panel1.Controls.Add(BuildThreadPanel());

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Microsoft YaHei UI", 9F);
            tabs.Padding = new Point(14, 5);
            tabs.TabPages.Add(BuildComposeTab());
            tabs.TabPages.Add(BuildJobsTab());
            tabs.TabPages.Add(BuildLogsTab());
            split.Panel2.Controls.Add(tabs);
            return split;
        }

        Control BuildThreadPanel()
        {
            var card = new TableLayoutPanel();
            card.Dock = DockStyle.Fill;
            card.BackColor = C_PANEL;
            card.Padding = new Padding(14, 10, 14, 10);
            card.ColumnCount = 1;
            card.RowCount = 6;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));

            var h = new Label();
            h.Text = "1 · 选择会话";
            h.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            h.ForeColor = C_TEXT;
            h.Dock = DockStyle.Fill;
            h.TextAlign = ContentAlignment.MiddleLeft;
            card.Controls.Add(h, 0, 0);

            lblSel = new Label();
            lblSel.Text = "已选 0 个会话";
            lblSel.ForeColor = C_ACCENT;
            lblSel.Dock = DockStyle.Fill;
            lblSel.TextAlign = ContentAlignment.MiddleLeft;
            card.Controls.Add(lblSel, 0, 1);

            txtSearch = new TextBox();
            txtSearch.Dock = DockStyle.Fill;
            txtSearch.BackColor = C_INPUT;
            txtSearch.ForeColor = C_TEXT;
            txtSearch.BorderStyle = BorderStyle.FixedSingle;
            txtSearch.TextChanged += delegate { FillThreadList(); };
            var searchHost = new Panel();
            searchHost.Dock = DockStyle.Fill;
            searchHost.Padding = new Padding(0, 6, 0, 4);
            searchHost.Controls.Add(txtSearch);
            card.Controls.Add(searchHost, 0, 2);

            lvThreads = new ListView();
            lvThreads.Dock = DockStyle.Fill;
            lvThreads.View = View.Details;
            lvThreads.CheckBoxes = true;
            lvThreads.FullRowSelect = true;
            lvThreads.HideSelection = false;
            lvThreads.BackColor = C_PANEL;
            lvThreads.ForeColor = C_TEXT;
            lvThreads.BorderStyle = BorderStyle.None;
            lvThreads.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvThreads.Columns.Add("会话", 220);
            lvThreads.Columns.Add("最近", 80);
            lvThreads.ItemChecked += delegate { UpdateSelLabel(); };
            card.Controls.Add(lvThreads, 0, 3);

            var spacer = new Panel();
            spacer.Dock = DockStyle.Fill;
            card.Controls.Add(spacer, 0, 4);

            var bottom = new Panel();
            bottom.Dock = DockStyle.Fill;
            card.Controls.Add(bottom, 0, 5);

            var btnAll = MakeButton("全选", 0, 0, 70);
            btnAll.Click += delegate { SelectVisible(true); };
            bottom.Controls.Add(btnAll);

            var btnNone = MakeButton("清空", 76, 0, 70);
            btnNone.Click += delegate
            {
                foreach (ListViewItem it in lvThreads.Items) it.Checked = false;
                UpdateSelLabel();
            };
            bottom.Controls.Add(btnNone);

            var btnRefresh = MakeButton("刷新", 152, 0, 70);
            btnRefresh.Click += delegate { RefreshThreads(true); };
            bottom.Controls.Add(btnRefresh);

            var btnQuick = MakeButton("填入「继续」", 0, 32, 222);
            btnQuick.Click += delegate
            {
                txtMessage.Text = "继续";
                tabs.SelectedIndex = 0;
                txtMessage.Focus();
                txtMessage.SelectionStart = txtMessage.Text.Length;
            };
            bottom.Controls.Add(btnQuick);
            return card;
        }

        TabPage BuildComposeTab()
        {
            var tab = new TabPage("新建 / 编辑任务");
            tab.BackColor = C_PANEL;

            var scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = C_PANEL;
            tab.Controls.Add(scroll);

            int y = 14;
            AddLabel(scroll, "任务名称", 16, y, 200, C_MUTED, 8.5F);
            txtTitle = MakeText(scroll, 16, y + 20, 900, "");
            txtTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            y += 58;
            AddLabel(scroll, "发送内容", 16, y, 200, C_MUTED, 8.5F);
            txtMessage = MakeText(scroll, 16, y + 20, 900, "继续");
            txtMessage.Multiline = true;
            txtMessage.Height = 66;
            txtMessage.ScrollBars = ScrollBars.Vertical;
            txtMessage.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            y += 96;
            string[] presets = new string[] { "继续", "接着做", "继续，出结论或到达停止条件就汇报。" };
            string[] labels = new string[] { "继续", "接着做", "继续并汇报" };
            int px = 16;
            for (int i = 0; i < presets.Length; i++)
            {
                string text = presets[i];
                int w = i == 2 ? 104 : 68;
                var b = MakeButton(labels[i], px, y, w);
                b.Click += delegate { txtMessage.Text = text; txtMessage.Focus(); };
                scroll.Controls.Add(b);
                px += w + 8;
            }

            y += 44;
            AddLabel(scroll, "发送周期", 16, y, 200, C_MUTED, 8.5F);
            cbType = new ComboBox();
            cbType.DropDownStyle = ComboBoxStyle.DropDownList;
            cbType.Items.AddRange(new object[] { "单次", "每天", "每周", "间隔" });
            cbType.SelectedIndex = 0;
            cbType.SetBounds(16, y + 20, 130, 26);
            cbType.BackColor = C_INPUT;
            cbType.ForeColor = C_TEXT;
            cbType.FlatStyle = FlatStyle.Flat;
            cbType.SelectedIndexChanged += delegate { SwitchType(); };
            scroll.Controls.Add(cbType);

            pOnce = MakeSubPanel(scroll, 168, y + 18, 620, 40);
            AddLabel(pOnce, "发送时间", 0, 4, 70, C_MUTED, 8.5F);
            dtOnce = MakeDateTime(pOnce, 74, 2, 200, "yyyy-MM-dd HH:mm");

            pDaily = MakeSubPanel(scroll, 168, y + 18, 620, 40);
            AddLabel(pDaily, "每天几点", 0, 4, 70, C_MUTED, 8.5F);
            dtDailyTime = MakeDateTime(pDaily, 74, 2, 100, "HH:mm");
            dtDailyTime.ShowUpDown = true;

            pWeekly = MakeSubPanel(scroll, 168, y + 18, 640, 40);
            AddLabel(pWeekly, "星期", 0, 4, 40, C_MUTED, 8.5F);
            string[] dayNames = new string[] { "一", "二", "三", "四", "五", "六", "日" };
            int[] dayVals = new int[] { 1, 2, 3, 4, 5, 6, 0 };
            for (int i = 0; i < 7; i++)
            {
                var cb = new CheckBox();
                cb.Text = dayNames[i];
                cb.Tag = dayVals[i];
                cb.SetBounds(40 + i * 38, 2, 36, 24);
                cb.ForeColor = C_TEXT;
                cb.FlatStyle = FlatStyle.Flat;
                if (i == 0) cb.Checked = true;
                pWeekly.Controls.Add(cb);
                chkDays[i] = cb;
            }
            AddLabel(pWeekly, "几点", 316, 4, 34, C_MUTED, 8.5F);
            dtWeeklyTime = MakeDateTime(pWeekly, 352, 2, 100, "HH:mm");
            dtWeeklyTime.ShowUpDown = true;

            pInterval = MakeSubPanel(scroll, 168, y + 18, 660, 40);
            AddLabel(pInterval, "每隔", 0, 4, 40, C_MUTED, 8.5F);
            numEvery = new NumericUpDown();
            numEvery.Minimum = 1;
            numEvery.Maximum = 100000;
            numEvery.Value = 120;
            numEvery.SetBounds(40, 2, 80, 24);
            numEvery.BackColor = C_INPUT;
            numEvery.ForeColor = C_TEXT;
            numEvery.BorderStyle = BorderStyle.FixedSingle;
            pInterval.Controls.Add(numEvery);
            AddLabel(pInterval, "分钟", 126, 4, 40, C_MUTED, 8.5F);
            AddLabel(pInterval, "开始（可空）", 176, 4, 90, C_MUTED, 8.5F);
            dtIntervalStart = MakeDateTime(pInterval, 266, 2, 180, "yyyy-MM-dd HH:mm");
            dtIntervalStart.ShowCheckBox = true;
            dtIntervalStart.Checked = false;

            y += 70;
            AddLabel(scroll, "结束条件（可选）", 16, y, 200, C_MUTED, 8.5F);
            var lblMax = new Label();
            lblMax.Text = "最多执行";
            lblMax.ForeColor = C_MUTED;
            lblMax.SetBounds(16, y + 24, 60, 20);
            scroll.Controls.Add(lblMax);

            numMaxRuns = new NumericUpDown();
            numMaxRuns.Minimum = 0;
            numMaxRuns.Maximum = 100000;
            numMaxRuns.SetBounds(78, y + 22, 70, 24);
            numMaxRuns.BackColor = C_INPUT;
            numMaxRuns.ForeColor = C_TEXT;
            numMaxRuns.BorderStyle = BorderStyle.FixedSingle;
            scroll.Controls.Add(numMaxRuns);

            AddLabel(scroll, "次（0 = 不限）", 154, y + 24, 100, C_MUTED, 8.5F);
            AddLabel(scroll, "截止日期", 280, y + 24, 60, C_MUTED, 8.5F);
            dtEnd = MakeDateTime(scroll, 344, y + 22, 150, "yyyy-MM-dd");
            dtEnd.ShowCheckBox = true;
            dtEnd.Checked = false;

            y += 62;
            var line = new Panel();
            line.SetBounds(16, y, 900, 1);
            line.BackColor = C_LINE;
            line.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            scroll.Controls.Add(line);

            y += 14;
            btnPreview = MakeButton("预览命令", 16, y, 100);
            btnPreview.Click += delegate { DoPreview(); };
            scroll.Controls.Add(btnPreview);

            btnSave = MakeButton("保存任务", 124, y, 110);
            btnSave.BackColor = C_ACCENT;
            btnSave.ForeColor = Color.White;
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            btnSave.Click += delegate { DoSave(); };
            scroll.Controls.Add(btnSave);

            btnCancelEdit = MakeButton("取消编辑", 242, y, 90);
            btnCancelEdit.Visible = false;
            btnCancelEdit.Click += delegate { ExitEdit(); };
            scroll.Controls.Add(btnCancelEdit);

            y += 42;
            txtPreview = MakeText(scroll, 16, y, 900, "");
            txtPreview.Multiline = true;
            txtPreview.Height = 78;
            txtPreview.ReadOnly = true;
            txtPreview.ScrollBars = ScrollBars.Both;
            txtPreview.WordWrap = false;
            txtPreview.Font = new Font("Consolas", 8.5F);
            txtPreview.ForeColor = Color.FromArgb(159, 208, 255);
            txtPreview.Visible = false;
            txtPreview.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            // 宽度跟随面板，避免出现横向滚动条
            scroll.Resize += delegate
            {
                int w = Math.Max(420, scroll.ClientSize.Width - 34);
                txtTitle.Width = w;
                txtMessage.Width = w;
                line.Width = w;
                txtPreview.Width = w;
            };
            return tab;
        }

        TabPage BuildJobsTab()
        {
            var tab = new TabPage("任务列表");
            tab.BackColor = C_PANEL;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.Padding = new Padding(0);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tab.Controls.Add(root);

            var bar = new Panel();
            bar.Dock = DockStyle.Fill;
            bar.Margin = new Padding(0);
            bar.Padding = new Padding(12, 9, 12, 8);
            root.Controls.Add(bar, 0, 0);

            var bRun = MakeButton("立即执行", 0, 0, 90);
            bRun.Click += delegate { RunSelected(); };
            bar.Controls.Add(bRun);

            var bEdit = MakeButton("编辑", 98, 0, 70);
            bEdit.Click += delegate { EditSelected(); };
            bar.Controls.Add(bEdit);

            var bToggle = MakeButton("暂停 / 启用", 176, 0, 100);
            bToggle.Click += delegate { ToggleSelected(); };
            bar.Controls.Add(bToggle);

            var bDel = MakeButton("删除", 284, 0, 70);
            bDel.Click += delegate { DeleteSelected(); };
            bar.Controls.Add(bDel);

            lblJobHint = new Label();
            lblJobHint.ForeColor = C_MUTED;
            lblJobHint.TextAlign = ContentAlignment.MiddleRight;
            lblJobHint.Dock = DockStyle.Right;
            lblJobHint.Width = 280;
            bar.Controls.Add(lblJobHint);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = C_PANEL;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = C_LINE;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeight = 34;
            grid.RowTemplate.Height = 46;
            grid.ColumnHeadersDefaultCellStyle.BackColor = C_PANEL2;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = C_MUTED;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = C_PANEL2;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.5F);
            grid.DefaultCellStyle.BackColor = C_PANEL;
            grid.DefaultCellStyle.ForeColor = C_TEXT;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(37, 52, 74);
            grid.DefaultCellStyle.SelectionForeColor = C_TEXT;
            grid.DefaultCellStyle.Padding = new Padding(6, 2, 6, 2);
            grid.Columns.Add("title", "任务");
            grid.Columns.Add("threads", "会话");
            grid.Columns.Add("sched", "周期");
            grid.Columns.Add("next", "下次执行");
            grid.Columns.Add("status", "状态");
            grid.Columns["title"].FillWeight = 32;
            grid.Columns["threads"].FillWeight = 15;
            grid.Columns["sched"].FillWeight = 13;
            grid.Columns["next"].FillWeight = 18;
            grid.Columns["status"].FillWeight = 22;
            grid.CellDoubleClick += delegate { EditSelected(); };
            grid.Margin = new Padding(0);
            root.Controls.Add(grid, 0, 1);
            return tab;
        }

        TabPage BuildLogsTab()
        {
            var tab = new TabPage("运行日志");
            tab.BackColor = C_PANEL;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.Padding = new Padding(0);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tab.Controls.Add(root);

            var bar = new Panel();
            bar.Dock = DockStyle.Fill;
            bar.Margin = new Padding(0);
            bar.Padding = new Padding(12, 8, 12, 8);
            root.Controls.Add(bar, 0, 0);

            var bRefresh = MakeButton("刷新", 0, 0, 74);
            bRefresh.Click += delegate { FillLogs(); };
            bar.Controls.Add(bRefresh);

            var hint = new Label();
            hint.Text = "双击一行查看完整输出";
            hint.ForeColor = C_MUTED;
            hint.Dock = DockStyle.Right;
            hint.Width = 220;
            hint.TextAlign = ContentAlignment.MiddleRight;
            bar.Controls.Add(hint);

            lvLogs = new ListView();
            lvLogs.Dock = DockStyle.Fill;
            lvLogs.View = View.Details;
            lvLogs.FullRowSelect = true;
            lvLogs.BackColor = C_PANEL;
            lvLogs.ForeColor = C_TEXT;
            lvLogs.BorderStyle = BorderStyle.None;
            lvLogs.Columns.Add("时间", 150);
            lvLogs.Columns.Add("任务", 240);
            lvLogs.Columns.Add("结果", 110);
            lvLogs.Columns.Add("触发", 70);
            lvLogs.Columns.Add("内容", 340);
            lvLogs.DoubleClick += delegate { ShowLogDetail(); };
            lvLogs.Margin = new Padding(0);
            root.Controls.Add(lvLogs, 0, 1);
            return tab;
        }

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "Codex 定时发送台";
            tray.Visible = true;

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开主界面", null, delegate { ShowMain(); });

            var miAuto = new ToolStripMenuItem("开机自启动");
            miAuto.Checked = IsAutoStartOn();
            miAuto.CheckOnClick = true;
            miAuto.Click += delegate(object s, EventArgs e)
            {
                var mi = (ToolStripMenuItem)s;
                SetAutoStart(mi.Checked);
                _settings.AutoStart = mi.Checked;
                Store.SaveSettings(_settings);
            };
            menu.Items.Add(miAuto);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { _exiting = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowMain(); };
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IsAutoStartOn()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    var v = k.GetValue("CodexScheduler") as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue("CodexScheduler", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("CodexScheduler", false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置自启动失败：" + ex.Message);
            }
        }

        // ---------------------------------------------------------- 控件工厂

        static Button MakeButton(string text, int x, int y, int w)
        {
            var b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 28);
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(16, 23, 34);
            b.ForeColor = C_TEXT;
            b.FlatAppearance.BorderColor = C_LINE;
            b.Cursor = Cursors.Hand;
            b.Font = new Font("Microsoft YaHei UI", 9F);
            return b;
        }

        TextBox MakeText(Control parent, int x, int y, int w, string value)
        {
            var t = new TextBox();
            t.SetBounds(x, y, w, 26);
            t.BackColor = C_INPUT;
            t.ForeColor = C_TEXT;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Text = value;
            parent.Controls.Add(t);
            return t;
        }

        static DateTimePicker MakeDateTime(Control parent, int x, int y, int w, string format)
        {
            var d = new DateTimePicker();
            d.SetBounds(x, y, w, 26);
            d.Format = DateTimePickerFormat.Custom;
            d.CustomFormat = format;
            d.Font = new Font("Microsoft YaHei UI", 9F);
            parent.Controls.Add(d);
            return d;
        }

        static Panel MakeSubPanel(Control parent, int x, int y, int w, int h)
        {
            var p = new Panel();
            p.SetBounds(x, y, w, h);
            p.BackColor = C_PANEL;
            parent.Controls.Add(p);
            return p;
        }

        static Label AddLabel(Control parent, string text, int x, int y, int w, Color color, float size)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.Font = new Font("Microsoft YaHei UI", size);
            l.SetBounds(x, y, w, 20);
            parent.Controls.Add(l);
            return l;
        }

        // ---------------------------------------------------------- 生命周期

        void OnInit()
        {
            SwitchType();
            BuildTray();

            // 分栏宽度要在控件已经有真实尺寸之后再设，否则会被压缩
            if (_split != null && _split.Width > 600) _split.SplitterDistance = 350;

            tickTimer = new System.Windows.Forms.Timer();
            tickTimer.Interval = 10000;
            tickTimer.Tick += delegate { Tick(); };
            tickTimer.Start();

            RefreshThreads(false);
            ReloadJobs();
            FillLogs();
            Store.Log("启动 host=" + Ssh.Host + " 数据目录=" + Paths.Dir + " 任务数=" + _jobs.Count);
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(1000, "仍在后台运行", "点托盘图标可重新打开；要真正退出请用托盘菜单。", ToolTipIcon.Info);
            }
        }

        void ShowMain()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void SetStatus(string text, bool bad)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, bool>(SetStatus), text, bad); return; }
            lblStatus.Text = text;
            lblStatus.ForeColor = bad ? C_BAD : C_MUTED;
        }

        // ---------------------------------------------------------- 会话

        void RefreshThreads(bool force)
        {
            SetStatus("正在读取远端会话…", false);
            Task.Factory.StartNew(delegate
            {
                var r = Ssh.Run(RemoteScripts.ListThreads(), 60000);
                var list = new List<ThreadInfo>();
                string err = null;

                if (r.Ok && !string.IsNullOrWhiteSpace(r.Out))
                {
                    try
                    {
                        string json = r.Out.Trim();
                        int idx = json.LastIndexOf('\n');
                        if (idx >= 0) json = json.Substring(idx + 1);
                        var dict = Store.Serializer.Deserialize<Dictionary<string, object>>(json);
                        var arr = Store.AsArray(dict["threads"]);
                        foreach (var item in arr)
                        {
                            var o = item as Dictionary<string, object>;
                            if (o == null) continue;
                            var t = new ThreadInfo();
                            t.Id = o.ContainsKey("id") ? Convert.ToString(o["id"]) : "";
                            t.Name = o.ContainsKey("name") ? Convert.ToString(o["name"]) : "";
                            t.Source = o.ContainsKey("source") ? Convert.ToString(o["source"]) : "";
                            t.UpdatedAt = o.ContainsKey("updatedAt") ? Convert.ToInt64(o["updatedAt"]) : 0;
                            t.Tokens = o.ContainsKey("tokens") ? Convert.ToInt64(o["tokens"]) : 0;
                            list.Add(t);
                        }
                    }
                    catch (Exception ex) { err = "解析失败: " + ex.Message; }
                }
                else
                {
                    err = string.IsNullOrEmpty(r.Err) ? ("ssh 退出码 " + r.Code) : r.Err.Trim();
                }

                BeginInvoke(new Action(delegate
                {
                    if (err != null)
                    {
                        Store.Log("读取会话失败: " + Shorten(err, 200));
                        SetStatus("读取会话失败：" + Shorten(err, 80), true);
                    }
                    else
                    {
                        _threads = list;
                        Store.Log("会话列表 OK: " + list.Count + " 个");
                        SetStatus("已连接 " + Ssh.Host + " · " + list.Count + " 个会话", false);
                        FillThreadList();
                    }
                }));
            });
        }

        static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        void FillThreadList()
        {
            if (lvThreads == null) return;
            string q = (txtSearch.Text ?? "").Trim().ToLowerInvariant();
            var checkedIds = new HashSet<string>();
            foreach (ListViewItem it in lvThreads.Items) if (it.Checked) checkedIds.Add(((ThreadInfo)it.Tag).Id);

            lvThreads.BeginUpdate();
            lvThreads.Items.Clear();
            int n = 0;
            foreach (var t in _threads)
            {
                if (q.Length > 0)
                {
                    string hay = ((t.Name ?? "") + " " + (t.Id ?? "")).ToLowerInvariant();
                    if (!hay.Contains(q)) continue;
                }
                if (n++ >= 500) break;
                var it = new ListViewItem(string.IsNullOrEmpty(t.Name) ? t.Id.Substring(0, 8) : t.Name);
                it.SubItems.Add(RelPast(t.UpdatedAt));
                it.Tag = t;
                it.Checked = checkedIds.Contains(t.Id);
                it.ForeColor = C_TEXT;
                lvThreads.Items.Add(it);
            }
            lvThreads.EndUpdate();
            UpdateSelLabel();
        }

        static string RelPast(long epochSec)
        {
            if (epochSec <= 0) return "";
            long nowSec = (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000000L;
            long diff = nowSec - epochSec;
            if (diff < 0) diff = 0;
            if (diff < 60) return "刚刚";
            if (diff < 3600) return (diff / 60) + " 分前";
            if (diff < 86400) return (diff / 3600) + " 小时前";
            return (diff / 86400) + " 天前";
        }

        void SelectVisible(bool on)
        {
            foreach (ListViewItem it in lvThreads.Items) it.Checked = on;
            UpdateSelLabel();
        }

        List<string> SelectedIds()
        {
            var res = new List<string>();
            foreach (ListViewItem it in lvThreads.Items) if (it.Checked) res.Add(((ThreadInfo)it.Tag).Id);
            return res;
        }

        void UpdateSelLabel()
        {
            int n = 0;
            foreach (ListViewItem it in lvThreads.Items) if (it.Checked) n++;
            lblSel.Text = "已选 " + n + " 个会话";
        }

        // ---------------------------------------------------------- 自检：导出控件树

        public string DumpLayout()
        {
            var sb = new StringBuilder();
            sb.AppendLine("FORM " + Text + "  client=" + ClientSize.Width + "x" + ClientSize.Height);
            Walk(this, sb, 0);
            return sb.ToString();
        }

        public int TabCount { get { return tabs == null ? 0 : tabs.TabPages.Count; } }

        public void SelectTab(int i)
        {
            if (tabs != null && i >= 0 && i < tabs.TabPages.Count) tabs.SelectedIndex = i;
        }

        public string DumpPage(int i)
        {
            if (tabs == null || i < 0 || i >= tabs.TabPages.Count) return "";
            var sb = new StringBuilder();
            sb.AppendLine("--- tab[" + i + "] " + tabs.TabPages[i].Text + "  page=" + tabs.TabPages[i].Width + "x" + tabs.TabPages[i].Height);
            Walk(tabs.TabPages[i], sb, 0);
            return sb.ToString();
        }

        static void Walk(Control parent, StringBuilder sb, int depth)
        {
            if (depth > 8) return;
            foreach (Control c in parent.Controls)
            {
                bool interesting = c is Panel || c is ListView || c is DataGridView || c is TabControl
                    || c is TextBox || c is ComboBox || c is DateTimePicker || c is NumericUpDown
                    || c is Button || c is Label || c is SplitContainer || c is TableLayoutPanel
                    || c is TabPage || c is CheckBox;
                if (interesting)
                {
                    string txt = c.Text == null ? "" : c.Text;
                    if (txt.Length > 22) txt = txt.Substring(0, 22) + "…";
                    sb.AppendLine(new string(' ', depth * 2) + c.GetType().Name
                        + " '" + txt + "' bounds=" + c.Bounds.X + "," + c.Bounds.Y + "," + c.Bounds.Width + "x" + c.Bounds.Height
                        + " vis=" + c.Visible);
                }
                Walk(c, sb, depth + 1);
            }
        }

        // ---------------------------------------------------------- 表单

        void SwitchType()
        {
            int i = cbType.SelectedIndex;
            pOnce.Visible = i == 0;
            pDaily.Visible = i == 1;
            pWeekly.Visible = i == 2;
            pInterval.Visible = i == 3;
        }

        Job CollectJob()
        {
            var ids = SelectedIds();
            if (ids.Count == 0) throw new Exception("请先在左侧勾选会话");
            if (string.IsNullOrWhiteSpace(txtMessage.Text)) throw new Exception("发送内容不能为空");

            var j = new Job();
            j.Id = _editingId ?? (DateTime.Now.ToString("yyMMddHHmmss") + new Random().Next(1000, 9999).ToString());
            j.Title = string.IsNullOrWhiteSpace(txtTitle.Text)
                ? (ids.Count + " 个会话 · " + Shorten(txtMessage.Text.Trim(), 12))
                : txtTitle.Text.Trim();
            j.Threads = ids.ToArray();
            j.Message = txtMessage.Text;
            j.Schedule = new Schedule();
            j.MaxRuns = (int)numMaxRuns.Value;
            j.Enabled = true;

            int type = cbType.SelectedIndex;
            if (type == 0)
            {
                j.Schedule.Type = "once";
                j.Schedule.At = dtOnce.Value.ToString("yyyy-MM-ddTHH:mm");
                if (dtOnce.Value <= DateTime.Now.AddMinutes(-1)) throw new Exception("单次时间已经过去了，请重新选");
            }
            else if (type == 1)
            {
                j.Schedule.Type = "daily";
                j.Schedule.Time = dtDailyTime.Value.ToString("HH:mm");
            }
            else if (type == 2)
            {
                j.Schedule.Type = "weekly";
                j.Schedule.Time = dtWeeklyTime.Value.ToString("HH:mm");
                var days = new List<int>();
                foreach (var cb in chkDays) if (cb != null && cb.Checked) days.Add((int)cb.Tag);
                if (days.Count == 0) throw new Exception("请至少选一个星期");
                j.Schedule.Weekdays = days.ToArray();
            }
            else
            {
                j.Schedule.Type = "interval";
                j.Schedule.EveryMinutes = (int)numEvery.Value;
                j.Schedule.StartAt = dtIntervalStart.Checked ? dtIntervalStart.Value.ToString("yyyy-MM-ddTHH:mm") : null;
            }
            j.Schedule.EndAt = dtEnd.Checked ? dtEnd.Value.ToString("yyyy-MM-ddT23:59:59") : null;

            var next = Scheduler.ComputeNextRun(j, DateTime.Now);
            j.NextRun = Scheduler.Iso(next);
            return j;
        }

        void DoSave()
        {
            try
            {
                var j = CollectJob();
                bool editing = _editingId != null;
                if (editing)
                {
                    var old = _jobs.FirstOrDefault(x => x.Id == _editingId);
                    if (old != null)
                    {
                        j.Runs = old.Runs;
                        j.LastRunTs = old.LastRunTs;
                        j.LastOk = old.LastOk;
                        j.LastTotal = old.LastTotal;
                        j.CreatedAt = old.CreatedAt;
                        _jobs[_jobs.IndexOf(old)] = j;
                    }
                    else _jobs.Add(j);
                }
                else
                {
                    j.CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    _jobs.Add(j);
                }
                Store.SaveJobs(_jobs);
                ReloadJobs();
                ExitEdit();
                Store.Log((editing ? "更新任务「" : "新建任务「") + j.Title + "」 下次=" + j.NextRun);
                SetStatus((editing ? "已更新任务：" : "已保存任务：") + Scheduler.Describe(j), false);
                tabs.SelectedIndex = 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void ExitEdit()
        {
            _editingId = null;
            btnSave.Text = "保存任务";
            btnCancelEdit.Visible = false;
        }

        void FillForm(Job job)
        {
            _editingId = job.Id;
            txtTitle.Text = job.Title;
            txtMessage.Text = job.Message;

            foreach (ListViewItem it in lvThreads.Items)
            {
                var t = (ThreadInfo)it.Tag;
                it.Checked = job.Threads != null && job.Threads.Contains(t.Id);
            }
            UpdateSelLabel();

            var s = job.Schedule ?? new Schedule();
            if (s.Type == "daily") cbType.SelectedIndex = 1;
            else if (s.Type == "weekly") cbType.SelectedIndex = 2;
            else if (s.Type == "interval") cbType.SelectedIndex = 3;
            else cbType.SelectedIndex = 0;

            DateTime tmp;
            if (s.Type == "once" && Scheduler.TryParseLocal(s.At, out tmp) && tmp > DateTime.Now) dtOnce.Value = tmp;
            if (!string.IsNullOrEmpty(s.Time) && Scheduler.TryParseLocal(DateTime.Now.ToString("yyyy-MM-dd") + " " + s.Time, out tmp))
            {
                dtDailyTime.Value = tmp;
                dtWeeklyTime.Value = tmp;
            }
            if (s.Weekdays != null)
            {
                var set = new HashSet<int>(s.Weekdays);
                foreach (var cb in chkDays) if (cb != null) cb.Checked = set.Contains((int)cb.Tag);
            }
            if (s.Type == "interval")
            {
                numEvery.Value = Math.Max(1, s.EveryMinutes);
                if (Scheduler.TryParseLocal(s.StartAt, out tmp)) { dtIntervalStart.Value = tmp; dtIntervalStart.Checked = true; }
                else dtIntervalStart.Checked = false;
            }
            if (Scheduler.TryParseLocal(s.EndAt, out tmp)) { dtEnd.Value = tmp; dtEnd.Checked = true; }
            else dtEnd.Checked = false;

            numMaxRuns.Value = Math.Max(0, job.MaxRuns);
            btnSave.Text = "更新任务";
            btnCancelEdit.Visible = true;
            SwitchType();
            tabs.SelectedIndex = 0;
        }

        void DoPreview()
        {
            try
            {
                var ids = SelectedIds();
                if (ids.Count == 0) throw new Exception("请先在左侧勾选会话");
                var sb = new StringBuilder();
                foreach (var id in ids.Take(6))
                    sb.AppendLine("codex queue --thread " + id + " --message \"$msg\" --resume");
                if (ids.Count > 6) sb.AppendLine("… 其余 " + (ids.Count - 6) + " 条同理");
                sb.AppendLine();
                sb.AppendLine("$msg = 「" + Shorten(txtMessage.Text, 60) + "」");
                txtPreview.Text = sb.ToString();
                txtPreview.Visible = true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "预览", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        // ---------------------------------------------------------- 任务列表

        void ReloadJobs()
        {
            _jobs = Store.LoadJobs();
            try { _jobsMtime = File.Exists(Paths.JobsFile) ? File.GetLastWriteTimeUtc(Paths.JobsFile) : DateTime.MinValue; }
            catch { _jobsMtime = DateTime.MinValue; }
            FillJobs();
        }

        void FillJobs()
        {
            if (grid == null) return;
            grid.Rows.Clear();
            var nameById = new Dictionary<string, string>();
            foreach (var t in _threads) nameById[t.Id] = t.Name;

            var ordered = _jobs.OrderBy(x => x.Enabled ? 0 : 1).ThenBy(x => x.NextRun ?? "9999").ToList();
            foreach (var j in ordered)
            {
                var tids = j.Threads ?? new string[0];
                string threads = string.Join("、", tids.Select(id => Shorten(nameById.ContainsKey(id) ? nameById[id] : id.Substring(0, 8), 10)).ToArray());
                if (tids.Length > 2) threads += " …";

                string next = "—";
                DateTime nr;
                if (Scheduler.TryParseLocal(j.NextRun, out nr))
                    next = nr.ToString("MM-dd HH:mm") + " (" + RelFuture(nr) + ")";

                string status;
                if (!j.Enabled) status = "已暂停";
                else if (string.IsNullOrEmpty(j.NextRun)) status = "已完成";
                else status = "已排期";
                if (!string.IsNullOrEmpty(j.LastRunTs))
                    status += " · 上次 " + j.LastOk + "/" + j.LastTotal;

                int idx = grid.Rows.Add(j.Title, threads, Scheduler.Describe(j), next, status);
                grid.Rows[idx].Tag = j;
                if (!j.Enabled) grid.Rows[idx].DefaultCellStyle.ForeColor = C_MUTED;
            }
            lblJobHint.Text = _jobs.Count + " 条任务，其中 " + _jobs.Count(x => x.Enabled) + " 条启用中";
        }

        static string RelFuture(DateTime d)
        {
            var span = d - DateTime.Now;
            if (span.TotalMinutes <= 0) return "即将执行";
            if (span.TotalMinutes < 60) return "还有 " + (int)span.TotalMinutes + " 分";
            if (span.TotalHours < 24) return "还有 " + (int)span.TotalHours + " 小时";
            return "还有 " + (int)span.TotalDays + " 天";
        }

        Job SelectedJob()
        {
            if (grid.SelectedRows.Count == 0) return null;
            return grid.SelectedRows[0].Tag as Job;
        }

        void RunSelected()
        {
            var j = SelectedJob();
            if (j == null) { MessageBox.Show(this, "先在列表里选一条任务", "提示"); return; }
            RunJobAsync(j, "manual");
        }

        void EditSelected()
        {
            var j = SelectedJob();
            if (j == null) return;
            FillForm(j);
        }

        void ToggleSelected()
        {
            var j = SelectedJob();
            if (j == null) return;
            j.Enabled = !j.Enabled;
            if (j.Enabled)
            {
                var n = Scheduler.ComputeNextRun(j, DateTime.Now);
                j.NextRun = Scheduler.Iso(n);
                if (n == null && j.Schedule != null && j.Schedule.Type != "once")
                    MessageBox.Show(this, "这条任务已经到结束条件，启用后不会触发。", "提示");
            }
            Store.SaveJobs(_jobs);
            FillJobs();
        }

        void DeleteSelected()
        {
            var j = SelectedJob();
            if (j == null) return;
            if (MessageBox.Show(this, "删除任务「" + j.Title + "」？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _jobs.Remove(j);
            Store.SaveJobs(_jobs);
            if (_editingId == j.Id) ExitEdit();
            FillJobs();
        }

        // ---------------------------------------------------------- 执行

        void RunJobAsync(Job job, string trigger)
        {
            if (_running)
            {
                MessageBox.Show(this, "还有任务正在发送，稍等几秒。", "提示");
                return;
            }
            _running = true;
            Store.Log("触发任务「" + job.Title + "」 trigger=" + trigger);
            SetStatus("正在发送：" + job.Title + " …", false);

            Task.Factory.StartNew(delegate
            {
                RunEntry entry = null;
                Exception err = null;
                try { entry = Scheduler.Execute(job, trigger); }
                catch (Exception ex) { err = ex; }

                Action done = delegate
                {
                    _running = false;
                    if (err != null)
                    {
                        SetStatus("发送失败：" + err.Message, true);
                        tray.ShowBalloonTip(2500, "发送失败", job.Title + "：" + err.Message, ToolTipIcon.Error);
                        return;
                    }
                    job.Runs = job.Runs + 1;
                    job.LastRunTs = entry.Ts;
                    job.LastOk = entry.OkCount;
                    job.LastTotal = entry.Total;
                    var n = Scheduler.ComputeNextRun(job, DateTime.Now);
                    job.NextRun = Scheduler.Iso(n);
                    if ((job.Schedule != null && job.Schedule.Type == "once") || n == null) job.Enabled = false;
                    Store.SaveJobs(_jobs);

                    bool allOk = entry.OkCount == entry.Total;
                    Store.Log("执行「" + job.Title + "」 " + entry.Trigger + " " + entry.OkCount + "/" + entry.Total);
                    SetStatus("发送完成：" + entry.OkCount + "/" + entry.Total + " 成功", !allOk);
                    tray.ShowBalloonTip(2500, "发送完成", job.Title + "：" + entry.OkCount + "/" + entry.Total + " 成功",
                        allOk ? ToolTipIcon.Info : ToolTipIcon.Warning);
                    FillJobs();
                    FillLogs();
                };

                try { BeginInvoke(done); }
                catch { }
            });
        }

        void Tick()
        {
            try
            {
                // 外部改动（或另一实例保存）时自动重载
                try
                {
                    var mt = File.Exists(Paths.JobsFile) ? File.GetLastWriteTimeUtc(Paths.JobsFile) : DateTime.MinValue;
                    if (mt != _jobsMtime && !_running)
                    {
                        ReloadJobs();
                        Store.Log("检测到 jobs.json 变化，已重载（" + _jobs.Count + " 条）");
                    }
                }
                catch { }

                bool changed = false;
                foreach (var j in _jobs.ToList())
                {
                    if (!j.Enabled) continue;
                    DateTime next;
                    if (!Scheduler.TryParseLocal(j.NextRun, out next))
                    {
                        var n = Scheduler.ComputeNextRun(j, DateTime.Now);
                        j.NextRun = Scheduler.Iso(n);
                        changed = true;
                        continue;
                    }
                    if (next <= DateTime.Now)
                    {
                        if (_running) return;
                        RunJobAsync(j, "schedule");
                        return;
                    }
                }
                if (changed) { Store.SaveJobs(_jobs); FillJobs(); }
            }
            catch { }
        }

        // ---------------------------------------------------------- 日志

        void FillLogs()
        {
            if (lvLogs == null) return;
            var entries = Store.ReadLog(60);
            lvLogs.BeginUpdate();
            lvLogs.Items.Clear();
            foreach (var e in entries)
            {
                DateTime ts;
                string when = Scheduler.TryParseLocal(e.Ts, out ts) ? ts.ToString("MM-dd HH:mm:ss") : (e.Ts ?? "");
                var it = new ListViewItem(when);
                it.SubItems.Add(Shorten(e.JobTitle ?? "", 28));
                it.SubItems.Add(e.OkCount + "/" + e.Total + (e.OkCount == e.Total ? " 成功" : " 有失败"));
                it.SubItems.Add(e.Trigger == "manual" ? "手动" : "定时");
                it.SubItems.Add(Shorten(e.Message ?? "", 40));
                it.Tag = e;
                it.ForeColor = e.OkCount == e.Total ? C_TEXT : Color.FromArgb(255, 185, 185);
                lvLogs.Items.Add(it);
            }
            lvLogs.EndUpdate();
        }

        void ShowLogDetail()
        {
            if (lvLogs.SelectedItems.Count == 0) return;
            var e = lvLogs.SelectedItems[0].Tag as RunEntry;
            if (e == null) return;
            using (var f = new Form())
            {
                f.Text = "执行详情 · " + (e.JobTitle ?? "");
                f.Size = new Size(780, 540);
                f.StartPosition = FormStartPosition.CenterParent;
                f.BackColor = C_PANEL;
                f.ForeColor = C_TEXT;
                var box = new TextBox();
                box.Multiline = true;
                box.ReadOnly = true;
                box.Dock = DockStyle.Fill;
                box.ScrollBars = ScrollBars.Both;
                box.WordWrap = false;
                box.BackColor = C_INPUT;
                box.ForeColor = C_TEXT;
                box.Font = new Font("Consolas", 9F);
                box.Text = e.Detail ?? "";
                f.Controls.Add(box);
                f.ShowDialog(this);
            }
        }
    }

    // ============================================================ 入口

    public static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Any(a => a == "--selftest")) return SelfTest();
                if (args.Any(a => a == "--layout")) return LayoutTest();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("TYPE: " + ex.GetType().FullName);
                    sb.AppendLine("MSG : " + ex.Message);
                    sb.AppendLine("TRACE:");
                    sb.AppendLine(ex.StackTrace);
                    Exception inner = ex.InnerException;
                    int depth = 0;
                    while (inner != null && depth < 5)
                    {
                        sb.AppendLine("---- inner " + depth);
                        sb.AppendLine("TYPE: " + inner.GetType().FullName);
                        sb.AppendLine("MSG : " + inner.Message);
                        sb.AppendLine(inner.StackTrace);
                        inner = inner.InnerException;
                        depth++;
                    }
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "codex-scheduler-crash.txt"), sb.ToString(), new UTF8Encoding(false));
                }
                catch { }
                return 99;
            }
        }

        static int LayoutTest()
        {
            var f = new MainForm();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-5000, -5000);
            f.Show();
            Application.DoEvents();
            Thread.Sleep(1500);
            Application.DoEvents();
            var sb = new StringBuilder();
            sb.AppendLine(f.DumpLayout());
            for (int i = 0; i < f.TabCount; i++)
            {
                f.SelectTab(i);
                Application.DoEvents();
                Thread.Sleep(300);
                Application.DoEvents();
                sb.AppendLine(f.DumpPage(i));
            }
            f.Hide();
            string dump = sb.ToString();
            try
            {
                File.WriteAllText(Path.Combine(Paths.Dir, "layout.txt"), dump, new UTF8Encoding(false));
                AttachConsole(-1);
                var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                w.AutoFlush = true;
                w.Write(dump);
            }
            catch { }
            return 0;
        }

        static int SelfTest()
        {
            int fail = 0;
            var sb = new StringBuilder();

            var st = Store.LoadSettings();
            Ssh.Host = st.SshHost;
            sb.AppendLine("配置  host=" + Ssh.Host + "  data=" + Paths.Dir);

            var now = new DateTime(2026, 11, 27, 19, 55, 0);

            var j1 = new Job(); j1.Schedule = new Schedule(); j1.Schedule.Type = "daily"; j1.Schedule.Time = "02:00";
            var n1 = Scheduler.ComputeNextRun(j1, now);
            bool ok1 = n1.HasValue && n1.Value == new DateTime(2026, 11, 28, 2, 0, 0);
            sb.AppendLine("daily 02:00 -> " + (n1.HasValue ? n1.Value.ToString("yyyy-MM-dd HH:mm") : "null") + "  " + (ok1 ? "PASS" : "FAIL"));
            if (!ok1) fail++;

            var j2 = new Job(); j2.Schedule = new Schedule(); j2.Schedule.Type = "weekly"; j2.Schedule.Time = "02:00"; j2.Schedule.Weekdays = new int[] { 1, 3 };
            var n2 = Scheduler.ComputeNextRun(j2, now);
            bool ok2 = n2.HasValue && n2.Value.DayOfWeek == DayOfWeek.Monday && n2.Value.Hour == 2;
            sb.AppendLine("weekly Mon/Wed 02:00 -> " + (n2.HasValue ? n2.Value.ToString("yyyy-MM-dd HH:mm") : "null") + "  " + (ok2 ? "PASS" : "FAIL"));
            if (!ok2) fail++;

            var j3 = new Job(); j3.Schedule = new Schedule(); j3.Schedule.Type = "interval"; j3.Schedule.EveryMinutes = 90; j3.Schedule.StartAt = "2026-11-27T18:00";
            var n3 = Scheduler.ComputeNextRun(j3, now);
            bool ok3 = n3.HasValue && n3.Value == new DateTime(2026, 11, 27, 21, 0, 0);
            sb.AppendLine("interval 90m from 18:00 -> " + (n3.HasValue ? n3.Value.ToString("yyyy-MM-dd HH:mm") : "null") + "  " + (ok3 ? "PASS" : "FAIL"));
            if (!ok3) fail++;

            var j4 = new Job(); j4.Schedule = new Schedule(); j4.Schedule.Type = "once"; j4.Schedule.At = "2020-01-01T00:00";
            bool ok4 = Scheduler.ComputeNextRun(j4, now) == null;
            sb.AppendLine("once in the past -> " + (ok4 ? "null PASS" : "not null FAIL"));
            if (!ok4) fail++;

            var r = Ssh.Run(RemoteScripts.ListThreads(), 60000);
            if (r.Ok)
            {
                int count = 0;
                try
                {
                    string json = r.Out.Trim();
                    int idx = json.LastIndexOf('\n');
                    if (idx >= 0) json = json.Substring(idx + 1);
                    var d = Store.Serializer.Deserialize<Dictionary<string, object>>(json);
                    count = Store.AsArray(d["threads"]).Length;
                }
                catch (Exception ex) { sb.AppendLine("解析失败: " + ex.Message); }
                sb.AppendLine("remote threads -> " + count + "  " + (count > 0 ? "PASS" : "FAIL"));
                if (count == 0) fail++;
            }
            else
            {
                sb.AppendLine("remote read failed: " + (r.Err ?? "").Trim());
                fail++;
            }

            try
            {
                var jobs = Store.LoadJobs();
                Store.SaveJobs(jobs);
                sb.AppendLine("jobs file -> " + Paths.JobsFile + "  PASS");
            }
            catch (Exception ex)
            {
                sb.AppendLine("jobs file -> FAIL " + ex.Message);
                fail++;
            }

            sb.AppendLine(fail == 0 ? "SELFTEST OK" : ("SELFTEST FAILED: " + fail));

            try
            {
                File.WriteAllText(Path.Combine(Paths.Dir, "selftest.log"), sb.ToString(), new UTF8Encoding(false));
                AttachConsole(-1);
                var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                w.AutoFlush = true;
                w.Write(sb.ToString());
            }
            catch { }

            return fail == 0 ? 0 : 1;
        }
    }
}
