using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TodoClock
{
    public partial class ClockFace : UserControl
    {
        private DateTime _lastSync = DateTime.MinValue;

        public static readonly DependencyProperty ShowInfoProperty = DependencyProperty.Register(
            nameof(ShowInfo), typeof(bool), typeof(ClockFace), new PropertyMetadata(true, (d, e) =>
            {
                var c = (ClockFace)d;
                var v = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
                c.TopText.Visibility = v;
                c.BottomText.Visibility = v;
            }));

        public bool ShowInfo
        {
            get => (bool)GetValue(ShowInfoProperty);
            set => SetValue(ShowInfoProperty, value);
        }

        public ClockFace()
        {
            InitializeComponent();
            BuildTicks();
            Loaded += (s, e) => Sync(true);
            IsVisibleChanged += (s, e) => { if (IsVisible) Sync(true); };
        }

        private void BuildTicks()
        {
            const double c = 100;
            for (int i = 0; i < 60; i++)
            {
                bool hour = i % 5 == 0;
                bool major = i % 15 == 0;
                double a = i * Math.PI / 30;
                double r1 = 88, r2 = major ? 76 : hour ? 79 : 84;
                var line = new Line
                {
                    X1 = c + r1 * Math.Sin(a), Y1 = c - r1 * Math.Cos(a),
                    X2 = c + r2 * Math.Sin(a), Y2 = c - r2 * Math.Cos(a),
                    Stroke = new SolidColorBrush(hour ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = major ? 3 : hour ? 2 : 1,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                Ticks.Children.Add(line);
            }

            int[] nums = { 12, 3, 6, 9 };
            foreach (int n in nums)
            {
                double a = n * Math.PI / 6;
                double r = 64;
                var tb = new TextBlock
                {
                    Text = TimeHelper.Fa(n),
                    FontSize = 14,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                    Width = 30,
                    TextAlignment = TextAlignment.Center,
                };
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
                Canvas.SetLeft(tb, c + r * Math.Sin(a) - 15);
                Canvas.SetTop(tb, c - r * Math.Cos(a) - 11);
                Ticks.Children.Add(tb);
            }
        }

        /// <summary>Called every second by the owner.</summary>
        public void Tick(DateTime now)
        {
            if (!IsVisible) return;
            if (ShowInfo)
            {
                TopText.Text = TimeHelper.FaShortDate(now);
                BottomText.Text = TimeHelper.Fa2(now.Hour) + ":" + TimeHelper.Fa2(now.Minute);
            }
            if ((DateTime.UtcNow - _lastSync).TotalSeconds >= 30) Sync(false);
        }

        /// <summary>Restart the hand animations from the exact current Tehran time.</summary>
        public void Sync(bool force)
        {
            _lastSync = DateTime.UtcNow;
            var now = TimeHelper.Now;
            double sec = now.Second + now.Millisecond / 1000.0;
            double min = now.Minute + sec / 60.0;
            double hr = (now.Hour % 12) + min / 60.0;

            Start(SecRot, sec * 6, TimeSpan.FromMinutes(1), 30);
            Start(MinRot, min * 6, TimeSpan.FromHours(1), 4);
            Start(HourRot, hr * 30, TimeSpan.FromHours(12), 1);

            if (ShowInfo)
            {
                TopText.Text = TimeHelper.FaShortDate(now);
                BottomText.Text = TimeHelper.Fa2(now.Hour) + ":" + TimeHelper.Fa2(now.Minute);
            }
        }

        private static void Start(RotateTransform rt, double from, TimeSpan period, int fps)
        {
            var anim = new DoubleAnimation(from, from + 360, new Duration(period))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(anim, fps);
            rt.BeginAnimation(RotateTransform.AngleProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
