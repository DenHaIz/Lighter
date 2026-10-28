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
        const int FilterThirdParty = 0, FilterUnsigned = 1, FilterAll = 2;

        readonly Panel root = new BufferedPanel(), header = new BufferedPanel(), toolbar = new BufferedPanel();
        readonly Panel content = new BufferedPanel(), listHost = new BufferedPanel(), footer = new BufferedPanel();
        readonly TabBar tabBar = new TabBar();
        readonly Segmented themeSwitch, filter;
        readonly FlatButton btnLang, btnUser;
        readonly ToolTip tip = new ToolTip();
        readonly LoadingBar loadBar = new LoadingBar();
        readonly FlatButton btnEnable, btnDisable, btnDelete, btnKill, btnOpen, btnRefresh, btnBackup;
        readonly SearchBox search = new SearchBox();
        readonly StatusText status = new StatusText();
        readonly Toast toast = new Toast();
        readonly ThemedListView lvStartup, lvTasks, lvServices, lvProcs;
        readonly ThemedListView[] lists;
        readonly ContextMenuStrip menuAutorun = new ContextMenuStrip(), menuProc = new ContextMenuStrip(), menuUser = new ContextMenuStrip();
        readonly ToolStripMenuItem miEnable, miDisable, miDelete, miOpen, miCopy, miKill, miProcOpen, miProcCopy;
        readonly System.Windows.Forms.Timer searchDebounce = new System.Windows.Forms.Timer { Interval = 200 };
        readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        List<AutorunItem> startup = new List<AutorunItem>(), tasks = new List<AutorunItem>(), services = new List<AutorunItem>();
        List<ProcInfo> procs = new List<ProcInfo>();
        bool loading, firstLoad = true, buttonsQueued, compactTheme;
        int currentTab, titleWidth;
        string[] themeTexts = { "", "" };
        /// <summary>Only this user's entries (null = everyone's). Applies to all autorun tabs.</summary>
        string userFilter;
        // Lists are rebuilt only when shown: a hidden list just remembers that its data changed.
        readonly bool[] dirty = new bool[4];

        static int S(int v) { return Theme.S(v); }

        public MainForm()
        {
            Font = Theme.UiFont;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(1180), S(720));
            MinimumSize = new Size(S(980), S(620));
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
            filter = new Segmented(new[] { "", "", "" }, new[] { "", "", "" }, FilterThirdParty);
            filter.SelectedIndexChanged += (s, e) => Rerender(200);
            btnUser = new FlatButton("", "");
            btnUser.Click += (s, e) => ShowUserMenu();
            btnEnable = new FlatButton("", "");
            btnDisable = new FlatButton("", "");
            btnDelete = new FlatButton("", "", ButtonKind.Danger);
            btnKill = new FlatButton("", "", ButtonKind.Danger);
            btnOpen = new FlatButton("", "");
            btnRefresh = new FlatButton("", "");
            btnEnable.Click += (s, e) => DoToggle(true);
            btnDisable.Click += (s, e) => DoToggle(false);
            btnDelete.Click += (s, e) => DoDelete();
            btnKill.Click += (s, e) => DoKill();
            btnOpen.Click += (s, e) => DoOpen();
            btnRefresh.Click += (s, e) => Reload();
            search.TextChanged += (s, e) => { searchDebounce.Stop(); searchDebounce.Start(); };
            searchDebounce.Tick += (s, e) =>
            {
                searchDebounce.Stop();
                Rerender(160);
            };
            foreach (Control c in new Control[] { filter, btnUser, btnEnable, btnDisable, btnDelete, btnKill, btnOpen, btnRefresh, search })
                toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar();

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
            SetupMenu(menuUser);

            // ----- lists -----
            var autorunWidths = new[] { 230, 130, 190, 120, 290, 220 };
            var autorunWeights = new[] { 0.19f, 0.13f, 0.16f, 0.1f, 0.24f, 0.18f };
            var autorunMins = new[] { 150, 120, 120, 90, 150, 130 };
            lvStartup = MakeList(autorunWidths, 1, menuAutorun, autorunWeights, autorunMins);
            lvTasks = MakeList(autorunWidths, 1, menuAutorun, autorunWeights, autorunMins);
            lvServices = MakeList(autorunWidths, 1, menuAutorun, autorunWeights, autorunMins);
            lvProcs = MakeList(new[] { 230, 80, 120, 210, 120, 320 }, 4, menuProc,
                new[] { 0.2f, 0.07f, 0.1f, 0.2f, 0.1f, 0.33f }, new[] { 150, 60, 90, 120, 90, 160 });
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
            btnBackup = new FlatButton("", "", ButtonKind.Ghost);
            btnBackup.SetBaseHeight(32);
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

        ThemedListView MakeList(int[] widths, int dotColumn, ContextMenuStrip menu, float[] weights, int[] mins)
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
                ColumnWeights = weights,
                ColumnMins = mins,
                Loading = true,
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
            menu.Padding = new Padding(0, S(4), 0, S(4));
            menu.Opening += (s, e) =>
            {
                if (menu != menuUser) UpdateButtons();
                foreach (ToolStripItem item in menu.Items)
                    if (item is ToolStripMenuItem) item.Padding = new Padding(S(20), S(4), 0, S(4));
                menu.Opacity = 0;
            };
            menu.Opened += (s, e) => Anim.Run(menu, 160, t => menu.Opacity = t);
        }

        void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int size = S(36);
            Logo.DrawSharp(g, S(18), (header.Height - size) / 2, size);
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextDraw.DrawText(g, "Lighter", Theme.TitleFont, new Rectangle(S(64), 0, titleWidth, header.Height), Theme.Current.Text, flags);
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
            themeTexts = new[] { L.T("Тёмная", "Dark"), L.T("Светлая", "Light") };
            themeSwitch.SetTexts(themeTexts);
            compactTheme = false;
            tip.SetToolTip(themeSwitch, L.T("Тема оформления", "Theme"));
            SetButton(btnLang, L.En ? "EN" : "RU");
            tip.SetToolTip(btnLang, L.T("Язык интерфейса: русский. Switch to English", "Interface language: English. Переключить на русский"));

            filter.SetTexts(new[] { L.T("Сторонние", "Third-party"), L.T("Без подписи", "Unsigned"), L.T("Все", "All") });
            tip.SetToolTip(filter, L.T("Сторонние — без компонентов Windows (подписанных Microsoft).\nБез подписи — файлы без действительной цифровой подписи: их стоит проверить.",
                "Third-party: without Windows components (signed by Microsoft).\nUnsigned: files without a valid digital signature, worth a look."));
            tip.SetToolTip(btnUser, L.T("Показать записи только одного пользователя этого ПК", "Show the entries of one user of this PC only"));
            tip.SetToolTip(btnEnable, L.T("Включить запуск (Пробел)", "Turn startup on (Space)"));
            tip.SetToolTip(btnDisable, L.T("Отключить запуск, не удаляя запись (Пробел)", "Turn startup off without deleting the entry (Space)"));
            tip.SetToolTip(btnDelete, L.T("Удалить запись, сначала сохранив резервную копию (Del)", "Delete the entry after saving a backup copy (Del)"));
            tip.SetToolTip(btnKill, L.T("Завершить выбранные процессы (Del)", "End the selected processes (Del)"));
            tip.SetToolTip(btnOpen, L.T("Показать файл в Проводнике", "Show the file in Explorer"));
            tip.SetToolTip(btnRefresh, L.T("Перечитать все списки (F5)", "Reload all lists (F5)"));
            SetButton(btnBackup, L.T("Резервные копии", "Backups"));
            tip.SetToolTip(btnBackup, L.T("Папка с копиями удалённых записей", "The folder with copies of deleted entries"));
            UpdateSearchCue();
            UpdateUserButton();

            miEnable.Text = L.T("Включить", "Enable");
            miDisable.Text = L.T("Отключить", "Disable");
            miDelete.Text = L.T("Удалить", "Delete");
            miOpen.Text = miProcOpen.Text = L.T("Открыть расположение файла", "Show file in Explorer");
            miCopy.Text = L.T("Копировать команду", "Copy command");
            miKill.Text = L.T("Завершить процесс", "End process");
            miProcCopy.Text = L.T("Копировать путь", "Copy path");

            string[] autorunCols =
            {
                L.T("Имя", "Name"), L.T("Состояние", "Status"), L.T("Издатель (подпись)", "Publisher (signature)"),
                L.T("Пользователь", "User"), L.T("Команда", "Command"), L.T("Расположение", "Location")
            };
            foreach (var lv in new[] { lvStartup, lvTasks, lvServices }) SetColumns(lv, autorunCols);
            SetColumns(lvProcs, L.T("Имя", "Name"), "PID", L.T("Память, МБ", "Memory, MB"), L.T("Издатель", "Publisher"),
                L.T("Автозапуск", "Autostart"), L.T("Путь", "Path"));

            if (!firstLoad)
            {
                // Status, signature and location texts are built per row: rebuild the visible list now, the rest on demand.
                MarkAllDirty();
                RenderCurrent();
            }
            foreach (var lv in lists) lv.Invalidate();
            LayoutHeader();
            LayoutToolbar();
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

        // ---------- window lifetime ----------

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
            // The first opacity change composes the layered window once, which is slow: pay for it
            // (and for the first paint) before the clock starts.
            Opacity = 0.01;
            WindowFx.PaintNow(this);
            Anim.Run(this, 280, t => Opacity = 0.01 + 0.99 * t, () =>
            {
                WindowFx.EndFade(this);
                // Loading starts once the window is in: results landing mid-fade made it stutter.
                Reload();
            });
            lvStartup.Focus();
        }

        // ---------- layout and zoom ----------

        /// <summary>
        /// The interface grows with the window: zoom 1 at the default size, up to Theme.MaxZoom.
        /// Steps of 0.1 with a little hysteresis, so dragging the border does not flip between two steps.
        /// </summary>
        float ZoomFor(Size client)
        {
            float raw = Math.Min(client.Width / (1180f * Theme.Dpi), client.Height / (720f * Theme.Dpi));
            float step = (float)Math.Floor(raw * 10 + 0.0001f) / 10;
            step = Math.Max(1f, Math.Min(Theme.MaxZoom, step));
            float now = Theme.Zoom;
            if (step > now && raw < step + 0.02f && step - now < 0.11f) return now;
            if (step < now && raw > now - 0.03f) return now;
            return step;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized || ClientSize.Width < 10 || lists == null) return;
            float z = ZoomFor(ClientSize);
            if (Math.Abs(z - Theme.Zoom) > 0.001f) ApplyZoom(z);
        }

        /// <summary>Switches every control to the new zoom: fonts, sizes, then the layout.</summary>
        void ApplyZoom(float zoom)
        {
            if (!Theme.SetZoom(zoom)) return;
            // Running transitions hold pictures of the old layout.
            Transition.FinishAll();
            root.SuspendLayout();
            Rescale(root);
            header.Height = S(60);
            tabBar.Height = header.Height;
            loadBar.Height = S(2);
            toolbar.Height = S(60);
            footer.Height = S(40);
            content.Padding = new Padding(S(16), 0, S(16), S(4));
            foreach (var m in new[] { menuAutorun, menuProc, menuUser })
            {
                m.Font = Theme.UiFont;
                m.Padding = new Padding(0, S(4), 0, S(4));
            }
            root.ResumeLayout(true);
            foreach (var lv in lists) lv.Bounds = listHost.DisplayRectangle;
            LayoutHeader();
            LayoutToolbar();
            LayoutFooter();
            toast.Reposition(ToastAnchor());
            root.Invalidate(true);
        }

        static void Rescale(Control c)
        {
            foreach (Control child in c.Controls) Rescale(child);
            var f = Theme.Remap(c.Font);
            if (f != c.Font) c.Font = f;
            var s = c as IScalable;
            if (s != null) s.ApplyScale();
        }

        void LayoutHeader()
        {
            titleWidth = TextDraw.MeasureText("Lighter", Theme.TitleFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(4);
            tabBar.Location = new Point(S(64) + titleWidth + S(28), 0);
            // Narrow window: the theme switch keeps only its icons so nothing overlaps the tabs.
            bool compact = header.Width < tabBar.Right + S(24) + btnLang.Width + S(8) + themeSwitch.Width + S(16) + (compactTheme ? S(200) : 0);
            if (compact != compactTheme)
            {
                compactTheme = compact;
                themeSwitch.SetTexts(compact ? new[] { "", "" } : themeTexts);
            }
            int y = (header.Height - themeSwitch.Height) / 2;
            themeSwitch.Location = new Point(header.Width - themeSwitch.Width - S(16), y);
            btnLang.Location = new Point(themeSwitch.Left - btnLang.Width - S(8), y);
            header.Invalidate();
        }

        void LayoutFooter()
        {
            status.SetBounds(S(18), 0, status.Width, footer.Height);
            btnBackup.Location = new Point(footer.Width - btnBackup.Width - S(16), (footer.Height - btnBackup.Height) / 2);
            status.Width = Math.Max(S(100), btnBackup.Left - status.Left - S(12));
        }

        static int CaptionWidth(string text)
        {
            return S(24) + S(16) + S(8) + TextDraw.MeasureText(text, Theme.UiFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        }

        /// <summary>
        /// Filter, user and action buttons from the left, search on the right. In a narrow window the buttons
        /// give up their captions one by one (tooltips still explain them): the least needed first.
        /// </summary>
        void LayoutToolbar()
        {
            bool procsTab = tabBar.SelectedIndex == TabProcs;
            filter.Visible = btnUser.Visible = !procsTab;
            btnEnable.Visible = btnDisable.Visible = btnDelete.Visible = !procsTab;
            btnKill.Visible = procsTab;

            int x = S(16), y = (toolbar.Height - S(Theme.ControlH)) / 2;
            if (!procsTab)
            {
                filter.Location = new Point(x, y);
                x = filter.Right + S(8);
                SetButton(btnUser, UserButtonText());
                btnUser.Location = new Point(x, y);
                x = btnUser.Right + S(16);
            }

            var buttons = procsTab ? new[] { btnKill, btnOpen } : new[] { btnEnable, btnDisable, btnDelete, btnOpen };
            var captions = procsTab
                ? new[] { L.T("Завершить процесс", "End process"), L.T("Расположение", "Show file") }
                : new[] { L.T("Включить", "Enable"), L.T("Отключить", "Disable"), L.T("Удалить", "Delete"), L.T("Расположение", "Show file") };
            // Fold order: last button first (Show file, then Delete, Disable, Enable).
            var widths = captions.Select(CaptionWidth).ToArray();
            int room = toolbar.Width - x - S(16) - (S(40) + S(8)) - S(160) - S(28);
            int bare = S(40), folded = 0;
            while (folded < buttons.Length && widths.Select((wd, i) => (i >= buttons.Length - folded ? bare : wd) + S(8)).Sum() + S(16) > room) folded++;
            for (int i = 0; i < buttons.Length; i++)
            {
                string want = i >= buttons.Length - folded ? "" : captions[i];
                if (buttons[i].Text != want) SetButton(buttons[i], want);
                buttons[i].Location = new Point(x, y);
                x = buttons[i].Right + S(8);
                if (buttons[i] == btnDelete || buttons[i] == btnKill) x += S(8);
            }
            btnRefresh.Location = new Point(x, y);
            x = btnRefresh.Right + S(8);
            int w = Math.Max(S(160), Math.Min(S(320), toolbar.Width - x - S(28)));
            search.SetBounds(toolbar.Width - S(16) - w, y, w, S(Theme.ControlH));
        }

        void UpdateSearchCue()
        {
            search.SetCue(tabBar.SelectedIndex == TabProcs ? L.T("Поиск процессов", "Search processes") : L.T("Поиск записей", "Search entries"));
            tip.SetToolTip(search, L.T("Ищет по имени, команде, издателю, пользователю и расположению (Ctrl+F)",
                "Searches names, commands, publishers, users and locations (Ctrl+F)"));
        }

        Point ToastAnchor()
        {
            return new Point(root.ClientSize.Width - S(28), footer.Top - S(14));
        }

        void ShowToast(string text, ToastKind kind)
        {
            toast.Show(text, kind, ToastAnchor());
        }

        // ---------- user filter ----------

        string UserButtonText()
        {
            return userFilter ?? L.T("Все пользователи", "All users");
        }

        void UpdateUserButton()
        {
            SetButton(btnUser, UserButtonText());
        }

        void ShowUserMenu()
        {
            menuUser.Items.Clear();
            // People first (profiles of this PC and "everyone"), then system accounts and groups tasks/services run as.
            var cmp = StringComparer.CurrentCultureIgnoreCase;
            var all = startup.Concat(tasks).Concat(services).ToList();
            var people = new HashSet<string>(all.Where(i => i.Kind == SourceKind.Registry || i.Kind == SourceKind.StartupFolder)
                .Select(i => i.UserName), StringComparer.OrdinalIgnoreCase);
            var other = all.Select(i => i.UserName).Where(n => n.Length > 0 && !people.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, cmp).ToList();
            var groups = new List<List<string>> { new List<string> { null }, people.OrderBy(n => n, cmp).ToList(), other };
            foreach (var group in groups.Where(g => g.Count > 0))
            {
                if (menuUser.Items.Count > 0) menuUser.Items.Add(new ToolStripSeparator());
                foreach (string name in group)
                {
                    string n = name;
                    var mi = new ToolStripMenuItem(n ?? L.T("Все пользователи", "All users"))
                    {
                        Tag = new MenuTag { Glyph = string.Equals(n, userFilter, StringComparison.OrdinalIgnoreCase) ? "" : "" },
                    };
                    mi.Click += (s, e) => SetUserFilter(n);
                    menuUser.Items.Add(mi);
                }
            }
            menuUser.Show(btnUser, new Point(0, btnUser.Height + S(4)));
        }

        void SetUserFilter(string name)
        {
            if (string.Equals(name, userFilter, StringComparison.OrdinalIgnoreCase)) return;
            userFilter = name;
            Transition.CrossFade(toolbar, LayoutToolbar, 200);
            Rerender(200);
        }

        // ---------- tabs ----------

        ThemedListView CurrentList { get { return lists[tabBar.SelectedIndex]; } }

        void OnTabChanged()
        {
            int idx = tabBar.SelectedIndex;
            int dir = idx > currentTab ? 1 : -1;
            bool toolbarChanges = (idx == TabProcs) != (currentTab == TabProcs);
            currentTab = idx;
            Transition.CrossFade(listHost, () =>
            {
                EnsureRendered(idx);
                for (int i = 0; i < lists.Length; i++) lists[i].Visible = i == idx;
            }, 260, dir * S(36));
            if (toolbarChanges) Transition.CrossFade(toolbar, () => { LayoutToolbar(); UpdateSearchCue(); }, 260);
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

            // Sources are independent: load them side by side, total time = the slowest one. Each checks its
            // own signatures; the process job also extracts its (many) icons meanwhile.
            var jobs = new[]
            {
                StartJob(errors, L.T("реестр", "registry"), () => { reg.AddRange(RegistryRunSource.Load()); Signature.Apply(reg); }),
                StartJob(errors, L.T("папки автозагрузки", "startup folders"), () => { folders.AddRange(StartupFolderSource.Load()); Signature.Apply(folders); }),
                StartJob(errors, L.T("планировщик", "task scheduler"), () => { tk.AddRange(TaskSchedulerSource.Load()); Signature.Apply(tk); }),
                StartJob(errors, L.T("службы", "services"), () => { sv.AddRange(ServiceSource.Load()); Signature.Apply(sv); }),
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
            bool wasFirst = firstLoad;
            firstLoad = false;
            Transition.CrossFade(listHost, () =>
            {
                foreach (var lv in lists) lv.Loading = false;
                RenderCurrent();
            }, wasFirst ? 320 : 220);

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

        void MarkAllDirty()
        {
            for (int i = 0; i < dirty.Length; i++) dirty[i] = true;
        }

        /// <summary>Filter, user or search changed: the visible list fades into the new rows, the others follow when shown.</summary>
        void Rerender(int ms)
        {
            MarkAllDirty();
            Transition.CrossFade(listHost, RenderCurrent, ms);
            UpdateButtons();
            UpdateStatus();
        }

        void RenderCurrent()
        {
            EnsureRendered(tabBar.SelectedIndex);
        }

        void EnsureRendered(int tab)
        {
            if (!dirty[tab] || firstLoad) return;
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

        static string NotFoundText(string q)
        {
            return L.T("Ничего не найдено по запросу ", "Nothing matches ") + L.Quote(q);
        }

        /// <summary>Whether the filter and the user choice let this entry through (search aside).</summary>
        bool Passes(AutorunItem it)
        {
            if (filter.SelectedIndex == FilterThirdParty && it.IsSystem) return false;
            if (filter.SelectedIndex == FilterUnsigned && !it.Unsigned) return false;
            if (userFilter != null && !string.Equals(it.UserName, userFilter, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        void RenderAutoruns(ThemedListView lv, List<AutorunItem> items)
        {
            var selected = SelectedTags(lv);
            string q = search.Text.Trim();
            var rows = new List<ListViewItem>();
            int hidden = 0;
            foreach (var it in items)
            {
                if (!Passes(it)) { hidden++; continue; }
                string location = it.Location;
                string publisher = it.PublisherText;
                if (!Matches(q, it.Name, it.Command, publisher, location, it.UserName)) continue;
                rows.Add(new ListViewItem(new[] { it.Name, it.StateText, publisher, it.UserName, it.Command, location }) { Tag = it });
            }
            if (q.Length > 0)
            {
                lv.EmptyGlyph = "";
                lv.EmptyText = NotFoundText(q);
            }
            else if (filter.SelectedIndex == FilterUnsigned && userFilter == null)
            {
                lv.EmptyGlyph = "";
                lv.EmptyText = L.T("Все файлы здесь подписаны", "Every file here is signed");
            }
            else if (hidden > 0)
            {
                lv.EmptyGlyph = "";
                lv.EmptyText = L.T("Подходящих записей нет. Скрыто фильтром: " + hidden + " — выберите «Все», чтобы их показать",
                    "No matching entries. " + hidden + " hidden by the filter — choose \"All\" to show them");
            }
            else
            {
                lv.EmptyGlyph = "";
                lv.EmptyText = L.T("Записей нет", "No entries");
            }
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
            lvProcs.EmptyGlyph = q.Length > 0 ? "" : "";
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
            int unsigned = items.Count(i => i.Unsigned);
            if (unsigned > 0) text += L.T("  ·  без подписи: ", "  ·  unsigned: ") + unsigned;
            int missing = items.Count(i => !i.FileExists);
            if (missing > 0) text += L.T("  ·  файл не найден: ", "  ·  file missing: ") + missing;
            int hidden = (lv == lvTasks ? tasks : lv == lvServices ? services : startup).Count(i => !Passes(i));
            if (hidden > 0) text += L.T("  ·  скрыто фильтром: ", "  ·  hidden by filter: ") + hidden;
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
                miDelete.Text = sel.Count > 0 && sel.All(i => i.ResetToDefault) ? L.T("Вернуть значение Windows", "Restore the Windows value") : L.T("Удалить", "Delete");
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
            return names.Count == 1 ? L.Quote(names[0]) + " " + one : many + names.Count;
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
            var first = (AutorunItem)rows[0].Tag;
            string msg;
            if (rows.Count == 1 && first.ResetToDefault)
                msg = L.T("Вернуть для ", "Put ") + L.Quote(first.Name) + L.T(" значение Windows по умолчанию?\n\nСейчас: ", " back to the Windows default?\n\nNow: ")
                    + first.Command + L.T("\nСтанет: ", "\nWill be: ") + first.DefaultValue;
            else if (rows.Count == 1)
                msg = L.T("Удалить ", "Remove ") + L.Quote(first.Name) + L.T(" из автозагрузки?", " from startup?");
            else
                msg = L.T("Удалить выбранные записи (" + rows.Count + ") из автозагрузки?", "Remove the selected entries (" + rows.Count + ") from startup?");
            msg += L.T("\n\nСначала будет сохранена резервная копия:\n", "\n\nA backup copy is saved first:\n") + Backup.Dir;
            bool reset = rows.All(r => ((AutorunItem)r.Tag).ResetToDefault);
            if (!ThemedDialog.Confirm(this, reset ? L.T("Значение Windows", "Windows default") : L.T("Удаление", "Delete"), msg,
                    reset ? L.T("Вернуть", "Restore") : L.T("Удалить", "Delete"), reset ? "" : "", true)) return;

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
                ? L.T("Завершить процесс ", "End process ") + L.Quote(first.Name) + " (PID " + first.Pid + ")?"
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
}
