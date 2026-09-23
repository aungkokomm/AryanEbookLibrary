namespace AryanEbookLibrary.Reader;

/// <summary>
/// Where the reader was before each jump (a link, the contents, a page number typed in, Home or End), for Back and
/// Forward: Alt+Left and Alt+Right, or the mouse's side buttons. Turning or scrolling pages is not a jump.
/// </summary>
internal sealed class JumpHistory<T> where T : struct
{
    private const int Limit = 50;
    private readonly List<T> _back = new();
    private readonly List<T> _forward = new();

    public void Jumped(T from)
    {
        _back.Add(from);
        if (_back.Count > Limit) _back.RemoveAt(0);
        _forward.Clear();
    }

    /// <summary>Where to go back to from <paramref name="here"/>, or null.</summary>
    public T? Back(T here) => Step(_back, _forward, here);

    public T? Forward(T here) => Step(_forward, _back, here);

    private static T? Step(List<T> from, List<T> to, T here)
    {
        if (from.Count == 0) return null;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(here);
        return target;
    }
}
