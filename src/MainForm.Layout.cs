using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexScheduler
{
    /// <summary>主窗体：这里只放界面搭建，行为逻辑在 MainForm.Logic.cs</summary>
    public partial class MainForm : Form
    {
        static int S(int v) { return Theme.S(v); }

        // ---------------------------------------------------------- 控件
        ToolTip _tip;

        Label lblStatus, _subtitle;
        TextBox txtHost;
        Button btnConnect, btnPause;

        SplitContainer _split, _logSplit;
        TextBox txtSearch;
        CheckBox chkOnlySel;
        ListView lvThreads;
        Label lblSel;

        NavTab[] _navs;
        Control[] _pages;

        Panel _composeScroll;
        TableLayoutPanel editBanner;
        Label lblEditing;
        TextBox txtTitle, txtMessage, txtPreview;
        FlowLayoutPanel flowPresets;
        Label lblCount, lblSummary;
        RadioButton rbOnce, rbDaily, rbWeekly, rbInterval;
        FlowLayoutPanel fOnce, fDaily, fWeeklyDays, fWeeklyTime, fInterval, fEnd;
        Label lblEnd;
        DateTimePicker dtOnce, dtDaily, dtWeekly, dtIntervalStart, dtEnd;
        CheckBox[] chkDays = new CheckBox[7];
        NumericUpDown numEvery, numMaxRuns;
        ComboBox cbUnit;
        Button btnSave, btnSendNow, btnPreview, btnReset;

        DataGridView grid;
        Label lblJobHint, lblEmpty;
        Button bNew, bRun, bEdit, bCopy, bToggle, bDel;
        ToolStripMenuItem miRun, miEdit, miCopy, miToggle, miDel;

        ListView lvLogs;
        TextBox txtLogDetail;
        CheckBox chkFailOnly;

        void BuildAll()
        {
            var root = Ui.Table(1, 3);
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.Bg;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(62)));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(Ui.Separator(), 0, 1);
            root.Controls.Add(BuildBody(), 0, 2);
        }

        static void AutoRows(TableLayoutPanel t, int n)
        {
            for (int i = 0; i < n; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        // ---------------------------------------------------------- 顶栏

        Control BuildHeader()
        {
            var h = Ui.Table(8, 1);
            h.Dock = DockStyle.Fill;
            h.BackColor = Theme.Card;
            h.Padding = new Padding(S(18), 0, S(18), 0);
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // logo
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // 标题
            h.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));      // 空白
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // 状态
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // 暂停
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // “SSH 主机”
            h.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(180)));  // 主机输入
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // 连接
            h.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            h.Controls.Add(new Logo(), 0, 0);

            var titles = Ui.Table(1, 2);
            titles.AutoSize = true;
            titles.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            titles.Anchor = AnchorStyles.Left;
            AutoRows(titles, 2);
            var t1 = Ui.Lbl("Codex 定时发送台", Theme.Text, Theme.Title);
            t1.Margin = new Padding(0);
            var t2 = Ui.Lbl("按计划把消息投给指定会话，让它们自己继续跑", Theme.Muted, Theme.Small);
            t2.Margin = new Padding(S(1), S(1), 0, 0);
            titles.Controls.Add(t1, 0, 0);
            titles.Controls.Add(t2, 0, 1);
            _subtitle = t2;
            h.Controls.Add(titles, 1, 0);

            lblStatus = Ui.Lbl("● 正在启动…", Theme.Muted, Theme.Base);
            lblStatus.Padding = new Padding(S(10), S(5), S(10), S(5));
            lblStatus.BackColor = Theme.Hover;
            lblStatus.Margin = new Padding(S(12), 0, S(10), 0);
            lblStatus.MaximumSize = new Size(S(460), S(34));
            h.Controls.Add(lblStatus, 3, 0);

            btnPause = Ui.Btn("暂停调度", BtnKind.Secondary);
            btnPause.Click += delegate { SetPaused(!_settings.Paused); };
            _tip.SetToolTip(btnPause, "暂停后所有定时任务都不会触发，手动执行不受影响");
            h.Controls.Add(btnPause, 4, 0);

            var lh = Ui.Lbl("SSH 主机", Theme.Muted, Theme.Base);
            lh.Margin = new Padding(S(10), 0, S(6), 0);
            h.Controls.Add(lh, 5, 0);

            txtHost = new TextBox();
            txtHost.Text = Ssh.Host;
            txtHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            txtHost.Margin = new Padding(0, 0, S(8), 0);
            txtHost.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; ApplyHost(true); }
                else if (e.KeyCode == Keys.Escape) { e.Handled = true; e.SuppressKeyPress = true; txtHost.Text = Ssh.Host; }
            };
            txtHost.Leave += delegate { ApplyHost(false); };
            _tip.SetToolTip(txtHost, "~/.ssh/config 里的主机别名，或 user@host；回车连接");
            h.Controls.Add(txtHost, 6, 0);

            btnConnect = Ui.Btn("连接", BtnKind.Secondary);
            btnConnect.Margin = new Padding(0);
            btnConnect.Click += delegate { ApplyHost(true); };
            _tip.SetToolTip(btnConnect, "重新连接并刷新会话列表（F5）");
            h.Controls.Add(btnConnect, 7, 0);
            return h;
        }

        // ---------------------------------------------------------- 主体

        Control BuildBody()
        {
            var wrap = new Panel();
            wrap.Dock = DockStyle.Fill;
            wrap.BackColor = Theme.Bg;
            wrap.Padding = new Padding(S(16));
            wrap.Margin = new Padding(0);

            _split = new SplitContainer();
            _split.Size = new Size(2400, 1400); // 先给足尺寸，后面设最小宽度时才不会抛异常
            _split.Dock = DockStyle.Fill;
            _split.BackColor = Theme.Bg;
            _split.FixedPanel = FixedPanel.Panel1;
            _split.SplitterWidth = S(12);
            _split.Panel1.BackColor = Theme.Bg;
            _split.Panel2.BackColor = Theme.Bg;
            _split.TabStop = false;

            _split.Panel1.Controls.Add(BuildThreadPanel());
            _split.Panel2.Controls.Add(BuildMainPanel());
            wrap.Controls.Add(_split);
            return wrap;
        }

        Control BuildThreadPanel()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(S(14), S(12), S(14), S(12));

            var t = Ui.Table(1, 5);
            t.Dock = DockStyle.Fill;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var head = Ui.Table(2, 1);
            head.Dock = DockStyle.Fill;
            head.AutoSize = true;
            head.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            head.Margin = new Padding(0, 0, 0, S(10));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AutoRows(head, 1);
            var title = Ui.Lbl("选择会话", Theme.Text, Theme.H2);
            title.Margin = new Padding(0);
            head.Controls.Add(title, 0, 0);
            lblSel = Ui.Lbl("已选 0", Theme.Accent, Theme.Bold);
            lblSel.Anchor = AnchorStyles.Right;
            lblSel.Margin = new Padding(0);
            head.Controls.Add(lblSel, 1, 0);
            t.Controls.Add(head, 0, 0);

            txtSearch = new TextBox();
            txtSearch.Dock = DockStyle.Fill;
            txtSearch.Margin = new Padding(0, 0, 0, S(6));
            Ui.Cue(txtSearch, "搜索名称或 ID（Ctrl+F）");
            txtSearch.TextChanged += delegate { FillThreadList(); };
            txtSearch.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; e.SuppressKeyPress = true; txtSearch.Clear(); }
            };
            t.Controls.Add(txtSearch, 0, 1);

            var opt = Ui.Flow();
            opt.Margin = new Padding(0, 0, 0, S(6));
            chkOnlySel = new CheckBox();
            chkOnlySel.Text = "只看已选";
            chkOnlySel.AutoSize = true;
            chkOnlySel.ForeColor = Theme.Muted;
            chkOnlySel.Anchor = AnchorStyles.Left;
            chkOnlySel.Margin = new Padding(0);
            chkOnlySel.CheckedChanged += delegate { FillThreadList(); };
            opt.Controls.Add(chkOnlySel);
            t.Controls.Add(opt, 0, 2);

            var listBox = new Card();
            listBox.Dock = DockStyle.Fill;
            listBox.Margin = new Padding(0);
            lvThreads = new ListView();
            lvThreads.Dock = DockStyle.Fill;
            lvThreads.View = View.Details;
            lvThreads.CheckBoxes = true;
            lvThreads.FullRowSelect = true;
            lvThreads.HideSelection = false;
            lvThreads.MultiSelect = true;
            lvThreads.ShowItemToolTips = true;
            lvThreads.BorderStyle = BorderStyle.None;
            lvThreads.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvThreads.BackColor = Theme.Card;
            lvThreads.ForeColor = Theme.Text;
            lvThreads.Columns.Add("会话", S(190));
            lvThreads.Columns.Add("最近活跃", S(78));
            lvThreads.ItemChecked += OnThreadChecked;
            lvThreads.Resize += delegate { FitThreadColumns(); };
            lvThreads.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.C) { e.Handled = true; CopyThreadIds(); }
                else if (e.Control && e.KeyCode == Keys.A)
                {
                    e.Handled = true;
                    foreach (ListViewItem it in lvThreads.Items) it.Selected = true;
                }
            };
            Ui.ExplorerTheme(lvThreads);

            var menu = new ContextMenuStrip();
            menu.Font = Theme.Base;
            menu.Items.Add("勾选选中的会话", null, delegate { CheckSelectedThreads(true); });
            menu.Items.Add("取消勾选选中的会话", null, delegate { CheckSelectedThreads(false); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("复制会话 ID（Ctrl+C）", null, delegate { CopyThreadIds(); });
            lvThreads.ContextMenuStrip = menu;

            listBox.Controls.Add(lvThreads);
            t.Controls.Add(listBox, 0, 3);

            var btns = Ui.Flow();
            btns.Margin = new Padding(0, S(10), 0, 0);
            var bAll = Ui.Btn("全选", BtnKind.Secondary);
            bAll.Click += delegate { SelectVisible(true); };
            _tip.SetToolTip(bAll, "勾选当前列表里显示的全部会话（会受搜索过滤影响）");
            var bNone = Ui.Btn("清空", BtnKind.Secondary);
            bNone.Click += delegate { ClearChecked(); };
            _tip.SetToolTip(bNone, "取消所有勾选（包括被搜索隐藏的）");
            var bRefresh = Ui.Btn("刷新", BtnKind.Secondary);
            bRefresh.Margin = new Padding(0);
            bRefresh.Click += delegate { RefreshThreads(); };
            _tip.SetToolTip(bRefresh, "重新读取远端会话列表（F5）");
            btns.Controls.Add(bAll);
            btns.Controls.Add(bNone);
            btns.Controls.Add(bRefresh);
            t.Controls.Add(btns, 0, 4);

            card.Controls.Add(t);
            return card;
        }

        Control BuildMainPanel()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;

            var t = Ui.Table(1, 3);
            t.Dock = DockStyle.Fill;
            t.BackColor = Theme.Card;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(45)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var nav = Ui.Flow();
            nav.AutoSize = false;
            nav.Dock = DockStyle.Fill;
            nav.BackColor = Theme.Card;
            nav.Padding = new Padding(S(10), 0, 0, 0);
            _navs = new NavTab[] { new NavTab("新建任务"), new NavTab("任务列表"), new NavTab("运行日志") };
            for (int i = 0; i < _navs.Length; i++)
            {
                int idx = i;
                _navs[i].Click += delegate { SwitchPage(idx); };
                nav.Controls.Add(_navs[i]);
            }
            _tip.SetToolTip(_navs[0], "Ctrl+1");
            _tip.SetToolTip(_navs[1], "Ctrl+2");
            _tip.SetToolTip(_navs[2], "Ctrl+3");

            var content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Card;
            content.Margin = new Padding(0);
            _pages = new Control[] { BuildComposePage(), BuildJobsPage(), BuildLogsPage() };
            foreach (var p in _pages)
            {
                p.Dock = DockStyle.Fill;
                p.Visible = false;
                content.Controls.Add(p);
            }

            t.Controls.Add(nav, 0, 0);
            t.Controls.Add(Ui.Separator(), 0, 1);
            t.Controls.Add(content, 0, 2);
            card.Controls.Add(t);
            return card;
        }

        // ---------------------------------------------------------- 新建 / 编辑

        static int AddRow(TableLayoutPanel t, Control label, Control field)
        {
            int r = t.RowCount;
            t.RowCount = r + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label != null) t.Controls.Add(label, 0, r);
            if (field != null) t.Controls.Add(field, 1, r);
            return r;
        }

        Button QuickChip(string text, EventHandler onClick)
        {
            var b = Ui.Btn(text, BtnKind.Chip);
            b.Anchor = AnchorStyles.Left;
            b.Click += onClick;
            return b;
        }

        Control BuildComposePage()
        {
            var page = Ui.Table(1, 3);
            page.BackColor = Theme.Card;
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _composeScroll = new Panel();
            _composeScroll.Dock = DockStyle.Fill;
            _composeScroll.AutoScroll = true;
            _composeScroll.BackColor = Theme.Card;
            _composeScroll.Padding = new Padding(S(24), S(18), S(24), S(8));
            _composeScroll.Margin = new Padding(0);

            var f = Ui.Table(2, 0);
            f.Dock = DockStyle.Top;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.BackColor = Theme.Card;
            f.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(84)));
            f.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _composeScroll.Controls.Add(f);

            // 编辑提示条
            editBanner = Ui.Table(2, 1);
            editBanner.Dock = DockStyle.Fill;
            editBanner.AutoSize = true;
            editBanner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            editBanner.BackColor = Theme.AccentSoft;
            editBanner.Padding = new Padding(S(12), S(6), S(6), S(6));
            editBanner.Margin = new Padding(0, 0, 0, S(16));
            editBanner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AutoRows(editBanner, 1);
            lblEditing = Ui.Lbl("", Theme.Accent, Theme.Bold);
            lblEditing.AutoSize = false;
            lblEditing.AutoEllipsis = true;
            lblEditing.Dock = DockStyle.Fill;
            lblEditing.TextAlign = ContentAlignment.MiddleLeft;
            var bCancelEdit = Ui.Btn("取消编辑", BtnKind.Ghost);
            bCancelEdit.BackColor = Theme.AccentSoft;
            bCancelEdit.FlatAppearance.MouseOverBackColor = Theme.AccentSoft2;
            bCancelEdit.Margin = new Padding(0);
            bCancelEdit.Click += delegate { ExitEdit(true); };
            editBanner.Controls.Add(lblEditing, 0, 0);
            editBanner.Controls.Add(bCancelEdit, 1, 0);
            int br = AddRow(f, null, null);
            f.Controls.Add(editBanner, 0, br);
            f.SetColumnSpan(editBanner, 2);
            editBanner.Visible = false;

            // 任务名称
            txtTitle = new TextBox();
            txtTitle.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            txtTitle.Margin = new Padding(0, 0, 0, S(14));
            Ui.Cue(txtTitle, "可留空，自动生成（例如：3 个会话 · 继续）");
            AddRow(f, Ui.FieldLabel("任务名称"), txtTitle);

            // 发送内容
            var msgBox = Ui.Table(1, 2);
            msgBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            msgBox.AutoSize = true;
            msgBox.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            msgBox.Margin = new Padding(0, 0, 0, S(12));
            msgBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AutoRows(msgBox, 2);

            txtMessage = new TextBox();
            txtMessage.Multiline = true;
            txtMessage.AcceptsReturn = true;
            txtMessage.ScrollBars = ScrollBars.Vertical;
            txtMessage.Height = S(104);
            txtMessage.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            txtMessage.Margin = new Padding(0, 0, 0, S(6));
            txtMessage.TextChanged += delegate { UpdateCount(); UpdateSummary(); };
            msgBox.Controls.Add(txtMessage, 0, 0);

            var under = Ui.Table(2, 1);
            under.Dock = DockStyle.Fill;
            under.AutoSize = true;
            under.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            under.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            under.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AutoRows(under, 1);
            flowPresets = Ui.Flow();
            flowPresets.WrapContents = true;
            flowPresets.Dock = DockStyle.Fill;
            lblCount = Ui.Lbl("", Theme.Subtle, Theme.Small);
            lblCount.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            lblCount.Margin = new Padding(S(8), S(6), 0, 0);
            under.Controls.Add(flowPresets, 0, 0);
            under.Controls.Add(lblCount, 1, 0);
            msgBox.Controls.Add(under, 0, 1);
            AddRow(f, Ui.FieldLabel("发送内容"), msgBox);

            // 发送周期
            var types = Ui.Flow();
            types.Margin = new Padding(0, 0, 0, S(10));
            rbOnce = Ui.Segment("单次");
            rbDaily = Ui.Segment("每天");
            rbWeekly = Ui.Segment("每周");
            rbInterval = Ui.Segment("间隔");
            foreach (var rb in new RadioButton[] { rbOnce, rbDaily, rbWeekly, rbInterval })
            {
                var r = rb;
                r.CheckedChanged += delegate { if (r.Checked) SwitchType(); };
                types.Controls.Add(r);
            }
            AddRow(f, Ui.FieldLabel("发送周期"), types);

            // 单次
            fOnce = Ui.Flow();
            fOnce.WrapContents = true;
            fOnce.Dock = DockStyle.Fill;
            fOnce.Margin = new Padding(0, 0, 0, S(14));
            dtOnce = Ui.Picker("yyyy-MM-dd  HH:mm", 176);
            dtOnce.ValueChanged += delegate { UpdateSummary(); };
            fOnce.Controls.Add(dtOnce);
            fOnce.Controls.Add(QuickChip("+10 分钟", delegate { SetOnce(DateTime.Now.AddMinutes(10)); }));
            fOnce.Controls.Add(QuickChip("+1 小时", delegate { SetOnce(DateTime.Now.AddHours(1)); }));
            fOnce.Controls.Add(QuickChip("今晚 22:00", delegate
            {
                var d = DateTime.Today.AddHours(22);
                SetOnce(d > DateTime.Now ? d : d.AddDays(1));
            }));
            fOnce.Controls.Add(QuickChip("明早 09:00", delegate { SetOnce(DateTime.Today.AddDays(1).AddHours(9)); }));
            AddRow(f, null, fOnce);

            // 每天
            fDaily = Ui.Flow();
            fDaily.WrapContents = true;
            fDaily.Dock = DockStyle.Fill;
            fDaily.Margin = new Padding(0, 0, 0, S(14));
            fDaily.Controls.Add(Ui.Lbl("每天", Theme.Muted, Theme.Base));
            dtDaily = Ui.Picker("HH:mm", 88);
            dtDaily.ShowUpDown = true;
            dtDaily.ValueChanged += delegate { UpdateSummary(); };
            fDaily.Controls.Add(dtDaily);
            foreach (var hm in new string[] { "02:00", "09:00", "13:00", "22:00" })
            {
                string v = hm;
                fDaily.Controls.Add(QuickChip(v, delegate { Ui.SetValueSafe(dtDaily, Scheduler.AtTime(DateTime.Today, v)); }));
            }
            AddRow(f, null, fDaily);

            // 每周
            fWeeklyDays = Ui.Flow();
            fWeeklyDays.WrapContents = true;
            fWeeklyDays.Dock = DockStyle.Fill;
            fWeeklyDays.Margin = new Padding(0, 0, 0, S(8));
            string[] dayNames = new string[] { "一", "二", "三", "四", "五", "六", "日" };
            int[] dayVals = new int[] { 1, 2, 3, 4, 5, 6, 0 };
            for (int i = 0; i < 7; i++)
            {
                var cb = Ui.Toggle(dayNames[i]);
                cb.Tag = dayVals[i];
                cb.CheckedChanged += delegate { UpdateSummary(); };
                chkDays[i] = cb;
                fWeeklyDays.Controls.Add(cb);
            }
            AddRow(f, null, fWeeklyDays);

            fWeeklyTime = Ui.Flow();
            fWeeklyTime.WrapContents = true;
            fWeeklyTime.Dock = DockStyle.Fill;
            fWeeklyTime.Margin = new Padding(0, 0, 0, S(14));
            fWeeklyTime.Controls.Add(Ui.Lbl("时间", Theme.Muted, Theme.Base));
            dtWeekly = Ui.Picker("HH:mm", 88);
            dtWeekly.ShowUpDown = true;
            dtWeekly.ValueChanged += delegate { UpdateSummary(); };
            fWeeklyTime.Controls.Add(dtWeekly);
            fWeeklyTime.Controls.Add(QuickChip("工作日", delegate { SetDays(new int[] { 1, 2, 3, 4, 5 }); }));
            fWeeklyTime.Controls.Add(QuickChip("周末", delegate { SetDays(new int[] { 6, 0 }); }));
            fWeeklyTime.Controls.Add(QuickChip("每天", delegate { SetDays(new int[] { 0, 1, 2, 3, 4, 5, 6 }); }));
            AddRow(f, null, fWeeklyTime);

            // 间隔
            fInterval = Ui.Flow();
            fInterval.WrapContents = true;
            fInterval.Dock = DockStyle.Fill;
            fInterval.Margin = new Padding(0, 0, 0, S(14));
            fInterval.Controls.Add(Ui.Lbl("每隔", Theme.Muted, Theme.Base));
            numEvery = Ui.Num(1, 100000, 2, 72);
            numEvery.ValueChanged += delegate { UpdateSummary(); };
            fInterval.Controls.Add(numEvery);
            cbUnit = new ComboBox();
            cbUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            cbUnit.Items.AddRange(new object[] { "分钟", "小时" });
            cbUnit.SelectedIndex = 1;
            cbUnit.Width = S(70);
            cbUnit.Anchor = AnchorStyles.Left;
            cbUnit.Margin = new Padding(0, 0, S(16), 0);
            cbUnit.SelectedIndexChanged += delegate { UpdateSummary(); };
            fInterval.Controls.Add(cbUnit);
            fInterval.Controls.Add(Ui.Lbl("首次", Theme.Muted, Theme.Base));
            dtIntervalStart = Ui.Picker("yyyy-MM-dd  HH:mm", 196);
            dtIntervalStart.ShowCheckBox = true;
            dtIntervalStart.ValueChanged += delegate { UpdateSummary(); };
            _tip.SetToolTip(dtIntervalStart, "勾选后从这个时间开始按间隔触发；不勾选则从保存时开始计算");
            fInterval.Controls.Add(dtIntervalStart);
            AddRow(f, null, fInterval);

            // 结束条件
            fEnd = Ui.Flow();
            fEnd.WrapContents = true;
            fEnd.Dock = DockStyle.Fill;
            fEnd.Margin = new Padding(0, 0, 0, S(14));
            fEnd.Controls.Add(Ui.Lbl("最多执行", Theme.Muted, Theme.Base));
            numMaxRuns = Ui.Num(0, 100000, 0, 72);
            numMaxRuns.ValueChanged += delegate { UpdateSummary(); };
            fEnd.Controls.Add(numMaxRuns);
            var lTimes = Ui.Lbl("次（0 = 不限）", Theme.Muted, Theme.Base);
            lTimes.Margin = new Padding(0, 0, S(20), 0);
            fEnd.Controls.Add(lTimes);
            fEnd.Controls.Add(Ui.Lbl("截止日期", Theme.Muted, Theme.Base));
            dtEnd = Ui.Picker("yyyy-MM-dd", 150);
            dtEnd.ShowCheckBox = true;
            dtEnd.ValueChanged += delegate { UpdateSummary(); };
            _tip.SetToolTip(dtEnd, "勾选后，过了这一天的 23:59 就不再触发");
            fEnd.Controls.Add(dtEnd);
            lblEnd = Ui.FieldLabel("结束条件");
            AddRow(f, lblEnd, fEnd);

            // 下次执行摘要
            lblSummary = Ui.Lbl("", Theme.Accent, Theme.Bold);
            lblSummary.AutoSize = false;
            lblSummary.AutoEllipsis = true;
            lblSummary.Height = S(28);
            lblSummary.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            lblSummary.Margin = new Padding(0, S(1), 0, S(10));
            var lblNext = Ui.FieldLabel("下次执行");
            lblNext.Margin = new Padding(0, S(6), S(10), S(10));
            AddRow(f, lblNext, lblSummary);

            // 命令预览
            txtPreview = new TextBox();
            txtPreview.Multiline = true;
            txtPreview.ReadOnly = true;
            txtPreview.WordWrap = false;
            txtPreview.ScrollBars = ScrollBars.Both;
            txtPreview.Font = Theme.Mono;
            txtPreview.BackColor = Theme.Header;
            txtPreview.Height = S(120);
            txtPreview.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            txtPreview.Margin = new Padding(0, 0, 0, S(10));
            txtPreview.Visible = false;
            AddRow(f, null, txtPreview);

            // 底部固定操作栏：窗口再小也不会被挤出可视区
            var bar = Ui.Table(3, 1);
            bar.Dock = DockStyle.Fill;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.BackColor = Theme.Header;
            bar.Padding = new Padding(S(20), S(10), S(20), S(10));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AutoRows(bar, 1);

            var left = Ui.Flow();
            btnSendNow = Ui.Btn("立即发送一次", BtnKind.Secondary);
            btnSendNow.Click += delegate { DoSendNow(); };
            _tip.SetToolTip(btnSendNow, "不保存任务，马上向勾选的会话发送当前内容");
            btnPreview = Ui.Btn("预览命令", BtnKind.Ghost);
            btnPreview.BackColor = Theme.Header;
            btnPreview.Click += delegate { DoPreview(); };
            left.Controls.Add(btnSendNow);
            left.Controls.Add(btnPreview);

            var right = Ui.Flow();
            btnReset = Ui.Btn("清空表单", BtnKind.Ghost);
            btnReset.BackColor = Theme.Header;
            btnReset.Click += delegate { ExitEdit(true); };
            _tip.SetToolTip(btnReset, "恢复默认值，开始写一条新任务（Ctrl+N）");
            btnSave = Ui.Btn("保存任务", BtnKind.Primary);
            btnSave.MinimumSize = new Size(S(104), S(32));
            btnSave.Margin = new Padding(0);
            btnSave.Click += delegate { DoSave(); };
            _tip.SetToolTip(btnSave, "Ctrl+S");
            right.Controls.Add(btnReset);
            right.Controls.Add(btnSave);

            bar.Controls.Add(left, 0, 0);
            bar.Controls.Add(right, 2, 0);

            page.Controls.Add(_composeScroll, 0, 0);
            page.Controls.Add(Ui.Separator(), 0, 1);
            page.Controls.Add(bar, 0, 2);
            return page;
        }

        // ---------------------------------------------------------- 任务列表

        Control BuildJobsPage()
        {
            var page = Ui.Table(1, 3);
            page.BackColor = Theme.Card;
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var bar = Ui.Table(2, 1);
            bar.Dock = DockStyle.Fill;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.Padding = new Padding(S(16), S(10), S(16), S(10));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AutoRows(bar, 1);

            var left = Ui.Flow();
            left.WrapContents = true;
            left.Dock = DockStyle.Fill;
            bNew = Ui.Btn("＋ 新建", BtnKind.Primary);
            bNew.Click += delegate { NewJob(); };
            _tip.SetToolTip(bNew, "Ctrl+N");
            bRun = Ui.Btn("立即执行", BtnKind.Secondary);
            bRun.Click += delegate { RunSelected(); };
            bEdit = Ui.Btn("编辑", BtnKind.Secondary);
            bEdit.Click += delegate { EditSelected(false); };
            _tip.SetToolTip(bEdit, "双击行或按回车也可以编辑");
            bCopy = Ui.Btn("复制", BtnKind.Secondary);
            bCopy.Click += delegate { EditSelected(true); };
            _tip.SetToolTip(bCopy, "以这条任务为模板新建一条");
            bToggle = Ui.Btn("暂停", BtnKind.Secondary);
            bToggle.Click += delegate { ToggleSelected(); };
            _tip.SetToolTip(bToggle, "空格键");
            bDel = Ui.Btn("删除", BtnKind.Danger);
            bDel.Click += delegate { DeleteSelected(); };
            _tip.SetToolTip(bDel, "Delete 键");
            foreach (var b in new Button[] { bNew, bRun, bEdit, bCopy, bToggle, bDel })
            {
                b.Margin = new Padding(0, S(2), S(8), S(2));
                left.Controls.Add(b);
            }
            lblJobHint = Ui.Lbl("", Theme.Muted, Theme.Small);
            lblJobHint.Anchor = AnchorStyles.Right;
            lblJobHint.Margin = new Padding(S(8), 0, 0, 0);
            bar.Controls.Add(left, 0, 0);
            bar.Controls.Add(lblJobHint, 1, 0);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Theme.Card;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Theme.Border;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.ReadOnly = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.ShowCellToolTips = true;
            grid.StandardTab = true;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = S(36);
            grid.RowTemplate.Height = S(40);
            grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Header;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Header;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;
            grid.ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(8), 0, S(4), 0);
            grid.DefaultCellStyle.BackColor = Theme.Card;
            grid.DefaultCellStyle.ForeColor = Theme.Text;
            grid.DefaultCellStyle.SelectionBackColor = Theme.AccentSoft2;
            grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
            grid.DefaultCellStyle.Padding = new Padding(S(8), 0, S(4), 0);
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.DefaultCellStyle.Font = Theme.Base;
            Ui.DoubleBuffer(grid);

            AddCol("status", "状态", 0);
            AddCol("title", "任务", 30);
            AddCol("threads", "会话", 22);
            AddCol("sched", "周期", 22);
            AddCol("next", "下次执行", 24);
            AddCol("last", "上次结果", 16);

            grid.SelectionChanged += delegate { UpdateJobButtons(); };
            grid.KeyDown += delegate(object s, KeyEventArgs e)
            {
                // ProcessCmdKey 已处理时这里不会再触发；兜底防止 DataGridView 自己吞掉回车
                if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None) { e.Handled = true; e.SuppressKeyPress = true; EditSelected(false); }
                else if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None) { e.Handled = true; e.SuppressKeyPress = true; DeleteSelected(); }
            };
            grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) EditSelected(false); };
            grid.CellMouseDown += delegate(object s, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.RowIndex < grid.Rows.Count)
                {
                    grid.ClearSelection();
                    grid.Rows[e.RowIndex].Selected = true;
                    try { grid.CurrentCell = grid.Rows[e.RowIndex].Cells[1]; } catch { }
                }
            };

            var menu = new ContextMenuStrip();
            menu.Font = Theme.Base;
            miRun = new ToolStripMenuItem("立即执行", null, delegate { RunSelected(); });
            miEdit = new ToolStripMenuItem("编辑", null, delegate { EditSelected(false); });
            miCopy = new ToolStripMenuItem("复制为新任务", null, delegate { EditSelected(true); });
            miToggle = new ToolStripMenuItem("暂停", null, delegate { ToggleSelected(); });
            miDel = new ToolStripMenuItem("删除", null, delegate { DeleteSelected(); });
            miDel.ForeColor = Theme.Bad;
            menu.Items.Add(miRun);
            menu.Items.Add(miEdit);
            menu.Items.Add(miCopy);
            menu.Items.Add(miToggle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miDel);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("新建任务", null, delegate { NewJob(); });
            menu.Opening += delegate { UpdateJobButtons(); };
            grid.ContextMenuStrip = menu;

            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Margin = new Padding(0);
            host.BackColor = Theme.Card;
            lblEmpty = new Label();
            lblEmpty.Dock = DockStyle.Fill;
            lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblEmpty.ForeColor = Theme.Subtle;
            lblEmpty.BackColor = Theme.Card;
            lblEmpty.Font = Theme.Base;
            lblEmpty.Text = "还没有任务\r\n\r\n在左侧勾选会话，到「新建任务」里写好内容和时间后保存";
            lblEmpty.Visible = false;
            host.Controls.Add(grid);
            host.Controls.Add(lblEmpty);
            lblEmpty.BringToFront();

            page.Controls.Add(bar, 0, 0);
            page.Controls.Add(Ui.Separator(), 0, 1);
            page.Controls.Add(host, 0, 2);
            return page;
        }

        void AddCol(string name, string header, int fillWeight)
        {
            int i = grid.Columns.Add(name, header);
            var c = grid.Columns[i];
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            if (fillWeight <= 0)
            {
                c.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                c.Width = S(104);
            }
            else
            {
                c.FillWeight = fillWeight;
                c.MinimumWidth = S(60);
            }
        }

        // ---------------------------------------------------------- 运行日志

        Control BuildLogsPage()
        {
            var page = Ui.Table(1, 3);
            page.BackColor = Theme.Card;
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var bar = Ui.Table(2, 1);
            bar.Dock = DockStyle.Fill;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.Padding = new Padding(S(16), S(10), S(16), S(10));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AutoRows(bar, 1);

            var left = Ui.Flow();
            left.WrapContents = true;
            left.Dock = DockStyle.Fill;
            var bRefresh = Ui.Btn("刷新", BtnKind.Secondary);
            bRefresh.Click += delegate { FillLogs(); };
            chkFailOnly = new CheckBox();
            chkFailOnly.Text = "只看失败";
            chkFailOnly.AutoSize = true;
            chkFailOnly.ForeColor = Theme.Text;
            chkFailOnly.Anchor = AnchorStyles.Left;
            chkFailOnly.Margin = new Padding(S(4), 0, S(16), 0);
            chkFailOnly.CheckedChanged += delegate { FillLogs(); };
            var bOpen = Ui.Btn("打开数据目录", BtnKind.Ghost);
            bOpen.Click += delegate { OpenDataDir(); };
            _tip.SetToolTip(bOpen, Paths.Dir);
            var bClear = Ui.Btn("清空日志", BtnKind.Danger);
            bClear.Click += delegate { ClearLogs(); };
            foreach (Control c in new Control[] { bRefresh, chkFailOnly, bOpen, bClear })
            {
                if (c is Button) c.Margin = new Padding(0, S(2), S(8), S(2));
                left.Controls.Add(c);
            }
            var hint = Ui.Lbl("选中一行，下方显示完整输出", Theme.Subtle, Theme.Small);
            hint.Anchor = AnchorStyles.Right;
            hint.Margin = new Padding(S(8), 0, 0, 0);
            bar.Controls.Add(left, 0, 0);
            bar.Controls.Add(hint, 1, 0);

            _logSplit = new SplitContainer();
            _logSplit.Size = new Size(2400, 1400);
            _logSplit.Orientation = Orientation.Horizontal;
            _logSplit.Dock = DockStyle.Fill;
            _logSplit.SplitterWidth = S(6);
            _logSplit.BackColor = Theme.Border;
            _logSplit.Panel1.BackColor = Theme.Card;
            _logSplit.Panel2.BackColor = Theme.Header;
            _logSplit.Panel2.Padding = new Padding(S(14), S(10), S(8), S(6));
            _logSplit.TabStop = false;

            lvLogs = new ListView();
            lvLogs.Dock = DockStyle.Fill;
            lvLogs.View = View.Details;
            lvLogs.FullRowSelect = true;
            lvLogs.HideSelection = false;
            lvLogs.MultiSelect = false;
            lvLogs.ShowItemToolTips = true;
            lvLogs.BorderStyle = BorderStyle.None;
            lvLogs.BackColor = Theme.Card;
            lvLogs.ForeColor = Theme.Text;
            lvLogs.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvLogs.Columns.Add("时间", S(118));
            lvLogs.Columns.Add("任务", S(200));
            lvLogs.Columns.Add("结果", S(110));
            lvLogs.Columns.Add("触发", S(52));
            lvLogs.Columns.Add("内容", S(260));
            lvLogs.SelectedIndexChanged += delegate { ShowLogDetail(); };
            lvLogs.Resize += delegate { FitLogColumns(); };
            Ui.ExplorerTheme(lvLogs);
            _logSplit.Panel1.Controls.Add(lvLogs);

            txtLogDetail = new TextBox();
            txtLogDetail.Dock = DockStyle.Fill;
            txtLogDetail.Multiline = true;
            txtLogDetail.ReadOnly = true;
            txtLogDetail.WordWrap = false;
            txtLogDetail.ScrollBars = ScrollBars.Both;
            txtLogDetail.BorderStyle = BorderStyle.None;
            txtLogDetail.BackColor = Theme.Header;
            txtLogDetail.ForeColor = Theme.Text;
            txtLogDetail.Font = Theme.Mono;
            _logSplit.Panel2.Controls.Add(txtLogDetail);

            page.Controls.Add(bar, 0, 0);
            page.Controls.Add(Ui.Separator(), 0, 1);
            page.Controls.Add(_logSplit, 0, 2);
            return page;
        }

        // ---------------------------------------------------------- 托盘

        NotifyIcon tray;
        ToolStripMenuItem trayPause, trayAuto;

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.Text = "Codex 定时发送台";

            var menu = new ContextMenuStrip();
            menu.Font = Theme.Base;
            var miOpen = new ToolStripMenuItem("打开主界面", null, delegate { ShowMain(); });
            miOpen.Font = Theme.Bold;
            menu.Items.Add(miOpen);
            menu.Items.Add(new ToolStripSeparator());
            trayPause = new ToolStripMenuItem("暂停所有定时任务", null, delegate { SetPaused(!_settings.Paused); });
            menu.Items.Add(trayPause);
            trayAuto = new ToolStripMenuItem("开机自启动（后台运行）", null, delegate
            {
                bool on = !trayAuto.Checked;
                if (AutoStart.Set(on))
                {
                    trayAuto.Checked = on;
                    _settings.AutoStart = on;
                    Store.SaveSettings(_settings);
                }
            });
            trayAuto.Checked = AutoStart.IsOn();
            menu.Items.Add(trayAuto);
            menu.Items.Add("打开数据目录", null, delegate { OpenDataDir(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMain(); };
            tray.Visible = true;
        }

        // ---------------------------------------------------------- 自检：导出控件树

        public string DumpLayout()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("FORM " + Text + "  client=" + ClientSize.Width + "x" + ClientSize.Height + "  scale=" + Theme.Scale);
            Walk(this, sb, 0);
            return sb.ToString();
        }

        public int PageCount { get { return _pages == null ? 0 : _pages.Length; } }

        public void SelectPage(int i) { SwitchPage(i); }

        public string DumpPage(int i)
        {
            if (_pages == null || i < 0 || i >= _pages.Length) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("--- page[" + i + "] " + _navs[i].Text + "  size=" + _pages[i].Width + "x" + _pages[i].Height);
            Walk(_pages[i], sb, 0);
            return sb.ToString();
        }

        static void Walk(Control parent, System.Text.StringBuilder sb, int depth)
        {
            if (depth > 12) return;
            foreach (Control c in parent.Controls)
            {
                string txt = c.Text ?? "";
                if (c is TextBox) txt = "";
                txt = txt.Replace("\r", " ").Replace("\n", " ");
                if (txt.Length > 22) txt = txt.Substring(0, 22) + "…";
                var b = c.Bounds;
                bool clipped = parent.ClientSize.Width > 0 && (b.Right > parent.ClientSize.Width + 1 || b.Bottom > parent.ClientSize.Height + 1) && !(parent is ScrollableControl && ((ScrollableControl)parent).AutoScroll);
                sb.AppendLine(new string(' ', depth * 2) + c.GetType().Name
                    + " '" + txt + "' bounds=" + b.X + "," + b.Y + "," + b.Width + "x" + b.Height
                    + " vis=" + c.Visible + (clipped && c.Visible ? "  !!CLIPPED" : ""));
                Walk(c, sb, depth + 1);
            }
        }
    }
}
