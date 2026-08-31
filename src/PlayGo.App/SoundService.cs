using System.IO;
using System.Runtime.InteropServices;

namespace PlayGo.App;

/// <summary>
/// Plays the small stone effects. These go through the Win32 wave API rather
/// than a media framework: the clips are a few kilobytes of PCM each and need
/// to fire with no perceptible latency while the board keeps rendering.
/// </summary>
internal static class SoundService
{
    private const uint SoundFileName = 0x00020000;
    private const uint SoundAsync = 0x0001;
    private const uint SoundNoDefault = 0x0002;

    /// <summary>Whether stone sounds play at all (the View menu toggles this).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>A stone meeting the board.</summary>
    public static void PlayStone() => Play("place.wav");

    /// <summary>Stones coming off the board.</summary>
    public static void PlayCapture() => Play("capture.wav");

    private static void Play(string fileName)
    {
        if (!Enabled) return;

        string path = Path.Combine(AppContext.BaseDirectory, "Sounds", fileName);
        if (!File.Exists(path)) return;

        // SoundAsync restarts the clip if one is still playing, which is what
        // you want when stones land in quick succession.
        PlaySound(path, IntPtr.Zero, SoundFileName | SoundAsync | SoundNoDefault);
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string path, IntPtr module, uint flags);
}
