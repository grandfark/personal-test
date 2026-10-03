namespace MakeMusic;

public sealed class ProjectDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "새 프로젝트";
    public int TempoBpm { get; set; } = 120;
    public double MasterReferencePitch { get; set; } = 440;
    public double MasterFineTuneCents { get; set; }
    public List<InstrumentTrackDocument> Tracks { get; set; } = [];
    public List<AudioTrackDocument> AudioTracks { get; set; } = [];
    public List<string> InstrumentRoles { get; set; } = [];
}

public sealed class InstrumentTrackDocument
{
    public string Name { get; set; } = "악기";
    public string Family { get; set; } = "악기";
    public string? InstrumentType { get; set; }
    public string? Role { get; set; }
    public double Volume { get; set; } = 0.8;
    public bool IsMuted { get; set; }
    public bool IsSolo { get; set; }
    public List<MusicalNoteDocument> Notes { get; set; } = [];
    public string? AudioAssetPath { get; set; }
}

public sealed class MusicalNoteDocument
{
    public int Pitch { get; set; } = 60;
    public int StartStep { get; set; }
    public int LengthSteps { get; set; } = 4;
    public int Velocity { get; set; } = 100;
}

public sealed class AudioTrackDocument
{
    public string Name { get; set; } = "오디오";
    public string AudioAssetPath { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public int StartBar { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool IsMuted { get; set; }
    public bool IsSolo { get; set; }
}