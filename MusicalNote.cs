using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MakeMusic;

public sealed class MusicalNote(int pitch, int startStep, int lengthSteps = 4, int velocity = 100) : INotifyPropertyChanged
{
    private int _pitch = Math.Clamp(pitch, 60, 83);
    private int _startStep = Math.Clamp(startStep, 0, AudioTrack.TimelineBarCount * 16 - 1);
    private int _lengthSteps = Math.Clamp(lengthSteps, 1, AudioTrack.TimelineBarCount * 16);
    private int _velocity = Math.Clamp(velocity, 1, 127);

    public int Pitch
    {
        get => _pitch;
        set => SetField(ref _pitch, Math.Clamp(value, 60, 83));
    }

    public int StartStep
    {
        get => _startStep;
        set => SetField(ref _startStep, Math.Clamp(value, 0, AudioTrack.TimelineBarCount * 16 - 1));
    }

    public int LengthSteps
    {
        get => _lengthSteps;
        set => SetField(ref _lengthSteps, Math.Clamp(value, 1, AudioTrack.TimelineBarCount * 16));
    }

    public int Velocity
    {
        get => _velocity;
        set => SetField(ref _velocity, Math.Clamp(value, 1, 127));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref int field, int value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
