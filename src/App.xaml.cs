using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using WF = System.Windows.Forms;

namespace TodoClock
{
    public partial class App : Application
    {
        private const string MutexName = "TodoClock_Foad_SingleInstance_v1";
        private const string ShowEventName = "TodoClock_Foad_Show_v1";

        private Mutex _mutex;
        private EventWaitHandle _showEvent;
        private WF.NotifyIcon _tray;
        private WF.ToolStripMenuItem _trayStartup;
        private MainWindow _win;
        private bool _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // Already running: ask the running instance to show itself.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }

            base.OnStartup(e);

            DispatcherUnhandledException += (s, a) =>
            {
                Log(a.Exception);
                a.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log(a.ExceptionObject as Exception);

            try
            {
                Resources["AppFont"] = new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Vazirmatn, Segoe UI, Tahoma");
            }
            catch { }

            StartupHelper.RefreshPath();

            _win = new MainWindow();
            MainWindow = _win;
            _win.Show();

            SetupTray();
            ListenForShowRequests();
        }

        private void ListenForShowRequests()
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                var th = new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            _showEvent.WaitOne();
                            if (_exiting) return;
                            Dispatcher.BeginInvoke(new Action(() => _win?.ShowWidget()));
                        }
                        catch { return; }
                    }
                }) { IsBackground = true, Name = "TodoClockShowListener" };
                th.Start();
            }
            catch { }
        }

        private void SetupTray()
        {
            try
            {
                var menu = new WF.ContextMenuStrip
                {
                    RightToLeft = WF.RightToLeft.Yes,
                    Font = new System.Drawing.Font("Segoe UI", 10f),
                    ShowImageMargin = false,
                    ShowCheckMargin = true,
                };

                var showHide = new WF.ToolStripMenuItem("نمایش / پنهان");
                showHide.Click += (s, a) => ToggleVisible();
                var toggle = new WF.ToolStripMenuItem("باز کردن / جمع کردن");
                toggle.Click += (s, a) => { _win.ShowWidget(); _win.ToggleExpand(); };
                _trayStartup = new WF.ToolStripMenuItem("اجرا با روشن شدن ویندوز");
                _trayStartup.Click += (s, a) => StartupHelper.Set(!StartupHelper.IsEnabled());
                var exit = new WF.ToolStripMenuItem("خروج");
                exit.Click += (s, a) => ExitApp();

                menu.Items.Add(showHide);
                menu.Items.Add(toggle);
                menu.Items.Add(new WF.ToolStripSeparator());
                menu.Items.Add(_trayStartup);
                menu.Items.Add(new WF.ToolStripSeparator());
                menu.Items.Add(exit);
                menu.Opening += (s, a) => _trayStartup.Checked = StartupHelper.IsEnabled();

                _tray = new WF.NotifyIcon
                {
                    Icon = LoadIcon(),
                    Text = "TodoClock",
                    ContextMenuStrip = menu,
                    Visible = true,
                };
                _tray.MouseClick += (s, a) =>
                {
                    if (a.Button == WF.MouseButtons.Left) ToggleVisible();
                };
            }
            catch (Exception ex) { Log(ex); }
        }

        private static System.Drawing.Icon LoadIcon()
        {
            try
            {
                using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("TodoClock.app.ico");
                if (st != null) return new System.Drawing.Icon(st, WF.SystemInformation.SmallIconSize);
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        private void ToggleVisible()
        {
            if (_win == null) return;
            if (_win.IsVisible) _win.Hide();
            else _win.ShowWidget();
        }

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            try
            {
                if (_win != null)
                {
                    _win.AllowClose = true;
                    _win.Close();
                }
            }
            catch { }
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            }
            catch { }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }

        private static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Storage.Dir);
                File.AppendAllText(Path.Combine(Storage.Dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
