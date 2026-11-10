using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MakeMusic;

public sealed class MusicalNote(int pitch, int startStep, int lengthSteps = 4, int velocity = 100) : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private int _pitch = Math.Clamp(pitch, 48, 83);
    private int _startStep = Math.Clamp(startStep, 0, AudioTrack.TimelineBarCount * 16 - 1);
    private int _lengthSteps = Math.Clamp(lengthSteps, 1, AudioTrack.TimelineBarCount * 16);
    private int _velocity = Math.Clamp(velocity, 1, 127);
    private string _articulation = "Normal";
    private string _stemDirection = "Up";
    private bool _isSlurredToNext;
    private Guid? _slurredToNoteId;
    private bool _isTiedToNext;
    private Guid? _tiedToNoteId;

    public int Pitch
    {
        get => _pitch;
        set => SetField(ref _pitch, Math.Clamp(value, 48, 83));
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

    public string Articulation
    {
        get => _articulation;
        set
        {
            var normalized = value is "Tenuto" or "Staccato" ? value : "Normal";
            if (_articulation == normalized)
            {
                return;
            }

            _articulation = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Articulation)));
        }
    }

    public string StemDirection
    {
        get => _stemDirection;
        set
        {
            var normalized = value == "Down" ? "Down" : "Up";
            if (_stemDirection == normalized)
            {
                return;
            }

            _stemDirection = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StemDirection)));
        }
    }

    public bool IsSlurredToNext
    {
        get => _isSlurredToNext;
        set
        {
            if (_isSlurredToNext == value)
            {
                return;
            }

            _isSlurredToNext = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSlurredToNext)));
        }
    }

    public Guid? SlurredToNoteId
    {
        get => _slurredToNoteId;
        set
        {
            if (_slurredToNoteId == value)
            {
                return;
            }

            _slurredToNoteId = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SlurredToNoteId)));
        }
    }

    public bool IsTiedToNext
    {
        get => _isTiedToNext;
        set
        {
            if (_isTiedToNext == value)
            {
                return;
            }

            _isTiedToNext = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTiedToNext)));
        }
    }

    public Guid? TiedToNoteId
    {
        get => _tiedToNoteId;
        set
        {
            if (_tiedToNoteId == value)
            {
                return;
            }

            _tiedToNoteId = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TiedToNoteId)));
        }
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
