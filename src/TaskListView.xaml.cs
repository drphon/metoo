using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace TodoClock
{
    /// <summary>A reusable task list (input, items with due dates, footer). Used for the daily list and each project.</summary>
    public partial class TaskListView : UserControl
    {
        private ObservableCollection<TodoItem> _items;
        private ListCollectionView _view;
        private DateTime? _pendingDue;

        /// <summary>Asks the host to show the due date picker: (current value, callback with result).</summary>
        public event Action<DateTime?, string, Action<DateTime?>> DueRequested;
        /// <summary>Raised when the sort toggle changes.</summary>
        public event Action<bool> SortChanged;

        public TaskListView()
        {
            InitializeComponent();
        }

        public string Placeholder { get => NewTodo.Tag as string; set => NewTodo.Tag = value; }
        public string EmptyTitle { get => EmptyTitleText.Text; set => EmptyTitleText.Text = value; }
        public string EmptySub { get => EmptySubText.Text; set => EmptySubText.Text = value; }

        public ObservableCollection<TodoItem> Items
        {
            get => _items;
            set
            {
                if (_items != null)
                {
                    _items.CollectionChanged -= Items_CollectionChanged;
                    foreach (var t in _items) t.PropertyChanged -= Item_PropertyChanged;
                }
                _items = value;
                _pendingDue = null;
                UpdatePending();
                NewTodo.Text = "";
                if (_items != null)
                {
                    _items.CollectionChanged += Items_CollectionChanged;
                    foreach (var t in _items) t.PropertyChanged += Item_PropertyChanged;
                    _view = new ListCollectionView(_items);
                    _view.IsLiveSorting = true;
                    _view.LiveSortingProperties.Add(nameof(TodoItem.Done));
                    _view.LiveSortingProperties.Add(nameof(TodoItem.SortDue));
                    ApplySort();
                    TodoList.ItemsSource = _view;
                }
                else
                {
                    _view = null;
                    TodoList.ItemsSource = null;
                }
                RefreshCounts();
                TodoScroll.ScrollToTop();
            }
        }

        public bool SortByDate
        {
            get => SortBtn.IsChecked == true;
            set { SortBtn.IsChecked = value; ApplySort(); }
        }

        private void ApplySort()
        {
            if (_view == null) return;
            using (_view.DeferRefresh())
            {
                _view.SortDescriptions.Clear();
                if (SortBtn.IsChecked == true)
                {
                    _view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Done), ListSortDirection.Ascending));
                    _view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.SortDue), ListSortDirection.Ascending));
                    _view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Created), ListSortDirection.Ascending));
                }
            }
        }

        private void SortBtn_Click(object sender, RoutedEventArgs e)
        {
            ApplySort();
            SortChanged?.Invoke(SortByDate);
        }

        private void Items_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (TodoItem t in e.NewItems) { t.PropertyChanged -= Item_PropertyChanged; t.PropertyChanged += Item_PropertyChanged; }
            if (e.OldItems != null) foreach (TodoItem t in e.OldItems) t.PropertyChanged -= Item_PropertyChanged;
            RefreshCounts();
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TodoItem.Done) || e.PropertyName == nameof(TodoItem.Due)) RefreshCounts();
        }

        public void RefreshCounts()
        {
            int total = _items?.Count ?? 0;
            int left = _items?.Count(t => !t.Done) ?? 0;
            int done = total - left;
            int late = _items?.Count(t => t.DueState == "overdue") ?? 0;

            EmptyState.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearDoneBtn.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            SortBtn.Visibility = total > 1 ? Visibility.Visible : Visibility.Collapsed;

            if (total == 0) FooterText.Text = "";
            else if (left == 0) FooterText.Text = "همه انجام شده";
            else FooterText.Text = TimeHelper.Fa(left) + " مانده" + (late > 0 ? "، " + TimeHelper.Fa(late) + " عقب افتاده" : "");
            FooterText.Foreground = late > 0 ? (Brush)FindResource("FridayBrush") : (Brush)FindResource("SubTextBrush");
        }

        // ---------------- adding ----------------
        private void AddTodo()
        {
            if (_items == null) return;
            string text = (NewTodo.Text ?? "").Trim();
            if (text.Length == 0) return;
            var item = new TodoItem { Text = text, Created = DateTime.UtcNow, Due = _pendingDue };
            _items.Add(item);
            NewTodo.Text = "";
            _pendingDue = null;
            UpdatePending();
            if (SortBtn.IsChecked != true) TodoScroll.ScrollToEnd();
        }

        private void NewTodo_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                AddTodo();
                e.Handled = true;
            }
        }

        private void AddBtn_Click(object sender, RoutedEventArgs e)
        {
            AddTodo();
            NewTodo.Focus();
        }

        public void FocusInput() => NewTodo.Focus();

        // ---------------- due dates ----------------
        private void PendingDue_Click(object sender, RoutedEventArgs e)
        {
            string label = string.IsNullOrWhiteSpace(NewTodo.Text) ? "کار بعدی" : NewTodo.Text.Trim();
            DueRequested?.Invoke(_pendingDue, label, d => { _pendingDue = d; UpdatePending(); NewTodo.Focus(); });
        }

        private void ClearPending_Click(object sender, RoutedEventArgs e)
        {
            _pendingDue = null;
            UpdatePending();
        }

        private void UpdatePending()
        {
            PendingRow.Visibility = _pendingDue.HasValue ? Visibility.Visible : Visibility.Collapsed;
            PendingText.Text = _pendingDue.HasValue ? TimeHelper.DueLabel(_pendingDue.Value) : "";
            PendingDueIcon.Stroke = _pendingDue.HasValue ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("SubTextBrush");
        }

        private void SetDue_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TodoItem t)
                DueRequested?.Invoke(t.Due, t.Text, d => t.Due = d);
        }

        // ---------------- removing ----------------
        private void DeleteTodo_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TodoItem t) _items?.Remove(t);
        }

        private void ClearDone_Click(object sender, RoutedEventArgs e)
        {
            if (_items == null) return;
            foreach (var t in _items.Where(x => x.Done).ToList()) _items.Remove(t);
        }
    }
}
