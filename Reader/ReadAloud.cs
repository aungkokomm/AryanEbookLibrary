using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// Read aloud with Windows' own voices, as Edge does, and nothing leaves the computer. The reader hands it a piece of the
/// book at a time (a paragraph, a page's text); it is spoken in a voice for that text's script and language, and
/// <see cref="Ended"/> asks for the next. A language Windows has no voice for (Burmese has none) is not spoken:
/// <see cref="SpeakAsync"/> says so.
/// </summary>
public sealed class ReadAloud : IDisposable
{
    private readonly MediaPlayer _player = new();
    private readonly SpeechSynthesizer _synth = new();
    private int _run;   // a newer piece, or Stop, makes an older one's end come to nothing
    private string _voiceId = "";

    /// <summary>The piece being spoken came to its end. Raised off the UI thread.</summary>
    public event Action? Ended;

    /// <summary>A word of the piece is being spoken: where it starts in the piece's text, and its length. Raised off the UI thread.</summary>
    public event Action<int, int>? Word;

    public ReadAloud()
    {
        // ARYAN_READ_ALOUD_MUTED plays without sound, at the same pace, so off-screen checks are not heard.
        _player.IsMuted = Environment.GetEnvironmentVariable("ARYAN_READ_ALOUD_MUTED") is { Length: > 0 };
        _synth.Options.IncludeWordBoundaryMetadata = true;
        _player.MediaEnded += (_, _) => Ended?.Invoke();
        _player.MediaFailed += (_, e) =>
        {
            Services.Log.Write($"read aloud: playback failed ({e.Error}): {e.ErrorMessage}");
            Ended?.Invoke();
        };
    }

    /// <summary>The installed voices' languages, for Settings: "English (United States), Hindi (India)".</summary>
    public static string VoiceLanguages() => string.Join(", ", SpeechSynthesizer.AllVoices
        .Select(v => new System.Globalization.CultureInfo(v.Language).DisplayName).Distinct());

    /// <summary>
    /// The voice for some text, by its script: Devanagari reads in a Hindi voice, Latin in an English one (or Windows'
    /// own choice). Null when the script has no voice here, as Burmese never has.
    /// </summary>
    public static VoiceInformation? VoiceFor(string text)
    {
        var voices = SpeechSynthesizer.AllVoices;
        VoiceInformation? Speaking(string tag) =>
            voices.FirstOrDefault(v => v.Language.StartsWith(tag, StringComparison.OrdinalIgnoreCase));
        return Script(text) is { } script ? Speaking(script) : Speaking("en") ?? SpeechSynthesizer.DefaultVoice;
    }

    /// <summary>The language a script is read in, when most of the letters are in one Latin is not: hi, my, or null.</summary>
    private static string? Script(string text)
    {
        int latin = 0, devanagari = 0, myanmar = 0;
        foreach (var c in text)
        {
            if (c is >= (char)0x0900 and <= (char)0x097F) devanagari++;
            else if (c is >= (char)0x1000 and <= (char)0x109F or >= (char)0xAA60 and <= (char)0xAA7F) myanmar++;
            else if (char.IsLetter(c) && c < (char)0x0250) latin++;
        }
        if (myanmar > latin && myanmar >= devanagari) return "my";
        if (devanagari > latin) return "hi";
        return null;
    }

    /// <summary>Speaks a piece at the given speed (1 is Windows' usual). False when Windows has no voice for it.</summary>
    public async Task<bool> SpeakAsync(string text, double rate)
    {
        var run = ++_run;
        if (VoiceFor(text) is not { } voice) return false;
        if (voice.Id != _voiceId)
        {
            _voiceId = voice.Id;
            Services.Log.Write($"read aloud: {voice.DisplayName} ({voice.Language})");
        }
        _synth.Voice = voice;
        _synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 3);
        var stream = await _synth.SynthesizeTextToStreamAsync(text);
        if (run != _run)
        {
            stream.Dispose();   // stopped, or another piece asked for, while this one was being made
            return true;
        }
        var item = new MediaPlaybackItem(MediaSource.CreateFromStream(stream, stream.ContentType));
        // Windows says when each word begins, so the reader can mark it and keep it in view.
        for (var i = 0; i < item.TimedMetadataTracks.Count; i++)
        {
            if (item.TimedMetadataTracks[i].Id != "SpeechWord") continue;
            item.TimedMetadataTracks[i].CueEntered += (_, e) =>
            {
                if (run == _run && e.Cue is SpeechCue { StartPositionInInput: { } start, EndPositionInInput: { } end })
                    Word?.Invoke(start, end - start + 1);
            };
            item.TimedMetadataTracks.SetPresentationMode((uint)i, TimedMetadataTrackPresentationMode.ApplicationPresented);
        }
        _player.Source = item;
        _player.Play();
        return true;
    }

    public void Stop()
    {
        _run++;
        _player.Pause();
        _player.Source = null;
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
        _synth.Dispose();
    }
}
