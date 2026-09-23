namespace AryanEbookLibrary.Reader.Pdf;

/// <summary>
/// The page colours besides plain paper, applied to the rendered PIXELS, so nothing is ever written to the book and
/// switching back renders the page exactly as it was. Night is Ayaan PDF's NightMode; sepia maps white to warm paper
/// and black to dark brown, channel by channel, so pictures stay pictures, only warmer.
/// </summary>
public static class PageColors
{
    public static readonly Windows.UI.Color SepiaPaper = Windows.UI.Color.FromArgb(0xFF, 0xF4, 0xEC, 0xD8);
    private static readonly Windows.UI.Color SepiaInk = Windows.UI.Color.FromArgb(0xFF, 0x5B, 0x46, 0x36);

    private static readonly byte[] SepiaB = Ramp(SepiaInk.B, SepiaPaper.B);
    private static readonly byte[] SepiaG = Ramp(SepiaInk.G, SepiaPaper.G);
    private static readonly byte[] SepiaR = Ramp(SepiaInk.R, SepiaPaper.R);

    /// <summary>Colours a BGRA buffer in place for a theme: 0 paper (unchanged), 1 sepia, 2 night.</summary>
    public static void Apply(byte[] bgra, int theme)
    {
        if (theme == 2)
        {
            NightMode.Apply(bgra);
            return;
        }
        if (theme != 1) return;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            bgra[i] = SepiaB[bgra[i]];
            bgra[i + 1] = SepiaG[bgra[i + 1]];
            bgra[i + 2] = SepiaR[bgra[i + 2]];
        }
    }

    private static byte[] Ramp(byte dark, byte light)
    {
        var ramp = new byte[256];
        for (int v = 0; v < 256; v++) ramp[v] = (byte)(dark + (light - dark) * v / 255);
        return ramp;
    }
}
