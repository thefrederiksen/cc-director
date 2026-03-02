using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CcDirector.Core.Utilities;
using CommunicationManager.Models;

namespace CommunicationManager.Views;

public partial class TimelineView : UserControl
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(ObservableCollection<ContentItem>),
            typeof(TimelineView),
            new PropertyMetadata(null, OnItemsChanged));

    public ObservableCollection<ContentItem> Items
    {
        get => (ObservableCollection<ContentItem>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public event EventHandler<ContentItem>? ItemSelected;

    public TimelineView()
    {
        InitializeComponent();
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineView view)
        {
            view.RebuildTimeline();

            if (e.NewValue is ObservableCollection<ContentItem> newCollection)
            {
                newCollection.CollectionChanged += (_, _) => view.RebuildTimeline();
            }
        }
    }

    private void RebuildTimeline()
    {
        FileLog.Write("[TimelineView] RebuildTimeline");

        if (Items == null || Items.Count == 0)
        {
            DayGroups.ItemsSource = null;
            return;
        }

        var today = DateTime.Today;
        var groups = new List<DayGroup>();

        // Group items by their effective date
        var itemsByDate = new SortedDictionary<DateTime, List<ContentItem>>();

        foreach (var item in Items)
        {
            DateTime effectiveDate;
            if (item.IsScheduled && item.ScheduledFor.HasValue)
            {
                effectiveDate = item.ScheduledFor.Value.Date;
            }
            else
            {
                // ASAP and Hold items go under today
                effectiveDate = today;
            }

            if (!itemsByDate.ContainsKey(effectiveDate))
            {
                itemsByDate[effectiveDate] = new List<ContentItem>();
            }
            itemsByDate[effectiveDate].Add(item);
        }

        foreach (var kvp in itemsByDate)
        {
            var date = kvp.Key;
            var items = kvp.Value;

            // Sort items within a day: scheduled items by time, then ASAP, then Hold
            items.Sort((a, b) =>
            {
                if (a.IsScheduled && b.IsScheduled)
                    return (a.ScheduledFor ?? DateTime.MaxValue).CompareTo(b.ScheduledFor ?? DateTime.MaxValue);
                if (a.IsScheduled) return -1;
                if (b.IsScheduled) return 1;
                if (a.IsAsap && b.IsHold) return -1;
                if (a.IsHold && b.IsAsap) return 1;
                return 0;
            });

            var dayLabel = date == today ? "Today"
                : date == today.AddDays(1) ? "Tomorrow"
                : date < today ? "Overdue"
                : date.ToString("dddd");

            groups.Add(new DayGroup
            {
                Date = date,
                DayLabel = dayLabel,
                DateDisplay = date.ToString("MMM d, yyyy"),
                IsToday = date == today,
                ItemCount = items.Count,
                Items = items.Select(i => new TimelineItem
                {
                    Item = i,
                    TimeDisplay = i.IsScheduled && i.ScheduledFor.HasValue
                        ? i.ScheduledFor.Value.ToString("h:mm tt")
                        : i.IsHold ? "HOLD" : "ASAP"
                }).ToList()
            });
        }

        DayGroups.ItemsSource = groups;
    }

    private void TimelineItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TimelineItem timelineItem
            && timelineItem.Item is ContentItem item)
        {
            FileLog.Write($"[TimelineView] Item clicked: {item.DisplayTitle}");
            ItemSelected?.Invoke(this, item);
        }
    }
}

public class DayGroup
{
    public DateTime Date { get; set; }
    public string DayLabel { get; set; } = string.Empty;
    public string DateDisplay { get; set; } = string.Empty;
    public bool IsToday { get; set; }
    public int ItemCount { get; set; }
    public List<TimelineItem> Items { get; set; } = new();
}

public class TimelineItem
{
    public ContentItem? Item { get; set; }
    public string TimeDisplay { get; set; } = string.Empty;
}
