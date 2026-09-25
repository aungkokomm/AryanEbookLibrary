using System.Globalization;
using AryanEbookLibrary.Helpers;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>A book in the reading log, with the line under or beside it ("Reading, 40%", "21 Sep").</summary>
public sealed record LogBook(Book Book, string Why);

/// <summary>One month's column in the chart: its name, how many books, and how tall the bar is.</summary>
public sealed record MonthBar(string Name, string CountText, double BarHeight, bool HasBooks, Windows.UI.Text.FontWeight Weight);

/// <summary>
/// The reading log: books finished per month of a year, a goal for the year, what to read next, and the
/// list of finished books. Worked out from each book's status and finish date (Services\ReadingLog.cs).
/// </summary>
public sealed partial class ReadingPage : Page
{
    private List<int> _years = new();
    private int _year = DateTime.Today.Year;
    private bool _filling;

    public ReadingPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private static IReadOnlyList<Book> Books => AppServices.Library.AllBooks;

    private void Refresh()
    {
        var today = DateTime.Today;
        var thisYear = ReadingLog.Year(Books, today.Year);
        var lastYear = ReadingLog.Year(Books, today.Year - 1);
        var reading = Books.Count(b => b.Status == ReadStatus.Reading);

        ThisYearNumber.Text = thisYear.Total.ToString("N0");
        ThisYearLabel.Text = $"Finished in {today.Year}";
        LastYearNumber.Text = lastYear.Total.ToString("N0");
        LastYearLabel.Text = $"Finished in {today.Year - 1}";
        AllTimeNumber.Text = ReadingLog.AllTime(Books).ToString("N0");
        ReadingNumber.Text = reading.ToString("N0");

        SummaryText.Text = thisYear.Total == 0 && reading == 0
            ? "Nothing finished this year yet. Mark a book Finished (right-click it) and it is counted here."
            : $"{Plural(thisYear.Total)} finished this year  ·  {Plural(reading)} being read";

        _years = ReadingLog.Years(Books, today.Year);
        if (!_years.Contains(_year)) _year = today.Year;
        _filling = true;
        YearBox.ItemsSource = _years.Select(y => $"{y}  ·  {Plural(ReadingLog.Year(Books, y).Total)}").ToList();
        YearBox.SelectedIndex = _years.IndexOf(_year);
        _filling = false;

        var next = ReadingLog.UpNext(Books);
        UpNextList.ItemsSource = next.Select(i => new LogBook(i.Book, i.Why)).ToList();
        UpNextEmpty.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowYear();
        ShowTime();

        static string Plural(int n) => n == 1 ? "1 book" : $"{n:N0} books";
    }

    private void OnYearChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || YearBox.SelectedIndex < 0) return;
        _year = _years[YearBox.SelectedIndex];
        ShowYear();
    }

    /// <summary>The goal, the month chart and the list for the chosen year.</summary>
    private void ShowYear()
    {
        var year = ReadingLog.Year(Books, _year);

        _filling = true;
        var goal = AppServices.Settings.ReadingGoals.GetValueOrDefault(_year);
        GoalBox.Value = goal > 0 ? goal : double.NaN;
        _filling = false;
        ShowGoal(year.Total, goal);

        DrawMonths(year);

        ListHeader.Text = $"Finished in {_year}";
        var culture = CultureInfo.CurrentCulture;
        FinishedList.ItemsSource = year.Finished
            .Select(b => new LogBook(b, ReadingLog.FinishedLocal(b)!.Value.ToString("d MMM", culture)))
            .ToList();
        ListEmpty.Text = _year == DateTime.Today.Year
            ? $"No books finished in {_year} yet."
            : $"No books finished in {_year}.";
        ListEmpty.Visibility = year.Total == 0 ? Visibility.Visible : Visibility.Collapsed;

        var undated = ReadingLog.Undated(Books);
        UndatedText.Text = undated == 1
            ? "1 finished book has no date, so it counts for all time but in no year. Its details can give it one."
            : $"{undated:N0} finished books have no date, so they count for all time but in no year. A book's details can give it one.";
        UndatedText.Visibility = undated > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- the goal ----

    private void OnGoalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_filling) return;
        var goal = double.IsNaN(sender.Value) ? 0 : (int)Math.Round(Math.Clamp(sender.Value, 0, 999));
        var goals = AppServices.Settings.ReadingGoals;
        if (goal > 0) goals[_year] = goal;
        else goals.Remove(_year);
        AppServices.Settings.Save();
        ShowGoal(ReadingLog.Year(Books, _year).Total, goal);
    }

    private void ShowGoal(int done, int goal)
    {
        GoalBar.Visibility = goal > 0 ? Visibility.Visible : Visibility.Collapsed;
        GoalBar.Maximum = Math.Max(goal, 1);
        GoalBar.Value = Math.Min(done, goal);

        var today = DateTime.Today;
        if (goal <= 0)
        {
            GoalText.Text = $"No goal for {_year}. Set one to see how the year is going.";
            return;
        }
        if (done >= goal)
        {
            GoalText.Text = $"Goal reached: {done:N0} of {goal:N0} books.";
            return;
        }
        if (_year != today.Year)
        {
            GoalText.Text = _year < today.Year
                ? $"{done:N0} of {goal:N0} books."
                : $"{Fn.Count(goal, "book")} to finish in {_year}.";
            return;
        }

        // Where the year should be by now, if the books were spread evenly over it.
        var expected = (int)Math.Floor(goal * (today.DayOfYear / (double)(DateTime.IsLeapYear(today.Year) ? 366 : 365)));
        var pace = done >= expected ? "on track"
            : expected - done == 1 ? "1 book behind" : $"{expected - done:N0} books behind";
        GoalText.Text = $"{done:N0} of {goal:N0} books, {goal - done:N0} to go ({pace}).";
    }

    // ---- time spent in the app's own reader ----

    private void ShowTime()
    {
        List<ReadingSession> sessions;
        try
        {
            sessions = AppServices.Sessions.All();
        }
        catch (Exception ex)
        {
            Log.Write("Reading log: sessions could not be read: " + ex.Message);
            sessions = new();
        }

        var none = sessions.Count == 0;
        TimeEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        TimeNumbers.Visibility = DayChart.Visibility = MostTimeText.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        StreakText.Text = "";
        if (none) return;

        var today = DateTime.Today;
        var byDay = ReadingTime.ByDay(sessions);
        TodayTime.Text = ReadingTime.Format(byDay.GetValueOrDefault(today));
        WeekTime.Text = ReadingTime.Format(ReadingTime.Between(byDay, ReadingTime.WeekStart(today), today));
        YearTime.Text = ReadingTime.Format(ReadingTime.Between(byDay, new DateTime(today.Year, 1, 1), today));
        YearTimeLabel.Text = $"In {today.Year}";
        var thisYear = sessions.Where(s => s.StartedUtc.ToLocalTime().Year == today.Year).ToList();
        PagesYear.Text = thisYear.Sum(s => s.Pages).ToString("N0");

        var streak = ReadingTime.Streak(byDay, today);
        StreakText.Text = streak switch
        {
            0 => "",
            1 => "1 day in a row",
            _ => $"{streak:N0} days in a row",
        };

        var culture = CultureInfo.CurrentCulture;
        var days = Enumerable.Range(0, 14).Select(i => today.AddDays(i - 13)).ToList();
        var most = Math.Max(60, days.Max(d => byDay.GetValueOrDefault(d)));
        const double tallest = 80;
        DayChart.ItemsSource = days.Select(d =>
        {
            var seconds = byDay.GetValueOrDefault(d);
            var name = culture.DateTimeFormat.GetShortestDayName(d.DayOfWeek);
            return new MonthBar(name, seconds >= 60 ? ReadingTime.Short(seconds) : "",
                seconds >= 60 ? Math.Max(4, tallest * seconds / most) : 0, seconds >= 60,
                d == today ? FontWeights.SemiBold : FontWeights.Normal);
        }).ToList();

        // The books this year's time went into, by what they are called now.
        var byBook = thisYear.GroupBy(s => s.BookKey).Select(g => (Key: g.Key, Seconds: g.Sum(s => s.Seconds)))
            .OrderByDescending(x => x.Seconds).Take(3).ToList();
        // The title the library shows (the user's own, if they gave one), else the catalogue's, which still knows
        // books whose files are missing.
        var titles = AppServices.Repo.GetTitlesByKey();
        foreach (var b in Books) titles[b.StateKey] = b.Title;
        MostTimeText.Text = byBook.Count == 0 ? ""
            : "Most time this year: " + string.Join(",  ", byBook.Select(x =>
                $"{(titles.TryGetValue(x.Key, out var t) && !string.IsNullOrWhiteSpace(t) ? t : "a book no longer in the library")} ({ReadingTime.Format(x.Seconds)})"));
    }

    // ---- the month chart ----

    private void DrawMonths(ReadingYear year)
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedMonthNames;
        var most = Math.Max(1, year.Months.Max());
        const double tallest = 120;
        var today = DateTime.Today;
        MonthChart.ItemsSource = Enumerable.Range(0, 12).Select(m =>
        {
            var n = year.Months[m];
            var now = year.Year == today.Year && m == today.Month - 1;
            return new MonthBar(names[m], n > 0 ? n.ToString("N0") : "", n > 0 ? Math.Max(6, tallest * n / most) : 0, n > 0,
                now ? FontWeights.SemiBold : FontWeights.Normal);
        }).ToList();
    }

    // ---- a book ----

    private void OnBookClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Book book) return;
        BookDetailsWindow.Show(book, Refresh);   // its status or finish date may have changed
    }
}
