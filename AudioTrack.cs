using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MakeMusic;

public sealed class AudioTrack(string name, string audioFilePath, TimeSpan duration) : INotifyPropertyChanged
{
    public const int TimelineBarCount = 32;
    public const double PixelsPerBar = 24;

    private double _volume = 1;
    private bool _isMuted;
    private bool _isSolo;
    private int _startBar = 1;
    private string? _audioFilePath = audioFilePath;

    public string Name { get; } = name;
    public TimeSpan Duration { get; } = duration;
    public string DurationLabel => Duration.ToString(@"m\:ss");
    public string AudioFileName => AudioFilePath is null ? "음원 경로 없음" : Path.GetFileName(AudioFilePath);
    public string AudioStatus => AudioFilePath is null ? "음원 경로 없음" : File.Exists(AudioFilePath) ? "오디오 연결됨" : "오디오 파일을 찾을 수 없음";
    public double TimelineLeft => (StartBar - 1) * PixelsPerBar;
    public double TimelineClipWidth => Math.Clamp(Duration.TotalSeconds * 12, PixelsPerBar, Math.Max(PixelsPerBar, TimelineBarCount * PixelsPerBar - TimelineLeft));

    public int StartBar
    {
        get => _startBar;
        set
        {
            var clampedValue = Math.Clamp(value, 1, TimelineBarCount);
            if (_startBar == clampedValue)
            {
                return;
            }

            _startBar = clampedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimelineLeft));
            OnPropertyChanged(nameof(TimelineClipWidth));
        }
    }

    public string? AudioFilePath
    {
        get => _audioFilePath;
        set
        {
            if (_audioFilePath == value)
            {
                return;
            }

            _audioFilePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioFileName));
            OnPropertyChanged(nameof(AudioStatus));
        }
    }

    public double Volume
    {
        get => _volume;
        set => SetField(ref _volume, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetField(ref _isMuted, value);
    }

    public bool IsSolo
    {
        get => _isSolo;
        set => SetField(ref _isSolo, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
