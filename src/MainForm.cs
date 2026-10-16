using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Lighter
{
    class MainForm : Form
    {
        const int TabStartup = 0, TabTasks = 1, TabServices = 2, TabProcs = 3;

        readonly Panel root = new BufferedPanel(), header = new BufferedPanel(), toolbar = new BufferedPanel();
        readonly Panel content = new BufferedPanel(), listHost = new BufferedPanel(), footer = new BufferedPanel();
        readonly TabBar tabBar = new TabBar();
        readonly Segmented themeSwitch;
        readonly FlatButton btnLang;
        readonly ToolTip tip = new ToolTip();
        readonly LoadingBar loadBar = new LoadingBar();
        readonly FlatButton btnEnable, btnDisable, btnDelete, btnKill, btnOpen, btnRefresh, btnBackup;
        readonly ToggleSwitch swSystem;
        readonly SearchBox search = new SearchBox();
        readonly StatusText status = new StatusText();
        readonly Toast toast = new Toast();
        readonly ThemedListView lvStartup, lvTasks, lvServices, lvProcs;
        readonly ThemedListView[] lists;
        readonly ContextMenuStrip menuAutorun = new ContextMenuStrip(), menuProc = new ContextMenuStrip();
        readonly ToolStripMenuItem miEnable, miDisable, miDelete, miOpen, miCopy, miKill, miProcOpen, miProcCopy;
        readonly System.Windows.Forms.Timer searchDebounce = new System.Windows.Forms.Timer { Interval = 200 };
        readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        List<AutorunItem> startup = new List<AutorunItem>(), tasks = new List<AutorunItem>(), services = new List<AutorunItem>();
        List<ProcInfo> procs = new List<ProcInfo>();
        bool loading, firstLoad = true, buttonsQueued;
        int currentTab;
        // Lists are rebuilt only when shown: a hidden list just remembers that its data changed.
        readonly bool[] dirty = new bool[4];

        static int S(int v) { return Theme.S(v); }

        public MainForm()
        {
            Font = Theme.UiFont;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(1180), S(700));
            MinimumSize = new Size(S(860), S(500));
            KeyPreview = true;
            DoubleBuffered = true;
            Icon = Logo.AppIcon;
            Opacity = 0;

            // ----- header: brand, tabs, language and theme switches -----
            // Texts are set in ApplyLanguage(); constructors only get placeholders.
            header.Dock = DockStyle.Top;
            header.Height = S(60);
            header.Paint += PaintHeader;
            tabBar.Height = header.Height;
            tabBar.Location = new Point(S(150), 0);
            tabBar.AddTab("", "");
            tabBar.AddTab("", "");
            tabBar.AddTab("", "");
            tabBar.AddTab("", "");
            tabBar.SelectedIndexChanged += (s, e) => OnTabChanged();
            themeSwitch = new Segmented(new[] { "", "" }, new[] { "", "" }, Theme.Current.IsDark ? 0 : 1);
            themeSwitch.SelectedIndexChanged += (s, e) => OnThemeChanged();
            btnLang = new FlatButton("", "");
            btnLang.Click += (s, e) => OnLanguageClick();
            header.Controls.Add(tabBar);
            header.Controls.Add(btnLang);
            header.Controls.Add(themeSwitch);
            header.Resize += (s, e) => LayoutHeader();

            loadBar.Dock = DockStyle.Top;
            loadBar.Height = S(2);

            // ----- toolbar -----
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = S(60);
            btnEnable = new FlatButton("", "");
            btnDisable = new FlatButton("", "");
            btnDelete = new FlatButton("", "", ButtonKind.Danger);
            btnKill = new FlatButton("", "", ButtonKind.Danger);
            btnOpen = new FlatButton("", "");
            btnRefresh = new FlatButton("", "");
            swSystem = new ToggleSwitch("");
            btnEnable.Click += (s, e) => DoToggle(true);
            btnDisable.Click += (s, e) => DoToggle(false);
            btnDelete.Click += (s, e) => DoDelete();
            btnKill.Click += (s, e) => DoKill();
            btnOpen.Click += (s, e) => DoOpen();
            btnRefresh.Click += (s, e) => Reload();
            swSystem.CheckedChanged += (s, e) =>
            {
                dirty[TabTasks] = dirty[TabServices] = true;
                Transition.CrossFade(listHost, RenderCurrent, 220);
                UpdateButtons();
                UpdateStatus();
            };
            search.TextChanged += (s, e) => { searchDebounce.Stop(); searchDebounce.Start(); };
            searchDebounce.Tick += (s, e) =>
            {
                searchDebounce.Stop();
                MarkAllDirty();
                Transition.CrossFade(listHost, RenderCurrent, 160);
                UpdateButtons();
                UpdateStatus();
            };
            foreach (Control c in new Control[] { btnEnable, btnDisable, btnDelete, btnKill, btnOpen, btnRefresh, swSystem, search })
                toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar(false);

            // ----- menus -----
            miEnable = AddMenuItem(menuAutorun, "", false, (s, e) => DoToggle(true));
            miDisable = AddMenuItem(menuAutorun, "", false, (s, e) => DoToggle(false));
            miDelete = AddMenuItem(menuAutorun, "", true, (s, e) => DoDelete());
            menuAutorun.Items.Add(new ToolStripSeparator());
            miOpen = AddMenuItem(menuAutorun, "", false, (s, e) => DoOpen());
            miCopy = AddMenuItem(menuAutorun, "", false, (s, e) => DoCopy());
            miKill = AddMenuItem(menuProc, "", true, (s, e) => DoKill());
            menuProc.Items.Add(new ToolStripSeparator());
            miProcOpen = AddMenuItem(menuProc, "", false, (s, e) => DoOpen());
            miProcCopy = AddMenuItem(menuProc, "", false, (s, e) => DoCopy());
            SetupMenu(menuAutorun);
            SetupMenu(menuProc);

            // ----- lists -----
            var autorunWidths = new[] { 240, 150, 190, 380, 260 };
            lvStartup = MakeList(autorunWidths, 1, menuAutorun);
            lvTasks = MakeList(autorunWidths, 1, menuAutorun);
            lvServices = MakeList(autorunWidths, 1, menuAutorun);
            lvProcs = MakeList(new[] { 230, 80, 120, 210, 120, 320 }, 4, menuProc);
            lvProcs.Numeric = new[] { false, true, true, false, false, false };
            lvProcs.SortColumn = 2;
            lvProcs.SortDesc = true;
            lists = new[] { lvStartup, lvTasks, lvServices, lvProcs };
            lvStartup.Visible = true;

            listHost.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(1);
            listHost.Resize += (s, e) =>
            {
                foreach (var lv in lists) lv.Bounds = listHost.DisplayRectangle;
            };
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(S(16), 0, S(16), S(4));
            content.Controls.Add(listHost);

            // ----- footer -----
            footer.Dock = DockStyle.Bottom;
            footer.Height = S(40);
            status.SetBounds(S(18), 0, S(700), footer.Height);
            status.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            btnBackup = new FlatButton("", "", ButtonKind.Ghost);
            btnBackup.Height = S(30);
            btnBackup.Click += (s, e) => OpenBackupFolder();
            footer.Controls.Add(status);
            footer.Controls.Add(btnBackup);
            footer.Resize += (s, e) => LayoutFooter();

            // Docking: later-added controls are docked first (outermost).
            root.Dock = DockStyle.Fill;
            root.Controls.Add(content);
            root.Controls.Add(footer);
            root.Controls.Add(toolbar);
            root.Controls.Add(loadBar);
            root.Controls.Add(header);
            root.Controls.Add(toast);
            root.Resize += (s, e) => toast.Reposition(ToastAnchor());
            Controls.Add(root);

            ApplyTheme();
            ApplyLanguage();
            UpdateButtons();
        }

        // ---------- construction helpers ----------

        ThemedListView MakeList(int[] widths, int dotColumn, ContextMenuStrip menu)
        {
            // Not docked: docking skips hidden controls, and a hidden list must already have its real
            // size so it can be filled in the background and shown without any relayout.
            var lv = new ThemedListView
            {
                Bounds = listHost.DisplayRectangle,
                Visible = false,
                Icons = icons,
                DotColumn = dotColumn,
                ContextMenuStrip = menu,
            };
            foreach (int w in widths) lv.Columns.Add("", S(w));
            lv.SelectedIndexChanged += (s, e) => QueueUpdateButtons();
            lv.KeyDown += ListKeyDown;
            lv.ItemActivate += (s, e) => DoOpen();
            listHost.Controls.Add(lv);
            return lv;
        }

        static ToolStripMenuItem AddMenuItem(ContextMenuStrip menu, string glyph, bool danger, EventHandler click)
        {
            var mi = new ToolStripMenuItem { Tag = new MenuTag { Glyph = glyph, Danger = danger } };
            mi.Click += click;
            menu.Items.Add(mi);
            return mi;
        }

        void SetupMenu(ContextMenuStrip menu)
        {
            menu.Renderer = new ThemedRenderer();
            menu.Font = Theme.UiFont;
            menu.ImageScalingSize = new Size(S(18), S(18));
            menu.Padding = new Padding(0, S(4), 0, S(4));
            foreach (ToolStripItem item in menu.Items)
            {
                if (item is ToolStripMenuItem) item.Padding = new Padding(0, S(4), 0, S(4));
            }
            menu.Opening += (s, e) =>
            {
                UpdateButtons();
                menu.Opacity = 0;
            };
            menu.Opened += (s, e) => Anim.Run(menu, 160, t => menu.Opacity = t);
        }

        void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int size = S(32);
            Logo.Draw(g, new RectangleF(S(18), (header.Height - size) / 2, size, size), true);
            TextRenderer.DrawText(g, "Lighter", Theme.TitleFont, new Rectangle(S(58), 0, S(92), header.Height), Theme.Current.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---------- language ----------

        static void SetButton(FlatButton b, string text)
        {
            b.Text = text;
            b.Width = b.PreferredWidth;
        }

        static void SetColumns(ThemedListView lv, params string[] names)
        {
            for (int i = 0; i < names.Length; i++) lv.Columns[i].Text = names[i];
        }

        /// <summary>Puts every visible string into the current language and re-lays out what changed width.</summary>
        void ApplyLanguage()
        {
            Text = L.T("Lighter — автозагрузка Windows", "Lighter — Windows startup manager");
            tabBar.SetTabText(TabStartup, L.T("Автозагрузка", "Startup"));
            tabBar.SetTabText(TabTasks, L.T("Планировщик", "Scheduler"));
            tabBar.SetTabText(TabServices, L.T("Службы", "Services"));
            tabBar.SetTabText(TabProcs, L.T("Процессы", "Processes"));
            themeSwitch.SetTexts(new[] { L.T("Тёмная", "Dark"), L.T("Светлая", "Light") });
            SetButton(btnLang, L.En ? "EN" : "RU");
            tip.SetToolTip(btnLang, L.T("Язык интерфейса: русский. Switch to English", "Interface language: English. Переключить на русский"));

            SetButton(btnEnable, L.T("Включить", "Enable"));
            SetButton(btnDisable, L.T("Отключить", "Disable"));
            SetButton(btnDelete, L.T("Удалить", "Delete"));
            SetButton(btnKill, L.T("Завершить процесс", "End process"));
            SetButton(btnOpen, L.T("Расположение", "Show file"));
            SetButton(btnRefresh, L.T("Обновить", "Refresh"));
            SetButton(btnBackup, L.T("Резервные копии", "Backups"));
            swSystem.Text = L.T("Системные", "System");
            search.SetCue(L.T("Поиск по имени, команде, издателю", "Search by name, command, publisher"));

            miEnable.Text = L.T("Включить", "Enable");
            miDisable.Text = L.T("Отключить", "Disable");
            miDelete.Text = L.T("Удалить", "Delete");
            miOpen.Text = miProcOpen.Text = L.T("Открыть расположение файла", "Show file in Explorer");
            miCopy.Text = L.T("Копировать команду", "Copy command");
            miKill.Text = L.T("Завершить процесс", "End process");
            miProcCopy.Text = L.T("Копировать путь", "Copy path");

            string[] autorunCols =
            {
                L.T("Имя", "Name"), L.T("Состояние", "Status"), L.T("Издатель", "Publisher"),
                L.T("Команда", "Command"), L.T("Расположение", "Location")
            };
            foreach (var lv in new[] { lvStartup, lvTasks, lvServices }) SetColumns(lv, autorunCols);
            SetColumns(lvProcs, L.T("Имя", "Name"), "PID", L.T("Память, МБ", "Memory, MB"), L.T("Издатель", "Publisher"),
                L.T("Автозапуск", "Autostart"), L.T("Путь", "Path"));

            if (firstLoad)
            {
                foreach (var lv in lists) lv.EmptyText = L.T("Загрузка…", "Loading…");
            }
            else
            {
                // Status and location texts are built per row, so rebuild the visible list now and the rest on demand.
                MarkAllDirty();
                RenderCurrent();
            }
            foreach (var lv in lists) lv.Invalidate();
            LayoutHeader();
            LayoutToolbar(false);
            LayoutFooter();
            UpdateStatus();
        }

        void OnLanguageClick()
        {
            Transition.CrossFade(root, () =>
            {
                L.En = !L.En;
                L.Save();
                ApplyLanguage();
            }, 340);
        }

        // ---------- theme ----------

        void ApplyTheme()
        {
            var p = Theme.Current;
            BackColor = p.Bg;
            foreach (Control c in new Control[] { root, header, toolbar, content, footer, loadBar })
                c.BackColor = p.Bg;
            listHost.BackColor = p.Border;
            search.ApplyTheme();
            foreach (var lv in lists) lv.ApplyTheme();
            if (IsHandleCreated) Theme.ApplyTitleBar(Handle);
            root.Invalidate(true);
        }

        void OnThemeChanged()
        {
            // Let the switch finish sliding, then cross-fade the whole window into the new palette.
            Anim.Delay(280, () => Transition.CrossFade(root, () =>
            {
                Theme.Current = themeSwitch.SelectedIndex == 0 ? Theme.Dark : Theme.Light;
                Theme.Save();
                ApplyTheme();
            }, 380));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Create every list window now, so the first switch to a tab does no setup work mid-animation.
            foreach (var lv in lists)
            {
                IntPtr unused = lv.Handle;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Anim.Run(this, 280, t => Opacity = t, () =>
            {
                // Fade-in is done: drop the layered style so the window paints as a normal one.
                Opacity = 1;
                AllowTransparency = false;
                // RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0400 | 0x0080);
            });
            lvStartup.Focus();
            Reload();
        }

        // ---------- layout ----------

        void LayoutHeader()
        {
            int y = (header.Height - themeSwitch.Height) / 2;
            themeSwitch.Location = new Point(header.Width - themeSwitch.Width - S(16), y);
            btnLang.Location = new Point(themeSwitch.Left - btnLang.Width - S(8), y);
        }

        void LayoutFooter()
        {
            btnBackup.Location = new Point(footer.Width - btnBackup.Width - S(16), (footer.Height - btnBackup.Height) / 2);
            status.Width = Math.Max(S(100), btnBackup.Left - status.Left - S(12));
        }

        void LayoutToolbar(bool animate)
        {
            int idx = tabBar.SelectedIndex;
            var shown = new List<Control>();
            if (idx == TabProcs) shown.Add(btnKill);
            else { shown.Add(btnEnable); shown.Add(btnDisable); shown.Add(btnDelete); }
            shown.Add(btnOpen);
            shown.Add(btnRefresh);
            if (idx == TabTasks || idx == TabServices) shown.Add(swSystem);

            foreach (Control c in new Control[] { btnEnable, btnDisable, btnDelete, btnKill, btnOpen, btnRefresh, swSystem })
                if (!shown.Contains(c)) c.Visible = false;

            int x = S(16);
            int y = (toolbar.Height - S(32)) / 2;
            foreach (var c in shown)
            {
                int targetX = x;
                if (!c.Visible)
                {
                    c.Location = new Point(targetX, y);
                    c.Visible = true;
                    if (animate)
                    {
                        var fb = c as FlatButton;
                        if (fb != null) fb.Appear();
                        var sw = c as ToggleSwitch;
                        if (sw != null) sw.Appear();
                    }
                }
                else if (animate && c.Left != targetX)
                {
                    var cc = c;
                    int from = c.Left;
                    Anim.Run(Tuple.Create(cc, "x"), 280, t => cc.Left = (int)Anim.Lerp(from, targetX, t));
                }
                else
                {
                    Anim.Cancel(Tuple.Create(c, "x"));
                    c.Location = new Point(targetX, y);
                }
                x += c.Width + S(8);
                if (c == btnDelete || c == btnKill || c == btnRefresh) x += S(12);
            }

            int w = Math.Max(S(180), Math.Min(S(340), toolbar.Width - x - S(32)));
            search.SetBounds(toolbar.Width - S(16) - w, y, w, S(32));
        }

        Point ToastAnchor()
        {
            return new Point(root.ClientSize.Width - S(28), footer.Top - S(14));
        }

        void ShowToast(string text, ToastKind kind)
        {
            toast.Show(text, kind, ToastAnchor());
        }

        // ---------- tabs ----------

        ThemedListView CurrentList { get { return lists[tabBar.SelectedIndex]; } }

        void OnTabChanged()
        {
            int idx = tabBar.SelectedIndex;
            int dir = idx > currentTab ? 1 : -1;
            currentTab = idx;
            Transition.CrossFade(listHost, () =>
            {
                EnsureRendered(idx);
                for (int i = 0; i < lists.Length; i++) lists[i].Visible = i == idx;
            }, 260, dir * S(36));
            LayoutToolbar(true);
            UpdateButtons();
            UpdateStatus();
            CurrentList.Focus();
        }

        // ---------- loading ----------

        void Reload()
        {
            if (loading) return;
            loading = true;
            loadBar.Start();
            UpdateButtons();
            var known = new HashSet<string>(icons.Keys, StringComparer.OrdinalIgnoreCase);
            var th = new Thread(() => LoadWorker(known)) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA); // COM: shell icons
            th.Start();
        }

        static void Try(List<string> errors, string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { lock (errors) errors.Add(what + " (" + ex.Message + ")"); }
        }

        /// <summary>Runs a loader on its own STA thread (WScript.Shell and Task Scheduler are COM).</summary>
        static Thread StartJob(List<string> errors, string what, Action action)
        {
            var t = new Thread(() => Try(errors, what, action)) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return t;
        }

        void LoadWorker(HashSet<string> knownIcons)
        {
            var errors = new List<string>();
            var reg = new List<AutorunItem>();
            var folders = new List<AutorunItem>();
            var tk = new List<AutorunItem>();
            var sv = new List<AutorunItem>();
            var pr = new List<ProcInfo>();
            var newIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

            // Sources are independent: load them side by side, total time = the slowest one.
            // The process job also extracts its (many) icons while the scheduler is still being read.
            var jobs = new[]
            {
                StartJob(errors, L.T("реестр", "registry"), () => reg.AddRange(RegistryRunSource.Load())),
                StartJob(errors, L.T("папки автозагрузки", "startup folders"), () => folders.AddRange(StartupFolderSource.Load())),
                StartJob(errors, L.T("планировщик", "task scheduler"), () => tk.AddRange(TaskSchedulerSource.Load())),
                StartJob(errors, L.T("службы", "services"), () => sv.AddRange(ServiceSource.Load())),
                StartJob(errors, L.T("процессы", "processes"), () =>
                {
                    pr.AddRange(ProcessList.Load());
                    ExtractIcons(pr.Select(i => i.IconPath), knownIcons, newIcons);
                }),
            };
            foreach (var j in jobs) j.Join();
            var st = reg.Concat(folders).ToList();

            var autorunPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in st.Concat(tk).Concat(sv))
                if (it.Enabled && it.ExePath.Length > 0 && !PathUtil.IsHost(it.ExePath)) autorunPaths.Add(it.ExePath);
            ProcessList.MarkAutorun(pr, autorunPaths);
            ExtractIcons(st.Concat(tk).Concat(sv).Select(i => i.IconPath), knownIcons, newIcons);

            try
            {
                BeginInvoke((MethodInvoker)delegate { ApplyData(st, tk, sv, pr, newIcons, errors); });
            }
            catch (InvalidOperationException) { } // window closed while loading
        }

        static void ExtractIcons(IEnumerable<string> paths, HashSet<string> known, Dictionary<string, Bitmap> into)
        {
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || known.Contains(path) || into.ContainsKey(path)) continue;
                if (!PathUtil.SafeExists(path) && !Directory.Exists(path)) continue;
                try
                {
                    var bmp = Native.GetSmallIcon(path);
                    if (bmp != null) into[path] = bmp;
                }
                catch { }
            }
        }

        void ApplyData(List<AutorunItem> st, List<AutorunItem> tk, List<AutorunItem> sv, List<ProcInfo> pr,
            Dictionary<string, Bitmap> newIcons, List<string> errors)
        {
            foreach (var kv in newIcons) icons[kv.Key] = kv.Value;
            startup = st;
            tasks = tk;
            services = sv;
            procs = pr;
            MarkAllDirty();
            Transition.CrossFade(listHost, RenderCurrent, firstLoad ? 320 : 220);

            bool wasFirst = firstLoad;
            firstLoad = false;
            loading = false;
            loadBar.Stop();
            UpdateButtons();
            UpdateStatus();
            if (errors.Count > 0) ShowToast(L.T("Не удалось прочитать: ", "Could not read: ") + string.Join("; ", errors), ToastKind.Error);
            else if (!wasFirst) ShowToast(L.T("Данные обновлены", "Data refreshed"), ToastKind.Info);

            // Loading leaves a lot of short-lived garbage (COM wrappers, task XML, old rows).
            // Collect it once the transition is over, so it never costs an animation frame.
            Anim.Delay(700, () =>
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            });
        }

        // ---------- rendering ----------

        static bool Matches(string q, params string[] fields)
        {
            if (q.Length == 0) return true;
            foreach (string f in fields)
                if (f != null && f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static string Quote(string name)
        {
            return L.En ? "\"" + name + "\"" : "«" + name + "»";
        }

        void MarkAllDirty()
        {
            for (int i = 0; i < dirty.Length; i++) dirty[i] = true;
        }

        void RenderCurrent()
        {
            EnsureRendered(tabBar.SelectedIndex);
        }

        void EnsureRendered(int tab)
        {
            if (!dirty[tab]) return;
            dirty[tab] = false;
            switch (tab)
            {
                case TabStartup: RenderAutoruns(lvStartup, startup); break;
                case TabTasks: RenderAutoruns(lvTasks, tasks); break;
                case TabServices: RenderAutoruns(lvServices, services); break;
                default: RenderProcs(); break;
            }
        }

        /// <summary>Selecting many rows fires one event per row; refresh the buttons once afterwards.</summary>
        void QueueUpdateButtons()
        {
            if (buttonsQueued || !IsHandleCreated) return;
            buttonsQueued = true;
            BeginInvoke((MethodInvoker)delegate
            {
                buttonsQueued = false;
                UpdateButtons();
            });
        }

        static void Fill(ThemedListView lv, List<ListViewItem> rows, HashSet<object> selected)
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.ResetRowStates();
            lv.Items.AddRange(rows.ToArray());
            lv.Sort();
            lv.QuietSelection = true; // restoring the selection must not replay its animation
            foreach (var row in rows)
                if (selected.Contains(row.Tag)) row.Selected = true;
            lv.QuietSelection = false;
            lv.EndUpdate();
            lv.FitLastColumn();
        }

        static HashSet<object> SelectedTags(ThemedListView lv)
        {
            return new HashSet<object>(lv.SelectedItems.Cast<ListViewItem>().Select(r => r.Tag));
        }

        string NotFoundText(string q)
        {
            return L.T("Ничего не найдено по запросу ", "Nothing matches ") + Quote(q);
        }

        void RenderAutoruns(ThemedListView lv, List<AutorunItem> items)
        {
            var selected = SelectedTags(lv);
            string q = search.Text.Trim();
            var rows = new List<ListViewItem>();
            int hidden = 0;
            foreach (var it in items)
            {
                if (it.IsSystem && !swSystem.Checked) { hidden++; continue; }
                string location = it.Location;
                if (!Matches(q, it.Name, it.Command, it.Publisher, location)) continue;
                rows.Add(new ListViewItem(new[] { it.Name, it.StateText, it.Publisher, it.Command, location }) { Tag = it });
            }
            if (q.Length > 0) lv.EmptyText = NotFoundText(q);
            else if (hidden > 0)
                lv.EmptyText = L.T("Сторонних записей нет. Системных скрыто: " + hidden + " — включите «Системные», чтобы их показать",
                    "No third-party entries. " + hidden + " system entries are hidden — turn on \"System\" to show them");
            else lv.EmptyText = L.T("Записей нет", "No entries");
            Fill(lv, rows, selected);
        }

        void RenderProcs()
        {
            var selected = new HashSet<object>(lvProcs.SelectedItems.Cast<ListViewItem>().Select(r => (object)((ProcInfo)r.Tag).Pid));
            string q = search.Text.Trim();
            var rows = new List<ListViewItem>();
            var selectedRows = new HashSet<object>();
            string yes = L.T("Да", "Yes");
            foreach (var pi in procs)
            {
                if (!Matches(q, pi.Name, pi.Path, pi.Publisher, pi.Pid.ToString())) continue;
                var row = new ListViewItem(new[]
                {
                    pi.Name, pi.Pid.ToString(), (pi.Memory / 1048576.0).ToString("N1"),
                    pi.Publisher, pi.Autorun ? yes : "", pi.Path
                }) { Tag = pi };
                rows.Add(row);
                if (selected.Contains(pi.Pid)) selectedRows.Add(pi);
            }
            lvProcs.EmptyText = q.Length > 0 ? NotFoundText(q) : L.T("Записей нет", "No entries");
            Fill(lvProcs, rows, selectedRows);
        }

        void UpdateStatus()
        {
            var lv = CurrentList;
            if (firstLoad)
            {
                status.SetText(L.T("Загрузка…", "Loading…"));
                return;
            }
            if (lv == lvProcs)
            {
                var shown = lvProcs.Items.Cast<ListViewItem>().Select(r => (ProcInfo)r.Tag).ToList();
                long mem = shown.Sum(p => p.Memory);
                status.SetText(string.Format(L.T("Процессов: {0}  ·  из автозапуска: {1}  ·  память: {2:N1} ГБ",
                    "Processes: {0}  ·  from autostart: {1}  ·  memory: {2:N1} GB"),
                    shown.Count, shown.Count(p => p.Autorun), mem / 1073741824.0));
                return;
            }
            var items = lv.Items.Cast<ListViewItem>().Select(r => (AutorunItem)r.Tag).ToList();
            int on = items.Count(i => i.Enabled);
            string text = string.Format(L.T("Показано: {0}  ·  включено: {1}  ·  отключено: {2}", "Shown: {0}  ·  enabled: {1}  ·  disabled: {2}"),
                items.Count, on, items.Count - on);
            int missing = items.Count(i => !i.FileExists);
            if (missing > 0) text += L.T("  ·  файл не найден: ", "  ·  file missing: ") + missing;
            int hidden = (lv == lvTasks ? tasks : lv == lvServices ? services : startup).Count(i => i.IsSystem);
            if (!swSystem.Checked && hidden > 0 && lv != lvStartup) text += L.T("  ·  скрыто системных: ", "  ·  system hidden: ") + hidden;
            status.SetText(text);
        }

        void UpdateButtons()
        {
            bool procsTab = tabBar.SelectedIndex == TabProcs;
            if (procsTab)
            {
                bool any = lvProcs.SelectedItems.Count > 0;
                btnKill.Enabled = miKill.Enabled = any;
                btnOpen.Enabled = miProcOpen.Enabled = miProcCopy.Enabled = any;
            }
            else
            {
                var sel = SelectedAutoruns();
                btnEnable.Enabled = miEnable.Enabled = sel.Any(i => i.CanToggle && !i.Enabled);
                btnDisable.Enabled = miDisable.Enabled = sel.Any(i => i.CanToggle && i.Enabled);
                btnDelete.Enabled = miDelete.Enabled = sel.Any(i => i.CanDelete);
                miDelete.Visible = tabBar.SelectedIndex != TabServices;
                btnOpen.Enabled = miOpen.Enabled = miCopy.Enabled = sel.Count > 0;
            }
            btnRefresh.Enabled = !loading;
        }

        List<AutorunItem> SelectedAutoruns()
        {
            return CurrentList.SelectedItems.Cast<ListViewItem>().Select(r => r.Tag as AutorunItem).Where(i => i != null).ToList();
        }

        List<AutorunItem> ListFor(AutorunItem it)
        {
            switch (it.Kind)
            {
                case SourceKind.Task: return tasks;
                case SourceKind.Service: return services;
                default: return startup;
            }
        }

        // ---------- actions ----------

        /// <summary>"«name» done" for one item, "Done: N" for several.</summary>
        static string Describe(List<string> names, string one, string many)
        {
            return names.Count == 1 ? Quote(names[0]) + " " + one : many + names.Count;
        }

        void ReportErrors(List<string> errors)
        {
            string text = L.T("Ошибка: ", "Error: ") + errors[0];
            if (errors.Count > 1) text += L.T(" (и ещё ", " (and ") + (errors.Count - 1) + L.T(")", " more)");
            ShowToast(text, ToastKind.Error);
        }

        void DoToggle(bool enable)
        {
            if (tabBar.SelectedIndex == TabProcs) return;
            var lv = CurrentList;
            var rows = lv.SelectedItems.Cast<ListViewItem>()
                .Where(r => { var it = r.Tag as AutorunItem; return it != null && it.CanToggle && it.Enabled != enable; })
                .ToList();
            if (rows.Count == 0) return;
            bool hasServices = rows.Any(r => ((AutorunItem)r.Tag).Kind == SourceKind.Service);
            if (!enable && hasServices && !ThemedDialog.Confirm(this, L.T("Отключение служб", "Disable services"),
                    L.T("Отключение служб может нарушить работу Windows или установленных программ.\n" +
                        "Служба продолжит работать сейчас, но не запустится при следующей загрузке.\n\nОтключить?",
                        "Disabling services can break Windows or installed programs.\n" +
                        "The service keeps running now but will not start on the next boot.\n\nDisable?"),
                    L.T("Отключить", "Disable"), "", false))
                return;

            var done = new List<ListViewItem>();
            var names = new List<string>();
            var errors = new List<string>();
            Cursor = Cursors.WaitCursor;
            foreach (var row in rows)
            {
                var it = (AutorunItem)row.Tag;
                try
                {
                    Autoruns.SetEnabled(it, enable);
                    row.SubItems[1].Text = it.StateText;
                    done.Add(row);
                    names.Add(it.Name);
                }
                catch (Exception ex) { errors.Add(it.Name + ": " + ex.Message); }
            }
            Cursor = Cursors.Default;

            lv.Flash(done, enable ? Theme.Current.Success : Theme.Current.Warning);
            UpdateButtons();
            UpdateStatus();
            if (errors.Count > 0) ReportErrors(errors);
            else if (names.Count > 0)
            {
                string text = enable
                    ? Describe(names, L.T("включено", "enabled"), L.T("Включено записей: ", "Entries enabled: "))
                    : Describe(names, L.T("отключено", "disabled"), L.T("Отключено записей: ", "Entries disabled: "));
                if (hasServices) text += L.T(" · для служб — со следующей загрузки", " · services: from the next boot");
                ShowToast(text, ToastKind.Success);
            }
        }

        void DoDelete()
        {
            if (tabBar.SelectedIndex == TabProcs) return;
            var lv = CurrentList;
            var rows = lv.SelectedItems.Cast<ListViewItem>()
                .Where(r => { var it = r.Tag as AutorunItem; return it != null && it.CanDelete; })
                .ToList();
            if (rows.Count == 0) return;
            string msg = rows.Count == 1
                ? L.T("Удалить ", "Remove ") + Quote(((AutorunItem)rows[0].Tag).Name) + L.T(" из автозагрузки?", " from startup?")
                : L.T("Удалить выбранные записи (" + rows.Count + ") из автозагрузки?", "Remove the selected entries (" + rows.Count + ") from startup?");
            msg += L.T("\n\nПеред удалением будет сохранена резервная копия:\n", "\n\nA backup copy will be saved first:\n") + Backup.Dir;
            if (!ThemedDialog.Confirm(this, L.T("Удаление", "Delete"), msg, L.T("Удалить", "Delete"), "", true)) return;

            var removed = new List<ListViewItem>();
            var names = new List<string>();
            var errors = new List<string>();
            foreach (var row in rows)
            {
                var it = (AutorunItem)row.Tag;
                try
                {
                    Autoruns.Delete(it);
                    ListFor(it).Remove(it);
                    removed.Add(row);
                    names.Add(it.Name);
                }
                catch (Exception ex) { errors.Add(it.Name + ": " + ex.Message); }
            }
            lv.FadeOutAndRemove(removed, () => { UpdateButtons(); UpdateStatus(); });
            if (errors.Count > 0) ReportErrors(errors);
            else ShowToast(Describe(names, L.T("удалено, копия — в «Резервные копии»", "deleted, a copy is in \"Backups\""),
                L.T("Удалено записей: ", "Entries deleted: ")), ToastKind.Success);
        }

        void DoKill()
        {
            var rows = lvProcs.SelectedItems.Cast<ListViewItem>()
                .Where(r => ((ProcInfo)r.Tag).Pid != Process.GetCurrentProcess().Id)
                .ToList();
            if (rows.Count == 0) return;
            var first = (ProcInfo)rows[0].Tag;
            string msg = rows.Count == 1
                ? L.T("Завершить процесс ", "End process ") + Quote(first.Name) + " (PID " + first.Pid + ")?"
                : L.T("Завершить выбранные процессы (" + rows.Count + ")?", "End the selected processes (" + rows.Count + ")?");
            msg += L.T("\n\nНесохранённые данные в этих программах будут потеряны.", "\n\nUnsaved data in these programs will be lost.");
            if (!ThemedDialog.Confirm(this, L.T("Завершение процесса", "End process"), msg, L.T("Завершить", "End"), "", true)) return;

            var removed = new List<ListViewItem>();
            var names = new List<string>();
            var errors = new List<string>();
            foreach (var row in rows)
            {
                var pi = (ProcInfo)row.Tag;
                try
                {
                    ProcessList.Kill(pi.Pid);
                    procs.Remove(pi);
                    removed.Add(row);
                    names.Add(pi.Name);
                }
                catch (Exception ex) { errors.Add(pi.Name + ": " + ex.Message); }
            }
            lvProcs.FadeOutAndRemove(removed, () => { UpdateButtons(); UpdateStatus(); });
            if (errors.Count > 0) ReportErrors(errors);
            else ShowToast(Describe(names, L.T("завершён", "ended"), L.T("Завершено процессов: ", "Processes ended: ")), ToastKind.Success);
        }

        string SelectedPath()
        {
            var row = CurrentList.SelectedItems.Cast<ListViewItem>().FirstOrDefault();
            if (row == null) return null;
            var it = row.Tag as AutorunItem;
            if (it != null) return it.OpenPath;
            var pi = row.Tag as ProcInfo;
            return pi != null ? pi.Path : null;
        }

        void DoOpen()
        {
            string path = SelectedPath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (PathUtil.SafeExists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else ShowToast(L.T("Файл не найден: ", "File not found: ") + path, ToastKind.Error);
            }
            catch (Exception ex) { ShowToast(L.T("Не удалось открыть: ", "Could not open: ") + ex.Message, ToastKind.Error); }
        }

        void DoCopy()
        {
            var row = CurrentList.SelectedItems.Cast<ListViewItem>().FirstOrDefault();
            if (row == null) return;
            var it = row.Tag as AutorunItem;
            string text = it != null ? it.Command : ((ProcInfo)row.Tag).Path;
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                ShowToast(L.T("Скопировано в буфер обмена", "Copied to clipboard"), ToastKind.Info);
            }
            catch (Exception ex) { ShowToast(L.T("Не удалось скопировать: ", "Could not copy: ") + ex.Message, ToastKind.Error); }
        }

        void OpenBackupFolder()
        {
            try
            {
                Directory.CreateDirectory(Backup.Dir);
                Process.Start("explorer.exe", "\"" + Backup.Dir + "\"");
            }
            catch (Exception ex) { ShowToast(L.T("Не удалось открыть папку: ", "Could not open the folder: ") + ex.Message, ToastKind.Error); }
        }

        // ---------- keyboard ----------

        void ListKeyDown(object sender, KeyEventArgs e)
        {
            var lv = (ThemedListView)sender;
            if (e.KeyCode == Keys.Delete)
            {
                if (lv == lvProcs) DoKill(); else DoDelete();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Space && lv != lvProcs)
            {
                var sel = SelectedAutoruns().Where(i => i.CanToggle).ToList();
                if (sel.Count > 0) DoToggle(!sel.All(i => i.Enabled));
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.A && e.Control)
            {
                lv.BeginUpdate();
                lv.QuietSelection = true;
                foreach (ListViewItem row in lv.Items) row.Selected = true;
                lv.QuietSelection = false;
                lv.EndUpdate();
                lv.Invalidate();
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5) { Reload(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && e.Control) { search.FocusBox(); e.Handled = true; }
            else if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D4)
            {
                tabBar.SelectedIndex = e.KeyCode - Keys.D1;
                e.Handled = true;
            }
        }
    }

    /// <summary>Footer text that fades in whenever it changes.</summary>
    class StatusText : SkinControl
    {
        readonly Tweened alpha;

        public StatusText()
        {
            alpha = new Tweened(Invalidate, 1f);
        }

        public void SetText(string text)
        {
            if (text == Text) return;
            Text = text;
            alpha.Set(0.15f);
            alpha.To(1, 320);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color pb = ParentBg;
            e.Graphics.Clear(pb);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                Anim.Blend(pb, Theme.Current.TextDim, alpha.Value), TextLeft);
        }
    }
}
