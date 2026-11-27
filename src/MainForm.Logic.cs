using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexScheduler
{
    public enum StatusKind { Info, Busy, Ok, Warn, Bad }

    public partial class MainForm
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        Settings _settings;
        List<ThreadInfo> _threads = new List<ThreadInfo>();
        readonly HashSet<string> _checked = new HashSet<string>();
        List<Job> _jobs = new List<Job>();
        DateTime _jobsMtime = DateTime.MinValue;
        DateTime _brokenMtime = DateTime.MinValue;
        string _editingId;
        string _runningJobId;
        bool _exiting, _running, _filling, _loadingForm, _inited, _layoutDone, _logSplitDone;
        readonly bool _startHidden, _passive;
        int _refreshSeq, _page;
        System.Windows.Forms.Timer tickTimer;

        /// <param name="startHidden">开机自启时只进托盘，不弹窗口</param>
        /// <param name="passive">--layout 自检用：不启动调度、不建托盘，避免和正在运行的实例重复发送</param>
        public MainForm(bool startHidden, bool passive)
        {
            _startHidden = startHidden;
            _passive = passive;
            _settings = Store.LoadSettings();
            Ssh.Host = Ssh.IsValidHost(_settings.SshHost) ? _settings.SshHost : new Settings().SshHost;

            Text = "Codex 定时发送台";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            AutoScaleMode = AutoScaleMode.None; // 尺寸已经按 DPI 手动换算
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(S(1280), wa.Width - 40), Math.Min(S(820), wa.Height - 40));
            MinimumSize = new Size(Math.Min(S(960), wa.Width), Math.Min(S(620), wa.Height));

            _tip = new ToolTip();
            _tip.AutoPopDelay = 15000;
            _tip.InitialDelay = 400;

            SuspendLayout();
            BuildAll();
            ResumeLayout(true);

            Resize += delegate { AdaptHeader(); };
            Load += delegate { OnFirstShow(); };
            Shown += delegate { OnFirstShow(); };
            FormClosing += OnClosing;
            FormClosed += OnClosed;
        }

        // ---------------------------------------------------------- 生命周期

        protected override void SetVisibleCore(bool value)
        {
            if (!_inited)
            {
                _inited = true;
                if (!IsHandleCreated) CreateHandle();
                OnInit();
                if (_startHidden) value = false;
            }
            base.SetVisibleCore(value);
        }

        void OnInit()
        {
            if (!_passive)
            {
                BuildTray();
                AutoStart.Repair();
            }
            RebuildPresets();
            ResetForm();
            SwitchPage(0);
            ApplyPausedUi();
            AdaptHeader();
            ReloadJobs(true);
            FillLogs();
            UpdateSelLabel();

            if (!_passive)
            {
                tickTimer = new System.Windows.Forms.Timer();
                tickTimer.Interval = 10000;
                tickTimer.Tick += delegate { Tick(); };
                tickTimer.Start();
            }

            RefreshThreads();
            Store.Log("启动 host=" + Ssh.Host + " 数据目录=" + Paths.Dir + " 任务数=" + _jobs.Count + (_startHidden ? "（后台）" : ""));
        }

        void OnFirstShow()
        {
            if (_layoutDone) return;
            _layoutDone = true;
            // 先设距离再设最小宽度，顺序反了会抛异常
            try { _split.SplitterDistance = S(330); } catch { }
            try { _split.Panel1MinSize = S(240); } catch { }
            try { _split.Panel2MinSize = S(440); } catch { }
            FitThreadColumns();
            AdaptHeader();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!_exiting && !_passive && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_settings.TrayHintShown && tray != null)
                {
                    tray.ShowBalloonTip(4000, "仍在后台运行", "定时任务会照常执行。单击托盘图标重新打开，右键托盘图标可以退出。", ToolTipIcon.Info);
                    _settings.TrayHintShown = true;
                    Store.SaveSettings(_settings);
                }
            }
        }

        void OnClosed(object sender, FormClosedEventArgs e)
        {
            try { if (tickTimer != null) tickTimer.Stop(); } catch { }
            try
            {
                // 不主动隐藏的话，托盘里会残留一个“幽灵”图标直到鼠标划过
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            }
            catch { }
            Store.Log("退出");
        }

        void ExitApp()
        {
            if (_running && !Ask("有任务正在发送，现在退出会中断发送。\n\n确定退出？")) return;
            _exiting = true;
            Close();
        }

        public void ShowMain()
        {
            if (IsDisposed) return;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        /// <summary>另一个实例启动时通知本实例显示窗口（在线程池线程上调用）</summary>
        public void ShowFromOtherInstance()
        {
            UI(ShowMain);
        }

        void UI(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.S:
                case Keys.Control | Keys.Enter:
                    if (_page == 0) { DoSave(); return true; }
                    break;
                case Keys.Control | Keys.N:
                    NewJob(); return true;
                case Keys.Control | Keys.D1: SwitchPage(0); return true;
                case Keys.Control | Keys.D2: SwitchPage(1); return true;
                case Keys.Control | Keys.D3: SwitchPage(2); return true;
                case Keys.Control | Keys.F:
                    txtSearch.Focus(); txtSearch.SelectAll(); return true;
                case Keys.F5:
                    RefreshThreads();
                    if (_page == 2) FillLogs();
                    return true;
                case Keys.Enter:
                    if (grid.Focused && _page == 1) { EditSelected(false); return true; }
                    break;
                case Keys.Delete:
                    if (grid.Focused && _page == 1) { DeleteSelected(); return true; }
                    break;
                case Keys.Space:
                    if (grid.Focused && _page == 1) { ToggleSelected(); return true; }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------------------------------------------------- 提示

        IWin32Window DialogOwner { get { return Visible ? this : null; } }

        void Info(string m) { MessageBox.Show(DialogOwner, m, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        void Warn(string m, string title) { MessageBox.Show(DialogOwner, m, title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        bool Ask(string m) { return MessageBox.Show(DialogOwner, m, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }

        string _statusText = "";
        bool _narrow;

        /// <summary>窗口较窄时收起副标题、缩短状态文字，保证右侧的主机输入框和按钮始终完整可见</summary>
        void AdaptHeader()
        {
            if (_subtitle == null || lblStatus == null) return;
            bool narrow = ClientSize.Width < S(1200);
            if (narrow == _narrow && ClientSize.Width > 0) return;
            _narrow = narrow;
            _subtitle.Visible = !narrow;
            lblStatus.MaximumSize = new Size(narrow ? S(300) : S(460), S(34));
            lblStatus.Text = "● " + Ui.Shorten(_statusText, narrow ? 20 : 34);
        }

        void SetStatus(string text, StatusKind kind)
        {
            if (InvokeRequired) { UI(delegate { SetStatus(text, kind); }); return; }
            if (lblStatus == null) return;
            _statusText = text ?? "";
            lblStatus.Text = "● " + Ui.Shorten(_statusText, _narrow ? 20 : 34);
            _tip.SetToolTip(lblStatus, text);
            switch (kind)
            {
                case StatusKind.Ok: lblStatus.ForeColor = Theme.Ok; lblStatus.BackColor = Theme.OkSoft; break;
                case StatusKind.Bad: lblStatus.ForeColor = Theme.Bad; lblStatus.BackColor = Theme.BadSoft; break;
                case StatusKind.Warn: lblStatus.ForeColor = Theme.Warn; lblStatus.BackColor = Theme.WarnSoft; break;
                case StatusKind.Busy: lblStatus.ForeColor = Theme.Accent; lblStatus.BackColor = Theme.AccentSoft; break;
                default: lblStatus.ForeColor = Theme.Muted; lblStatus.BackColor = Theme.Hover; break;
            }
        }

        // ---------------------------------------------------------- 页面

        void SwitchPage(int i)
        {
            if (_pages == null || i < 0 || i >= _pages.Length) return;
            _page = i;
            for (int k = 0; k < _pages.Length; k++)
            {
                _navs[k].Selected = k == i;
                _pages[k].Visible = k == i;
            }
            if (i == 1) FillJobs();
            if (i == 2)
            {
                FillLogs();
                if (!_logSplitDone && _logSplit.Height > S(200))
                {
                    _logSplitDone = true;
                    try { _logSplit.SplitterDistance = (int)(_logSplit.Height * 0.55); } catch { }
                    try { _logSplit.Panel1MinSize = S(80); } catch { }
                    try { _logSplit.Panel2MinSize = S(60); } catch { }
                }
            }
        }

        // ---------------------------------------------------------- 主机 / 调度开关

        void ApplyHost(bool forceRefresh)
        {
            string h = (txtHost.Text ?? "").Trim();
            if (h.Length == 0) { txtHost.Text = Ssh.Host; return; }
            if (h != Ssh.Host)
            {
                if (!Ssh.IsValidHost(h))
                {
                    SetStatus("主机名不合法：只能包含字母、数字和 . _ - @ :", StatusKind.Bad);
                    txtHost.Text = Ssh.Host;
                    return;
                }
                Ssh.Host = h;
                _settings.SshHost = h;
                Store.SaveSettings(_settings);
                Store.Log("切换主机 " + h);
                RefreshThreads();
                FillJobs();
                UpdateSummary();
                return;
            }
            if (forceRefresh) RefreshThreads();
        }

        void SetPaused(bool paused)
        {
            _settings.Paused = paused;
            Store.SaveSettings(_settings);
            Store.Log(paused ? "暂停调度" : "恢复调度");
            ApplyPausedUi();
            FillJobs();
            SetStatus(paused ? "已暂停所有定时任务（手动执行不受影响）" : "已恢复定时调度", paused ? StatusKind.Warn : StatusKind.Ok);
            if (!paused) Tick();
        }

        void ApplyPausedUi()
        {
            bool p = _settings.Paused;
            btnPause.Text = p ? "恢复调度" : "暂停调度";
            Ui.Style(btnPause, p ? BtnKind.Primary : BtnKind.Secondary);
            if (p) { btnPause.BackColor = Theme.Warn; btnPause.FlatAppearance.BorderColor = Theme.Warn; btnPause.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 83, 9); }
            if (trayPause != null) trayPause.Checked = p;
            if (tray != null) tray.Text = p ? "Codex 定时发送台（调度已暂停）" : "Codex 定时发送台";
        }

        void OpenDataDir()
        {
            try { Process.Start("explorer.exe", "\"" + Paths.Dir + "\""); }
            catch (Exception ex) { Warn("打不开目录：" + ex.Message, "提示"); }
        }

        // ---------------------------------------------------------- 会话

        void RefreshThreads()
        {
            int seq = ++_refreshSeq;
            string host = Ssh.Host;
            SetStatus("正在读取 " + host + " 的会话…", StatusKind.Busy);
            Task.Factory.StartNew(delegate
            {
                var r = Ssh.Run(host, RemoteScripts.ListThreads(), 60000);
                List<ThreadInfo> list = null;
                string err = null;
                if (r.Ok)
                {
                    try
                    {
                        string src;
                        list = RemoteScripts.ParseThreads(r.Out, out src);
                        if (src == null && list.Count == 0)
                            err = "远端读不到 ~/.codex/state_5.sqlite" + (string.IsNullOrWhiteSpace(r.Err) ? "" : "：" + r.Err.Trim());
                    }
                    catch (Exception ex) { err = "解析失败：" + ex.Message; }
                }
                else
                {
                    err = r.TimedOut ? "连接超时" : (string.IsNullOrWhiteSpace(r.Err) ? ("ssh 退出码 " + r.Code) : r.Err.Trim());
                }

                UI(delegate
                {
                    if (seq != _refreshSeq) return; // 已经有更新的一次刷新（比如中途换了主机）
                    if (err != null)
                    {
                        Store.Log("读取会话失败（" + host + "）：" + Ui.Shorten(err, 300));
                        SetStatus("连不上 " + host + "：" + err, StatusKind.Bad);
                        return;
                    }
                    _threads = list;
                    Store.Log("会话列表 OK（" + host + "）：" + list.Count + " 个");
                    SetStatus("已连接 " + host + " · " + list.Count + " 个会话", StatusKind.Ok);
                    FillThreadList();
                    FillJobs();
                    UpdateSummary();
                });
            });
        }

        void FillThreadList()
        {
            if (lvThreads == null) return;
            string q = (txtSearch.Text ?? "").Trim().ToLowerInvariant();
            bool onlySel = chkOnlySel.Checked;
            var known = new HashSet<string>(_threads.Select(t => t.Id));

            _filling = true;
            lvThreads.BeginUpdate();
            try
            {
                lvThreads.Items.Clear();

                // 已勾选但不在当前列表里的会话（例如编辑的任务属于已归档会话）也显示出来，
                // 以前它们会在保存时被悄悄丢掉
                foreach (var id in _checked.Where(x => !known.Contains(x)).ToList())
                {
                    if (q.Length > 0 && !id.ToLowerInvariant().Contains(q)) continue;
                    var it = new ListViewItem("（不在列表）" + Scheduler.Short(id));
                    it.SubItems.Add("");
                    it.Tag = id;
                    it.Checked = true;
                    it.ForeColor = Theme.Warn;
                    it.ToolTipText = id + "\r\n当前主机的会话列表里没有它（可能已归档，或属于别的主机）";
                    lvThreads.Items.Add(it);
                }

                int n = 0;
                foreach (var t in _threads)
                {
                    bool isChecked = _checked.Contains(t.Id);
                    if (onlySel && !isChecked) continue;
                    if (q.Length > 0)
                    {
                        string hay = ((t.Name ?? "") + " " + (t.Id ?? "")).ToLowerInvariant();
                        if (!hay.Contains(q)) continue;
                    }
                    if (n++ >= 1000) break;
                    var it = new ListViewItem(string.IsNullOrEmpty(t.Name) ? Scheduler.Short(t.Id) : t.Name);
                    it.SubItems.Add(RelPast(t.UpdatedAt));
                    it.Tag = t.Id;
                    it.Checked = isChecked;
                    it.ToolTipText = (t.Name ?? "") + "\r\n" + t.Id + (string.IsNullOrEmpty(t.Source) ? "" : "\r\n来源：" + t.Source);
                    lvThreads.Items.Add(it);
                }
            }
            finally
            {
                lvThreads.EndUpdate();
                _filling = false;
            }
            UpdateSelLabel();
        }

        void OnThreadChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_filling) return;
            var id = e.Item.Tag as string;
            if (id == null) return;
            if (e.Item.Checked) _checked.Add(id); else _checked.Remove(id);
            UpdateSelLabel();
            UpdateSummary();
        }

        void SelectVisible(bool on)
        {
            _filling = true;
            lvThreads.BeginUpdate();
            try
            {
                foreach (ListViewItem it in lvThreads.Items)
                {
                    var id = it.Tag as string;
                    if (id == null) continue;
                    it.Checked = on;
                    if (on) _checked.Add(id); else _checked.Remove(id);
                }
            }
            finally { lvThreads.EndUpdate(); _filling = false; }
            UpdateSelLabel();
            UpdateSummary();
        }

        void CheckSelectedThreads(bool on)
        {
            _filling = true;
            try
            {
                foreach (ListViewItem it in lvThreads.SelectedItems)
                {
                    var id = it.Tag as string;
                    if (id == null) continue;
                    it.Checked = on;
                    if (on) _checked.Add(id); else _checked.Remove(id);
                }
            }
            finally { _filling = false; }
            UpdateSelLabel();
            UpdateSummary();
        }

        void ClearChecked()
        {
            _checked.Clear();
            FillThreadList();
            UpdateSummary();
        }

        void CopyThreadIds()
        {
            var ids = new List<string>();
            foreach (ListViewItem it in lvThreads.SelectedItems) { var id = it.Tag as string; if (id != null) ids.Add(id); }
            if (ids.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join("\r\n", ids.ToArray()));
                SetStatus("已复制 " + ids.Count + " 个会话 ID", StatusKind.Ok);
            }
            catch { }
        }

        void UpdateSelLabel()
        {
            if (lblSel == null) return;
            lblSel.Text = "已选 " + _checked.Count + " / " + _threads.Count;
        }

        void FitThreadColumns()
        {
            if (lvThreads == null || lvThreads.Columns.Count < 2) return;
            int w = lvThreads.ClientSize.Width - lvThreads.Columns[1].Width - 2;
            if (w > S(80)) lvThreads.Columns[0].Width = w;
        }

        static string RelPast(long epochSec)
        {
            if (epochSec <= 0) return "";
            long nowSec = (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000000L;
            long diff = Math.Max(0, nowSec - epochSec);
            if (diff < 60) return "刚刚";
            if (diff < 3600) return (diff / 60) + " 分钟前";
            if (diff < 86400) return (diff / 3600) + " 小时前";
            return (diff / 86400) + " 天前";
        }

        string[] OrderedChecked()
        {
            var order = new Dictionary<string, int>();
            for (int i = 0; i < _threads.Count; i++) order[_threads[i].Id] = i;
            return _checked.OrderBy(id => order.ContainsKey(id) ? order[id] : int.MaxValue).ThenBy(id => id).ToArray();
        }

        // ---------------------------------------------------------- 快捷短语

        void RebuildPresets()
        {
            flowPresets.SuspendLayout();
            foreach (Control c in flowPresets.Controls.Cast<Control>().ToList())
            {
                _tip.SetToolTip(c, null);
                c.Dispose();
            }
            flowPresets.Controls.Clear();

            var lab = Ui.Lbl("快捷短语", Theme.Subtle, Theme.Small);
            lab.Margin = new Padding(0, S(2), S(8), S(2));
            flowPresets.Controls.Add(lab);

            foreach (var p in _settings.Presets ?? new string[0])
            {
                string text = p;
                var b = Ui.Btn(Ui.Shorten(p, 14), BtnKind.Chip);
                _tip.SetToolTip(b, p + "\r\n\r\n单击填入；右键删除");
                b.Click += delegate
                {
                    txtMessage.Text = text;
                    txtMessage.Focus();
                    txtMessage.SelectionStart = txtMessage.TextLength;
                };
                b.MouseUp += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Right) RemovePreset(text); };
                flowPresets.Controls.Add(b);
            }

            var add = Ui.Btn("＋ 存为短语", BtnKind.Ghost);
            add.Font = Theme.Small;
            add.MinimumSize = new Size(S(40), S(26));
            add.Margin = new Padding(0, S(2), 0, S(2));
            add.Click += delegate { AddPreset(); };
            _tip.SetToolTip(add, "把当前发送内容存成快捷短语");
            flowPresets.Controls.Add(add);
            flowPresets.ResumeLayout(true);
        }

        void AddPreset()
        {
            string m = (txtMessage.Text ?? "").Trim();
            if (m.Length == 0) { Info("先在上面写好要保存的内容。"); return; }
            var list = (_settings.Presets ?? new string[0]).ToList();
            if (list.Contains(m)) { SetStatus("这条短语已经有了", StatusKind.Info); return; }
            if (list.Count >= 12) { Info("快捷短语最多 12 条，右键删掉一些再加。"); return; }
            list.Add(m);
            _settings.Presets = list.ToArray();
            Store.SaveSettings(_settings);
            RebuildPresets();
            SetStatus("已保存快捷短语", StatusKind.Ok);
        }

        void RemovePreset(string text)
        {
            if (!Ask("删除快捷短语「" + Ui.Shorten(text, 30) + "」？")) return;
            _settings.Presets = (_settings.Presets ?? new string[0]).Where(x => x != text).ToArray();
            Store.SaveSettings(_settings);
            RebuildPresets();
        }

        // ---------------------------------------------------------- 表单

        int SelectedType()
        {
            if (rbDaily.Checked) return 1;
            if (rbWeekly.Checked) return 2;
            if (rbInterval.Checked) return 3;
            return 0;
        }

        void SwitchType()
        {
            int i = SelectedType();
            fOnce.Visible = i == 0;
            fDaily.Visible = i == 1;
            fWeeklyDays.Visible = i == 2;
            fWeeklyTime.Visible = i == 2;
            fInterval.Visible = i == 3;
            lblEnd.Visible = i != 0;
            fEnd.Visible = i != 0;
            UpdateSummary();
        }

        static DateTime RoundUp(DateTime d, int minutes)
        {
            var t = new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0);
            int extra = (minutes - t.Minute % minutes) % minutes;
            if (extra == 0 && d.Second > 0) extra = minutes;
            return t.AddMinutes(extra);
        }

        void SetOnce(DateTime d)
        {
            Ui.SetValueSafe(dtOnce, new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0));
        }

        void SetDays(int[] days)
        {
            var set = new HashSet<int>(days);
            foreach (var cb in chkDays) cb.Checked = set.Contains((int)cb.Tag);
        }

        /// <summary>恢复默认值；保留左侧的会话勾选</summary>
        void ResetForm()
        {
            _loadingForm = true;
            try
            {
                txtTitle.Text = "";
                txtMessage.Text = (_settings.Presets != null && _settings.Presets.Length > 0) ? _settings.Presets[0] : "继续";
                rbOnce.Checked = true;
                SetOnce(RoundUp(DateTime.Now.AddMinutes(10), 5));
                var two = Scheduler.AtTime(DateTime.Today, "02:00");
                Ui.SetValueSafe(dtDaily, two);
                Ui.SetValueSafe(dtWeekly, two);
                SetDays(new int[] { 1 });
                Ui.SetValueSafe(numEvery, 2);
                cbUnit.SelectedIndex = 1;
                Ui.SetValueSafe(dtIntervalStart, RoundUp(DateTime.Now, 5));
                dtIntervalStart.Checked = false;
                Ui.SetValueSafe(numMaxRuns, 0);
                Ui.SetValueSafe(dtEnd, DateTime.Today.AddDays(7));
                dtEnd.Checked = false;
                txtPreview.Visible = false;
                btnPreview.Text = "预览命令";
            }
            finally { _loadingForm = false; }
            SwitchType();
            UpdateCount();
            UpdateSummary();
        }

        void ExitEdit(bool reset)
        {
            _editingId = null;
            editBanner.Visible = false;
            btnSave.Text = "保存任务";
            if (reset) ResetForm();
            UpdateSummary();
        }

        void NewJob()
        {
            ExitEdit(true);
            SwitchPage(0);
            txtMessage.Focus();
            txtMessage.SelectAll();
        }

        void UpdateCount()
        {
            if (lblCount == null) return;
            lblCount.Text = (txtMessage.Text ?? "").Length + " 字";
        }

        Job BuildJobFromForm()
        {
            var j = new Job();
            j.Threads = OrderedChecked();
            j.Message = txtMessage.Text ?? "";
            string title = (txtTitle.Text ?? "").Trim();
            j.Title = title.Length > 0 ? title : (j.Threads.Length + " 个会话 · " + Ui.Shorten(j.Message.Trim(), 12));
            j.MaxRuns = (int)numMaxRuns.Value;
            var s = new Schedule();
            j.Schedule = s;
            switch (SelectedType())
            {
                case 0:
                    s.Type = "once";
                    s.At = Scheduler.IsoMinute(dtOnce.Value);
                    j.MaxRuns = 0;
                    break;
                case 1:
                    s.Type = "daily";
                    s.Time = Scheduler.HhMm(dtDaily.Value);
                    break;
                case 2:
                    s.Type = "weekly";
                    s.Time = Scheduler.HhMm(dtWeekly.Value);
                    s.Weekdays = chkDays.Where(c => c.Checked).Select(c => (int)c.Tag).ToArray();
                    break;
                default:
                    s.Type = "interval";
                    s.EveryMinutes = (int)numEvery.Value * (cbUnit.SelectedIndex == 1 ? 60 : 1);
                    s.StartAt = dtIntervalStart.Checked ? Scheduler.IsoMinute(dtIntervalStart.Value) : null;
                    break;
            }
            if (s.Type != "once" && dtEnd.Checked)
                s.EndAt = dtEnd.Value.Date.ToString("yyyy-MM-dd", Inv) + "T23:59:59";
            return j;
        }

        static string ValidateJob(Job j)
        {
            if (j.Threads == null || j.Threads.Length == 0) return "请先在左侧勾选至少一个会话";
            if (string.IsNullOrWhiteSpace(j.Message)) return "发送内容不能为空";
            var bad = j.Threads.FirstOrDefault(id => !Ssh.IsValidThreadId(id));
            if (bad != null) return "会话 ID 含非法字符：" + bad;
            var s = j.Schedule;
            if (s.Type == "once")
            {
                DateTime at;
                if (!Scheduler.TryParseLocal(s.At, out at)) return "发送时间无效";
                if (at <= DateTime.Now.AddMinutes(-1)) return "单次发送时间已经过去了，请重新选择";
            }
            if (s.Type == "weekly" && (s.Weekdays == null || s.Weekdays.Length == 0)) return "请至少选择一个星期";
            return null;
        }

        void UpdateSummary()
        {
            if (lblSummary == null || _loadingForm) return;
            try
            {
                var j = BuildJobFromForm();
                var now = DateTime.Now;
                var s = j.Schedule;
                if (j.Threads.Length == 0) { SetSummary("← 先在左侧勾选要发送的会话", Theme.Warn); return; }
                if (string.IsNullOrWhiteSpace(j.Message)) { SetSummary("发送内容为空", Theme.Warn); return; }
                if (s.Type == "weekly" && s.Weekdays.Length == 0) { SetSummary("请至少选择一个星期", Theme.Warn); return; }
                if (s.Type == "once")
                {
                    DateTime at;
                    if (Scheduler.TryParseLocal(s.At, out at) && at <= now.AddMinutes(-1)) { SetSummary("这个时间已经过去了，请重新选择", Theme.Bad); return; }
                }

                var old = _editingId != null ? FindJob(_editingId) : null;
                j.CreatedAt = (old != null && !string.IsNullOrEmpty(old.CreatedAt)) ? old.CreatedAt : Scheduler.Iso(now);
                if (old != null && s.Type != "once") j.Runs = old.Runs;

                var next = Scheduler.ComputeNextRun(j, now);
                if (!next.HasValue) { SetSummary("按当前设置不会触发（截止日期已过，或执行次数已用完）", Theme.Bad); return; }

                string text = Scheduler.Friendly(next.Value) + "（" + Scheduler.RelFuture(next.Value) + "）";
                if (s.Type == "interval") text += "，之后" + Scheduler.Describe(j).Split('·')[0].Trim();
                text += "   ·   " + j.Threads.Length + " 个会话 @ " + Ssh.Host;
                SetSummary(text, Theme.Accent);
            }
            catch { }
        }

        void SetSummary(string text, Color c)
        {
            lblSummary.Text = text;
            lblSummary.ForeColor = c;
            _tip.SetToolTip(lblSummary, text);
        }

        void DoSave()
        {
            CheckExternalChange();
            var j = BuildJobFromForm();
            string err = ValidateJob(j);
            if (err != null) { Warn(err, "还不能保存"); return; }

            var now = DateTime.Now;
            Job old = null;
            if (_editingId != null)
            {
                old = FindJob(_editingId);
                if (old == null)
                {
                    if (!Ask("正在编辑的任务已经被删除了，要另存为一条新任务吗？")) return;
                    _editingId = null;
                }
            }
            if (old != null && !string.IsNullOrEmpty(old.Host) && !string.Equals(old.Host, Ssh.Host, StringComparison.OrdinalIgnoreCase))
            {
                if (!Ask("这条任务原来发往主机「" + old.Host + "」，保存后会改成发往当前主机「" + Ssh.Host + "」。\n\n继续保存？")) return;
            }

            j.Host = Ssh.Host;
            if (old != null)
            {
                j.Id = old.Id;
                j.CreatedAt = old.CreatedAt;
                j.LastRunTs = old.LastRunTs;
                j.LastOk = old.LastOk;
                j.LastTotal = old.LastTotal;
                // 单次任务重新设了时间就应该再发一次；周期任务保留已执行次数（用于“最多执行 N 次”）
                j.Runs = Scheduler.TypeOf(j) == "once" ? 0 : old.Runs;
            }
            else
            {
                j.Id = Store.NewId();
                j.CreatedAt = Scheduler.Iso(now);
            }
            j.Enabled = true;
            j.Finished = false;
            j.Retry = 0;
            j.LastNote = null;

            var next = Scheduler.ComputeNextRun(j, now);
            if (!next.HasValue)
            {
                if (!Ask("按当前设置，这条任务不会再触发（截止日期已过，或执行次数已用完）。\n\n仍然保存吗？")) return;
                j.Enabled = false;
                j.Finished = true;
            }
            j.NextRun = Scheduler.Iso(next);

            int idx = old != null ? _jobs.IndexOf(old) : -1;
            if (idx >= 0) _jobs[idx] = j; else _jobs.Add(j);
            if (!PersistJobs())
            {
                if (idx >= 0) _jobs[idx] = old; else _jobs.Remove(j);
                Warn("保存失败，任务没有写入磁盘。详情见状态栏。", "保存失败");
                return;
            }

            bool editing = old != null;
            Store.Log((editing ? "更新任务「" : "新建任务「") + j.Title + "」 host=" + j.Host + " 下次=" + j.NextRun);
            ExitEdit(false);
            SwitchPage(1);
            SelectJobRow(j.Id);
            SetStatus((editing ? "已更新：" : "已保存：") + j.Title + (next.HasValue ? " · " + Scheduler.Friendly(next.Value) : ""), StatusKind.Ok);
        }

        void FillForm(Job job, bool asCopy)
        {
            _loadingForm = true;
            try
            {
                txtPreview.Visible = false;
                btnPreview.Text = "预览命令";
                txtTitle.Text = asCopy ? ((job.Title ?? "") + " 副本") : (job.Title ?? "");
                txtMessage.Text = job.Message ?? "";

                _checked.Clear();
                foreach (var id in job.Threads ?? new string[0]) if (!string.IsNullOrEmpty(id)) _checked.Add(id);

                var s = job.Schedule ?? new Schedule();
                string type = Scheduler.TypeOf(job);
                if (type == "daily") rbDaily.Checked = true;
                else if (type == "weekly") rbWeekly.Checked = true;
                else if (type == "interval") rbInterval.Checked = true;
                else rbOnce.Checked = true;

                DateTime tmp;
                if (type == "once" && Scheduler.TryParseLocal(s.At, out tmp) && tmp > DateTime.Now) SetOnce(tmp);
                else SetOnce(RoundUp(DateTime.Now.AddMinutes(10), 5));

                if (!string.IsNullOrEmpty(s.Time))
                {
                    var t = Scheduler.AtTime(DateTime.Today, s.Time);
                    Ui.SetValueSafe(dtDaily, t);
                    Ui.SetValueSafe(dtWeekly, t);
                }
                if (s.Weekdays != null && s.Weekdays.Length > 0) SetDays(s.Weekdays);

                int m = s.EveryMinutes > 0 ? s.EveryMinutes : 60;
                if (m % 60 == 0) { cbUnit.SelectedIndex = 1; Ui.SetValueSafe(numEvery, m / 60); }
                else { cbUnit.SelectedIndex = 0; Ui.SetValueSafe(numEvery, m); }
                if (type == "interval" && Scheduler.TryParseLocal(s.StartAt, out tmp))
                {
                    Ui.SetValueSafe(dtIntervalStart, tmp);
                    dtIntervalStart.Checked = true;
                }
                else dtIntervalStart.Checked = false;

                if (Scheduler.TryParseLocal(s.EndAt, out tmp)) { Ui.SetValueSafe(dtEnd, tmp.Date); dtEnd.Checked = true; }
                else dtEnd.Checked = false;
                Ui.SetValueSafe(numMaxRuns, Math.Max(0, job.MaxRuns));

                if (asCopy)
                {
                    _editingId = null;
                    editBanner.Visible = false;
                    btnSave.Text = "保存任务";
                }
                else
                {
                    _editingId = job.Id;
                    string host = JobHost(job);
                    string note = string.Equals(host, Ssh.Host, StringComparison.OrdinalIgnoreCase) ? "" : ("　（原主机 " + host + "，保存后改为当前主机）");
                    lblEditing.Text = "正在编辑：" + Ui.Shorten(job.Title, 40) + note;
                    editBanner.Visible = true;
                    btnSave.Text = "更新任务";
                }
            }
            finally { _loadingForm = false; }

            FillThreadList();
            SwitchType();
            UpdateCount();
            UpdateSummary();
            SwitchPage(0);
            _composeScroll.AutoScrollPosition = new Point(0, 0);
            txtMessage.Focus();
            if (asCopy) SetStatus("已复制为新任务，修改后保存即可", StatusKind.Info);
        }

        void DoPreview()
        {
            if (txtPreview.Visible)
            {
                txtPreview.Visible = false;
                btnPreview.Text = "预览命令";
                return;
            }
            var j = BuildJobFromForm();
            if (j.Threads.Length == 0) { Info("请先在左侧勾选会话。"); return; }
            var sb = new StringBuilder();
            sb.AppendLine("# 通过 ssh " + Ssh.Host + " 依次执行（每个会话一条，间隔约 1 秒），不会真的发送：");
            foreach (var id in j.Threads.Take(8))
                sb.AppendLine("codex queue --thread " + id + " --message \"$msg\" --resume");
            if (j.Threads.Length > 8) sb.AppendLine("# … 其余 " + (j.Threads.Length - 8) + " 个会话同理");
            sb.AppendLine();
            sb.AppendLine("# $msg =");
            sb.Append((j.Message ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"));
            txtPreview.Text = sb.ToString();
            txtPreview.Visible = true;
            btnPreview.Text = "隐藏预览";
            _composeScroll.ScrollControlIntoView(txtPreview);
        }

        void DoSendNow()
        {
            var j = BuildJobFromForm();
            if (j.Threads.Length == 0) { Info("请先在左侧勾选会话。"); return; }
            if (string.IsNullOrWhiteSpace(j.Message)) { Info("发送内容不能为空。"); return; }
            var bad = j.Threads.FirstOrDefault(id => !Ssh.IsValidThreadId(id));
            if (bad != null) { Warn("会话 ID 含非法字符：" + bad, "无法发送"); return; }
            if (_running) { Info("还有任务正在发送，请稍等。"); return; }
            if (!Ask("现在就向 " + j.Threads.Length + " 个会话（" + Ssh.Host + "）发送：\n\n" + Ui.Shorten(j.Message, 200) + "\n\n这不会保存为任务。确定发送？")) return;
            j.Id = "adhoc-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if ((txtTitle.Text ?? "").Trim().Length == 0) j.Title = "临时发送 · " + Ui.Shorten(j.Message.Trim(), 12);
            j.Host = Ssh.Host;
            RunJobAsync(j, "manual");
        }

        // ---------------------------------------------------------- 任务列表

        static string JobHost(Job j)
        {
            return string.IsNullOrEmpty(j.Host) ? Ssh.Host : j.Host;
        }

        Job FindJob(string id)
        {
            if (id == null) return null;
            return _jobs.FirstOrDefault(x => x.Id == id);
        }

        string SelectedJobId()
        {
            if (grid == null || grid.SelectedRows.Count == 0) return null;
            return grid.SelectedRows[0].Tag as string;
        }

        Job SelectedJob() { return FindJob(SelectedJobId()); }

        void SelectJobRow(string id)
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                if ((row.Tag as string) != id) continue;
                grid.ClearSelection();
                row.Selected = true;
                try { grid.CurrentCell = row.Cells[1]; } catch { }
                break;
            }
            UpdateJobButtons();
        }

        static bool IsFinished(Job j)
        {
            if (j.Finished) return true;
            // 兼容老数据：执行完后被停用的单次任务
            return !j.Enabled && string.IsNullOrEmpty(j.NextRun) && j.Runs > 0 && !Scheduler.ComputeNextRun(j, DateTime.Now).HasValue;
        }

        int Rank(Job j)
        {
            if (_running && j.Id == _runningJobId) return 0;
            if (IsFinished(j)) return 3;
            return j.Enabled ? 1 : 2;
        }

        string StatusText(Job j, out Color c)
        {
            if (_running && j.Id == _runningJobId) { c = Theme.Accent; return "● 发送中"; }
            if (IsFinished(j)) { c = Theme.Subtle; return "● 已完成"; }
            if (!j.Enabled) { c = Theme.Muted; return "● 已暂停"; }
            if (j.Retry > 0) { c = Theme.Warn; return "● 等待重试"; }
            if (_settings.Paused) { c = Theme.Warn; return "● 调度暂停"; }
            c = Theme.Ok;
            return "● 已排期";
        }

        void FillJobs()
        {
            if (grid == null) return;
            var names = new Dictionary<string, string>();
            foreach (var t in _threads) names[t.Id] = t.Name;

            var ordered = _jobs.OrderBy(x => Rank(x)).ThenBy(x => x.NextRun ?? "9999").ThenBy(x => x.Title).ToList();

            bool same = grid.Rows.Count == ordered.Count;
            for (int i = 0; same && i < ordered.Count; i++)
                if ((grid.Rows[i].Tag as string) != ordered[i].Id) same = false;

            if (!same)
            {
                string selId = SelectedJobId();
                int first = -1;
                try { first = grid.FirstDisplayedScrollingRowIndex; } catch { }
                grid.Rows.Clear();
                foreach (var j in ordered)
                {
                    int idx = grid.Rows.Add();
                    grid.Rows[idx].Tag = j.Id;
                }
                grid.ClearSelection();
                bool found = false;
                if (selId != null)
                {
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        if ((row.Tag as string) != selId) continue;
                        row.Selected = true;
                        try { grid.CurrentCell = row.Cells[1]; } catch { }
                        found = true;
                        break;
                    }
                }
                if (!found) { try { grid.CurrentCell = null; } catch { } }
                if (first >= 0 && first < grid.Rows.Count) { try { grid.FirstDisplayedScrollingRowIndex = first; } catch { } }
            }

            for (int i = 0; i < ordered.Count; i++) FillRow(grid.Rows[i], ordered[i], names);

            lblEmpty.Visible = _jobs.Count == 0;
            int enabled = _jobs.Count(x => x.Enabled);
            lblJobHint.Text = _jobs.Count == 0 ? "" : (_jobs.Count + " 条任务 · " + enabled + " 条启用" + (_settings.Paused ? " · 调度已暂停" : ""));
            _navs[1].SetText(_jobs.Count > 0 ? ("任务列表 (" + _jobs.Count + ")") : "任务列表");
            UpdateJobButtons();
        }

        void FillRow(DataGridViewRow row, Job j, Dictionary<string, string> names)
        {
            Color sc;
            string st = StatusText(j, out sc);
            var tids = j.Threads ?? new string[0];
            string host = JobHost(j);
            bool sameHost = string.Equals(host, Ssh.Host, StringComparison.OrdinalIgnoreCase);
            var nm = tids.Select(id => (sameHost && names.ContainsKey(id) && !string.IsNullOrEmpty(names[id])) ? names[id] : Scheduler.Short(id)).ToList();
            string threads = nm.Count == 0 ? "—" : (nm.Count == 1 ? Ui.Shorten(nm[0], 20) : (Ui.Shorten(nm[0], 12) + " 等 " + nm.Count + " 个"));
            if (!sameHost) threads = "[" + host + "] " + threads;

            string next = "—";
            DateTime nr;
            if (j.Enabled && Scheduler.TryParseLocal(j.NextRun, out nr))
                next = Scheduler.Friendly(nr) + " · " + Scheduler.RelFuture(nr);

            string last = "—";
            DateTime lr;
            if (Scheduler.TryParseLocal(j.LastRunTs, out lr))
                last = lr.ToString("MM-dd HH:mm", Inv) + "  " + j.LastOk + "/" + j.LastTotal;

            SetCell(row, 0, st);
            SetCell(row, 1, j.Title ?? "");
            SetCell(row, 2, threads);
            SetCell(row, 3, Scheduler.Describe(j));
            SetCell(row, 4, next);
            SetCell(row, 5, last);

            row.Cells[0].Style.ForeColor = sc;
            row.Cells[0].Style.SelectionForeColor = sc;
            Color fore = (j.Enabled || (_running && j.Id == _runningJobId)) ? Theme.Text : Theme.Muted;
            for (int c = 1; c <= 4; c++) { row.Cells[c].Style.ForeColor = fore; row.Cells[c].Style.SelectionForeColor = fore; }
            Color lastC = (j.LastTotal > 0 && j.LastOk < j.LastTotal) ? Theme.Bad : fore;
            row.Cells[5].Style.ForeColor = lastC;
            row.Cells[5].Style.SelectionForeColor = lastC;

            row.Cells[0].ToolTipText = string.IsNullOrEmpty(j.LastNote) ? "" : j.LastNote;
            row.Cells[1].ToolTipText = "发送内容：\r\n" + Ui.Shorten(j.Message, 300);
            row.Cells[2].ToolTipText = "主机：" + host + "\r\n" + string.Join("\r\n", nm.ToArray());
            row.Cells[5].ToolTipText = string.IsNullOrEmpty(j.LastNote) ? "" : j.LastNote;
        }

        static void SetCell(DataGridViewRow row, int i, string v)
        {
            if (!Equals(row.Cells[i].Value as string, v)) row.Cells[i].Value = v;
        }

        void UpdateJobButtons()
        {
            if (bRun == null) return;
            var j = SelectedJob();
            bool has = j != null;
            bRun.Enabled = has && !_running;
            bEdit.Enabled = has;
            bCopy.Enabled = has;
            bToggle.Enabled = has;
            bDel.Enabled = has;
            string t = (has && j.Enabled) ? "暂停" : "启用";
            if (bToggle.Text != t) bToggle.Text = t;
            miRun.Enabled = bRun.Enabled;
            miEdit.Enabled = has;
            miCopy.Enabled = has;
            miToggle.Enabled = has;
            miToggle.Text = t;
            miDel.Enabled = has;
            if (btnSendNow != null) btnSendNow.Enabled = !_running;
        }

        void EditSelected(bool asCopy)
        {
            var j = SelectedJob();
            if (j == null) return;
            FillForm(j, asCopy);
        }

        void RunSelected()
        {
            var j = SelectedJob();
            if (j == null) return;
            if (_running) { Info("还有任务正在发送，请稍等。"); return; }
            if (!Ask("立即执行「" + j.Title + "」？\n\n将向 " + (j.Threads ?? new string[0]).Length + " 个会话（" + JobHost(j) + "）发送：\n"
                     + Ui.Shorten(j.Message, 200) + "\n\n手动执行不会影响原定计划。")) return;
            RunJobAsync(j, "manual");
        }

        void ToggleSelected()
        {
            CheckExternalChange();
            var j = SelectedJob();
            if (j == null) return;
            if (j.Enabled)
            {
                j.Enabled = false;
                j.Retry = 0;
                if (PersistJobs()) SetStatus("已暂停：" + j.Title, StatusKind.Info);
            }
            else
            {
                var n = Scheduler.ComputeNextRun(j, DateTime.Now);
                if (!n.HasValue)
                {
                    Info(Scheduler.TypeOf(j) == "once"
                        ? "这条单次任务已经执行过或已经过期。\n\n要再发一次，请点「编辑」重新设定时间后保存。"
                        : "这条任务已经到达结束条件（截止日期或执行次数）。\n\n请点「编辑」调整后再保存。");
                    return;
                }
                j.Enabled = true;
                j.Finished = false;
                j.Retry = 0;
                j.LastNote = null;
                j.NextRun = Scheduler.Iso(n);
                if (PersistJobs()) SetStatus("已启用：" + j.Title + " · " + Scheduler.Friendly(n.Value), StatusKind.Ok);
            }
            FillJobs();
        }

        void DeleteSelected()
        {
            CheckExternalChange();
            var j = SelectedJob();
            if (j == null) return;
            if (!Ask("删除任务「" + j.Title + "」？\n\n删除后无法恢复（运行日志会保留）。")) return;
            int idx = _jobs.IndexOf(j);
            _jobs.Remove(j);
            if (!PersistJobs())
            {
                if (idx >= 0) _jobs.Insert(Math.Min(idx, _jobs.Count), j); else _jobs.Add(j);
                return;
            }
            if (_editingId == j.Id) ExitEdit(false);
            FillJobs();
            SetStatus("已删除：" + j.Title, StatusKind.Info);
        }

        // ---------------------------------------------------------- 持久化

        bool PersistJobs()
        {
            try
            {
                Store.SaveJobs(_jobs);
                _jobsMtime = Store.JobsMtime();
                return true;
            }
            catch (Exception ex)
            {
                Store.Log("保存 jobs.json 失败：" + ex.Message);
                SetStatus("保存任务失败：" + ex.Message, StatusKind.Bad);
                return false;
            }
        }

        void ReloadJobs(bool startup)
        {
            var mt = Store.JobsMtime();
            string err;
            var list = Store.LoadJobs(out err);
            if (list == null)
            {
                // 读不出来时绝不用空列表覆盖：先备份坏文件，内存里的任务保持不变
                _jobsMtime = mt;
                if (mt != _brokenMtime)
                {
                    _brokenMtime = mt;
                    string bak = Store.BackupBrokenJobs();
                    Store.Log("jobs.json 读取失败：" + err + "，已备份到 " + (bak ?? "（备份失败）"));
                    string m = "任务文件读取失败，已备份为 " + (bak == null ? "（备份失败）" : Path.GetFileName(bak));
                    SetStatus(m, StatusKind.Bad);
                    if (tray != null) tray.ShowBalloonTip(5000, "任务文件读取失败", m + "。内存中的任务保持不变，可在数据目录中找回。", ToolTipIcon.Error);
                }
                FillJobs();
                return;
            }
            _jobs = list;
            _jobsMtime = mt;
            if (!startup) Store.Log("检测到 jobs.json 变化，已重载（" + _jobs.Count + " 条）");
            FillJobs();
        }

        void CheckExternalChange()
        {
            if (_running) return;
            var mt = Store.JobsMtime();
            if (mt != _jobsMtime) ReloadJobs(false);
        }

        // ---------------------------------------------------------- 执行

        void RunJobAsync(Job job, string trigger)
        {
            if (_running)
            {
                if (trigger == "manual") Info("还有任务正在发送，请稍等。");
                return;
            }
            _running = true;
            _runningJobId = job.Id;

            // 后台线程只碰这份快照，界面上同时编辑/重载任务不会互相干扰
            var snap = new Job();
            snap.Id = job.Id;
            snap.Title = job.Title;
            snap.Message = job.Message;
            snap.Threads = (string[])(job.Threads ?? new string[0]).Clone();
            string host = JobHost(job);

            Store.Log("触发任务「" + snap.Title + "」 trigger=" + trigger + " host=" + host + " 会话=" + snap.Threads.Length);
            SetStatus("正在发送「" + snap.Title + "」…", StatusKind.Busy);
            FillJobs();

            Task.Factory.StartNew(delegate
            {
                RunEntry entry;
                try
                {
                    entry = Scheduler.Execute(snap, trigger, host, delegate(int done, int total)
                    {
                        if (done < total)
                            SetStatus("正在发送「" + Ui.Shorten(snap.Title, 16) + "」 " + (done + 1) + "/" + total + " …", StatusKind.Busy);
                    });
                }
                catch (Exception ex)
                {
                    entry = new RunEntry();
                    entry.Ts = Scheduler.Iso(DateTime.Now);
                    entry.JobId = snap.Id;
                    entry.JobTitle = snap.Title;
                    entry.Host = host;
                    entry.Trigger = trigger;
                    entry.Message = snap.Message;
                    entry.Total = snap.Threads.Length;
                    entry.Detail = "执行异常：" + ex;
                    Store.AppendLog(entry);
                }
                var e2 = entry;
                UI(delegate { OnRunDone(snap, e2, trigger, host); });
            });
        }

        void OnRunDone(Job snap, RunEntry entry, string trigger, string host)
        {
            _running = false;
            _runningJobId = null;
            CheckExternalChange(); // 发送期间外部改过 jobs.json 的话先合并进来，再写结果

            // 按 Id 找当前列表里的任务：发送期间列表可能被重载过，旧对象已经不在列表里了
            var live = FindJob(snap.Id);
            if (live != null)
            {
                ApplyRunResult(live, entry, trigger);
                PersistJobs();
            }

            bool allOk = entry.Total > 0 && entry.OkCount == entry.Total;
            string summary = entry.OkCount + "/" + entry.Total + " 成功";
            string extra = entry.ConnFailed ? ("，连不上 " + host) : (entry.RateLimited ? "，远端限流" : "");
            if (trigger == "schedule" && live != null && live.Retry > 0) extra += "，" + Scheduler.RetryMinutes + " 分钟后重试";
            SetStatus((allOk ? "发送完成：" : "发送有失败：") + snap.Title + " · " + summary + extra, allOk ? StatusKind.Ok : StatusKind.Bad);
            Store.Log("执行「" + snap.Title + "」 " + trigger + " " + entry.OkCount + "/" + entry.Total + extra);

            if (tray != null && (!allOk || trigger == "manual"))
                tray.ShowBalloonTip(3000, allOk ? "发送完成" : "发送失败", snap.Title + "：" + summary + extra, allOk ? ToolTipIcon.Info : ToolTipIcon.Warning);

            FillJobs();
            FillLogs();
            Tick(); // 可能还有排队等着的任务
        }

        static void ApplyRunResult(Job live, RunEntry entry, string trigger)
        {
            live.LastRunTs = entry.Ts;
            live.LastOk = entry.OkCount;
            live.LastTotal = entry.Total;

            // 手动执行只记录结果，不改动原定计划（以前会把单次任务直接标记为已执行并停用）
            if (trigger == "manual")
            {
                live.LastNote = "手动执行 " + entry.OkCount + "/" + entry.Total;
                return;
            }

            var now = DateTime.Now;
            bool once = Scheduler.TypeOf(live) == "once";
            if (entry.ConnFailed && live.Retry < Scheduler.MaxRetry)
            {
                DateTime retryAt = now.AddMinutes(Scheduler.RetryMinutes);
                DateTime? regular = null;
                if (!once)
                {
                    live.Runs++;
                    regular = Scheduler.ComputeNextRun(live, now);
                    live.Runs--;
                }
                if (once || !regular.HasValue || retryAt < regular.Value)
                {
                    live.Retry++;
                    live.NextRun = Scheduler.Iso(retryAt);
                    live.LastNote = "连不上主机，" + Scheduler.RetryMinutes + " 分钟后第 " + live.Retry + "/" + Scheduler.MaxRetry + " 次重试";
                    return;
                }
            }

            bool gaveUp = entry.ConnFailed;
            live.Retry = 0;
            live.Runs++;
            var n = Scheduler.ComputeNextRun(live, now);
            live.NextRun = Scheduler.Iso(n);
            live.LastNote = gaveUp ? "连不上主机，本次已放弃" : (entry.RateLimited ? "远端限流，消息可能没有生效" : null);
            if (!n.HasValue)
            {
                live.Enabled = false;
                live.Finished = true;
            }
        }

        void Tick()
        {
            if (_passive) return;
            try
            {
                CheckExternalChange();
                var now = DateTime.Now;
                bool changed = false;
                Job due = null;
                DateTime dueAt = DateTime.MaxValue;

                foreach (var j in _jobs.ToList())
                {
                    if (!j.Enabled) continue;
                    DateTime next;
                    if (!Scheduler.TryParseLocal(j.NextRun, out next))
                    {
                        var n = Scheduler.ComputeNextRun(j, now);
                        j.NextRun = Scheduler.Iso(n);
                        if (!n.HasValue) { j.Enabled = false; j.Finished = true; j.LastNote = "没有下一次执行时间"; }
                        changed = true;
                        continue;
                    }
                    if (next > now) continue;
                    if (_running && j.Id == _runningJobId) continue;

                    // 到点后再核对一遍：截止日期、次数、单次任务的补发窗口（以前这里不检查，关机几天后照样补发）
                    string skip = Scheduler.SkipReason(j, next, now);
                    if (skip != null)
                    {
                        j.Retry = 0;
                        DateTime? n2 = Scheduler.TypeOf(j) == "once" ? (DateTime?)null : Scheduler.ComputeNextRun(j, now);
                        j.NextRun = Scheduler.Iso(n2);
                        if (!n2.HasValue) { j.Enabled = false; j.Finished = true; }
                        j.LastNote = "已跳过：" + skip;
                        Store.Log("跳过「" + j.Title + "」：" + skip);
                        changed = true;
                        continue;
                    }
                    if (next < dueAt) { due = j; dueAt = next; }
                }

                if (changed) PersistJobs();
                if (due != null && !_running && !_settings.Paused) RunJobAsync(due, "schedule");
                else FillJobs();
                UpdateSummary();
            }
            catch (Exception ex)
            {
                Store.Log("调度异常：" + ex.Message);
            }
        }

        // ---------------------------------------------------------- 日志

        void FillLogs()
        {
            if (lvLogs == null) return;
            var entries = Store.ReadLog(300);
            if (chkFailOnly.Checked) entries = entries.Where(e => e.Total == 0 || e.OkCount < e.Total).ToList();

            lvLogs.BeginUpdate();
            try
            {
                lvLogs.Items.Clear();
                foreach (var e in entries)
                {
                    DateTime ts;
                    string when = Scheduler.TryParseLocal(e.Ts, out ts) ? ts.ToString("MM-dd HH:mm:ss", Inv) : (e.Ts ?? "");
                    bool ok = e.Total > 0 && e.OkCount == e.Total;
                    string result;
                    if (e.Total == 0) result = "✗ 无会话";
                    else if (ok) result = "✓ " + e.OkCount + "/" + e.Total;
                    else if (e.ConnFailed) result = "✗ 连接失败";
                    else if (e.RateLimited) result = "! 限流 " + e.OkCount + "/" + e.Total;
                    else result = "✗ " + e.OkCount + "/" + e.Total;

                    var it = new ListViewItem(when);
                    it.SubItems.Add(Ui.Shorten(e.JobTitle ?? "", 40));
                    it.SubItems.Add(result);
                    it.SubItems.Add(e.Trigger == "manual" ? "手动" : "定时");
                    it.SubItems.Add(Ui.Shorten(e.Message ?? "", 80));
                    it.Tag = e;
                    it.ForeColor = ok ? Theme.Text : Theme.Bad;
                    it.ToolTipText = (e.Host ?? "") + "\r\n" + Ui.Shorten(e.Message ?? "", 200);
                    lvLogs.Items.Add(it);
                }
            }
            finally { lvLogs.EndUpdate(); }
            txtLogDetail.Text = entries.Count == 0 ? "还没有执行记录。" : "选中上方一行查看完整输出。";
            FitLogColumns();
        }

        void FitLogColumns()
        {
            if (lvLogs == null || lvLogs.Columns.Count < 5) return;
            int used = 0;
            for (int i = 0; i < 4; i++) used += lvLogs.Columns[i].Width;
            int w = lvLogs.ClientSize.Width - used - 2;
            if (w > S(120)) lvLogs.Columns[4].Width = w;
        }

        void ShowLogDetail()
        {
            if (lvLogs.SelectedItems.Count == 0) return;
            var e = lvLogs.SelectedItems[0].Tag as RunEntry;
            if (e == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("任务: " + (e.JobTitle ?? ""));
            sb.AppendLine("结果: " + e.OkCount + "/" + e.Total + " 成功" + (e.ConnFailed ? "（连接失败）" : "") + (e.RateLimited ? "（限流）" : ""));
            sb.AppendLine("内容: " + (e.Message ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n         "));
            sb.AppendLine(new string('─', 60));
            sb.Append((e.Detail ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"));
            txtLogDetail.Text = sb.ToString();
            txtLogDetail.SelectionStart = 0;
            txtLogDetail.ScrollToCaret();
        }

        void ClearLogs()
        {
            if (!Ask("清空所有运行日志？\n\n不会影响任务本身。")) return;
            Store.ClearLog();
            FillLogs();
            SetStatus("运行日志已清空", StatusKind.Info);
        }
    }

    // ============================================================ 开机自启（注册表 Run 键）

    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "CodexScheduler";

        /// <summary>带 --tray：登录时只进托盘，不弹出窗口</summary>
        public static string Command { get { return "\"" + Application.ExecutablePath + "\" --tray"; } }

        static string Current()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k == null ? null : k.GetValue(Name) as string;
            }
            catch { return null; }
        }

        public static bool IsOn() { return !string.IsNullOrEmpty(Current()); }

        public static bool Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(Name, Command);
                    else k.DeleteValue(Name, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置自启动失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        /// <summary>exe 挪了位置、或是老版本写的（没有 --tray）自启项，自动改成当前路径</summary>
        public static void Repair()
        {
            var c = Current();
            if (!string.IsNullOrEmpty(c) && !string.Equals(c, Command, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                        if (k != null) k.SetValue(Name, Command);
                }
                catch { }
            }
        }
    }
}
