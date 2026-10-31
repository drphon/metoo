using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace TodoClock
{
    public partial class MainWindow : Window
    {
        private const double CW = 200, CH = 200;   // collapsed window size
        private const double EW = 400, EH = 640;   // expanded window size
        private const double ExpClockCenterY = 12 + 12 + 60; // panel margin + clock margin + radius

        private readonly ObservableCollection<TodoItem> _daily = new ObservableCollection<TodoItem>();
        private readonly ObservableCollection<Project> _projects = new ObservableCollection<Project>();
        private Project _openProject;
        private bool _loading = true;
        private readonly AppSettings _settings;
        private readonly DispatcherTimer _timer;

        private bool _expanded;
        private bool _animating;
        private bool _suppressLocation;
        private double _anchorLeft, _anchorTop;    // where the collapsed clock lives
        private DateTime _lastDay = DateTime.MinValue;

        private string _calMode = "fa";
        private int _calYear, _calMonth;

        public bool AllowClose { get; set; }
        public bool IsExpanded => _expanded;

        public MainWindow()
        {
            InitializeComponent();

            _settings = Storage.LoadSettings();
            bool startExpanded = _settings.Expanded;

            // ---- data (daily todos + projects) ----
            var data = Storage.LoadData();
            foreach (var t in data.Daily)
            {
                t.PropertyChanged += Daily_ItemChanged;
                _daily.Add(t);
            }
            _daily.CollectionChanged += Daily_CollectionChanged;
            foreach (var p in data.Projects) AttachProject(p);
            _projects.CollectionChanged += (s, e) => DataChanged(true);
            ProjectList.ItemsSource = _projects;

            TodoPane.SortByDate = _settings.SortByDate;
            ProjectTasks.SortByDate = _settings.SortByDate;
            TodoPane.Items = _daily;
            foreach (var tl in new[] { TodoPane, ProjectTasks })
            {
                tl.DueRequested += ShowDuePicker;
                tl.SortChanged += v =>
                {
                    _settings.SortByDate = v;
                    TodoPane.SortByDate = v;
                    ProjectTasks.SortByDate = v;
                    SaveSettings();
                };
            }
            var reopen = _projects.FirstOrDefault(p => p.Id == _settings.OpenProject);
            if (reopen != null) ShowProject(reopen, false);
            RefreshCounts();

            // ---- opacity ----
            Root.Opacity = ClampOpacity(_settings.Opacity);

            // ---- position ----
            if (!IsPositionOnScreen(_settings.Left, _settings.Top))
            {
                var wa = SystemParameters.WorkArea;
                _settings.Left = wa.Right - CW - 24;
                _settings.Top = wa.Top + 24;
            }
            _anchorLeft = _settings.Left;
            _anchorTop = _settings.Top;
            _suppressLocation = true;
            Left = _anchorLeft;
            Top = _anchorTop;
            Width = CW;
            Height = CH;
            _suppressLocation = false;

            // ---- calendar & tabs ----
            _calMode = _settings.CalendarMode == "en" ? "en" : "fa";
            SetCalendarTo(TimeHelper.Now);
            if (_settings.Tab == "cal") TabCal.IsChecked = true;
            else if (_settings.Tab == "proj") TabProj.IsChecked = true;
            else TabTodo.IsChecked = true;
            _loading = false;

            UpdateTexts(TimeHelper.Now);

            if (startExpanded) Expand(false);

            // ---- events ----
            LocationChanged += MainWindow_LocationChanged;
            Deactivated += (s, e) => ReassertTopmost();
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            Closing += MainWindow_Closing;
            SourceInitialized += MainWindow_SourceInitialized;

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => OnTick();
            _timer.Start();
        }

        // ====================================================================
        //  Win32: hide from Alt+Tab
        // ====================================================================
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_APPWINDOW = 0x00040000L;

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            }
            catch { }
        }

        public void ReassertTopmost()
        {
            Topmost = false;
            Topmost = true;
        }

        // ====================================================================
        //  Clock tick
        // ====================================================================
        private DateTime _lastSecond = DateTime.MinValue;

        private void OnTick()
        {
            var now = TimeHelper.Now;
            if (now.Second == _lastSecond.Second && (now - _lastSecond).TotalSeconds < 1) return;
            _lastSecond = now;

            SmallClock.Tick(now);
            BigClock.Tick(now);
            UpdateTexts(now);

            if (now.Date != _lastDay)
            {
                bool first = _lastDay == DateTime.MinValue;
                _lastDay = now.Date;
                if (!first)
                {
                    foreach (var t in AllItems()) t.Item.RefreshDue();
                    foreach (var p in _projects) p.RefreshCounts();
                    TodoPane.RefreshCounts();
                    ProjectTasks.RefreshCounts();
                    RefreshCounts();
                    BuildCalendar();
                    if (DayPanel.Visibility == Visibility.Visible) ShowDay(_dayDate, false);
                }
            }
        }

        private void UpdateTexts(DateTime now)
        {
            TimeMain.Text = TimeHelper.Fa2(now.Hour) + ":" + TimeHelper.Fa2(now.Minute);
            TimeSec.Text = ":" + TimeHelper.Fa2(now.Second);
            FaDateText.Text = TimeHelper.FaDate(now);
            EnDateText.Text = TimeHelper.EnDate(now);
        }

        // ====================================================================
        //  Data: daily todos + projects
        // ====================================================================
        private void AttachProject(Project p)
        {
            p.Hook();
            p.Changed = () => DataChanged(false);
            _projects.Add(p);
        }

        private void Daily_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (TodoItem t in e.NewItems) { t.PropertyChanged -= Daily_ItemChanged; t.PropertyChanged += Daily_ItemChanged; }
            if (e.OldItems != null)
                foreach (TodoItem t in e.OldItems) t.PropertyChanged -= Daily_ItemChanged;
            DataChanged(true);
        }

        private void Daily_ItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TodoItem.Done) || e.PropertyName == nameof(TodoItem.Due) || e.PropertyName == nameof(TodoItem.Text))
                DataChanged(false);
        }

        private bool _calDirty;

        /// <summary>Anything changed: save, update badges and the calendar dots.</summary>
        private void DataChanged(bool listChanged)
        {
            if (_loading) return;
            SaveData();
            RefreshCounts();
            if (!_calDirty)
            {
                _calDirty = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _calDirty = false;
                    BuildCalendar();
                    if (listChanged && DayPanel.Visibility == Visibility.Visible) ShowDay(_dayDate, false);
                }), DispatcherPriority.Background);
            }
        }

        private void SaveData() =>
            Storage.SaveData(new AppData { Daily = _daily.ToList(), Projects = _projects.ToList() });

        private System.Collections.Generic.IEnumerable<DayEntry> AllItems()
        {
            foreach (var t in _daily) yield return new DayEntry { Item = t, ListName = "کارها" };
            foreach (var p in _projects)
                foreach (var t in p.Items) yield return new DayEntry { Item = t, ListName = p.Name };
        }

        private Brush _badgeNormal;

        private void RefreshCounts()
        {
            var today = TimeHelper.Now.Date;
            int dailyLeft = _daily.Count(t => !t.Done);
            int projDue = _projects.Sum(p => p.Items.Count(t => !t.Done && t.Due.HasValue && t.Due.Value.Date <= today));
            bool late = AllItems().Any(x => x.Item.DueState == "overdue");
            int left = dailyLeft + projDue;

            if (_badgeNormal == null) _badgeNormal = Badge.Background;
            Badge.Background = late
                ? new LinearGradientBrush(Color.FromRgb(0xFB, 0x71, 0x85), Color.FromRgb(0xE1, 0x1D, 0x48), 45)
                : _badgeNormal;
            Badge.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
            BadgeText.Text = left > 99 ? "۹۹+" : TimeHelper.Fa(left);
            Badge.ToolTip = late ? "کار عقب افتاده داری" : null;

            HeaderBadgeText.Text = dailyLeft > 0 ? TimeHelper.Fa(dailyLeft) + " کار" : "آزاد";

            // projects tab
            ProjEmpty.Visibility = _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_projects.Count == 0) ProjFooter.Text = "";
            else
            {
                int rem = _projects.Sum(p => p.Remaining);
                int all = _projects.Sum(p => p.Total);
                ProjFooter.Text = TimeHelper.Fa(_projects.Count) + " پروژه" +
                                  (all == 0 ? "" : rem > 0 ? "، " + TimeHelper.Fa(rem) + " کار مانده" : "، همه کارها انجام شده");
            }
        }

        // ====================================================================
        //  Projects
        // ====================================================================
        private void AddProject()
        {
            string name = (NewProject.Text ?? "").Trim();
            if (name.Length == 0) return;
            AttachProject(new Project { Name = name, Created = DateTime.UtcNow });
            NewProject.Text = "";
            NewProject.Focus();
        }

        private void NewProject_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return) { AddProject(); e.Handled = true; }
        }

        private void AddProject_Click(object sender, RoutedEventArgs e) => AddProject();

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Project p) ShowProject(p, true);
        }

        private static readonly ProjectBrushConverter _projBrush = new ProjectBrushConverter();

        private void ShowProject(Project p, bool animate)
        {
            foreach (var x in _projects) x.ConfirmDelete = false;
            _openProject = p;
            ProjectTasks.Items = p.Items;
            UpdateDetailHeader();
            EndRename(false);
            ProjListView.Visibility = Visibility.Collapsed;
            ProjDetailView.Visibility = Visibility.Visible;
            if (animate) SlideIn(ProjDetailView, 40);
            if (_settings.OpenProject != p.Id) { _settings.OpenProject = p.Id; if (!_loading) SaveSettings(); }
            if (animate) ProjectTasks.FocusInput();
        }

        private void UpdateDetailHeader()
        {
            if (_openProject == null) return;
            DetailName.Text = _openProject.Name;
            DetailName.FlowDirection = TimeHelper.IsRtlText(_openProject.Name) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            DetailName.HorizontalAlignment = HorizontalAlignment.Left;
            DetailInitial.Text = _openProject.Initial;
            DetailAvatar.Fill = (Brush)_projBrush.Convert(_openProject.ColorIndex, typeof(Brush), null, CultureInfo.InvariantCulture);
        }

        private void BackToProjects()
        {
            EndRename(true);
            _openProject = null;
            ProjectTasks.Items = null;
            ProjDetailView.Visibility = Visibility.Collapsed;
            ProjListView.Visibility = Visibility.Visible;
            SlideIn(ProjListView, -40);
            _settings.OpenProject = null;
            SaveSettings();
        }

        private void BackToProjects_Click(object sender, RoutedEventArgs e) => BackToProjects();

        private void SlideIn(FrameworkElement el, double from)
        {
            var tt = new TranslateTransform(from, 0);
            el.RenderTransform = tt;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        private void RenameProject_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Project p)
            {
                ShowProject(p, true);
                StartRename();
            }
        }

        private void DetailRename_Click(object sender, RoutedEventArgs e)
        {
            if (RenameBox.Visibility == Visibility.Visible) EndRename(true); else StartRename();
        }

        private void DetailName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2) { e.Handled = true; StartRename(); }
        }

        private void StartRename()
        {
            if (_openProject == null) return;
            RenameBox.Text = _openProject.Name;
            RenameBox.Visibility = Visibility.Visible;
            DetailName.Visibility = Visibility.Hidden;
            Dispatcher.BeginInvoke(new Action(() => { RenameBox.Focus(); RenameBox.SelectAll(); }), DispatcherPriority.Input);
        }

        private void EndRename(bool commit)
        {
            if (RenameBox.Visibility != Visibility.Visible) return;
            string name = (RenameBox.Text ?? "").Trim();
            RenameBox.Visibility = Visibility.Collapsed;
            DetailName.Visibility = Visibility.Visible;
            if (commit && _openProject != null && name.Length > 0 && name != _openProject.Name)
            {
                _openProject.Name = name;
                UpdateDetailHeader();
                DataChanged(false);
            }
        }

        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return) { EndRename(true); e.Handled = true; ProjectTasks.FocusInput(); }
        }

        private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(true);

        private void AskDeleteProject_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Project p)
            {
                foreach (var x in _projects) x.ConfirmDelete = x == p;
            }
        }

        private void CancelDeleteProject_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Project p) p.ConfirmDelete = false;
        }

        private void DeleteProject_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Project p)
            {
                p.Changed = null;
                _projects.Remove(p);
                if (_settings.OpenProject == p.Id) { _settings.OpenProject = null; SaveSettings(); }
            }
        }

        // ====================================================================
        //  Due date picker
        // ====================================================================
        private Action<DateTime?> _pickerCb;
        private DateTime? _pickerSel;
        private int _pickYear, _pickMonth;

        private void ShowDuePicker(DateTime? current, string label, Action<DateTime?> cb)
        {
            _pickerCb = cb;
            _pickerSel = current?.Date;
            var d = current ?? TimeHelper.Now;
            _pickYear = TimeHelper.Pc.GetYear(d);
            _pickMonth = TimeHelper.Pc.GetMonth(d);
            PickerTask.Text = label;
            PickNoneBtn.Visibility = current.HasValue ? Visibility.Visible : Visibility.Collapsed;
            QuickPicks.Columns = current.HasValue ? 4 : 3;
            BuildPicker();
            DuePicker.Visibility = Visibility.Visible;
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            PickerScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            PickerScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            DuePicker.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        private void ClosePicker(bool apply, DateTime? value)
        {
            var cb = _pickerCb;
            _pickerCb = null;
            DuePicker.Visibility = Visibility.Collapsed;
            if (apply) cb?.Invoke(value?.Date);
        }

        private void BuildPicker()
        {
            PickerTitle.Text = TimeHelper.FaMonths[_pickMonth - 1] + " " + TimeHelper.Fa(_pickYear);
            PickerWeek.Children.Clear();
            string[] heads = { "ش", "ی", "د", "س", "چ", "پ", "ج" };
            for (int i = 0; i < 7; i++)
                PickerWeek.Children.Add(new TextBlock
                {
                    Text = heads[i], FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2),
                    Foreground = i == 6 ? (Brush)FindResource("FridayBrush") : (Brush)FindResource("SubTextBrush"),
                });

            PickerDays.Children.Clear();
            var first = TimeHelper.Pc.ToDateTime(_pickYear, _pickMonth, 1, 0, 0, 0, 0);
            int days = TimeHelper.Pc.GetDaysInMonth(_pickYear, _pickMonth);
            int offset = ((int)first.DayOfWeek + 1) % 7;
            for (int i = 0; i < offset; i++) PickerDays.Children.Add(new Border());
            var marks = DueMarks();
            for (int d = 1; d <= days; d++)
            {
                var date = first.AddDays(d - 1);
                var cell = MakeDayCell(date, true, marks, small: true);
                if (_pickerSel == date)
                {
                    cell.BorderBrush = (Brush)FindResource("AccentBrush");
                    cell.BorderThickness = new Thickness(2);
                }
                cell.Cursor = Cursors.Hand;
                cell.MouseLeftButtonDown += (s, e) => { e.Handled = true; ClosePicker(true, date); };
                PickerDays.Children.Add(cell);
            }
            while (PickerDays.Children.Count < 42) PickerDays.Children.Add(new Border());
        }

        private void PickerShift(int delta)
        {
            int m = _pickMonth + delta, y = _pickYear;
            while (m < 1) { m += 12; y--; }
            while (m > 12) { m -= 12; y++; }
            if (y < 1300 || y > 1500) return;
            _pickYear = y; _pickMonth = m;
            BuildPicker();
            PickerDays.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(180)));
        }

        private void PickerPrev_Click(object sender, RoutedEventArgs e) => PickerShift(-1);
        private void PickerNext_Click(object sender, RoutedEventArgs e) => PickerShift(+1);
        private void PickToday_Click(object sender, RoutedEventArgs e) => ClosePicker(true, TimeHelper.Now.Date);
        private void PickTomorrow_Click(object sender, RoutedEventArgs e) => ClosePicker(true, TimeHelper.Now.Date.AddDays(1));
        private void PickNextWeek_Click(object sender, RoutedEventArgs e) => ClosePicker(true, TimeHelper.Now.Date.AddDays(7));
        private void PickNone_Click(object sender, RoutedEventArgs e) => ClosePicker(true, null);
        private void PickerClose_Click(object sender, RoutedEventArgs e) => ClosePicker(false, null);
        private void PickerCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void DuePickerBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ClosePicker(false, null);
        }

        // ====================================================================
        //  Calendar: tasks of one day
        // ====================================================================
        private DateTime _dayDate;

        private void ShowDay(DateTime date, bool animate)
        {
            _dayDate = date.Date;
            DayTitle.Text = TimeHelper.FaDate(date);
            DaySub.Text = TimeHelper.EnDate(date);
            DaySub.FlowDirection = FlowDirection.LeftToRight;
            var list = AllItems().Where(x => x.Item.Due.HasValue && x.Item.Due.Value.Date == _dayDate)
                                 .OrderBy(x => x.Item.Done).ThenBy(x => x.Item.Created).ToList();
            DayList.ItemsSource = list;
            DayEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (DayPanel.Visibility != Visibility.Visible || animate)
            {
                DayPanel.Visibility = Visibility.Visible;
                if (animate)
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    DayPanelShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                    DayPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
                }
            }
        }

        private void HideDay()
        {
            DayPanel.Visibility = Visibility.Collapsed;
            DayList.ItemsSource = null;
            DayNewTodo.Text = "";
        }

        private void DayPanelBack_Click(object sender, RoutedEventArgs e) => HideDay();

        private void DayAdd()
        {
            string text = (DayNewTodo.Text ?? "").Trim();
            if (text.Length == 0) return;
            _daily.Add(new TodoItem { Text = text, Created = DateTime.UtcNow, Due = _dayDate });
            DayNewTodo.Text = "";
            ShowDay(_dayDate, false);
        }

        private void DayNewTodo_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return) { DayAdd(); e.Handled = true; }
        }

        private void DayAdd_Click(object sender, RoutedEventArgs e) { DayAdd(); DayNewTodo.Focus(); }

        /// <summary>date -> 0 only done, 1 has open tasks, 2 has overdue tasks</summary>
        private System.Collections.Generic.Dictionary<DateTime, int> DueMarks()
        {
            var map = new System.Collections.Generic.Dictionary<DateTime, int>();
            foreach (var x in AllItems())
            {
                if (!x.Item.Due.HasValue) continue;
                var d = x.Item.Due.Value.Date;
                int v = x.Item.Done ? 0 : x.Item.DueState == "overdue" ? 2 : 1;
                map[d] = map.TryGetValue(d, out var old) ? Math.Max(old, v) : v;
            }
            return map;
        }

        // ====================================================================
        //  Tabs
        // ====================================================================
        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (TodoPane == null || CalPane == null || ProjPane == null) return;
            FrameworkElement show = TabCal.IsChecked == true ? CalPane : TabProj.IsChecked == true ? ProjPane : (FrameworkElement)TodoPane;
            foreach (var el in new FrameworkElement[] { TodoPane, ProjPane, CalPane })
                if (el != show) el.Visibility = Visibility.Collapsed;
            show.Visibility = Visibility.Visible;
            show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            if (show != CalPane) HideDay();
            if (_settings != null)
            {
                _settings.Tab = show == CalPane ? "cal" : show == ProjPane ? "proj" : "todo";
                if (!_loading) SaveSettings();
            }
        }

        // ====================================================================
        //  Calendar
        // ====================================================================
        private void SetCalendarTo(DateTime d)
        {
            if (_calMode == "fa")
            {
                _calYear = TimeHelper.Pc.GetYear(d);
                _calMonth = TimeHelper.Pc.GetMonth(d);
            }
            else
            {
                _calYear = d.Year;
                _calMonth = d.Month;
            }
            BuildCalendar();
        }

        private DateTime CalFirstDay() =>
            _calMode == "fa"
                ? TimeHelper.Pc.ToDateTime(_calYear, _calMonth, 1, 0, 0, 0, 0)
                : new DateTime(_calYear, _calMonth, 1);

        private int CalDaysInMonth() =>
            _calMode == "fa" ? TimeHelper.Pc.GetDaysInMonth(_calYear, _calMonth) : DateTime.DaysInMonth(_calYear, _calMonth);

        private void CalShift(int delta)
        {
            int m = _calMonth + delta, y = _calYear;
            while (m < 1) { m += 12; y--; }
            while (m > 12) { m -= 12; y++; }
            if (y < 1300 && _calMode == "fa") return;
            if (y < 1900 && _calMode == "en") return;
            if (y > 9000) return;
            _calYear = y;
            _calMonth = m;
            BuildCalendar();
            DaysGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(200)));
        }

        private void BuildCalendar()
        {
            bool fa = _calMode == "fa";
            var today = TimeHelper.Now.Date;
            var first = CalFirstDay();
            int days = CalDaysInMonth();
            var last = first.AddDays(days - 1);

            CalPane.FlowDirection = fa ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            CalModeBtn.Content = fa ? "میلادی" : "شمسی";
            CalTodayBtn.Content = fa ? "امروز" : "Today";

            if (fa)
            {
                CalTitle.Text = TimeHelper.FaMonths[_calMonth - 1] + " " + TimeHelper.Fa(_calYear);
                string a = first.ToString("MMMM", CultureInfo.InvariantCulture);
                string b = last.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
                CalSub.Text = first.Month == last.Month ? b : a + " – " + b;
                CalSub.FlowDirection = FlowDirection.LeftToRight;
            }
            else
            {
                CalTitle.Text = first.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
                int m1 = TimeHelper.Pc.GetMonth(first), m2 = TimeHelper.Pc.GetMonth(last);
                string a = TimeHelper.FaMonths[m1 - 1];
                string b = TimeHelper.FaMonths[m2 - 1] + " " + TimeHelper.Fa(TimeHelper.Pc.GetYear(last));
                CalSub.Text = m1 == m2 ? b : a + " - " + b;
                CalSub.FlowDirection = FlowDirection.RightToLeft;
            }

            // week header
            WeekHeader.Children.Clear();
            string[] heads = fa
                ? new[] { "ش", "ی", "د", "س", "چ", "پ", "ج" }
                : new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };
            for (int i = 0; i < 7; i++)
            {
                bool friday = fa ? i == 6 : i == 4;
                WeekHeader.Children.Add(new TextBlock
                {
                    Text = heads[i],
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = friday ? (Brush)FindResource("FridayBrush") : (Brush)FindResource("SubTextBrush"),
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }

            // day cells
            DaysGrid.Children.Clear();
            int offset = fa ? ((int)first.DayOfWeek + 1) % 7 : ((int)first.DayOfWeek + 6) % 7;
            for (int i = 0; i < offset; i++) DaysGrid.Children.Add(new Border());

            var marks = DueMarks();
            for (int d = 1; d <= days; d++)
            {
                var date = first.AddDays(d - 1);
                var cell = MakeDayCell(date, fa, marks, small: false);
                cell.Cursor = Cursors.Hand;
                cell.MouseLeftButtonDown += (s, e) => { e.Handled = true; ShowDay(date, true); };
                DaysGrid.Children.Add(cell);
            }
            while (DaysGrid.Children.Count < 42) DaysGrid.Children.Add(new Border());
        }

        /// <summary>One day cell, shared by the calendar tab and the due date picker.</summary>
        private Border MakeDayCell(DateTime date, bool fa, System.Collections.Generic.Dictionary<DateTime, int> marks, bool small)
        {
            var today = TimeHelper.Now.Date;
            bool isToday = date.Date == today;
            bool isFriday = date.DayOfWeek == DayOfWeek.Friday;
            var textBrush = (Brush)FindResource("TextBrush");
            var subBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xAE, 0xB6, 0xD8));

            var g = new Grid();
            g.Children.Add(new TextBlock
            {
                Text = fa ? TimeHelper.Fa(TimeHelper.Pc.GetDayOfMonth(date)) : date.Day.ToString(CultureInfo.InvariantCulture),
                FontSize = small ? 12.5 : 13,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday ? Brushes.White : isFriday ? (Brush)FindResource("FridayBrush") : textBrush,
                Margin = new Thickness(0, 0, 0, 3),
            });
            if (!small)
                g.Children.Add(new TextBlock
                {
                    Text = fa ? date.Day.ToString(CultureInfo.InvariantCulture) : TimeHelper.Fa(TimeHelper.Pc.GetDayOfMonth(date)),
                    FontSize = 8.5,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 5, 2),
                    Foreground = isToday ? new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)) : subBrush,
                });

            if (marks != null && marks.TryGetValue(date.Date, out int m))
            {
                Brush dot = m == 2 ? new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85))
                          : m == 1 ? (isToday ? Brushes.White : (Brush)FindResource("AccentBrush"))
                          : new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
                g.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 6, Height = 6, Fill = dot,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(5, 4, 0, 0),
                    IsHitTestVisible = false,
                });
            }

            var cell = new Border
            {
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(1.5),
                Background = isToday ? (Brush)FindResource("AccentGradient") : new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Child = g,
                ToolTip = TimeHelper.FaDate(date) + "\n" + TimeHelper.EnDate(date),
            };
            if (isToday)
                cell.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0x22, 0xD3, 0xEE), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6
                };
            cell.MouseEnter += (s, e) => { if (!isToday) cell.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)); };
            cell.MouseLeave += (s, e) => { if (!isToday) cell.Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)); };
            return cell;
        }

        private void CalPrev_Click(object sender, RoutedEventArgs e) => CalShift(-1);
        private void CalNext_Click(object sender, RoutedEventArgs e) => CalShift(+1);
        private void CalToday_Click(object sender, RoutedEventArgs e) => SetCalendarTo(TimeHelper.Now);

        private void CalMode_Click(object sender, RoutedEventArgs e)
        {
            // keep roughly the same month in view (use the middle of the shown month)
            var mid = CalFirstDay().AddDays(14);
            _calMode = _calMode == "fa" ? "en" : "fa";
            _settings.CalendarMode = _calMode;
            SaveSettings();
            SetCalendarTo(mid);
            CalPane.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(220)));
        }

        // ====================================================================
        //  Expand / collapse
        // ====================================================================
        public void ToggleExpand()
        {
            if (_animating) return;
            if (_expanded) Collapse(true); else Expand(true);
        }

        private void Expand(bool animate)
        {
            if (_expanded) return;
            _expanded = true;
            _settings.Expanded = true;
            SaveSettings();

            var wa = WorkAreaFor(_anchorLeft + CW / 2, _anchorTop + CH / 2);
            double l = Clamp(_anchorLeft - (EW - CW) / 2, wa.Left, wa.Right - EW);
            double t = Clamp(_anchorTop, wa.Top, wa.Bottom - EH);

            MoveWindow(l, t, EW, EH);

            double offX = _anchorLeft - l, offY = _anchorTop - t;
            CollapsedView.Margin = new Thickness(offX, offY, 0, 0);
            ExpandedView.RenderTransformOrigin = new Point((offX + CW / 2) / EW, (offY + CH / 2) / EH);
            ExpandedView.Visibility = Visibility.Visible;
            ExpandedView.IsHitTestVisible = true;
            CollapsedView.IsHitTestVisible = false;
            UpdateTexts(TimeHelper.Now);

            if (!animate)
            {
                SetNow(ExpandedView, OpacityProperty, 1);
                SetNow(ExpScale, ScaleTransform.ScaleXProperty, 1);
                SetNow(ExpScale, ScaleTransform.ScaleYProperty, 1);
                CollapsedView.Visibility = Visibility.Collapsed;
                return;
            }

            _animating = true;
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
            double small = 120.0 / 184.0;

            SetNow(ExpScale, ScaleTransform.ScaleXProperty, 0.35);
            SetNow(ExpScale, ScaleTransform.ScaleYProperty, 0.35);
            SetNow(ExpandedView, OpacityProperty, 0);

            Animate(ExpScale, ScaleTransform.ScaleXProperty, 1, 420, back);
            Animate(ExpScale, ScaleTransform.ScaleYProperty, 1, 420, back);
            Animate(ExpandedView, OpacityProperty, 1, 260, easeOut);
            Animate(ColScale, ScaleTransform.ScaleXProperty, small, 300, easeOut);
            Animate(ColScale, ScaleTransform.ScaleYProperty, small, 300, easeOut);
            Animate(CollapsedView, OpacityProperty, 0, 220, easeOut, () =>
            {
                CollapsedView.Visibility = Visibility.Collapsed;
                SetNow(ColScale, ScaleTransform.ScaleXProperty, 1);
                SetNow(ColScale, ScaleTransform.ScaleYProperty, 1);
                SetNow(CollapsedView, OpacityProperty, 1);
            });
            var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(440) };
            done.Tick += (s, e) =>
            {
                done.Stop();
                _animating = false;
                if (!IsActive) return;
                if (TabTodo.IsChecked == true) TodoPane.FocusInput();
                else if (TabProj.IsChecked == true && _openProject != null) ProjectTasks.FocusInput();
            };
            done.Start();
        }

        private void Collapse(bool animate)
        {
            if (!_expanded) return;
            _expanded = false;
            _settings.Expanded = false;
            SaveSettings();

            // make sure the collapsed spot is on screen
            var wa = WorkAreaFor(_anchorLeft + CW / 2, _anchorTop + CH / 2);
            _anchorLeft = Clamp(_anchorLeft, wa.Left, wa.Right - CW);
            _anchorTop = Clamp(_anchorTop, wa.Top, wa.Bottom - CH);

            double offX = _anchorLeft - Left, offY = _anchorTop - Top;
            CollapsedView.Margin = new Thickness(offX, offY, 0, 0);
            ExpandedView.RenderTransformOrigin = new Point((offX + CW / 2) / EW, (offY + CH / 2) / EH);
            CollapsedView.Visibility = Visibility.Visible;
            CollapsedView.IsHitTestVisible = true;
            ExpandedView.IsHitTestVisible = false;
            SmallClock.Sync(true);

            Action finish = () =>
            {
                ExpandedView.Visibility = Visibility.Collapsed;
                CollapsedView.Margin = new Thickness(0);
                MoveWindow(_anchorLeft, _anchorTop, CW, CH);
                _animating = false;
            };

            if (!animate)
            {
                SetNow(ExpandedView, OpacityProperty, 0);
                finish();
                return;
            }

            _animating = true;
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            double small = 120.0 / 184.0;

            SetNow(ColScale, ScaleTransform.ScaleXProperty, small);
            SetNow(ColScale, ScaleTransform.ScaleYProperty, small);
            SetNow(CollapsedView, OpacityProperty, 0);

            Animate(ExpScale, ScaleTransform.ScaleXProperty, 0.35, 300, easeIn);
            Animate(ExpScale, ScaleTransform.ScaleYProperty, 0.35, 300, easeIn);
            Animate(ExpandedView, OpacityProperty, 0, 260, easeIn);
            Animate(CollapsedView, OpacityProperty, 1, 300, easeOut);
            Animate(ColScale, ScaleTransform.ScaleXProperty, 1, 360, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
            Animate(ColScale, ScaleTransform.ScaleYProperty, 1, 360, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }, finish);
        }

        private void MoveWindow(double l, double t, double w, double h)
        {
            _suppressLocation = true;
            try
            {
                Left = l;
                Top = t;
                Width = w;
                Height = h;
            }
            finally { _suppressLocation = false; }
        }

        private static void SetNow(IAnimatable target, DependencyProperty dp, double value)
        {
            target.BeginAnimation(dp, null);
            ((DependencyObject)target).SetValue(dp, value);
        }

        private static void Animate(IAnimatable target, DependencyProperty dp, double to, int ms, IEasingFunction ease, Action done = null)
        {
            var a = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd,
            };
            a.Completed += (s, e) =>
            {
                ((DependencyObject)target).SetValue(dp, to);
                target.BeginAnimation(dp, null);
                done?.Invoke();
            };
            target.BeginAnimation(dp, a);
        }

        private static double Clamp(double v, double min, double max)
        {
            if (max < min) return min;
            return v < min ? min : v > max ? max : v;
        }

        private (double X, double Y) Scale()
        {
            try
            {
                if (PresentationSource.FromVisual(this) != null)
                {
                    var d = VisualTreeHelper.GetDpi(this);
                    return (d.DpiScaleX, d.DpiScaleY);
                }
                var b = WF.Screen.PrimaryScreen.Bounds;
                double sx = b.Width / SystemParameters.PrimaryScreenWidth;
                double sy = b.Height / SystemParameters.PrimaryScreenHeight;
                if (sx > 0.5 && sx < 5 && sy > 0.5 && sy < 5) return (sx, sy);
            }
            catch { }
            return (1, 1);
        }

        private Rect WorkAreaFor(double xDip, double yDip)
        {
            try
            {
                var (dx, dy) = Scale();
                var dpi = new { DpiScaleX = dx, DpiScaleY = dy };
                var scr = WF.Screen.FromPoint(new System.Drawing.Point(
                    (int)(xDip * dpi.DpiScaleX), (int)(yDip * dpi.DpiScaleY)));
                var wa = scr.WorkingArea;
                return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY,
                                wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        private bool IsPositionOnScreen(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top)) return false;
            try
            {
                var (dx, dy) = Scale();
                var dpi = new { DpiScaleX = dx, DpiScaleY = dy };
                var p = new System.Drawing.Point((int)((left + CW / 2) * dpi.DpiScaleX), (int)((top + CH / 2) * dpi.DpiScaleY));
                return WF.Screen.AllScreens.Any(s => s.WorkingArea.Contains(p));
            }
            catch
            {
                var v = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                 SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                return v.Contains(new Point(left + CW / 2, top + CH / 2));
            }
        }

        // ====================================================================
        //  Mouse: drag, double-click, wheel
        // ====================================================================
        private void Collapsed_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount >= 2) { ToggleExpand(); return; }
            DoDrag();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount >= 2) { ToggleExpand(); return; }
            DoDrag();
        }

        private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            DoDrag();
        }

        private void DoDrag()
        {
            if (_animating) return;
            try
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed) DragMove();
            }
            catch (InvalidOperationException) { }
            SaveSettings();
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            if (_suppressLocation || _animating) return;
            if (_expanded)
            {
                _anchorLeft = Left + (EW - CW) / 2;
                _anchorTop = Top;
            }
            else
            {
                _anchorLeft = Left;
                _anchorTop = Top;
            }
        }

        private void Opacity_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double o = ClampOpacity(Root.Opacity + (e.Delta > 0 ? 0.05 : -0.05));
            Root.Opacity = o;
            _settings.Opacity = o;
            SaveSettings();
            e.Handled = true;
        }

        private static double ClampOpacity(double o)
        {
            if (double.IsNaN(o)) return 1;
            return Math.Max(0.3, Math.Min(1.0, o));
        }

        private void Collapsed_MouseEnter(object sender, MouseEventArgs e)
        {
            ExpandBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            GlowFx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(220)));
        }

        private void Collapsed_MouseLeave(object sender, MouseEventArgs e)
        {
            ExpandBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            GlowFx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(300)));
        }

        private void ToggleBtn_Click(object sender, RoutedEventArgs e) => ToggleExpand();

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !_expanded) return;
            e.Handled = true;
            if (DuePicker.Visibility == Visibility.Visible) ClosePicker(false, null);
            else if (RenameBox.Visibility == Visibility.Visible) { EndRename(false); ProjectTasks.FocusInput(); }
            else if (DayPanel.Visibility == Visibility.Visible) HideDay();
            else ToggleExpand();
        }

        // ====================================================================
        //  Context menu
        // ====================================================================
        private void WidgetMenu_Opened(object sender, RoutedEventArgs e)
        {
            MenuStartup.IsChecked = StartupHelper.IsEnabled();
            MenuToggle.Header = _expanded ? "جمع کردن" : "باز کردن کارها و تقویم";
        }

        private void MenuToggle_Click(object sender, RoutedEventArgs e) => ToggleExpand();

        private void MenuOpacity_Click(object sender, RoutedEventArgs e)
        {
            Root.Opacity = 1;
            _settings.Opacity = 1;
            SaveSettings();
        }

        private void MenuStartup_Click(object sender, RoutedEventArgs e)
        {
            StartupHelper.Set(!StartupHelper.IsEnabled());
        }

        private void MenuHide_Click(object sender, RoutedEventArgs e) => Hide();

        private void MenuExit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApp();

        // ====================================================================
        //  Lifetime
        // ====================================================================
        public void ShowWidget()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            ReassertTopmost();
            Activate();
        }

        public void SaveSettings()
        {
            _settings.Left = _anchorLeft;
            _settings.Top = _anchorTop;
            _settings.Expanded = _expanded;
            _settings.Opacity = ClampOpacity(Root.Opacity);
            _settings.CalendarMode = _calMode;
            Storage.SaveSettings(_settings);
        }

        public void SaveAll()
        {
            SaveSettings();
            SaveData();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!AllowClose)
            {
                e.Cancel = true;   // Alt+F4 just hides to tray
                Hide();
                return;
            }
            _timer.Stop();
            SaveAll();
        }
    }
}
