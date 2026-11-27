using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CodexScheduler
{
    public static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        static Mutex _mutex;
        static DateTime _lastUiError = DateTime.MinValue;

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Theme.InitScale();

                if (args.Any(a => a == "--selftest")) return SelfTest();
                if (args.Any(a => a == "--layout")) return LayoutTest();

                bool startHidden = args.Any(a => a == "--tray" || a == "--minimized");

                // 单实例：同一个数据目录只允许一个进程在调度，否则每条任务会被发送多次
                string key = "CodexScheduler-" + Fnv(Paths.Dir.ToLowerInvariant());
                bool created;
                _mutex = new Mutex(true, "Local\\" + key, out created);
                if (!created)
                {
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting("Local\\" + key + "-show")) ev.Set();
                    }
                    catch { }
                    return 0;
                }
                var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + key + "-show");

                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { OnUiError(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { WriteCrash(e.ExceptionObject as Exception); };

                var form = new MainForm(startHidden, false);
                ThreadPool.RegisterWaitForSingleObject(showEvt, delegate { form.ShowFromOtherInstance(); }, null, -1, false);
                Application.Run(form);

                GC.KeepAlive(showEvt);
                GC.KeepAlive(_mutex);
                return 0;
            }
            catch (Exception ex)
            {
                WriteCrash(ex);
                return 99;
            }
        }

        static string Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }

        /// <summary>界面线程上的意外异常：记日志、提示一次，不让整个程序崩掉</summary>
        static void OnUiError(Exception ex)
        {
            try { Store.Log("界面异常：" + ex); } catch { }
            if ((DateTime.Now - _lastUiError).TotalSeconds < 30) return;
            _lastUiError = DateTime.Now;
            try
            {
                MessageBox.Show("出现了一个意外错误，程序会继续运行：\n\n" + ex.Message + "\n\n详情已记录到 data\\app.log",
                    "Codex 定时发送台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        static void WriteCrash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(ex.ToString());
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "codex-scheduler-crash.txt"), sb.ToString(), new UTF8Encoding(false));
                Store.Log("崩溃：" + ex);
            }
            catch { }
        }

        static void WriteOut(string text, string file)
        {
            try
            {
                File.WriteAllText(Path.Combine(Paths.Dir, file), text, new UTF8Encoding(false));
                AttachConsole(-1);
                var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                w.AutoFlush = true;
                w.Write(text);
            }
            catch { }
        }

        // ---------------------------------------------------------- --layout

        static int LayoutTest()
        {
            var f = new MainForm(false, true); // passive：不调度、不建托盘
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-5000, -5000);
            f.Show();
            Application.DoEvents();
            Thread.Sleep(1200);
            Application.DoEvents();
            var sb = new StringBuilder();
            sb.AppendLine(f.DumpLayout());
            for (int i = 0; i < f.PageCount; i++)
            {
                f.SelectPage(i);
                Application.DoEvents();
                Thread.Sleep(300);
                Application.DoEvents();
                sb.AppendLine(f.DumpPage(i));
            }
            f.Hide();
            WriteOut(sb.ToString(), "layout.txt");
            return 0;
        }

        // ---------------------------------------------------------- --selftest

        static int _fail;
        static StringBuilder _sb;

        static void Check(string name, bool ok, string detail)
        {
            _sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : "  -> " + detail));
            if (!ok) _fail++;
        }

        static string Fmt(DateTime? d) { return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "null"; }

        static Job MakeJob(string type)
        {
            var j = new Job();
            j.Schedule = new Schedule();
            j.Schedule.Type = type;
            return j;
        }

        static int SelfTest()
        {
            _fail = 0;
            _sb = new StringBuilder();
            var st = Store.LoadSettings();
            Ssh.Host = st.SshHost;
            _sb.AppendLine("配置  host=" + Ssh.Host + "  data=" + Paths.Dir);

            var now = new DateTime(2026, 11, 27, 19, 55, 0); // 周五

            var j1 = MakeJob("daily"); j1.Schedule.Time = "02:00";
            var n1 = Scheduler.ComputeNextRun(j1, now);
            Check("daily 02:00", n1 == new DateTime(2026, 11, 28, 2, 0, 0), Fmt(n1));

            var j2 = MakeJob("weekly"); j2.Schedule.Time = "02:00"; j2.Schedule.Weekdays = new int[] { 1, 3 };
            var n2 = Scheduler.ComputeNextRun(j2, now);
            Check("weekly 一/三 02:00", n2 == new DateTime(2026, 11, 30, 2, 0, 0), Fmt(n2));

            var j2b = MakeJob("weekly"); j2b.Schedule.Time = "21:00"; j2b.Schedule.Weekdays = new int[] { 5 };
            var n2b = Scheduler.ComputeNextRun(j2b, now);
            Check("weekly 当天稍后", n2b == new DateTime(2026, 11, 27, 21, 0, 0), Fmt(n2b));

            var j3 = MakeJob("interval"); j3.Schedule.EveryMinutes = 90; j3.Schedule.StartAt = "2026-11-27T18:00";
            var n3 = Scheduler.ComputeNextRun(j3, now);
            Check("interval 90m from 18:00", n3 == new DateTime(2026, 11, 27, 21, 0, 0), Fmt(n3));

            var j4 = MakeJob("once"); j4.Schedule.At = "2020-01-01T00:00";
            Check("once 已过去很久 -> null", Scheduler.ComputeNextRun(j4, now) == null, null);

            var j4b = MakeJob("once"); j4b.Schedule.At = "2026-11-27T19:30";
            Check("once 错过 25 分钟仍补发", Scheduler.ComputeNextRun(j4b, now) == new DateTime(2026, 11, 27, 19, 30, 0), null);
            Check("once 错过 3 天应跳过", Scheduler.SkipReason(j4b, new DateTime(2026, 11, 24, 19, 30, 0), now) != null, null);
            Check("once 错过 10 分钟不跳过", Scheduler.SkipReason(j4b, now.AddMinutes(-10), now) == null, null);
            j4b.Runs = 1;
            Check("once 已执行过 -> null", Scheduler.ComputeNextRun(j4b, now) == null, null);

            // 截止日期：以前只比较“现在”，截止后还会多发一次
            var j5 = MakeJob("daily"); j5.Schedule.Time = "02:00"; j5.Schedule.EndAt = "2026-11-27T23:59:59";
            var n5 = Scheduler.ComputeNextRun(j5, now);
            Check("daily 截止今天 -> 不再有下一次", n5 == null, Fmt(n5));

            var j6 = MakeJob("interval"); j6.Schedule.EveryMinutes = 60; j6.Schedule.StartAt = "2026-11-27T18:00"; j6.Schedule.EndAt = "2026-11-27T20:30:00";
            Check("interval 截止前", Scheduler.ComputeNextRun(j6, now) == new DateTime(2026, 11, 27, 20, 0, 0), null);
            Check("interval 截止后", Scheduler.ComputeNextRun(j6, new DateTime(2026, 11, 27, 20, 10, 0)) == null, null);
            Check("SkipReason 超过截止", Scheduler.SkipReason(j6, new DateTime(2026, 11, 27, 21, 0, 0), now) != null, null);

            var j7 = MakeJob("daily"); j7.Schedule.Time = "02:00"; j7.MaxRuns = 3; j7.Runs = 3;
            Check("最多 3 次已满 -> null", Scheduler.ComputeNextRun(j7, now) == null, null);

            Check("线程 ID 合法", Ssh.IsValidThreadId("019a2b3c-4d5e-7f00-abcd-0123456789ab"), null);
            Check("线程 ID 注入被拒", !Ssh.IsValidThreadId("abc'; rm -rf ~; '"), null);
            Check("主机名合法", Ssh.IsValidHost("cad-3-5090-fgy") && Ssh.IsValidHost("user@10.0.0.2"), null);
            Check("主机名注入被拒", !Ssh.IsValidHost("-oProxyCommand=calc") && !Ssh.IsValidHost("a b") && !Ssh.IsValidHost("a&calc"), null);
            Check("识别本机主机名", LocalShell.IsLocal("local") && LocalShell.IsLocal(" 本机 ") && !LocalShell.IsLocal("cad-3-5090-fgy"), null);
            Check("本机主机名可保存", Ssh.IsValidHost("local"), null);
            _sb.AppendLine("INFO  本机组件：" + LocalShell.Describe());

            try
            {
                string src;
                var parsed = RemoteScripts.ParseThreads("Welcome banner\n{\"source\": \"state_5.sqlite\", \"threads\": [{\"id\": \"abc\", \"name\": \"测试\", \"updatedAt\": 1, \"source\": \"cli\", \"tokens\": 2}]}\n", out src);
                Check("解析会话（带登录横幅）", parsed.Count == 1 && parsed[0].Name == "测试" && src == "state_5.sqlite", null);
            }
            catch (Exception ex) { Check("解析会话（带登录横幅）", false, ex.Message); }

            try
            {
                var jj = MakeJob("daily"); jj.Id = "x1"; jj.Host = "h1"; jj.Finished = true; jj.Retry = 2; jj.Threads = new string[] { "a", "b" };
                string json = Store.Serializer.Serialize(new List<Job> { jj });
                var back = Store.Serializer.Deserialize<List<Job>>(json);
                Check("任务 JSON 往返", back.Count == 1 && back[0].Host == "h1" && back[0].Finished && back[0].Retry == 2 && back[0].Threads.Length == 2, null);
                var legacy = Store.Serializer.Deserialize<List<Job>>("[{\"Id\":\"old\",\"Title\":\"t\",\"Threads\":[\"a\"],\"Enabled\":true}]");
                Check("兼容老版 jobs.json", legacy.Count == 1 && legacy[0].Host == null && !legacy[0].Finished, null);
            }
            catch (Exception ex) { Check("任务 JSON 往返", false, ex.Message); }

            var r = Ssh.Run(Ssh.Host, RemoteScripts.ListThreadsFor(Ssh.Host), 60000);
            if (r.Ok)
            {
                try
                {
                    string src;
                    var list = RemoteScripts.ParseThreads(r.Out, out src);
                    Check("远端会话列表", list.Count > 0, list.Count + " 个");
                }
                catch (Exception ex) { Check("远端会话列表", false, ex.Message); }
            }
            else Check("远端连接", false, (r.Err ?? "").Trim());

            try
            {
                string err;
                var jobs = Store.LoadJobs(out err);
                if (jobs == null) Check("jobs 文件可读", false, err);
                else
                {
                    Store.SaveJobs(jobs);
                    Check("jobs 文件读写", true, Paths.JobsFile + "（" + jobs.Count + " 条）");
                }
            }
            catch (Exception ex) { Check("jobs 文件读写", false, ex.Message); }

            _sb.AppendLine(_fail == 0 ? "SELFTEST OK" : ("SELFTEST FAILED: " + _fail));
            WriteOut(_sb.ToString(), "selftest.log");
            return _fail == 0 ? 0 : 1;
        }
    }
}
