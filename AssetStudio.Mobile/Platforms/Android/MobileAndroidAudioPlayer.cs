#if ANDROID
using Android.Media;

namespace AssetStudioMobile;

internal sealed class MobileAndroidAudioPlayer : IDisposable
{
    private readonly MediaPlayer _player = new();
    private TaskCompletionSource<bool>? _prepareCompletion;
    private bool _isPrepared;
    private bool _isPaused;
    private bool _disposed;

    public MobileAndroidAudioPlayer()
    {
        _player.Prepared += OnPrepared;
        _player.Completion += OnCompletion;
        _player.Error += OnError;
    }

    public event EventHandler? PlaybackCompleted;

    public bool IsPrepared => _isPrepared;
    public bool IsPlaying => _isPrepared && _player.IsPlaying;
    public bool IsPaused => _isPrepared && _isPaused;
    public double CurrentTime => _isPrepared ? Math.Max(0, _player.CurrentPosition) / 1000d : 0;
    public double Duration => _isPrepared ? Math.Max(0, _player.Duration) / 1000d : 0;

    public bool Looping
    {
        get => _player.Looping;
        set => _player.Looping = value;
    }

    public double Volume
    {
        set
        {
            var normalized = (float)Math.Clamp(value, 0, 1);
            _player.SetVolume(normalized, normalized);
        }
    }

    public async Task PrepareAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_prepareCompletion != null || _isPrepared)
            throw new InvalidOperationException("The Android audio player has already been prepared.");

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prepareCompletion = completion;
        try
        {
            _player.SetDataSource(path);
            _player.PrepareAsync();
            await completion.Task;
        }
        catch
        {
            _prepareCompletion = null;
            throw;
        }
        _prepareCompletion = null;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isPrepared)
            throw new InvalidOperationException("The Android audio player is not prepared.");
        if (_player.CurrentPosition >= _player.Duration)
            _player.SeekTo(0);
        _player.Start();
        _isPaused = false;
    }

    public void Pause()
    {
        if (_isPrepared && _player.IsPlaying)
        {
            _player.Pause();
            _isPaused = true;
        }
    }

    public void Stop()
    {
        if (!_isPrepared)
            return;
        _player.Stop();
        _player.Reset();
        _isPrepared = false;
        _isPaused = false;
    }

    public void Seek(double seconds)
    {
        if (!_isPrepared)
            return;
        var milliseconds = Math.Clamp(seconds * 1000d, 0, _player.Duration);
        _player.SeekTo((int)milliseconds);
    }

    private void OnPrepared(object? sender, EventArgs e)
    {
        _isPrepared = true;
        _isPaused = false;
        _prepareCompletion?.TrySetResult(true);
    }

    private void OnCompletion(object? sender, EventArgs e) =>
        PlaybackCompleted?.Invoke(this, EventArgs.Empty);

    private void OnError(object? sender, MediaPlayer.ErrorEventArgs e)
    {
        _prepareCompletion?.TrySetException(
            new InvalidDataException($"Android could not prepare the audio clip (error {e.What}, detail {e.Extra})."));
        e.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _prepareCompletion?.TrySetException(new ObjectDisposedException(nameof(MobileAndroidAudioPlayer)));
        _prepareCompletion = null;
        _player.Prepared -= OnPrepared;
        _player.Completion -= OnCompletion;
        _player.Error -= OnError;
        _player.Release();
        _player.Dispose();
    }
}
#endif
