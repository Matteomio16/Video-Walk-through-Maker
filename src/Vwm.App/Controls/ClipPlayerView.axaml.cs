using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Vwm.App.Controls;

/// <summary>
/// Embedded video player for step previews, backed by libvlc. Fully local playback;
/// if the native libvlc libraries are missing, <see cref="TryPlay"/> returns false and
/// the caller falls back to the OS player.
/// </summary>
public partial class ClipPlayerView : UserControl
{
    private static LibVLC? _libVlc;
    private static bool _libVlcFailed;

    private MediaPlayer? _player;
    private bool _seekFromPlayer;

    public event EventHandler? Closed;

    public ClipPlayerView()
    {
        InitializeComponent();
    }

    private static LibVLC? GetLibVlc()
    {
        if (_libVlc is null && !_libVlcFailed)
        {
            try
            {
                LibVLCSharp.Shared.Core.Initialize(); // locates the bundled libvlc/win-x64 folder on Windows
                _libVlc = new LibVLC("--no-video-title-show");
            }
            catch
            {
                _libVlcFailed = true;
            }
        }
        return _libVlc;
    }

    public bool TryPlay(string mediaPath, string title)
    {
        var libVlc = GetLibVlc();
        if (libVlc is null)
            return false;

        try
        {
            if (_player is null)
            {
                _player = new MediaPlayer(libVlc);
                _player.TimeChanged += (_, e) => Dispatcher.UIThread.Post(() => OnPlayerTime(e.Time));
                _player.EndReached += (_, _) => Dispatcher.UIThread.Post(OnPlayerEnded);
                Video.MediaPlayer = _player;
            }

            TitleText.Text = title;
            using var media = new Media(libVlc, new Uri(Path.GetFullPath(mediaPath)));
            _player.Play(media);
            PlayPauseButton.Content = "⏸";
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        // Stop() must not be called from a libvlc event thread; all our callers are UI-side.
        _player?.Stop();
    }

    public void Shutdown()
    {
        var player = _player;
        _player = null;
        if (player is not null)
        {
            Video.MediaPlayer = null;
            player.Dispose();
        }
    }

    private void OnPlayerTime(long timeMs)
    {
        var length = _player?.Length ?? 0;
        _seekFromPlayer = true;
        SeekSlider.Value = length > 0 ? (double)timeMs / length : 0;
        _seekFromPlayer = false;
        TimeText.Text = $"{Format(timeMs)} / {Format(length)}";
    }

    private void OnPlayerEnded()
    {
        PlayPauseButton.Content = "▶";
        _seekFromPlayer = true;
        SeekSlider.Value = 1;
        _seekFromPlayer = false;
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_player is null)
            return;
        if (_player.IsPlaying)
        {
            _player.Pause();
            PlayPauseButton.Content = "▶";
        }
        else
        {
            if (_player.State == VLCState.Ended)
                _player.Position = 0;
            _player.Play();
            PlayPauseButton.Content = "⏸";
        }
    }

    private void OnSeek(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_seekFromPlayer || _player is null || _player.Length <= 0)
            return;
        if (_player.State == VLCState.Ended)
        {
            _player.Play();
            PlayPauseButton.Content = "⏸";
        }
        _player.Position = (float)e.NewValue;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Stop();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }
}
