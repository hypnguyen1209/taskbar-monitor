using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// control architecture

// Deskband
//      Options (class holding all options loaded from disk)
//      SystemWatcherControl(Options) (main control that displays graph and has context menu)
//      Settings dialog window (receives copy of options)
//          SystemWatcherControl(CopyOfOptions) (another instance for preview)        
namespace TaskbarMonitor
{
    public partial class SystemWatcherControl : UserControl
    {
        public bool SHOW_DEBUG = false;
        int taskbarHeight = 0;
        public delegate void SizeChangeHandler(Size size);
        public event SizeChangeHandler OnChangeSize;
        public Version Version { get; set; } = new Version(Properties.Resources.Version);
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Options Options { get; set; }

        private bool _previewMode = false;
        private ContextMenu _contextMenu = null;
        private ClaudeUsageMonitor claudeUsageMonitor;
        private System.Windows.Forms.Timer claudeUsageTimer;
        private bool claudeUsageRefreshing;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Monitor Monitor { get; private set; }
        public bool VerticalTaskbarMode
        {
            get; private set;
        }
        
        public bool PreviewMode
        {
            get
            {
                return _previewMode;
            }
            set
            {
                _previewMode = value;
                //this.ContextMenu = _previewMode ? null : _contextMenu;
            }
        }
        public int CountersCount
        {
            get
            {
                if (Monitor?.Counters == null) return 0;
                return Options.CounterOptions.Where(x => x.Value.Enabled == true).Count();
                //return Counters.Count;
            }
        }
        
        System.Drawing.Font fontCounter;
        Font fontTitle;
        int lastSize = 30;
        bool mouseOver = false;
        public GraphTheme customTheme;
        GraphTheme darkTheme;
        GraphTheme lightTheme;

        GraphTheme defaultTheme;


        Deskband AssociatedDeskband = null;
        TaskbarManager sTask;

        public SystemWatcherControl(Monitor monitor, bool verticalMode = false, Deskband associatedDeskband = null)//CSDeskBand.CSDeskBandWin w, 
        {
            this.VerticalTaskbarMode = verticalMode;
            this.AssociatedDeskband = associatedDeskband;
            this.SetStyle(ControlStyles.EnableNotifyMessage, true);
            AttachMonitor(monitor);
        }
        public SystemWatcherControl()
            :this(null)
        {
        }

        public SystemWatcherControl(Monitor monitor)            
        {            
            AttachMonitor(monitor); 
        }

        public void AttachMonitor(Monitor monitor)
        {
            Disposed += OnDispose;
            this.Monitor = monitor;
            if (this.Monitor != null)
            {                
                this.SetStyle(ControlStyles.EnableNotifyMessage, true);
                try
                {
                    Options opt = monitor.Options;
                    darkTheme = GraphTheme.DefaultDarkTheme();
                    lightTheme = GraphTheme.DefaultLightTheme();
                    customTheme = GraphTheme.ReadFromDisk();
                    opt.Upgrade(customTheme);

                    Initialize(opt);
                    this.BackColor = Color.Transparent;
                    monitor.OnMonitorUpdated += Monitor_OnMonitorUpdated;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading SystemWatcherControl: {ex.Message}", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnDispose(object sender, EventArgs e)
        {
            if(Monitor != null)
                Monitor.OnMonitorUpdated -= Monitor_OnMonitorUpdated;
            StopClaudeUsagePolling();
            if(BLL.WindowsInformation.IsWindows11())
                StopMousePolling();
        }

        private void Monitor_OnMonitorUpdated()
        {
            if (Options != null && this.Options.ThemeType == Options.ThemeList.AUTOMATIC)
            {
                this.defaultTheme = GetTheme(this.Options);
            }

            this.Invalidate();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if(sTask != null)
                sTask.RemoveControls();
            base.OnHandleDestroyed(e);
        }

        private GraphTheme GetTheme(Options opt)
        {
            GraphTheme theme = darkTheme;

            if (opt.ThemeType == Options.ThemeList.LIGHT)
            {
                theme = lightTheme;
            }
            else if (opt.ThemeType == Options.ThemeList.CUSTOM)
            {
                customTheme = GraphTheme.ReadFromDisk();
                opt.Upgrade(customTheme);
                theme = customTheme;
            }
            else if (opt.ThemeType == Options.ThemeList.AUTOMATIC)
            {
                // Try to detect Windows app mode (light/dark) from registry
                bool? isLightTheme = null;
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        if (key != null)
                        {
                            object regValue = key.GetValue("SystemUsesLightTheme");
                            if (regValue != null)
                            {
                                isLightTheme = ((int)regValue) > 0;
                            }
                            else
                            {
                                regValue = key.GetValue("AppsUseLightTheme");
                                if (regValue != null)
                                {
                                    isLightTheme = ((int)regValue) > 0;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore registry errors, fallback to color detection
                }

                if (isLightTheme.HasValue)
                {
                    theme = isLightTheme.Value ? lightTheme : darkTheme;
                }
                else
                {
                    // Sample several points along the taskbar and average their luminance
                    var taskbarRect = BLL.Win32Api.GetTaskbarPosition();
                    int sampleCount = 5;
                    double totalLuminance = 0;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        int x, y;
                        if (taskbarRect.Width > taskbarRect.Height)
                        {
                            // Horizontal taskbar (bottom or top)
                            x = taskbarRect.Left + (i * taskbarRect.Width) / (sampleCount - 1);
                            y = taskbarRect.Top + taskbarRect.Height / 2;
                        }
                        else
                        {
                            // Vertical taskbar (left or right)
                            x = taskbarRect.Left + taskbarRect.Width / 2;
                            y = taskbarRect.Top + (i * taskbarRect.Height) / (sampleCount - 1);
                        }
                        Color color = BLL.Win32Api.GetColourAt(new Point(x, y));
                        double luminance = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
                        totalLuminance += luminance;
                    }
                    double avgLuminance = totalLuminance / sampleCount;
                    theme = avgLuminance > 128 ? lightTheme : darkTheme;
                }
            }
            return theme;
        }



        public bool IsCustomTheme()
        {
            return GraphTheme.IsCustom(this.defaultTheme);
        }
        public void ApplyOptions(Options Options)
        {            
            ApplyOptions(Options, GetTheme(Options));
        }

        public void ApplyOptions(Options Options, GraphTheme theme)
        {
            this.Monitor.UpdateOptions(Options);            
            this.Options = Options;
            this.defaultTheme = theme;

            fontTitle = new Font(defaultTheme.TitleFont, defaultTheme.TitleSize, defaultTheme.TitleFontStyle);
            fontCounter = new Font(defaultTheme.CurrentValueFont, defaultTheme.CurrentValueSize, defaultTheme.CurrentValueFontStyle);

            if (!PreviewMode)
            {
                _contextMenu = new ContextMenu();
                _contextMenu.MenuItems.Add(new MenuItem("Settings...", MenuItem_Settings_onClick));
                _contextMenu.MenuItems.Add(new MenuItem("Open Task Manager...", (e, a) =>
                {
                    if (System.IO.File.Exists(Environment.SystemDirectory + @"\taskmgr.exe"))
                        System.Diagnostics.Process.Start(Environment.SystemDirectory + @"\taskmgr.exe");
                    else
                        System.Diagnostics.Process.Start(@"taskmgr.exe");
                }));
                _contextMenu.MenuItems.Add(new MenuItem("Open Resource Monitor...", (e, a) =>
                {
                    System.Diagnostics.Process.Start("resmon.exe");
                }));
                _contextMenu.MenuItems.Add(new MenuItem(String.Format("About taskbar-monitor (v{0})...", Version.ToString(3)), MenuItem_About_onClick));
                this.ContextMenu = _contextMenu;

                this.BackColor = Color.Transparent;
            }
            else
            {
                this.ContextMenu = null;

                var pos = BLL.Win32Api.GetTaskbarPosition();
                Color taskBarColour = BLL.Win32Api.GetColourAt(new Point(pos.Location.X + 1, pos.Location.Y + 1));
                this.BackColor = taskBarColour;
            }
            
            AdjustControlSize();
            //UpdateGraphs();
            this.Invalidate();

        }
        private void Initialize(Options opt)
        {

            var theme = GetTheme(opt);

           
            
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.DoubleBuffer, true);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Opaque, true);
            
            ApplyOptions(opt, theme);
            //Initialize();

            InitializeComponent();
            AdjustControlSize();
            if (BLL.WindowsInformation.IsWindows11())
                StartMousePolling();
            if (!PreviewMode)
                StartClaudeUsagePolling();
            //BLL.Win32Api.SetWindowPos(this.Handle, new IntPtr(0), this.Left, this.Top, this.Width, this.Height, 0);

        }

        private void AdjustControlSize()
        {
            if (PreviewMode)
                return;
            int taskbarWidth = GetTaskbarWidth();
            taskbarHeight = GetTaskbarHeight();

            // taskbar not being shown
            if(taskbarWidth == 0 && taskbarHeight == 0)
            {
                return;
            }
            int minimumHeight = taskbarHeight;            
            if (minimumHeight < 20)
                minimumHeight = 20;

            VerticalTaskbarMode = false;

            int counterSize = (Options.HistorySize + 10);
            int controlWidth = counterSize * CountersCount;
            int controlHeight = minimumHeight;

            if (!VerticalTaskbarMode)
            {
                controlWidth = GetUsageLimitWidth() + GetUsageLimitGap() + 34 + counterSize + 60;
                controlHeight = minimumHeight;
            }

            if (VerticalTaskbarMode && taskbarWidth < controlWidth)
            {
                int countersPerLine = Convert.ToInt32(Math.Floor((float)taskbarWidth / (float)counterSize));
                controlWidth = counterSize * countersPerLine;
                controlHeight = Convert.ToInt32(Math.Ceiling((float)CountersCount / (float)countersPerLine)) * (30 + 10);
            }
            if (VerticalTaskbarMode)
            {
                this.Left = 5;
                controlWidth = controlWidth - 5;
            }
            else 
            { 
                this.Top = 1;
                controlHeight = controlHeight - 2;
            }
            if (this.Size.Width != controlWidth || this.Size.Height != controlHeight)
            {
                this.Size = new Size(controlWidth, controlHeight);
                if (OnChangeSize != null)
                    OnChangeSize(new Size(controlWidth, controlHeight));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var maximumHeight = VerticalTaskbarMode ? 30 : this.Height;

            int graphPosition = 0;
            int graphPositionY = 0;
            int rowIndex = 0;


            System.Drawing.Graphics formGraphics = e.Graphics;// this.CreateGraphics();            
            formGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

            //formGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;//AntiAliasGridFit;
            formGraphics.Clear(Color.Transparent);
            if (SHOW_DEBUG)
            {
                using (SolidBrush BrushText = new SolidBrush(defaultTheme.TextColor))
                {
                    formGraphics.DrawString($"w: {this.Width}, h: {this.Height}", fontCounter, BrushText, new RectangleF(2, 2, 400, 100), new StringFormat());
                    formGraphics.DrawString($"tb h: {this.taskbarHeight}", fontCounter, BrushText, new RectangleF(2, 10, 400, 100), new StringFormat());

                    if (this.AssociatedDeskband != null)
                    {
                        formGraphics.DrawString($"db w: {this.AssociatedDeskband.Size.Width}, h: {this.AssociatedDeskband.Size.Height}", fontCounter, BrushText, new RectangleF(70, 2, 400, 100), new StringFormat());
                        formGraphics.DrawString($"tb h: {this.AssociatedDeskband.TaskbarInfo.Size.Height}", fontCounter, BrushText, new RectangleF(70, 10, 400, 100), new StringFormat());

                        formGraphics.DrawString($"min w: {this.AssociatedDeskband.Options.MinHorizontalSize.Width}, h: {this.AssociatedDeskband.Options.MinHorizontalSize.Height}", fontCounter, BrushText, new RectangleF(150, 2, 400, 100), new StringFormat());
                    }
                    using (Pen pen = new Pen(BrushText))
                    {
                        formGraphics.DrawRectangle(pen, new Rectangle(0, 0, this.Width - 1, this.Height - 1));
                    }
                }
            }
            else
            {
                if (Options == null)
                {
                    base.OnPaint(e);
                    return;
                }
                if (!VerticalTaskbarMode)
                {
                    if (ShouldShowClaudeUsage())
                    {
                        int cpuBlockX = GetCpuBlockX();
                        drawUsageLimitMockup(formGraphics, Math.Max(2, cpuBlockX - GetUsageLimitGap() - GetUsageLimitWidth()), maximumHeight);
                    }
                }
                foreach (var pair in Options.CounterOptions.Where(x => x.Value.Enabled == true).OrderBy(x => x.Value.Order))
                {
                    var name = pair.Key;
                    var opt = pair.Value;
                    var ct = Monitor.Counters.Where(x => x.GetName() == name).SingleOrDefault();
                    if (ct == null) continue;

                    var infos = ct.Infos;
                    //var opt = Options.CounterOptions[ct.GetName()];
                    //if (!opt.Enabled) continue;
                    var showCurrentValue = !opt.CurrentValueAsSummary &&
                        (opt.ShowCurrentValue == CounterOptions.DisplayType.SHOW || (opt.ShowCurrentValue == CounterOptions.DisplayType.HOVER && mouseOver));
                    int currentGraphPosition = graphPosition;
                    int currentGraphPositionY = graphPositionY;
                    int currentMaximumHeight = maximumHeight;

                    if (!VerticalTaskbarMode)
                    {
                        currentMaximumHeight = Math.Max(10, this.Height / Math.Max(1, CountersCount));
                        currentGraphPosition = GetCpuBlockX() + 34;
                        currentGraphPositionY = rowIndex * currentMaximumHeight;
                    }

                    lock (ct.ThreadLock)
                    {
                        if (infos.Count == 0)
                            continue;

                        if (!VerticalTaskbarMode)
                        {
                            drawPercentBar(formGraphics, ct.GetLabel(), ct.InfoSummary, GetCpuBlockX(), currentGraphPositionY, currentMaximumHeight, defaultTheme);
                        }
                        else if (ct.GetCounterType() == TaskbarMonitor.Counters.ICounter.CounterType.SINGLE)
                        {
                            var info = infos[0];
                            drawGraph(formGraphics, currentGraphPosition, currentGraphPositionY, currentMaximumHeight, false, info, defaultTheme, opt);

                        }
                        else if (ct.GetCounterType() == TaskbarMonitor.Counters.ICounter.CounterType.MIRRORED)
                        {


                            for (int z = 0; z < infos.Count; z++)
                            {
                                var info = opt.InvertOrder ? infos[infos.Count - 1 - z] : infos[z];
                                drawGraph(formGraphics, currentGraphPosition, z * (currentMaximumHeight / 2) + currentGraphPositionY, currentMaximumHeight / 2, z == 1, info, defaultTheme, opt);
                            }


                        }
                        else if (ct.GetCounterType() == TaskbarMonitor.Counters.ICounter.CounterType.STACKED)
                        {
                            drawStackedGraph(formGraphics, currentGraphPosition, currentGraphPositionY, currentMaximumHeight, opt.InvertOrder, infos, defaultTheme, opt);


                        }
                    }

                    var sizeTitle = formGraphics.MeasureString(ct.GetLabel(), fontTitle);
                    Dictionary<CounterOptions.DisplayPosition, float> positions = new Dictionary<CounterOptions.DisplayPosition, float>();

                    positions.Add(CounterOptions.DisplayPosition.MIDDLE, (currentMaximumHeight / 2 - sizeTitle.Height / 2) + 1 + currentGraphPositionY);
                    positions.Add(CounterOptions.DisplayPosition.TOP, currentGraphPositionY);
                    positions.Add(CounterOptions.DisplayPosition.BOTTOM, (currentMaximumHeight - sizeTitle.Height + 1) + currentGraphPositionY);

                    if (!VerticalTaskbarMode)
                    {
                        rowIndex++;
                        continue;
                    }

                    CounterOptions.DisplayPosition? usedPosition = null;
                    if (opt.ShowTitle == CounterOptions.DisplayType.SHOW
                     || opt.ShowTitle == CounterOptions.DisplayType.HOVER)
                    {

                        usedPosition = opt.TitlePosition;
                        var titleShadow = defaultTheme.TitleShadowColor;
                        var titleColor = defaultTheme.TitleColor;
                        
                        if (opt.ShowTitle == CounterOptions.DisplayType.HOVER && mouseOver)
                        {                            
                            //titleShadow = Color.FromArgb(40, titleShadow.R, titleShadow.G, titleShadow.B);
                        }
                        

                        System.Drawing.SolidBrush brushShadow = new System.Drawing.SolidBrush(titleShadow);
                        System.Drawing.SolidBrush brushTitle = new System.Drawing.SolidBrush(titleColor);

                        /*if (
                            (opt.ShowTitleShadowOnHover && opt.ShowTitle == CounterOptions.DisplayType.HOVER && !mouseOver)
                            || (opt.ShowTitle == CounterOptions.DisplayType.HOVER && mouseOver)
                            || opt.ShowTitle == CounterOptions.DisplayType.SHOW
                           )
                        {*/
                            // show shadow only on SHOW, or (HOVER and mouseover) or (HOVER and !mousever and showTitleShadow)
                            if ((opt.ShowTitle == CounterOptions.DisplayType.HOVER && opt.ShowTitleShadowOnHover) || mouseOver)
                            {
                            int offset = 1;
                            if (!mouseOver)
                                offset = 0;
                               formGraphics.DrawString(ct.GetLabel(), fontTitle, brushShadow, new RectangleF(graphPosition + (Options.HistorySize / 2) - (sizeTitle.Width / 2) + offset, positions[opt.TitlePosition] + offset, sizeTitle.Width, maximumHeight), new StringFormat());
                            }
                            // show title only on SHOW, or (HOVER and mouseover)
                            if ((opt.ShowTitle == CounterOptions.DisplayType.HOVER && mouseOver) || opt.ShowTitle == CounterOptions.DisplayType.SHOW)
                            {
                                formGraphics.DrawString(ct.GetLabel(), fontTitle, brushTitle, new RectangleF(graphPosition + (Options.HistorySize / 2) - (sizeTitle.Width / 2), positions[opt.TitlePosition], sizeTitle.Width, maximumHeight), new StringFormat());
                            }
                        //}
                        

                        brushShadow.Dispose();
                        brushTitle.Dispose();
                    }

                    if (opt.ShowCurrentValue == CounterOptions.DisplayType.SHOW
                     || opt.ShowCurrentValue == CounterOptions.DisplayType.HOVER)
                    {
                        Dictionary<CounterOptions.DisplayPosition, string> texts = new Dictionary<CounterOptions.DisplayPosition, string>();

                        if (opt.CurrentValueAsSummary || infos.Count > 2)
                        {
                            texts.Add(opt.SummaryPosition, ct.InfoSummary.CurrentStringValue);

                        }
                        else
                        {
                            List<CounterOptions.DisplayPosition> positionsAvailable = new List<CounterOptions.DisplayPosition> { CounterOptions.DisplayPosition.TOP, CounterOptions.DisplayPosition.MIDDLE, CounterOptions.DisplayPosition.BOTTOM };
                            if (usedPosition.HasValue)
                                positionsAvailable.Remove(usedPosition.Value);
                            var showName = infos.Count > 1;
                            for (int i = 0; i < infos.Count && i < 2; i++)
                            {
                                texts.Add(positionsAvailable[i], (showName ? infos[i].Name + " " : "") + infos[i].CurrentStringValue);
                            }
                        }
                        foreach (var item in texts)
                        {
                            string text = item.Value;

                            var sizeString = formGraphics.MeasureString(text, fontCounter);
                            float ypos = positions[item.Key];

                            var textShadow = defaultTheme.TextShadowColor;
                            var textColor = defaultTheme.TextColor;

                            if (opt.ShowCurrentValue == CounterOptions.DisplayType.HOVER && mouseOver)
                            {
                                //textShadow = Color.FromArgb(40, textShadow.R, textShadow.G, textShadow.B);
                            }

                            SolidBrush BrushText = new SolidBrush(textColor);
                            SolidBrush BrushTextShadow = new SolidBrush(textShadow);

                            /*if (
                            (opt.ShowCurrentValueShadowOnHover && opt.ShowCurrentValue == CounterOptions.DisplayType.HOVER && !mouseOver)
                            || (opt.ShowCurrentValue == CounterOptions.DisplayType.HOVER && mouseOver)
                            || opt.ShowCurrentValue == CounterOptions.DisplayType.SHOW
                           )
                            {*/
                            if ((opt.ShowCurrentValue == CounterOptions.DisplayType.HOVER && opt.ShowCurrentValueShadowOnHover) || mouseOver)
                            {
                                int offset = 1;
                                if (!mouseOver)
                                    offset = 0;
                                formGraphics.DrawString(text, fontCounter, BrushTextShadow, new RectangleF(graphPosition + (Options.HistorySize / 2) - (sizeString.Width / 2) + offset, ypos + offset, sizeString.Width, maximumHeight), new StringFormat());
                            }
                            if ((opt.ShowCurrentValue == CounterOptions.DisplayType.HOVER && mouseOver) || opt.ShowCurrentValue == CounterOptions.DisplayType.SHOW)
                            { 
                                formGraphics.DrawString(text, fontCounter, BrushText, new RectangleF(graphPosition + (Options.HistorySize / 2) - (sizeString.Width / 2), ypos, sizeString.Width, maximumHeight), new StringFormat());
                            }
                            //}
                            BrushText.Dispose();
                            BrushTextShadow.Dispose();
                        }
                    }


                    graphPosition += Options.HistorySize + 10;
                    if (VerticalTaskbarMode && graphPosition >= this.Size.Width)
                    {
                        graphPosition = 0;
                        graphPositionY += (maximumHeight + 10);
                    }

                }
            }

            AdjustControlSize();
            base.OnPaint(e);
        }

        private void drawPercentBar(System.Drawing.Graphics formGraphics, string label, TaskbarMonitor.Counters.CounterInfo info, int x, int y, int rowHeight, GraphTheme theme)
        {
            if (info == null || info.MaximumValue <= 0) return;

            float ratio = info.CurrentValue / info.MaximumValue;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            int labelWidth = 30;
            int barX = x + labelWidth + 4;
            int barWidth = Options.HistorySize;
            int barHeight = Math.Max(4, rowHeight - 6);
            int barY = y + Math.Max(0, (rowHeight - barHeight) / 2);
            int fillWidth = Convert.ToInt32(Math.Round(barWidth * ratio));
            string value = info.CurrentStringValue;

            using (SolidBrush brushTitle = new SolidBrush(theme.TitleColor))
            using (SolidBrush brushText = new SolidBrush(theme.TextColor))
            using (SolidBrush brushBack = new SolidBrush(Color.FromArgb(55, theme.TextColor)))
            using (SolidBrush brushFill = new SolidBrush(theme.BarColor))
            using (Pen border = new Pen(Color.FromArgb(120, theme.TextColor)))
            {
                var labelSize = formGraphics.MeasureString(label, fontTitle);
                var valueSize = formGraphics.MeasureString(value, fontCounter);
                float labelY = y + (rowHeight / 2f - labelSize.Height / 2f);
                float valueY = y + (rowHeight / 2f - valueSize.Height / 2f);

                formGraphics.DrawString(label, fontTitle, brushTitle, new RectangleF(x + 2, labelY, labelWidth, rowHeight), new StringFormat());
                formGraphics.FillRectangle(brushBack, new Rectangle(barX, barY, barWidth, barHeight));
                if (fillWidth > 0)
                    formGraphics.FillRectangle(brushFill, new Rectangle(barX, barY, fillWidth, barHeight));
                formGraphics.DrawRectangle(border, new Rectangle(barX, barY, barWidth, barHeight));
                formGraphics.DrawString(value, fontCounter, brushText, new RectangleF(barX + barWidth + 8, valueY, 52, rowHeight), new StringFormat());
            }
        }

        private static int GetUsageLimitWidth()
        {
            return 205;
        }

        private static int GetUsageLimitGap()
        {
            return 8;
        }

        private int GetCpuBlockX()
        {
            int cpuBlockWidth = 34 + Options.HistorySize + 10 + 60;
            return Math.Max(GetUsageLimitWidth() + GetUsageLimitGap(), this.Width - cpuBlockWidth);
        }

        private bool ShouldShowClaudeUsage()
        {
            return (Options?.EnableClaudeUsage).GetValueOrDefault(true)
                && (claudeUsageMonitor?.Snapshot.Visible).GetValueOrDefault();
        }

        private void drawUsageLimitMockup(System.Drawing.Graphics formGraphics, int x, int totalHeight)
        {
            int rowHeight = Math.Max(10, totalHeight / 2);
            ClaudeUsageSnapshot usage = claudeUsageMonitor?.Snapshot ?? new ClaudeUsageSnapshot();

            drawUsageLimitRow(formGraphics, "CURRENT", usage.CurrentRatio, usage.CurrentTimeLeft, x, 0, rowHeight);
            drawUsageLimitRow(formGraphics, "WEEKLY", usage.WeeklyRatio, usage.WeeklyTimeLeft, x, rowHeight, rowHeight);
        }

        private void drawUsageLimitRow(System.Drawing.Graphics formGraphics, string label, float ratio, string timeLeft, int x, int y, int rowHeight)
        {
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            int labelWidth = 58;
            int valueWidth = 82;
            int barWidth = GetUsageLimitWidth() - labelWidth - valueWidth - 12;
            int barHeight = Math.Max(4, rowHeight - 6);
            int barX = x + labelWidth + 4;
            int barY = y + Math.Max(0, (rowHeight - barHeight) / 2);
            int fillWidth = Convert.ToInt32(Math.Round(barWidth * ratio));
            string text = label + ":";
            string percentText = (ratio * 100).ToString("0") + "%";
            string timeText = " (" + timeLeft + ")";

            using (SolidBrush brushTitle = new SolidBrush(defaultTheme.TitleColor))
            using (SolidBrush brushText = new SolidBrush(defaultTheme.TextColor))
            using (SolidBrush brushTime = new SolidBrush(defaultTheme.TitleColor))
            using (SolidBrush brushBack = new SolidBrush(Color.FromArgb(45, defaultTheme.TextColor)))
            using (SolidBrush brushFill = new SolidBrush(label == "CURRENT"
                ? Color.FromArgb(0, 170, 255)
                : defaultTheme.getNthColor(2, 1)))
            using (Pen border = new Pen(Color.FromArgb(110, defaultTheme.TextColor)))
            {
                var textSize = formGraphics.MeasureString(text, fontTitle);
                var percentSize = formGraphics.MeasureString(percentText, fontCounter);
                var valueSize = formGraphics.MeasureString(percentText + timeText, fontCounter);
                float textY = y + (rowHeight / 2f - textSize.Height / 2f);
                float valueY = y + (rowHeight / 2f - valueSize.Height / 2f);
                float valueX = barX + barWidth + 4;

                formGraphics.DrawString(text, fontTitle, brushTitle, new RectangleF(x, textY, labelWidth, rowHeight), new StringFormat());
                formGraphics.FillRectangle(brushBack, new Rectangle(barX, barY, barWidth, barHeight));
                if (fillWidth > 0)
                    formGraphics.FillRectangle(brushFill, new Rectangle(barX, barY, fillWidth, barHeight));
                formGraphics.DrawRectangle(border, new Rectangle(barX, barY, barWidth, barHeight));
                formGraphics.DrawString(percentText, fontCounter, brushText, new RectangleF(valueX, valueY, valueWidth, rowHeight), new StringFormat());
                formGraphics.DrawString(timeText, fontCounter, brushTime, new RectangleF(valueX + percentSize.Width - 1, valueY, valueWidth, rowHeight), new StringFormat());
            }
        }
         
        private void drawGraph(System.Drawing.Graphics formGraphics, int x, int y, int maxH, bool invertido, TaskbarMonitor.Counters.CounterInfo info, GraphTheme theme, CounterOptions opt)
        {
            if (info.MaximumValue == 0) return;
            var pos = maxH - ((info.CurrentValue * maxH) / info.MaximumValue);
            if (pos > Int32.MaxValue) pos = Int32.MaxValue;
            int posInt = Convert.ToInt32(Math.Round(pos)) + y;

            var height = (info.CurrentValue * maxH) / info.MaximumValue;
            if (height > Int32.MaxValue) height = Int32.MaxValue;
            int heightInt = Convert.ToInt32(Math.Round(height));

            using (SolidBrush BrushBar = new SolidBrush(theme.BarColor))
            {
                if (invertido)
                    formGraphics.FillRectangle(BrushBar, new Rectangle(x + Options.HistorySize, maxH, 4, heightInt));
                else
                    formGraphics.FillRectangle(BrushBar, new Rectangle(x + Options.HistorySize, posInt, 4, heightInt));
            }

            var initialGraphPosition = x + Options.HistorySize - info.History.Count;
            Point[] points = new Point[info.History.Count + 2];
            int i = 0;
            int inverter = invertido ? -1 : 1;
            foreach (var item in info.History)
            {
                var heightItem = (item * maxH) / info.MaximumValue;
                if (heightItem > Int32.MaxValue) height = Int32.MaxValue;
                var convertido = Convert.ToInt32(Math.Round(heightItem));


                if (invertido)
                    points[i] = new Point(initialGraphPosition + i, 0 + convertido + y);
                else
                    points[i] = new Point(initialGraphPosition + i, maxH - convertido + y);
                i++;
            }
            if (invertido)
            {
                points[i] = new Point(initialGraphPosition + i, 0 + y);
                points[i + 1] = new Point(initialGraphPosition, 0 + y);
            }
            else
            {
                points[i] = new Point(initialGraphPosition + i, maxH + y);
                points[i + 1] = new Point(initialGraphPosition, maxH + y);
            }
            using (SolidBrush BrushGraph = new SolidBrush(theme.getNthColor(2, invertido ? 1 : 0)))
            {
                formGraphics.FillPolygon(BrushGraph, points);
            }

        }

        private void drawStackedGraph(System.Drawing.Graphics formGraphics, int x, int y, int maxH, bool invertido, List<TaskbarMonitor.Counters.CounterInfo> infos, GraphTheme theme, CounterOptions opt)
        {
            float absMax = 0;
            List<float> lastValue = new List<float>();

            // accumulate values for stacked effect
            List<List<float>> values = new List<List<float>>();
            foreach (var info in infos.AsEnumerable().Reverse())
            {
                absMax += info.MaximumValue;
                var value = new List<float>();
                int z = 0;
                foreach (var item in info.History)
                {
                    value.Add(item + (lastValue.Count > 0 ? lastValue.ElementAt(z) : 0));
                    z++;
                }
                values.Add(value);
                lastValue = value;
            }
            var historySize = values.Count > 0 ? values[0].Count : 0;
            // now we draw it

            var colors = theme.GetColorGradient(theme.StackedColors[0], theme.StackedColors[1], values.Count);
            int w = 0;
            if (!invertido)
                values.Reverse();
            foreach (var info in values)
            {
                float currentValue = info.Count > 0 ? info.Last() : 0;
                var pos = maxH - ((currentValue * maxH) / absMax);
                if (pos > Int32.MaxValue) pos = Int32.MaxValue;
                int posInt = Convert.ToInt32(Math.Round(pos)) + y;

                var height = (currentValue * maxH) / absMax;
                if (height > Int32.MaxValue) height = Int32.MaxValue;
                int heightInt = Convert.ToInt32(Math.Round(height));

                SolidBrush BrushBar = new SolidBrush(theme.BarColor);
                formGraphics.FillRectangle(BrushBar, new Rectangle(x + Options.HistorySize, posInt, 4, heightInt));
                BrushBar.Dispose();

                int i = 0;
                var initialGraphPosition = x + Options.HistorySize - historySize;
                Point[] points = new Point[historySize + 2];
                foreach (var item in info)
                {
                    var heightItem = (item * maxH) / absMax;
                    if (heightItem > Int32.MaxValue) heightItem = Int32.MaxValue;
                    var convertido = Convert.ToInt32(Math.Round(heightItem));

                    points[i] = new Point(initialGraphPosition + i, maxH - convertido + y);
                    i++;
                }
                points[i] = new Point(initialGraphPosition + i, maxH + y);
                points[i + 1] = new Point(initialGraphPosition, maxH + y);

                Brush brush = new SolidBrush(colors.ElementAt(w));
                w++;
                formGraphics.FillPolygon(brush, points);
                brush.Dispose();


            }
        }

        private static int GetTaskbarWidth()
        {
            return 0;
        }

        private static int GetTaskbarHeight()
        {
            var taskbar = BLL.Win32Api.GetTaskbarPosition();
            if (taskbar.Height > 0 && taskbar.Height < Screen.PrimaryScreen.Bounds.Height / 3)
                return taskbar.Height;

            int workingAreaDelta = Screen.PrimaryScreen.Bounds.Height - Screen.PrimaryScreen.WorkingArea.Height;
            return workingAreaDelta > 0 ? workingAreaDelta : 40;
        }

        private void SystemWatcherControl_MouseEnter(object sender, EventArgs e)
        {
            mouseOver = true;

            this.Invalidate();
        }

        private void SystemWatcherControl_MouseLeave(object sender, EventArgs e)
        {
            mouseOver = false;
            this.Invalidate();
        }
        private System.Windows.Forms.Timer mousePollTimer;
        private bool lastMouseOver = false;

        private void StartMousePolling()
        {
            mousePollTimer = new System.Windows.Forms.Timer();
            mousePollTimer.Interval = 50; // ms
            mousePollTimer.Tick += MousePollTimer_Tick;
            mousePollTimer.Start();
        }

        private void StopMousePolling()
        {
            if (mousePollTimer != null)
            {
                mousePollTimer.Stop();
                mousePollTimer.Tick -= MousePollTimer_Tick;
                mousePollTimer.Dispose();
                mousePollTimer = null;
            }
        }

        private void StartClaudeUsagePolling()
        {
            if (claudeUsageTimer != null) return;

            claudeUsageMonitor = new ClaudeUsageMonitor();
            claudeUsageTimer = new System.Windows.Forms.Timer();
            claudeUsageTimer.Interval = 120000;
            claudeUsageTimer.Tick += ClaudeUsageTimer_Tick;
            claudeUsageTimer.Start();
            RefreshClaudeUsage();
        }

        private void StopClaudeUsagePolling()
        {
            if (claudeUsageTimer != null)
            {
                claudeUsageTimer.Stop();
                claudeUsageTimer.Tick -= ClaudeUsageTimer_Tick;
                claudeUsageTimer.Dispose();
                claudeUsageTimer = null;
            }
        }

        private void ClaudeUsageTimer_Tick(object sender, EventArgs e)
        {
            RefreshClaudeUsage();
        }

        private async void RefreshClaudeUsage()
        {
            if (claudeUsageMonitor == null || claudeUsageRefreshing) return;
            claudeUsageRefreshing = true;

            try
            {
                await claudeUsageMonitor.RefreshAsync();
                if (!IsDisposed && IsHandleCreated)
                    Invalidate();
            }
            finally
            {
                claudeUsageRefreshing = false;
            }
        }

        private void MousePollTimer_Tick(object sender, EventArgs e)
        {
            var cursorPos = Cursor.Position;
            if (this.Disposing || this.IsDisposed) return;
            var clientRect = this.RectangleToScreen(this.ClientRectangle);
            bool isOver = clientRect.Contains(cursorPos);

            if (isOver && !lastMouseOver)
            {
                lastMouseOver = true;
                SystemWatcherControl_MouseEnter(this, EventArgs.Empty);
            }
            else if (!isOver && lastMouseOver)
            {
                lastMouseOver = false;
                SystemWatcherControl_MouseLeave(this, EventArgs.Empty);
            }
        }
        protected override void WndProc(ref Message m)
        {            
            base.WndProc(ref m);
        }

        private void OpenSettings(int activeIndex = 0)
        {
            var qtd = Application.OpenForms.OfType<OptionForm>();
            OptionForm optForm = null;
            if (qtd.Count() == 0)
            {
                optForm = new OptionForm(this.Options, this.customTheme, this.Version, TaskbarManager.GetInstance());
                optForm.Show();
            }
            else
            {
                optForm = qtd.First();
                optForm.Focus();
            }
            optForm.OpenTab(activeIndex);
        }
        private void MenuItem_Settings_onClick(object sender, EventArgs e)
        {
            OpenSettings();
        }
        private void MenuItem_About_onClick(object sender, EventArgs e)
        {
            OpenSettings(3);

        }

        protected override void OnParentBackColorChanged(EventArgs e)
        {
            this.Invalidate();
            base.OnParentBackColorChanged(e);
        }

        private void SystemWatcherControl_DoubleClick(object sender, EventArgs e)
        {
#if(DEBUG)
            SHOW_DEBUG = !SHOW_DEBUG;
            this.Invalidate();
#endif
        }
        protected override void OnNotifyMessage(Message m)
        {
            base.OnNotifyMessage(m);
        }
    }




}
