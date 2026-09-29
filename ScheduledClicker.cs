// Scheduled Clicker - Windows 11 / .NET Framework 4.8 (WinForms)
// Schedule mouse clicks at specific times, at X/Y screen coordinates,
// with a configurable gap between clicks.
//
// Written in C# 5 syntax on purpose, so it compiles with the csc.exe that is
// already built into Windows (see build.bat) - no Visual Studio / SDK needed.
//
// Emergency stop: press F8 at any time (or click Stop).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace ScheduledClicker
{
    // ------------------------------------------------------------------
    //  Win32 helpers
    // ------------------------------------------------------------------
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        public const int VK_F8 = 0x77;

        public static bool F8Pressed()
        {
            return (GetAsyncKeyState(VK_F8) & 0x8000) != 0;
        }

        public static void GetCursor(out int x, out int y)
        {
            POINT p;
            GetCursorPos(out p);
            x = p.X;
            y = p.Y;
        }

        public static void Click(int x, int y, string button)
        {
            uint down, up;
            switch (button)
            {
                case "right": down = 0x0008; up = 0x0010; break;
                case "middle": down = 0x0020; up = 0x0040; break;
                default: down = 0x0002; up = 0x0004; break;
            }
            SetCursorPos(x, y);
            Thread.Sleep(20);
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(20);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // ------------------------------------------------------------------
    //  Model
    // ------------------------------------------------------------------
    internal sealed class Job
    {
        public string Time;     // "HH:mm:ss"
        public int X;
        public int Y;
        public int Clicks;
        public double Gap;      // seconds between clicks
        public string Button;   // left / right / middle
        public bool Daily;

        // runtime only
        public DateTime NextRun;
        public bool HasNext;
        public string Status;

        public Job()
        {
            Clicks = 1;
            Gap = 1.0;
            Button = "left";
            Status = "Idle";
        }
    }

    internal sealed class StopRequestedException : Exception { }

    // Lets the mouse wheel change the start time while the pointer hovers over it
    internal sealed class WheelFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly MainForm owner;

        public WheelFilter(MainForm owner) { this.owner = owner; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            long lp = m.LParam.ToInt64();
            int x = (short)(lp & 0xFFFF);
            int y = (short)((lp >> 16) & 0xFFFF);
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            return owner.HandleTimeWheel(new Point(x, y), delta);
        }
    }

    // ------------------------------------------------------------------
    //  Main window
    // ------------------------------------------------------------------
    internal sealed class MainForm : Form
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] TimeFormats = { "H:m:s", "H:m" };
        private static readonly string[] ButtonNames = { "left", "right", "middle" };
        private static readonly string SaveFile =
            Path.Combine(Application.StartupPath, "clicker_jobs.json");

        private readonly List<Job> jobs = new List<Job>();
        private float scale = 1f;

        // controls
        private TextBox txtTime, txtX, txtY, txtClicks, txtGap, logBox;
        private CheckBox chkDaily;
        private ComboBox cmbButton;
        private Label lblCursor, lblState;
        private DataGridView grid;
        private Button btnPick, btnTest, btnAdd, btnRemove, btnClear, btnImport, btnStart, btnStop;

        // state
        private Thread worker;
        private volatile bool stopFlag;
        private bool running;
        private bool refreshing;
        private Job selectAfter;
        private int wheelAccum;
        private WheelFilter wheelFilter;
        private WinTimer cursorTimer;
        private WinTimer countdownTimer;
        private int countdownLeft;
        private string countdownFormat;
        private string countdownMessage;
        private Action countdownDone;

        public MainForm()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                scale = g.DpiX / 96f;
            }

            Text = "Scheduled Clicker";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }   // mouse icon embedded in the .exe
            catch (Exception) { }
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();

            wheelFilter = new WheelFilter(this);
            Application.AddMessageFilter(wheelFilter);

            cursorTimer = new WinTimer();
            cursorTimer.Interval = 100;
            cursorTimer.Tick += delegate { UpdateCursorLabel(); };
            cursorTimer.Start();

            LoadJobs();
        }

        // ------------------------------------------------------ UI build
        private int S(int v) { return (int)Math.Round(v * scale); }

        private Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(S(6));
            return l;
        }

        private TextBox MakeBox(string text, int width)
        {
            TextBox b = new TextBox();
            b.Text = text;
            b.Width = S(width);
            b.Anchor = AnchorStyles.Left;
            b.Margin = new Padding(S(6));
            return b;
        }

        private Button MakeButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(S(8), S(2), S(8), S(2));
            b.Margin = new Padding(S(4));
            b.Anchor = AnchorStyles.Left;
            b.UseVisualStyleBackColor = true;
            return b;
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Padding = new Padding(S(10));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // editor
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));      // job list
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // list buttons
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // start / stop
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));      // log
            Controls.Add(root);

            // ---- editor ----
            GroupBox gbEditor = new GroupBox();
            gbEditor.Text = "New click job";
            gbEditor.AutoSize = true;
            gbEditor.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            gbEditor.Dock = DockStyle.Fill;
            gbEditor.Padding = new Padding(S(6));

            TableLayoutPanel ed = new TableLayoutPanel();
            ed.AutoSize = true;
            ed.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ed.ColumnCount = 5;
            ed.RowCount = 5;
            ed.Location = new Point(S(6), S(20));
            for (int i = 0; i < 5; i++) ed.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            txtTime = MakeBox(DateTime.Now.AddMinutes(1).ToString("HH:mm:ss", Inv), 90);
            txtTime.KeyDown += TxtTimeKeyDown;
            chkDaily = new CheckBox();
            chkDaily.Text = "Repeat daily";
            chkDaily.AutoSize = true;
            chkDaily.Anchor = AnchorStyles.Left;
            chkDaily.Margin = new Padding(S(6));

            txtX = MakeBox("500", 80);
            txtY = MakeBox("500", 80);
            btnPick = MakeButton("Pick position (3s)");
            btnPick.Click += delegate { OnPickPosition(); };

            txtClicks = MakeBox("5", 80);
            txtGap = MakeBox("1.0", 80);

            cmbButton = new ComboBox();
            cmbButton.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbButton.Items.AddRange(new object[] { "left", "right", "middle" });
            cmbButton.SelectedIndex = 0;
            cmbButton.Width = S(90);
            cmbButton.Anchor = AnchorStyles.Left;
            cmbButton.Margin = new Padding(S(6));

            btnAdd = MakeButton("Add job");
            btnAdd.Click += delegate { OnAddJob(); };
            btnTest = MakeButton("Test click (3s)");
            btnTest.Click += delegate { OnTestClick(); };

            lblCursor = new Label();
            lblCursor.AutoSize = false;
            lblCursor.Width = S(560);
            lblCursor.Height = S(22);
            lblCursor.ForeColor = Color.FromArgb(90, 90, 90);
            lblCursor.Anchor = AnchorStyles.Left;
            lblCursor.Margin = new Padding(S(6));
            lblCursor.TextAlign = ContentAlignment.MiddleLeft;

            Label lblY = MakeLabel("Y");
            lblY.Anchor = AnchorStyles.Right;
            Label lblGap = MakeLabel("Gap between clicks (sec)");
            lblGap.Anchor = AnchorStyles.Right;

            ed.Controls.Add(MakeLabel("Start time (scroll to change)"), 0, 0);
            ed.Controls.Add(txtTime, 1, 0);
            ed.Controls.Add(chkDaily, 2, 0);
            ed.SetColumnSpan(chkDaily, 2);

            ed.Controls.Add(MakeLabel("X"), 0, 1);
            ed.Controls.Add(txtX, 1, 1);
            ed.Controls.Add(lblY, 2, 1);
            ed.Controls.Add(txtY, 3, 1);
            ed.Controls.Add(btnPick, 4, 1);

            ed.Controls.Add(MakeLabel("Number of clicks"), 0, 2);
            ed.Controls.Add(txtClicks, 1, 2);
            ed.Controls.Add(lblGap, 2, 2);
            ed.Controls.Add(txtGap, 3, 2);

            ed.Controls.Add(MakeLabel("Mouse button"), 0, 3);
            ed.Controls.Add(cmbButton, 1, 3);
            ed.Controls.Add(btnAdd, 3, 3);
            ed.Controls.Add(btnTest, 4, 3);

            ed.Controls.Add(lblCursor, 0, 4);
            ed.SetColumnSpan(lblCursor, 5);

            gbEditor.Controls.Add(ed);
            root.Controls.Add(gbEditor, 0, 0);

            // ---- job grid ----
            GroupBox gbList = new GroupBox();
            gbList.Text = "Scheduled jobs  (double-click any cell to edit it)";
            gbList.Dock = DockStyle.Fill;
            gbList.Padding = new Padding(S(8));

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.EnableHeadersVisualStyles = true;

            AddTextColumn("colTime", "Time", 110, false);
            AddTextColumn("colX", "X", 60, false);
            AddTextColumn("colY", "Y", 60, false);
            AddTextColumn("colClicks", "Clicks", 70, false);
            AddTextColumn("colGap", "Gap (s)", 70, false);

            DataGridViewComboBoxColumn cb = new DataGridViewComboBoxColumn();
            cb.Name = "colButton";
            cb.HeaderText = "Button";
            cb.FillWeight = 80;
            cb.FlatStyle = FlatStyle.Flat;
            cb.Items.AddRange(new object[] { "left", "right", "middle" });
            cb.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(cb);

            DataGridViewCheckBoxColumn ck = new DataGridViewCheckBoxColumn();
            ck.Name = "colDaily";
            ck.HeaderText = "Daily";
            ck.FillWeight = 50;
            ck.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(ck);

            AddTextColumn("colStatus", "Status", 90, true);

            grid.CellEndEdit += OnGridCellEndEdit;
            grid.CellValueChanged += OnGridCellValueChanged;
            grid.CurrentCellDirtyStateChanged += OnGridDirtyStateChanged;
            grid.DataError += delegate(object s, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };

            gbList.Controls.Add(grid);
            root.Controls.Add(gbList, 0, 1);

            // ---- list buttons ----
            FlowLayoutPanel flList = new FlowLayoutPanel();
            flList.AutoSize = true;
            flList.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flList.Dock = DockStyle.Fill;
            flList.WrapContents = false;
            btnRemove = MakeButton("Remove selected");
            btnRemove.Click += delegate { OnRemoveSelected(); };
            btnClear = MakeButton("Clear all");
            btnClear.Click += delegate { OnClearAll(); };
            btnImport = MakeButton("Import from JSON...");
            btnImport.Click += delegate { OnImport(); };
            flList.Controls.Add(btnRemove);
            flList.Controls.Add(btnClear);
            flList.Controls.Add(btnImport);
            root.Controls.Add(flList, 0, 2);

            // ---- start / stop ----
            TableLayoutPanel ctl = new TableLayoutPanel();
            ctl.AutoSize = true;
            ctl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ctl.Dock = DockStyle.Fill;
            ctl.ColumnCount = 2;
            ctl.RowCount = 1;
            ctl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ctl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            FlowLayoutPanel flRun = new FlowLayoutPanel();
            flRun.AutoSize = true;
            flRun.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flRun.WrapContents = false;
            btnStart = MakeButton("\u25B6  Start scheduler");
            btnStart.Click += delegate { OnStart(); };
            btnStop = MakeButton("\u25A0  Stop (F8)");
            btnStop.Enabled = false;
            btnStop.Click += delegate { stopFlag = true; };
            flRun.Controls.Add(btnStart);
            flRun.Controls.Add(btnStop);

            lblState = new Label();
            lblState.Text = "Not running";
            lblState.AutoSize = true;
            lblState.Anchor = AnchorStyles.Right;
            lblState.Margin = new Padding(S(6));

            ctl.Controls.Add(flRun, 0, 0);
            ctl.Controls.Add(lblState, 1, 0);
            root.Controls.Add(ctl, 0, 3);

            // ---- log ----
            GroupBox gbLog = new GroupBox();
            gbLog.Text = "Log";
            gbLog.Dock = DockStyle.Fill;
            gbLog.Padding = new Padding(S(8));
            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Dock = DockStyle.Fill;
            logBox.Font = new Font("Consolas", 9F);
            logBox.BackColor = SystemColors.Window;
            gbLog.Controls.Add(logBox);
            root.Controls.Add(gbLog, 0, 4);

            // Window size: always big enough that every control (e.g. "Pick position",
            // "Test click") is visible on launch, at any Windows display scaling.
            Size pref = gbEditor.PreferredSize;
            int minW = Math.Max(S(740), pref.Width + S(60));
            MinimumSize = new Size(minW, S(600));
            Size = new Size(Math.Max(minW, S(780)), S(700));
        }

        private void AddTextColumn(string name, string header, int weight, bool readOnly)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.Name = name;
            c.HeaderText = header;
            c.FillWeight = weight;
            c.ReadOnly = readOnly;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(c);
        }

        // ------------------------------------------------------ Helpers
        private void UI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void Log(string msg)
        {
            if (InvokeRequired) UI(delegate { AppendLog(msg); });
            else AppendLog(msg);
        }

        private void AppendLog(string msg)
        {
            if (logBox.TextLength > 200000) logBox.Text = logBox.Text.Substring(100000);
            logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss", Inv) + "] " + msg + Environment.NewLine);
        }

        private void UpdateCursorLabel()
        {
            if (countdownMessage != null)
            {
                lblCursor.Text = countdownMessage;
                return;
            }
            int x, y;
            Native.GetCursor(out x, out y);
            Rectangle sb = Screen.PrimaryScreen.Bounds;
            lblCursor.Text = string.Format(Inv, "Cursor now: X={0}, Y={1}   (screen: {2}x{3})",
                x, y, sb.Width, sb.Height);
        }

        private void StartCountdown(int seconds, string format, Action done)
        {
            if (countdownTimer != null) return;
            countdownLeft = seconds;
            countdownFormat = format;
            countdownDone = done;
            countdownMessage = string.Format(Inv, format, countdownLeft);
            lblCursor.Text = countdownMessage;
            countdownTimer = new WinTimer();
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += delegate
            {
                countdownLeft--;
                if (countdownLeft > 0)
                {
                    countdownMessage = string.Format(Inv, countdownFormat, countdownLeft);
                    lblCursor.Text = countdownMessage;
                }
                else
                {
                    countdownTimer.Stop();
                    countdownTimer.Dispose();
                    countdownTimer = null;
                    countdownMessage = null;
                    Action d = countdownDone;
                    d();
                }
            };
            countdownTimer.Start();
        }

        private void Info(string text)
        {
            MessageBox.Show(this, text, "Scheduled Clicker", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Error(string title, string text)
        {
            MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ------------------------------------------------ Parsing helpers
        private static string NormalizeTime(string s)
        {
            DateTime dt;
            if (!DateTime.TryParseExact((s ?? "").Trim(), TimeFormats, Inv, DateTimeStyles.None, out dt))
                throw new FormatException("'" + s + "' is not a valid time. Use HH:MM:SS (24-hour).");
            return dt.ToString("HH:mm:ss", Inv);
        }

        private static int ParseInt(string s)
        {
            int v;
            if (!int.TryParse((s ?? "").Trim(), NumberStyles.Integer, Inv, out v))
                throw new FormatException("'" + s + "' is not a whole number.");
            return v;
        }

        private static double ParseGap(string s)
        {
            double v;
            string t = (s ?? "").Trim().Replace(',', '.');
            if (!double.TryParse(t, NumberStyles.Float, Inv, out v) ||
                double.IsNaN(v) || double.IsInfinity(v) || v < 0)
                throw new FormatException("'" + s + "' is not a valid gap (number of seconds, 0 or more).");
            return v;
        }

        private static int ParseClicks(string s)
        {
            int n = ParseInt(s);
            if (n < 1) throw new FormatException("Number of clicks must be at least 1.");
            return n;
        }

        private static string ParseButton(string s)
        {
            string b = (s ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(ButtonNames, b) < 0)
                throw new FormatException("Button must be left, right or middle.");
            return b;
        }

        // ------------------------------------------------ Start-time scrolling
        internal bool HandleTimeWheel(Point screenPt, int delta)
        {
            if (Form.ActiveForm != this || !txtTime.IsHandleCreated) return false;
            Rectangle r = txtTime.RectangleToScreen(txtTime.ClientRectangle);
            if (!r.Contains(screenPt)) return false;

            int idx = txtTime.GetCharIndexFromPosition(txtTime.PointToClient(screenPt));
            wheelAccum += delta;
            while (Math.Abs(wheelAccum) >= 120)
            {
                int dir = wheelAccum > 0 ? 1 : -1;
                BumpTime(idx, dir);
                wheelAccum -= 120 * dir;
            }
            return true;
        }

        private void TxtTimeKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                BumpTime(txtTime.SelectionStart, e.KeyCode == Keys.Up ? 1 : -1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // Change hours / minutes / seconds depending on where the pointer or caret is.
        private void BumpTime(int idx, int dir)
        {
            DateTime cur;
            if (!DateTime.TryParseExact(txtTime.Text.Trim(), TimeFormats, Inv, DateTimeStyles.None, out cur))
                cur = DateTime.Now;
            int seg = idx < 3 ? 0 : (idx < 6 ? 1 : 2);
            int step = seg == 0 ? 3600 : (seg == 1 ? 60 : 1);
            int total = cur.Hour * 3600 + cur.Minute * 60 + cur.Second + dir * step;
            total = ((total % 86400) + 86400) % 86400;
            txtTime.Text = string.Format(Inv, "{0:00}:{1:00}:{2:00}", total / 3600, total % 3600 / 60, total % 60);
            txtTime.Focus();
            txtTime.Select(seg * 3, 2);
        }

        // ------------------------------------------------ Pick / test
        private void OnPickPosition()
        {
            StartCountdown(3, "Move the mouse to the target... capturing in {0}", delegate
            {
                int x, y;
                Native.GetCursor(out x, out y);
                txtX.Text = x.ToString(Inv);
                txtY.Text = y.ToString(Inv);
                Log("Picked position (" + x + ", " + y + ")");
            });
        }

        private void OnTestClick()
        {
            int x, y;
            try
            {
                x = ParseInt(txtX.Text);
                y = ParseInt(txtY.Text);
            }
            catch (FormatException)
            {
                Error("Invalid", "X and Y must be whole numbers.");
                return;
            }
            string button = cmbButton.Text;
            Log("Test click in 3 seconds at (" + x + ", " + y + ")...");
            StartCountdown(3, "Test click in {0}...", delegate
            {
                Native.Click(x, y, button);
                Log("Test click sent.");
            });
        }

        // ------------------------------------------------ Grid <-> jobs
        private void RefreshGrid()
        {
            refreshing = true;
            try
            {
                Job keep = selectAfter;
                selectAfter = null;
                if (keep == null && grid.CurrentRow != null && grid.CurrentRow.Index < jobs.Count)
                    keep = jobs[grid.CurrentRow.Index];

                grid.EndEdit();
                grid.Rows.Clear();
                foreach (Job j in jobs)
                {
                    grid.Rows.Add(j.Time, j.X, j.Y, j.Clicks, j.Gap.ToString("0.###", Inv),
                                  j.Button, j.Daily, j.Status);
                }
                int idx = keep == null ? -1 : jobs.IndexOf(keep);
                if (idx >= 0)
                {
                    grid.CurrentCell = grid.Rows[idx].Cells[0];
                    grid.Rows[idx].Selected = true;
                }
            }
            finally
            {
                refreshing = false;
            }
        }

        private void RefreshStatus()
        {
            if (grid.Rows.Count != jobs.Count) { RefreshGrid(); return; }
            refreshing = true;
            try
            {
                for (int i = 0; i < jobs.Count; i++)
                    grid.Rows[i].Cells["colStatus"].Value = jobs[i].Status;
            }
            finally { refreshing = false; }
        }

        private void FinishEdit()
        {
            jobs.Sort(delegate(Job a, Job b) { return string.CompareOrdinal(a.Time, b.Time); });
            RefreshGrid();
            SaveJobs();
        }

        private static void ApplyEdit(Job j, string col, object val)
        {
            string s = val == null ? "" : Convert.ToString(val, Inv);
            switch (col)
            {
                case "colTime": j.Time = NormalizeTime(s); break;
                case "colX": j.X = ParseInt(s); break;
                case "colY": j.Y = ParseInt(s); break;
                case "colClicks": j.Clicks = ParseClicks(s); break;
                case "colGap": j.Gap = ParseGap(s); break;
                case "colButton": j.Button = ParseButton(s); break;
                case "colDaily": j.Daily = (val is bool) && (bool)val; break;
            }
        }

        private void OnGridCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (refreshing || running || e.RowIndex < 0 || e.RowIndex >= jobs.Count) return;
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col == "colButton" || col == "colDaily" || col == "colStatus") return; // handled on value change
            Job j = jobs[e.RowIndex];
            try
            {
                ApplyEdit(j, col, grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value);
            }
            catch (FormatException ex)
            {
                Error("Invalid value", ex.Message);
            }
            selectAfter = j;
            BeginInvoke(new Action(FinishEdit));
        }

        private void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (refreshing || running || e.RowIndex < 0 || e.RowIndex >= jobs.Count) return;
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col != "colButton" && col != "colDaily") return;
            Job j = jobs[e.RowIndex];
            try
            {
                ApplyEdit(j, col, grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value);
            }
            catch (FormatException ex)
            {
                Error("Invalid value", ex.Message);
            }
            selectAfter = j;
            BeginInvoke(new Action(FinishEdit));
        }

        private void OnGridDirtyStateChanged(object sender, EventArgs e)
        {
            if (!grid.IsCurrentCellDirty || grid.CurrentCell == null) return;
            string col = grid.Columns[grid.CurrentCell.ColumnIndex].Name;
            if (col == "colButton" || col == "colDaily")
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        // ------------------------------------------------ Job list actions
        private void OnAddJob()
        {
            if (running) { Info("Stop the scheduler before editing jobs."); return; }
            try
            {
                Job j = new Job();
                j.Time = NormalizeTime(txtTime.Text);
                j.X = ParseInt(txtX.Text);
                j.Y = ParseInt(txtY.Text);
                j.Clicks = ParseClicks(txtClicks.Text);
                j.Gap = ParseGap(txtGap.Text);
                j.Button = ParseButton(cmbButton.Text);
                j.Daily = chkDaily.Checked;
                jobs.Add(j);
                selectAfter = j;
                FinishEdit();
                Log(string.Format(Inv, "Added job {0} -> ({1}, {2}) x{3}, gap {4}s",
                    j.Time, j.X, j.Y, j.Clicks, j.Gap.ToString("0.###", Inv)));
            }
            catch (FormatException ex)
            {
                Error("Invalid input", ex.Message);
            }
        }

        private void OnRemoveSelected()
        {
            if (running) { Info("Stop the scheduler before editing jobs."); return; }
            if (grid.CurrentRow == null || grid.CurrentRow.Index >= jobs.Count) return;
            jobs.RemoveAt(grid.CurrentRow.Index);
            FinishEdit();
        }

        private void OnClearAll()
        {
            if (running) { Info("Stop the scheduler before editing jobs."); return; }
            jobs.Clear();
            FinishEdit();
        }

        // ------------------------------------------------ JSON load / save / import
        private static object Require(IDictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
                throw new FormatException("missing '" + key + "'");
            return v;
        }

        private static Job ParseJob(object o)
        {
            IDictionary<string, object> d = o as IDictionary<string, object>;
            if (d == null) throw new FormatException("not an object");
            Job j = new Job();
            j.Time = NormalizeTime(Convert.ToString(Require(d, "time"), Inv));
            j.X = Convert.ToInt32(Require(d, "x"), Inv);
            j.Y = Convert.ToInt32(Require(d, "y"), Inv);

            object v;
            if (d.TryGetValue("clicks", out v) && v != null) j.Clicks = Convert.ToInt32(v, Inv);
            if (d.TryGetValue("gap", out v) && v != null) j.Gap = Convert.ToDouble(v, Inv);
            if (d.TryGetValue("button", out v) && v != null) j.Button = ParseButton(Convert.ToString(v, Inv));
            if (d.TryGetValue("daily", out v) && v != null)
            {
                if (v is bool) j.Daily = (bool)v;
                else
                {
                    string s = Convert.ToString(v, Inv).Trim().ToLowerInvariant();
                    j.Daily = s == "1" || s == "true" || s == "yes";
                }
            }
            if (j.Clicks < 1) throw new FormatException("clicks must be >= 1");
            if (j.Gap < 0 || double.IsNaN(j.Gap) || double.IsInfinity(j.Gap))
                throw new FormatException("gap must be >= 0");
            return j;
        }

        // Accepts  [ {...}, {...} ]  or  { "jobs": [ ... ] }  or a single { ... }
        private static List<Job> ParseJobs(string text, out int bad)
        {
            bad = 0;
            object data = new JavaScriptSerializer().DeserializeObject(text);
            List<object> items = new List<object>();

            IDictionary<string, object> dict = data as IDictionary<string, object>;
            if (dict != null)
            {
                object inner;
                if (dict.TryGetValue("jobs", out inner) && inner is IEnumerable && !(inner is string))
                {
                    foreach (object o in (IEnumerable)inner) items.Add(o);
                }
                else items.Add(dict);
            }
            else if (data is IEnumerable && !(data is string))
            {
                foreach (object o in (IEnumerable)data) items.Add(o);
            }
            else throw new FormatException("JSON must be a list of jobs.");

            List<Job> result = new List<Job>();
            foreach (object o in items)
            {
                try { result.Add(ParseJob(o)); }
                catch (Exception) { bad++; }
            }
            return result;
        }

        private void SaveJobs()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[");
                for (int i = 0; i < jobs.Count; i++)
                {
                    Job j = jobs[i];
                    sb.AppendLine(string.Format(Inv,
                        "  {{ \"time\": \"{0}\", \"x\": {1}, \"y\": {2}, \"clicks\": {3}, \"gap\": {4}, \"button\": \"{5}\", \"daily\": {6} }}{7}",
                        j.Time, j.X, j.Y, j.Clicks, j.Gap.ToString("R", Inv), j.Button,
                        j.Daily ? "true" : "false", i < jobs.Count - 1 ? "," : ""));
                }
                sb.AppendLine("]");
                File.WriteAllText(SaveFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("WARNING: could not save jobs to " + SaveFile + ": " + ex.Message);
            }
        }

        private void LoadJobs()
        {
            if (!File.Exists(SaveFile))
            {
                Log("Jobs are saved automatically to: " + SaveFile);
                return;
            }
            try
            {
                int bad;
                jobs.AddRange(ParseJobs(File.ReadAllText(SaveFile, Encoding.UTF8), out bad));
                RefreshGrid();
                Log("Loaded " + jobs.Count + " job(s) from " + SaveFile);
            }
            catch (Exception ex)
            {
                Log("WARNING: could not load saved jobs: " + ex.Message);
            }
        }

        private void OnImport()
        {
            if (running) { Info("Stop the scheduler before importing jobs."); return; }
            string path;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Import jobs from JSON";
                dlg.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }

            List<Job> imported;
            int bad;
            try
            {
                imported = ParseJobs(File.ReadAllText(path, Encoding.UTF8), out bad);
            }
            catch (Exception ex)
            {
                Error("Import failed", "Could not read the file:\n" + ex.Message);
                return;
            }
            if (imported.Count == 0)
            {
                Error("Import failed",
                    "No valid jobs found.\nEach job needs at least: time, x, y " +
                    "(optional: clicks, gap, button, daily).");
                return;
            }

            if (jobs.Count > 0)
            {
                DialogResult r = MessageBox.Show(this,
                    "Found " + imported.Count + " valid job(s).\n\n" +
                    "Yes = add to existing jobs\nNo = replace existing jobs\nCancel = abort",
                    "Import jobs", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.No) jobs.Clear();
            }
            jobs.AddRange(imported);
            FinishEdit();

            string msg = "Imported " + imported.Count + " job(s) from " + path;
            if (bad > 0) msg += " (" + bad + (bad == 1 ? " invalid entry" : " invalid entries") + " skipped)";
            Log(msg);
            if (bad > 0)
                MessageBox.Show(this, msg, "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ------------------------------------------------ Scheduler
        private void SetRunningUi(bool r)
        {
            running = r;
            btnStart.Enabled = !r;
            btnStop.Enabled = r;
            btnAdd.Enabled = !r;
            btnRemove.Enabled = !r;
            btnClear.Enabled = !r;
            btnImport.Enabled = !r;
            grid.ReadOnly = r;
            lblState.Text = r ? "Running - F8 to stop" : "Not running";
        }

        private void OnStart()
        {
            if (jobs.Count == 0) { Info("Add at least one job first."); return; }
            stopFlag = false;
            DateTime now = DateTime.Now;
            foreach (Job j in jobs)
            {
                DateTime tod;
                DateTime.TryParseExact(j.Time, TimeFormats, Inv, DateTimeStyles.None, out tod);
                DateTime t = now.Date + tod.TimeOfDay;
                if (t <= now) t = t.AddDays(1);
                j.NextRun = t;
                j.HasNext = true;
                j.Status = "Waiting";
            }
            SetRunningUi(true);
            RefreshStatus();
            worker = new Thread(SchedulerLoop);
            worker.IsBackground = true;
            worker.Start();
        }

        private void CheckStop()
        {
            if (stopFlag || Native.F8Pressed()) throw new StopRequestedException();
        }

        private void Wait(double seconds)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                CheckStop();
                if (sw.Elapsed.TotalSeconds >= seconds) return;
                Thread.Sleep(1);
            }
        }

        private void SchedulerLoop()
        {
            Native.timeBeginPeriod(1);
            Log("Scheduler started (press F8 to stop).");
            try
            {
                while (true)
                {
                    Wait(0.1);
                    DateTime now = DateTime.Now;
                    bool pending = false;
                    foreach (Job j in jobs)
                    {
                        if (!j.HasNext) continue;
                        pending = true;
                        if (now >= j.NextRun)
                        {
                            RunJob(j);
                            if (j.Daily)
                            {
                                j.NextRun = j.NextRun.AddDays(1);
                                j.Status = "Waiting";
                            }
                            else
                            {
                                j.HasNext = false;
                                j.Status = "Done";
                            }
                            UI(new Action(RefreshStatus));
                        }
                    }
                    if (!pending)
                    {
                        Log("All jobs finished.");
                        break;
                    }
                }
            }
            catch (StopRequestedException)
            {
                Log("Stopped.");
            }
            finally
            {
                Native.timeEndPeriod(1);
                foreach (Job j in jobs)
                {
                    if (j.Status == "Waiting" || j.Status == "Running") j.Status = "Idle";
                }
                UI(delegate
                {
                    SetRunningUi(false);
                    RefreshStatus();
                });
            }
        }

        private void RunJob(Job j)
        {
            j.Status = "Running";
            UI(new Action(RefreshStatus));
            Log(string.Format(Inv, "Running job {0}: {1} click(s) at ({2}, {3})", j.Time, j.Clicks, j.X, j.Y));

            // Gap is measured from click start to click start, so timing stays accurate.
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < j.Clicks; i++)
            {
                CheckStop();
                Native.Click(j.X, j.Y, j.Button);
                Log(string.Format(Inv, "  click {0}/{1} @ {2:HH:mm:ss.fff}", i + 1, j.Clicks, DateTime.Now));
                if (i < j.Clicks - 1)
                {
                    double target = (i + 1) * j.Gap;
                    Wait(target - sw.Elapsed.TotalSeconds);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            stopFlag = true;
            if (worker != null && worker.IsAlive) worker.Join(1000);
            Application.RemoveMessageFilter(wheelFilter);
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Must run before any window exists so X/Y coordinates are real screen pixels
            // (otherwise Windows display scaling shifts clicks on 125%/150% setups).
            try { Native.SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
