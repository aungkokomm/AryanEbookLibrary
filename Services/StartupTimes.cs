namespace AryanEbookLibrary.Services;

/// <summary>
/// Where a start's time goes before the books show: how long after the process began each step was reached, written
/// to the log with the books (LibraryViewModel.Initialize). Before Main is the .NET runtime starting; between Main and
/// the window, Aryan's own start.
/// </summary>
public static class StartupTimes
{
    private static readonly DateTime Began = System.Diagnostics.Process.GetCurrentProcess().StartTime;
    private static readonly List<(string Step, long Ms)> Marks = new();

    /// <summary>Milliseconds since the process began.</summary>
    public static long Now => (long)(DateTime.Now - Began).TotalMilliseconds;

    public static void Mark(string step) => Marks.Add((step, Now));

    /// <summary>"main 412, app 655, ..." in the order they came.</summary>
    public static string Summary() => string.Join(", ", Marks.Select(m => $"{m.Step} {m.Ms:N0}"));
}
