namespace AryanEbookLibrary.Reader.Pdf;

/// <summary>
/// One render the reader wants: a whole page <see cref="Width"/> pixels wide (Col &lt; 0), or one tile of the
/// page's pyramid at <see cref="Level"/>. <see cref="Theme"/> is the page colouring it was asked for, so a result
/// that arrives after the reader switched to Night is recognised as stale.
/// </summary>
public readonly record struct RenderJob(int Page, int Level, int Col, int Row, int Width, int Theme)
{
    public bool IsTile => Col >= 0;
}

/// <summary>
/// Renders off the UI thread, one job at a time, in the order of the reader's latest plan. A new plan replaces the
/// old one outright: whatever scrolled away is simply never rendered. The one render already under way finishes and
/// is handed back, and the reader drops it if nothing wants it any more. One thread, because PDFium serializes every
/// call anyway: a second thread would only queue behind the lock.
/// </summary>
public sealed class RenderQueue : IDisposable
{
    private readonly Func<RenderJob, (int Width, int Height, byte[] Bgra)?> _render;
    private readonly Action<RenderJob, (int Width, int Height, byte[] Bgra)> _done;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private List<RenderJob> _plan = new();
    private int _next;
    private volatile bool _stop;

    public RenderQueue(Func<RenderJob, (int, int, byte[])?> render, Action<RenderJob, (int, int, byte[])> done)
    {
        _render = render;
        _done = done;
        _thread = new Thread(Run) { IsBackground = true, Name = "Aryan PDF renders", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Replaces what is still to be rendered with <paramref name="plan"/>, most wanted first.</summary>
    public void SetPlan(List<RenderJob> plan)
    {
        lock (_gate)
        {
            _plan = plan;
            _next = 0;
        }
        _wake.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            RenderJob? job = null;
            lock (_gate)
            {
                if (_next < _plan.Count) job = _plan[_next++];
            }
            if (job is null)
            {
                _wake.WaitOne();
                continue;
            }

            try
            {
                if (_render(job.Value) is { } result && !_stop) _done(job.Value, result);
            }
            catch (Exception ex)
            {
                Services.Log.Write($"reader: render of page {job.Value.Page + 1} failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
