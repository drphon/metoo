using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoClock
{
    public abstract class Notifier : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class TodoItem : Notifier
    {
        private string _text = "";
        private bool _done;
        private DateTime? _due;

        public string Text
        {
            get => _text;
            set { if (_text != value) { _text = value; OnChanged(nameof(Text)); } }
        }

        public bool Done
        {
            get => _done;
            set { if (_done != value) { _done = value; OnChanged(nameof(Done)); OnChanged(nameof(DueState)); } }
        }

        /// <summary>Due day (Tehran calendar day, time part ignored). Null = no due date.</summary>
        public DateTime? Due
        {
            get => _due;
            set
            {
                var v = value?.Date;
                if (_due != v) { _due = v; RefreshDue(); }
            }
        }

        public DateTime Created { get; set; } = DateTime.UtcNow;

        // ---- UI helpers (not persisted) ----
        [JsonIgnore] public bool HasDue => _due.HasValue;
        [JsonIgnore] public DateTime SortDue => _due ?? DateTime.MaxValue;

        /// <summary>none | overdue | today | soon | later</summary>
        [JsonIgnore]
        public string DueState
        {
            get
            {
                if (!_due.HasValue) return "none";
                var today = TimeHelper.Now.Date;
                if (_done) return "later";
                if (_due.Value < today) return "overdue";
                if (_due.Value == today) return "today";
                if (_due.Value == today.AddDays(1)) return "soon";
                return "later";
            }
        }

        [JsonIgnore] public string DueText => _due.HasValue ? TimeHelper.DueLabel(_due.Value) : "";

        public void RefreshDue()
        {
            OnChanged(nameof(Due));
            OnChanged(nameof(HasDue));
            OnChanged(nameof(SortDue));
            OnChanged(nameof(DueState));
            OnChanged(nameof(DueText));
        }
    }

    public class Project : Notifier
    {
        private string _name = "";
        private bool _confirmDelete;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value ?? ""; OnChanged(nameof(Name)); OnChanged(nameof(Initial)); } }
        }

        public DateTime Created { get; set; } = DateTime.UtcNow;

        public ObservableCollection<TodoItem> Items { get; set; } = new ObservableCollection<TodoItem>();

        // ---- UI helpers ----
        [JsonIgnore] public int Total => Items.Count;
        [JsonIgnore] public int Remaining => Items.Count(t => !t.Done);
        [JsonIgnore] public int Overdue => Items.Count(t => t.DueState == "overdue");
        [JsonIgnore] public bool HasOverdue => Overdue > 0;
        [JsonIgnore] public bool HasRemaining => Remaining > 0;
        [JsonIgnore] public string RemainingText => Remaining > 99 ? "۹۹+" : TimeHelper.Fa(Remaining);
        [JsonIgnore] public string OverdueText => TimeHelper.Fa(Overdue) + " عقب افتاده";

        [JsonIgnore]
        public string Summary
        {
            get
            {
                if (Total == 0) return "هنوز کاری نداره";
                if (Remaining == 0) return "همه " + TimeHelper.Fa(Total) + " کار انجام شده";
                return TimeHelper.Fa(Remaining) + " کار مانده از " + TimeHelper.Fa(Total);
            }
        }

        [JsonIgnore]
        public string Initial
        {
            get
            {
                var s = (Name ?? "").Trim();
                return s.Length == 0 ? "?" : char.ToUpperInvariant(s[0]).ToString();
            }
        }

        [JsonIgnore]
        public int ColorIndex
        {
            get
            {
                int h = 0;
                foreach (char c in Id ?? "") h = (h * 31 + c) & 0x7FFFFFFF;
                return h % 6;
            }
        }

        [JsonIgnore]
        public bool ConfirmDelete
        {
            get => _confirmDelete;
            set { if (_confirmDelete != value) { _confirmDelete = value; OnChanged(nameof(ConfirmDelete)); } }
        }

        [JsonIgnore] public Action Changed;

        public void Hook()
        {
            Items.CollectionChanged -= Items_CollectionChanged;
            Items.CollectionChanged += Items_CollectionChanged;
            foreach (var t in Items) { t.PropertyChanged -= Item_PropertyChanged; t.PropertyChanged += Item_PropertyChanged; }
        }

        private void Items_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (TodoItem t in e.NewItems) { t.PropertyChanged -= Item_PropertyChanged; t.PropertyChanged += Item_PropertyChanged; }
            if (e.OldItems != null) foreach (TodoItem t in e.OldItems) t.PropertyChanged -= Item_PropertyChanged;
            RefreshCounts();
            Changed?.Invoke();
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TodoItem.Done) || e.PropertyName == nameof(TodoItem.Due) || e.PropertyName == nameof(TodoItem.Text))
            {
                RefreshCounts();
                Changed?.Invoke();
            }
        }

        public void RefreshCounts()
        {
            foreach (var n in new[] { nameof(Total), nameof(Remaining), nameof(Overdue), nameof(HasOverdue),
                                      nameof(HasRemaining), nameof(RemainingText), nameof(OverdueText), nameof(Summary) })
                OnChanged(n);
        }
    }

    public class AppData
    {
        public int Version { get; set; } = 2;
        public List<TodoItem> Daily { get; set; } = new List<TodoItem>();
        public List<Project> Projects { get; set; } = new List<Project>();
    }

    public class AppSettings
    {
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;
        public bool Expanded { get; set; }
        public double Opacity { get; set; } = 1.0;
        public string CalendarMode { get; set; } = "fa";
        public string Tab { get; set; } = "todo";          // todo | proj | cal
        public string OpenProject { get; set; }            // project id shown in the projects tab
        public bool SortByDate { get; set; }
    }

    public static class Storage
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoClock");
        private static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
        private static readonly string DataPath = Path.Combine(Dir, "data.json");
        private static readonly string LegacyTodosPath = Path.Combine(Dir, "todos.json");

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();
        public static void SaveSettings(AppSettings s) => Save(SettingsPath, s);

        /// <summary>
        /// Loads data.json (v2). On first run of v2, migrates the v1 todos.json into the daily list
        /// and keeps the original as todos.v1-backup.json. A corrupt data.json is preserved, never overwritten.
        /// </summary>
        public static AppData LoadData()
        {
            if (File.Exists(DataPath))
            {
                var d = Load<AppData>(DataPath);
                if (d != null) return Normalize(d);
                try { File.Copy(DataPath, Path.Combine(Dir, "data.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true); } catch { }
            }

            var data = new AppData();
            if (File.Exists(LegacyTodosPath))
            {
                var old = Load<List<TodoItem>>(LegacyTodosPath);
                if (old != null) data.Daily.AddRange(old);
                if (Save(DataPath, Normalize(data)))
                {
                    try { File.Copy(LegacyTodosPath, Path.Combine(Dir, "todos.v1-backup.json"), true); File.Delete(LegacyTodosPath); } catch { }
                }
            }
            return Normalize(data);
        }

        public static void SaveData(AppData d) => Save(DataPath, d);

        private static AppData Normalize(AppData d)
        {
            d.Daily = (d.Daily ?? new List<TodoItem>()).Where(t => t != null && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            d.Projects = (d.Projects ?? new List<Project>()).Where(p => p != null).ToList();
            foreach (var p in d.Projects)
            {
                if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                p.Items ??= new ObservableCollection<TodoItem>();
                foreach (var t in p.Items.Where(t => t == null || string.IsNullOrWhiteSpace(t.Text)).ToList()) p.Items.Remove(t);
            }
            return d;
        }

        private static T Load<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts);
            }
            catch { return null; }
        }

        private static bool Save<T>(string path, T data)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, Opts));
                File.Move(tmp, path, true);
                return true;
            }
            catch { return false; /* never crash on save */ }
        }
    }
}

namespace TodoClock
{
    /// <summary>A task shown in the calendar day panel together with the name of its list.</summary>
    public class DayEntry
    {
        public TodoItem Item { get; set; }
        public string ListName { get; set; }
    }
}
