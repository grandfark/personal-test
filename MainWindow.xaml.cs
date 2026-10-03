using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MakeMusic;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] BuiltInInstrumentRoles = ["주선율", "보조선율", "화음", "저음", "리듬"];
    private const int PianoRollLowestPitch = 60;
    private const int PianoRollHighestPitch = 83;
    private const int StepsPerBeat = 4;
    private const int StepsPerBar = 16;
    private const double PianoRollStepWidth = 12;
    private const double PianoRollPitchLabelWidth = 48;
    private const int AudioOutputSampleRate = 44100;
    private static readonly int[] NoteLengthOptions = [1, 2, 4, 8, 16];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string _projectName = "새 프로젝트";
    private InstrumentTrack? _selectedTrack;
    private MusicalNote? _selectedNote;
    private AudioTrack? _selectedAudioTrack;
    private int _tempoBpm = 120;
    private int _playheadBar = 1;
    private double _masterReferencePitch = 440;
    private double _masterFineTuneCents;
    private bool _isApplyingProject;
    private bool _isDirty;
    private string? _currentProjectPath;
    private string _audioStatus = "악보에 음표를 입력하거나 WAV 음원을 가져오세요";
    private WaveOutEvent? _audioOutput;
    private readonly List<AudioFileReader> _playbackReaders = [];
    private readonly Dictionary<AudioTrack, AudioFileReader> _readersByAudioTrack = [];
    private readonly Stopwatch _playbackStopwatch = new();
    private readonly DispatcherTimer _transportTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Border? _scorePlayheadIndicator;
    private AudioTrack? _draggingAudioTrack;
    private double _audioClipDragOffset;
    private Point _dockDragStartPoint;
    private readonly Dictionary<string, UIElement> _dockPanels = new(StringComparer.Ordinal);
    private bool _isUpdatingNoteControls;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _dockPanels["Project"] = (UIElement)ProjectPanelHost.Content;
        _dockPanels["Instrument"] = (UIElement)InstrumentPanelHost.Content;
        _dockPanels["Mixer"] = (UIElement)MixerPanelHost.Content;
        _dockPanels["Results"] = (UIElement)ResultsPanelHost.Content;
        RestoreDockLayout();
        _transportTimer.Tick += TransportTimer_Tick;
        InstrumentTracks.CollectionChanged += InstrumentTracks_CollectionChanged;
        AudioTracks.CollectionChanged += AudioTracks_CollectionChanged;

        InstrumentTracks.Add(new InstrumentTrack("피아노", "피아노"));
        SelectedTrack = InstrumentTracks[0];
        IsDirty = false;
    }

    public ObservableCollection<InstrumentTrack> InstrumentTracks { get; } = [];
    public ObservableCollection<AudioTrack> AudioTracks { get; } = [];
    public ObservableCollection<string> InstrumentRoles { get; } = new(BuiltInInstrumentRoles);

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (_projectName == value)
            {
                return;
            }

            _projectName = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public int TempoBpm
    {
        get => _tempoBpm;
        set
        {
            var clampedTempo = Math.Clamp(value, 40, 240);
            if (_tempoBpm == clampedTempo)
            {
                return;
            }

            _tempoBpm = clampedTempo;
            OnPropertyChanged();
            MarkDirty();
            if (_audioOutput is not null)
            {
                StopPlayback("템포 변경 · 다시 재생하세요");
            }
        }
    }

    public int PlayheadBar
    {
        get => _playheadBar;
        private set
        {
            if (_playheadBar == value)
            {
                return;
            }

            _playheadBar = value;
            OnPropertyChanged();
        }
    }

    public InstrumentTrack? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (_selectedTrack == value)
            {
                return;
            }

            _selectedTrack = value;
            SelectedNote = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedTrack));
            RenderScore();
        }
    }

    public bool HasSelectedTrack => SelectedTrack is not null;

    public MusicalNote? SelectedNote
    {
        get => _selectedNote;
        private set
        {
            if (_selectedNote == value)
            {
                return;
            }

            _selectedNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedNote));
            UpdateSelectedNoteControls();
            RenderScore();
        }
    }

    public bool HasSelectedNote => SelectedNote is not null;

    public AudioTrack? SelectedAudioTrack
    {
        get => _selectedAudioTrack;
        set
        {
            if (_selectedAudioTrack == value)
            {
                return;
            }

            _selectedAudioTrack = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedAudioTrack));
            OnPropertyChanged(nameof(CanPlayTimeline));
            if (_audioOutput is null)
            {
                AudioStatus = value?.AudioStatus ?? "오디오 트랙을 선택하세요";
            }
        }
    }

    public bool HasSelectedAudioTrack => SelectedAudioTrack is not null;

    public bool CanPlayTimeline
    {
        get
        {
            var hasSoloTrack = AudioTracks.Any(track => track.IsSolo) || InstrumentTracks.Any(track => track.IsSolo);
            var hasPlayableAudio = AudioTracks.Any(track => track.AudioFilePath is string path && File.Exists(path) &&
                !track.IsMuted && (!hasSoloTrack || track.IsSolo));
            var hasPlayableNotes = InstrumentTracks.Any(track => track.Notes.Count > 0 &&
                !track.IsMuted && (!hasSoloTrack || track.IsSolo));
            return hasPlayableAudio || hasPlayableNotes;
        }
    }

    public string AudioStatus
    {
        get => _audioStatus;
        private set
        {
            if (_audioStatus == value)
            {
                return;
            }

            _audioStatus = value;
            OnPropertyChanged();
        }
    }

    private void StopPlayback(string status)
    {
        _transportTimer.Stop();
        _playbackStopwatch.Reset();
        PlayheadBar = 1;
        ReleaseAudio(stopOutput: true);
        UpdateScorePlayhead(isVisible: false);
        AudioStatus = status;
    }

    private void ReleaseAudio(bool stopOutput)
    {
        var output = _audioOutput;
        _audioOutput = null;
        if (output is not null)
        {
            output.PlaybackStopped -= AudioOutput_PlaybackStopped;
            if (stopOutput)
            {
                output.Stop();
            }

            output.Dispose();
        }

        foreach (var reader in _playbackReaders)
        {
            reader.Dispose();
        }

        _playbackReaders.Clear();
        _readersByAudioTrack.Clear();
    }

    private void AudioOutput_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _audioOutput))
            {
                return;
            }

            _transportTimer.Stop();
            _playbackStopwatch.Stop();
            ReleaseAudio(stopOutput: false);
            AudioStatus = e.Exception is null ? "재생 완료" : $"재생 오류: {e.Exception.Message}";
        });
    }

    private void TransportTimer_Tick(object? sender, EventArgs e)
    {
        if (!_playbackStopwatch.IsRunning)
        {
            return;
        }

        var secondsPerBar = 240d / TempoBpm;
        PlayheadBar = Math.Clamp((int)(_playbackStopwatch.Elapsed.TotalSeconds / secondsPerBar) + 1, 1, AudioTrack.TimelineBarCount);
        UpdateScorePlayhead(isVisible: true);
        AudioStatus = $"재생 중 · 마디 {PlayheadBar}";
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value)
            {
                return;
            }

            _isDirty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SaveStatus));
            OnPropertyChanged(nameof(SaveStatusBrush));
        }
    }

    public string SaveStatus => IsDirty ? "저장 전" : CurrentProjectPath is null ? "새 프로젝트" : "저장됨";

    public Brush SaveStatusBrush => IsDirty ? Brushes.DarkGoldenrod : Brushes.SeaGreen;

    private string? CurrentProjectPath
    {
        get => _currentProjectPath;
        set
        {
            if (_currentProjectPath == value)
            {
                return;
            }

            _currentProjectPath = value;
            OnPropertyChanged(nameof(SaveStatus));
        }
    }

    public double MasterReferencePitch
    {
        get => _masterReferencePitch;
        set
        {
            if (_masterReferencePitch == value)
            {
                return;
            }

            _masterReferencePitch = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public double MasterFineTuneCents
    {
        get => _masterFineTuneCents;
        set
        {
            if (_masterFineTuneCents == value)
            {
                return;
            }

            _masterFineTuneCents = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    private void ResetInstrumentRoles(IEnumerable<string> customRoles)
    {
        InstrumentRoles.Clear();
        foreach (var role in BuiltInInstrumentRoles)
        {
            InstrumentRoles.Add(role);
        }

        foreach (var role in customRoles)
        {
            RegisterInstrumentRole(role);
        }
    }

    private void RegisterInstrumentRole(string role)
    {
        var normalizedRole = role.Trim();
        if (normalizedRole.Length > 0 && !InstrumentRoles.Contains(normalizedRole, StringComparer.OrdinalIgnoreCase))
        {
            InstrumentRoles.Add(normalizedRole);
        }
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSaveOrDiscard())
        {
            return;
        }

        _isApplyingProject = true;
        ProjectName = "새 프로젝트";
        TempoBpm = 120;
        MasterReferencePitch = 440;
        MasterFineTuneCents = 0;
        ResetInstrumentRoles([]);
        SelectedTrack = null;
        SelectedAudioTrack = null;
        InstrumentTracks.Clear();
        AudioTracks.Clear();
        InstrumentTracks.Add(new InstrumentTrack("피아노", "피아노"));
        SelectedTrack = InstrumentTracks[0];
        _isApplyingProject = false;
        CurrentProjectPath = null;
        IsDirty = true;
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        SaveProject();
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "프로젝트 열기",
            Filter = "Make Music 프로젝트 (*.mmproj)|*.mmproj",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ProjectDocument document;
        try
        {
            if (!string.Equals(Path.GetExtension(dialog.FileName), ".mmproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("프로젝트 파일은 .mmproj 형식만 열 수 있습니다.");
            }

            var json = File.ReadAllText(dialog.FileName);
            document = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("프로젝트 파일에 내용이 없습니다.");
            ValidateProject(document, dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            MessageBox.Show(this, $"프로젝트 파일을 열 수 없습니다.\n{exception.Message}", "프로젝트 열기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!ConfirmSaveOrDiscard())
        {
            return;
        }

        ApplyProject(document, dialog.FileName);
        CurrentProjectPath = dialog.FileName;
        IsDirty = false;
    }

    private bool SaveProject()
    {
        var path = CurrentProjectPath;
        if (path is null)
        {
            var fileName = new string(ProjectName
                .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
                .ToArray());

            var dialog = new SaveFileDialog
            {
                Title = "프로젝트 저장",
                Filter = "Make Music 프로젝트 (*.mmproj)|*.mmproj",
                DefaultExt = ".mmproj",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(fileName) ? "프로젝트.mmproj" : $"{fileName}.mmproj",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            var document = new ProjectDocument
            {
                Name = ProjectName,
                TempoBpm = TempoBpm,
                MasterReferencePitch = MasterReferencePitch,
                MasterFineTuneCents = MasterFineTuneCents,
                InstrumentRoles = InstrumentRoles.ToList(),
                Tracks = InstrumentTracks.Select(track => new InstrumentTrackDocument
                {
                    Name = track.Name,
                    Family = track.InstrumentType,
                    InstrumentType = track.InstrumentType,
                    Role = track.Role,
                    Volume = track.Volume,
                    IsMuted = track.IsMuted,
                    IsSolo = track.IsSolo,
                    Notes = track.Notes.Select(note => new MusicalNoteDocument
                    {
                        Pitch = note.Pitch,
                        StartStep = note.StartStep,
                        LengthSteps = note.LengthSteps,
                        Velocity = note.Velocity
                    }).ToList()
                }).ToList(),
                AudioTracks = AudioTracks.Select(track => new AudioTrackDocument
                {
                    Name = track.Name,
                    AudioAssetPath = PrepareAudioAsset(track, path),
                    DurationSeconds = track.Duration.TotalSeconds,
                    StartBar = track.StartBar,
                    Volume = track.Volume,
                    IsMuted = track.IsMuted,
                    IsSolo = track.IsSolo
                }).ToList()
            };

            var json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(path, json);
            CurrentProjectPath = path;
            IsDirty = false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            MessageBox.Show(this, $"프로젝트를 저장할 수 없습니다.\n{exception.Message}", "프로젝트 저장 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ApplyProject(ProjectDocument document, string projectFilePath)
    {
        _isApplyingProject = true;
        try
        {
            ProjectName = document.Name;
            TempoBpm = document.TempoBpm;
            MasterReferencePitch = document.MasterReferencePitch;
            MasterFineTuneCents = document.MasterFineTuneCents;
            ResetInstrumentRoles(document.InstrumentRoles ?? []);
            SelectedTrack = null;
            SelectedAudioTrack = null;
            InstrumentTracks.Clear();
            AudioTracks.Clear();

            foreach (var trackDocument in document.Tracks)
            {
                var instrumentTrack = new InstrumentTrack(trackDocument.Name, RestoreInstrumentType(trackDocument), trackDocument.Role ?? string.Empty)
                {
                    Volume = trackDocument.Volume,
                    IsMuted = trackDocument.IsMuted,
                    IsSolo = trackDocument.IsSolo
                };

                foreach (var noteDocument in trackDocument.Notes ?? [])
                {
                    instrumentTrack.Notes.Add(new MusicalNote(noteDocument.Pitch, noteDocument.StartStep, noteDocument.LengthSteps, noteDocument.Velocity));
                }

                InstrumentTracks.Add(instrumentTrack);

                if (trackDocument.AudioAssetPath is not null)
                {
                    var legacyAudioPath = ResolveProjectAudioPath(projectFilePath, trackDocument.AudioAssetPath);
                    AudioTracks.Add(new AudioTrack(trackDocument.Name, legacyAudioPath, GetAudioDuration(legacyAudioPath)));
                }
            }

            foreach (var audioTrackDocument in document.AudioTracks)
            {
                var audioPath = ResolveProjectAudioPath(projectFilePath, audioTrackDocument.AudioAssetPath);
                AudioTracks.Add(new AudioTrack(audioTrackDocument.Name, audioPath, TimeSpan.FromSeconds(audioTrackDocument.DurationSeconds))
                {
                    StartBar = audioTrackDocument.StartBar,
                    Volume = audioTrackDocument.Volume,
                    IsMuted = audioTrackDocument.IsMuted,
                    IsSolo = audioTrackDocument.IsSolo
                });
            }

            SelectedTrack = InstrumentTracks.FirstOrDefault();
            SelectedAudioTrack = AudioTracks.FirstOrDefault();
                UpdateSelectedNoteControls();
        }
        finally
        {
            _isApplyingProject = false;
        }
    }

    private static string RestoreInstrumentType(InstrumentTrackDocument trackDocument)
    {
        if (!string.IsNullOrWhiteSpace(trackDocument.InstrumentType))
        {
            return trackDocument.InstrumentType;
        }

        string[] knownTypes = ["피아노", "기타", "베이스", "바이올린", "첼로", "플루트", "신스", "드럼"];
        return knownTypes.FirstOrDefault(type => trackDocument.Name.StartsWith(type, StringComparison.OrdinalIgnoreCase))
            ?? trackDocument.Name;
    }

    private static void ValidateProject(ProjectDocument document, string projectFilePath)
    {
        if (document.FormatVersion != ProjectDocument.CurrentFormatVersion)
        {
            throw new InvalidDataException($"지원하지 않는 프로젝트 형식 버전입니다: {document.FormatVersion}");
        }

        if (string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > 200)
        {
            throw new InvalidDataException("프로젝트 이름이 올바르지 않습니다.");
        }

        if (document.TempoBpm is < 40 or > 240)
        {
            throw new InvalidDataException("템포는 40~240 BPM 범위여야 합니다.");
        }

        if (!double.IsFinite(document.MasterReferencePitch) || document.MasterReferencePitch <= 0 || document.MasterReferencePitch > 20000)
        {
            throw new InvalidDataException("공통 기준음 값이 올바르지 않습니다.");
        }

        if (!double.IsFinite(document.MasterFineTuneCents) || document.MasterFineTuneCents is < -100 or > 100)
        {
            throw new InvalidDataException("전체 미세 조율 값이 올바르지 않습니다.");
        }

        if (document.Tracks is null || document.Tracks.Count > 128)
        {
            throw new InvalidDataException("악기 트랙 목록이 올바르지 않습니다.");
        }

        if (document.InstrumentRoles is null || document.InstrumentRoles.Count > 128 ||
            document.InstrumentRoles.Any(role => string.IsNullOrWhiteSpace(role) || role.Length > 80))
        {
            throw new InvalidDataException("악기 역할 목록이 올바르지 않습니다.");
        }

        if (document.AudioTracks is null || document.AudioTracks.Count > 128)
        {
            throw new InvalidDataException("오디오 트랙 목록이 올바르지 않습니다.");
        }

        foreach (var track in document.Tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.Name) || track.Name.Length > 100 ||
                (string.IsNullOrWhiteSpace(track.InstrumentType) && string.IsNullOrWhiteSpace(track.Family)) ||
                track.Role is { Length: > 80 })
            {
                throw new InvalidDataException("악기 트랙 정보가 올바르지 않습니다.");
            }

            if (!double.IsFinite(track.Volume) || track.Volume is < 0 or > 1)
            {
                throw new InvalidDataException($"'{track.Name}' 악기의 볼륨 값이 올바르지 않습니다.");
            }

            if (track.Notes is null || track.Notes.Count > 16384)
            {
                throw new InvalidDataException($"'{track.Name}' 악기의 음표 목록이 올바르지 않습니다.");
            }

            foreach (var note in track.Notes)
            {
                if (note is null || note.Pitch is < 60 or > 83 || note.StartStep is < 0 or >= AudioTrack.TimelineBarCount * 16 ||
                    note.LengthSteps is < 1 or > AudioTrack.TimelineBarCount * 16 || note.StartStep + note.LengthSteps > AudioTrack.TimelineBarCount * 16 ||
                    note.Velocity is < 1 or > 127)
                {
                    throw new InvalidDataException($"'{track.Name}' 악기에 잘못된 음표가 있습니다.");
                }
            }

            if (track.AudioAssetPath is not null)
            {
                var normalizedPath = track.AudioAssetPath.Replace('\\', '/');
                if (Path.IsPathRooted(track.AudioAssetPath) ||
                    !string.Equals(Path.GetExtension(track.AudioAssetPath), ".wav", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
                {
                    throw new InvalidDataException($"'{track.Name}' 악기의 WAV 경로가 올바르지 않습니다.");
                }

                var legacyAudioPath = ResolveProjectAudioPath(projectFilePath, track.AudioAssetPath);
                if (File.Exists(legacyAudioPath))
                {
                    ValidateWaveFile(legacyAudioPath);
                }
            }
        }

        foreach (var audioTrack in document.AudioTracks)
        {
            if (audioTrack is null || string.IsNullOrWhiteSpace(audioTrack.Name) || audioTrack.Name.Length > 200)
            {
                throw new InvalidDataException("오디오 트랙 정보가 올바르지 않습니다.");
            }

            if (!double.IsFinite(audioTrack.DurationSeconds) || audioTrack.DurationSeconds < 0 || audioTrack.DurationSeconds > TimeSpan.MaxValue.TotalSeconds ||
                audioTrack.StartBar is < 1 or > AudioTrack.TimelineBarCount ||
                !double.IsFinite(audioTrack.Volume) || audioTrack.Volume is < 0 or > 1)
            {
                throw new InvalidDataException($"'{audioTrack.Name}' 오디오 트랙 설정이 올바르지 않습니다.");
            }

            if (string.IsNullOrWhiteSpace(audioTrack.AudioAssetPath))
            {
                throw new InvalidDataException($"'{audioTrack.Name}' 오디오 파일 경로가 비어 있습니다.");
            }

            var normalizedPath = audioTrack.AudioAssetPath.Replace('\\', '/');
            if (Path.IsPathRooted(audioTrack.AudioAssetPath) ||
                !string.Equals(Path.GetExtension(audioTrack.AudioAssetPath), ".wav", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidDataException($"'{audioTrack.Name}' 오디오 파일 경로가 올바르지 않습니다.");
            }

            var audioPath = ResolveProjectAudioPath(projectFilePath, audioTrack.AudioAssetPath);
            if (File.Exists(audioPath))
            {
                ValidateWaveFile(audioPath);
            }
        }
    }

    private bool ConfirmSaveOrDiscard()
    {
        if (!IsDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "저장되지 않은 변경 사항이 있습니다. 저장하시겠습니까?",
            "변경 사항 확인",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => SaveProject(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = !ConfirmSaveOrDiscard();
        if (!e.Cancel)
        {
            StopPlayback("정지");
        }
    }

    private void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "프로젝트 오디오 가져오기",
            Filter = "WAV 오디오 (*.wav)|*.wav",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ValidateWaveFile(dialog.FileName);
            using var reader = new WaveFileReader(dialog.FileName);
            var baseName = Path.GetFileNameWithoutExtension(dialog.FileName);
            var duplicateCount = AudioTracks.Count(track => track.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
            var trackName = duplicateCount == 0 ? baseName : $"{baseName} {duplicateCount + 1}";
            var audioTrack = new AudioTrack(trackName, dialog.FileName, reader.TotalTime);
            AudioTracks.Add(audioTrack);
            SelectedAudioTrack = audioTrack;
            AudioStatus = $"가져옴: {audioTrack.AudioFileName}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FormatException or ArgumentException)
        {
            MessageBox.Show(this, $"이 WAV 파일은 가져올 수 없습니다.\n{exception.Message}", "오디오 가져오기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlayTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPlayTimeline)
        {
            AudioStatus = "재생할 음표가 없습니다 · 악보 편집에서 오선을 클릭해 음표를 추가하세요";
            return;
        }

        StopPlayback("재생 준비");
        try
        {
            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(AudioOutputSampleRate, 2));
            var hasSoloTrack = AudioTracks.Any(track => track.IsSolo) || InstrumentTracks.Any(track => track.IsSolo);
            var scheduledTrackCount = 0;

            foreach (var track in AudioTracks)
            {
                if (track.IsMuted || (hasSoloTrack && !track.IsSolo))
                {
                    continue;
                }

                if (track.AudioFilePath is not string audioFilePath || !File.Exists(audioFilePath))
                {
                    throw new FileNotFoundException($"'{track.Name}' 오디오 파일을 찾을 수 없습니다.", track.AudioFilePath);
                }

                ValidateWaveFile(audioFilePath);
                var reader = new AudioFileReader(audioFilePath)
                {
                    Volume = (float)track.Volume
                };
                _playbackReaders.Add(reader);

                ISampleProvider sampleProvider = reader;
                if (sampleProvider.WaveFormat.SampleRate != AudioOutputSampleRate)
                {
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, AudioOutputSampleRate);
                }

                if (sampleProvider.WaveFormat.Channels == 1)
                {
                    sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
                }

                if (sampleProvider.WaveFormat.Channels != 2)
                {
                    throw new InvalidDataException($"'{track.Name}' 오디오는 모노 또는 스테레오만 재생할 수 있습니다.");
                }

                var delay = TimeSpan.FromSeconds((track.StartBar - 1) * 240d / TempoBpm);
                var offsetProvider = new OffsetSampleProvider(sampleProvider)
                {
                    DelayBy = delay
                };
                mixer.AddMixerInput(offsetProvider);
                _readersByAudioTrack[track] = reader;
                scheduledTrackCount++;
            }

            var secondsPerStep = 60d / TempoBpm / StepsPerBeat;
            foreach (var track in InstrumentTracks)
            {
                if (track.IsMuted || (hasSoloTrack && !track.IsSolo))
                {
                    continue;
                }

                foreach (var note in track.Notes)
                {
                    var duration = TimeSpan.FromSeconds(note.LengthSteps * secondsPerStep);
                    var delay = TimeSpan.FromSeconds(note.StartStep * secondsPerStep);
                    var semitonesFromA4 = note.Pitch - 69 + MasterFineTuneCents / 100d;
                    var frequency = MasterReferencePitch * Math.Pow(2, semitonesFromA4 / 12d);
                    var tone = new ToneNoteSampleProvider(
                        AudioOutputSampleRate,
                        frequency,
                        (float)(track.Volume * note.Velocity / 127d * 0.28d),
                        duration);
                    mixer.AddMixerInput(new OffsetSampleProvider(tone) { DelayBy = delay });
                    scheduledTrackCount++;
                }
            }

            if (scheduledTrackCount == 0)
            {
                ReleaseAudio(stopOutput: true);
                AudioStatus = "재생할 악보 음표나 오디오 트랙이 없습니다";
                return;
            }

            mixer.ReadFully = false;
            _audioOutput = new WaveOutEvent();
            _audioOutput.PlaybackStopped += AudioOutput_PlaybackStopped;
            _audioOutput.Init(mixer);
            PlayheadBar = 1;
            _playbackStopwatch.Restart();
            _transportTimer.Start();
            _audioOutput.Play();
            AudioStatus = $"재생 중 · {TempoBpm} BPM · 마디 1";
        }
        catch (Exception exception)
        {
            ReleaseAudio(stopOutput: true);
            _transportTimer.Stop();
            _playbackStopwatch.Reset();
            AudioStatus = "타임라인 재생 실패";
            MessageBox.Show(this, $"타임라인 재생에 실패했습니다.\n{exception.Message}", "오디오 재생 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopAudio_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback("정지");
    }

    private static TimeSpan GetAudioDuration(string path)
    {
        if (!File.Exists(path))
        {
            return TimeSpan.Zero;
        }

        using var reader = new WaveFileReader(path);
        return reader.TotalTime;
    }

    private static void ValidateWaveFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("WAV 파일만 불러올 수 있습니다.");
        }

        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : reader.WaveFormat;
        var supportedPcm = format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24;
        var supportedFloat = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;

        if (reader.Length == 0 || format.Channels is < 1 or > 2 || (!supportedPcm && !supportedFloat))
        {
            throw new InvalidDataException("PCM 16/24-bit 또는 IEEE float 32-bit 모노/스테레오 WAV만 지원합니다.");
        }
    }

    private static string PrepareAudioAsset(AudioTrack track, string projectFilePath)
    {
        if (track.AudioFilePath is null)
        {
            throw new InvalidDataException($"'{track.Name}' 오디오 파일 경로가 없습니다.");
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
        var audioAssetDirectory = GetProjectAudioDirectory(projectFilePath);
        if (!File.Exists(track.AudioFilePath))
        {
            if (IsPathWithinDirectory(track.AudioFilePath, audioAssetDirectory))
            {
                return Path.GetRelativePath(projectDirectory, track.AudioFilePath).Replace('\\', '/');
            }

            throw new FileNotFoundException($"'{track.AudioFileName}' 음원 파일을 찾을 수 없습니다.", track.AudioFilePath);
        }

        var copiedPath = CopyAudioToProjectAssets(track.AudioFilePath, projectFilePath);
        track.AudioFilePath = copiedPath;
        return Path.GetRelativePath(projectDirectory, copiedPath).Replace('\\', '/');
    }

    private static string CopyAudioToProjectAssets(string sourcePath, string projectFilePath)
    {
        ValidateWaveFile(sourcePath);
        var audioAssetDirectory = GetProjectAudioDirectory(projectFilePath);
        Directory.CreateDirectory(audioAssetDirectory);

        using var source = File.OpenRead(sourcePath);
        var hash = Convert.ToHexString(SHA256.HashData(source));
        var destinationPath = Path.Combine(audioAssetDirectory, $"{hash}.wav");
        if (!File.Exists(destinationPath))
        {
            File.Copy(sourcePath, destinationPath);
        }

        return destinationPath;
    }

    private static string GetProjectAudioDirectory(string projectFilePath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
        var projectAssetFolder = $"{Path.GetFileNameWithoutExtension(projectFilePath)}.Assets";
        return Path.Combine(projectDirectory, projectAssetFolder, "Audio");
    }

    private static bool IsPathWithinDirectory(string filePath, string directoryPath)
    {
        var directoryPrefix = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(filePath).StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveProjectAudioPath(string projectFilePath, string relativeAudioPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
        var audioPath = Path.GetFullPath(Path.Combine(projectDirectory, relativeAudioPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithinDirectory(audioPath, projectDirectory))
        {
            throw new InvalidDataException("프로젝트 음원 경로가 프로젝트 폴더를 벗어납니다.");
        }

        return audioPath;
    }

    private void AddInstrument_Click(object sender, RoutedEventArgs e)
    {
        var selectedItem = InstrumentTypePicker.SelectedItem as System.Windows.Controls.ComboBoxItem;
        var instrumentName = selectedItem?.Content?.ToString() ?? "악기";

        var sameTypeCount = InstrumentTracks.Count(track => string.Equals(track.InstrumentType, instrumentName, StringComparison.Ordinal));
        var trackName = sameTypeCount == 0 ? instrumentName : $"{instrumentName} {sameTypeCount + 1}";
        var track = new InstrumentTrack(trackName, instrumentName);
        InstrumentTracks.Add(track);
        SelectedTrack = track;
    }

    private void InstrumentRolePicker_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox rolePicker || SelectedTrack is null)
        {
            return;
        }

        var role = rolePicker.Text.Trim();
        if (role.Length > 80)
        {
            role = role[..80];
            rolePicker.Text = role;
        }

        SelectedTrack.Role = role;
        RegisterInstrumentRole(role);
    }

    private void InstrumentName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        var name = SelectedTrack.Name.Trim();
        SelectedTrack.Name = string.IsNullOrWhiteSpace(name) ? SelectedTrack.InstrumentType : name;
    }

    private void RemoveInstrument_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is not null)
        {
            RemoveInstrument(SelectedTrack);
        }
    }

    private void RemoveInstrumentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstrumentTrack track })
        {
            RemoveInstrument(track);
        }
    }

    private void RemoveInstrument(InstrumentTrack track)
    {
        var selectedIndex = InstrumentTracks.IndexOf(track);
        if (selectedIndex < 0)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedTrack, track);
        InstrumentTracks.RemoveAt(selectedIndex);
        if (wasSelected)
        {
            SelectedTrack = InstrumentTracks.Count == 0
                ? null
                : InstrumentTracks[Math.Min(selectedIndex, InstrumentTracks.Count - 1)];
        }
    }

    private void RemoveSelectedAudioTrack_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAudioTrack is null)
        {
            return;
        }

        var selectedIndex = AudioTracks.IndexOf(SelectedAudioTrack);
        SelectedAudioTrack = null;
        AudioTracks.RemoveAt(selectedIndex);
        if (AudioTracks.Count > 0)
        {
            SelectedAudioTrack = AudioTracks[Math.Min(selectedIndex, AudioTracks.Count - 1)];
        }
    }

    private void ScoreCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        var position = e.GetPosition(ScoreCanvas);
        const double scoreLeft = 52;
        const double staffBottom = 220;
        const double staffStepHeight = 12;
        if (position.X < scoreLeft || position.Y < 50 || position.Y > 270)
        {
            return;
        }

        var totalSteps = AudioTrack.TimelineBarCount * StepsPerBar;
        var startStep = (int)((position.X - scoreLeft) / PianoRollStepWidth);
        var diatonicStep = (int)Math.Round((staffBottom - position.Y) / staffStepHeight);
        var pitch = DiatonicStepToMidi(diatonicStep) + SelectedAccidentalOffset;
        if (startStep < 0 || startStep >= totalSteps || pitch is < PianoRollLowestPitch or > PianoRollHighestPitch)
        {
            return;
        }

        var length = Math.Min(SelectedNoteLengthSteps, totalSteps - startStep);
        var note = new MusicalNote(pitch, startStep, length, (int)Math.Round(NoteVelocitySlider.Value));
        SelectedTrack.Notes.Add(note);
        SelectedNote = note;
        e.Handled = true;
    }

    private void PianoRollNote_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MusicalNote note })
        {
            SelectedNote = note;
            e.Handled = true;
        }
    }

    private void NoteLengthPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingNoteControls || SelectedNote is null || SelectedTrack is null)
        {
            return;
        }

        var length = SelectedNoteLengthSteps;
        SelectedNote.LengthSteps = Math.Min(length, AudioTrack.TimelineBarCount * StepsPerBar - SelectedNote.StartStep);
    }

    private void NoteVelocitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NoteVelocityValue is not null)
        {
            NoteVelocityValue.Text = ((int)Math.Round(e.NewValue)).ToString();
        }

        if (!_isUpdatingNoteControls && SelectedNote is not null)
        {
            SelectedNote.Velocity = (int)Math.Round(e.NewValue);
        }
    }

    private void DeleteSelectedNote_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null || SelectedNote is null)
        {
            return;
        }

        SelectedTrack.Notes.Remove(SelectedNote);
        SelectedNote = null;
    }

    private void DockPanelHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dockDragStartPoint = e.GetPosition(this);
    }

    private void DockPanelHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { Tag: string panelId } header || !_dockPanels.TryGetValue(panelId, out var panel))
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        if (Math.Abs(currentPosition.X - _dockDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dockDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragData = new DataObject("MakeMusic.DockPanel", panelId);
        DragDrop.DoDragDrop(header, dragData, DragDropEffects.Move);
    }

    private void DockPanelHost_PreviewDragOver(object sender, DragEventArgs e)
    {
        var panelId = e.Data.GetData("MakeMusic.DockPanel") as string;
        var targetHost = sender as ContentControl;
        var sourceHost = panelId is null ? null : FindDockHost(panelId);
        e.Effects = targetHost is not null && sourceHost is not null && !ReferenceEquals(targetHost, sourceHost)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void DockPanelHost_PreviewDrop(object sender, DragEventArgs e)
    {
        if (sender is not ContentControl targetHost || e.Data.GetData("MakeMusic.DockPanel") is not string panelId ||
            !_dockPanels.TryGetValue(panelId, out var panel))
        {
            return;
        }

        var sourceHost = FindDockHost(panelId);
        if (sourceHost is null || ReferenceEquals(sourceHost, targetHost))
        {
            e.Handled = true;
            return;
        }

        RemovePanelFromHost(sourceHost, panel);
        AddPanelToHost(targetHost, panelId, panel);
        SaveDockLayout();
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private ContentControl? FindDockHost(string panelId)
    {
        var panel = _dockPanels[panelId];
        return new[] { ProjectPanelHost, InstrumentPanelHost, MixerPanelHost, ResultsPanelHost }
            .FirstOrDefault(host => ReferenceEquals(host.Content, panel) ||
                host.Content is TabControl tabs && tabs.Items.OfType<TabItem>().Any(item => ReferenceEquals(item.Content, panel)));
    }

    private static void RemovePanelFromHost(ContentControl host, UIElement panel)
    {
        if (ReferenceEquals(host.Content, panel))
        {
            host.Content = null;
            return;
        }

        if (host.Content is not TabControl tabs)
        {
            return;
        }

        var panelTab = tabs.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Content, panel));
        if (panelTab is null)
        {
            return;
        }

        tabs.Items.Remove(panelTab);
        if (tabs.Items.Count == 0)
        {
            host.Content = null;
        }
        else if (tabs.Items.Count == 1)
        {
            var remainingTab = (TabItem)tabs.Items[0];
            var remainingPanel = remainingTab.Content;
            tabs.Items.Clear();
            host.Content = remainingPanel;
        }
    }

    private void AddPanelToHost(ContentControl host, string panelId, UIElement panel)
    {
        if (host.Content is TabControl tabs)
        {
            tabs.Items.Add(new TabItem { Header = GetDockPanelTitle(panelId), Content = panel });
            tabs.SelectedIndex = tabs.Items.Count - 1;
            return;
        }

        if (host.Content is not UIElement existingPanel)
        {
            host.Content = panel;
            return;
        }

        host.Content = null;
        var tabControl = new TabControl();
        var existingPanelId = _dockPanels.First(item => ReferenceEquals(item.Value, existingPanel)).Key;
        tabControl.Items.Add(new TabItem { Header = GetDockPanelTitle(existingPanelId), Content = existingPanel });
        tabControl.Items.Add(new TabItem { Header = GetDockPanelTitle(panelId), Content = panel });
        tabControl.SelectedIndex = 1;
        host.Content = tabControl;
    }

    private static string GetDockPanelTitle(string panelId) => panelId switch
    {
        "Project" => "프로젝트",
        "Instrument" => "작업 악기",
        "Mixer" => "믹서",
        "Results" => "작업 결과",
        _ => panelId
    };

    private ContentControl? GetDockHost(string slotId) => slotId switch
    {
        "Project" => ProjectPanelHost,
        "Instrument" => InstrumentPanelHost,
        "Mixer" => MixerPanelHost,
        "Results" => ResultsPanelHost,
        _ => null
    };

    private static string DockLayoutFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MakeMusic",
        "workspace-layout.json");

    private void RestoreDockLayout()
    {
        if (!File.Exists(DockLayoutFilePath))
        {
            return;
        }

        try
        {
            var layout = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(DockLayoutFilePath));
            if (layout is null)
            {
                return;
            }

            foreach (var host in new[] { ProjectPanelHost, InstrumentPanelHost, MixerPanelHost, ResultsPanelHost })
            {
                host.Content = null;
            }

            var placedPanelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slotId in new[] { "Project", "Instrument", "Mixer", "Results" })
            {
                var host = GetDockHost(slotId);
                if (host is null || !layout.TryGetValue(slotId, out var panelIds))
                {
                    continue;
                }

                foreach (var panelId in panelIds)
                {
                    if (_dockPanels.TryGetValue(panelId, out var panel) && placedPanelIds.Add(panelId))
                    {
                        AddPanelToHost(host, panelId, panel);
                    }
                }
            }

            foreach (var panelId in _dockPanels.Keys.Where(panelId => !placedPanelIds.Contains(panelId)))
            {
                var preferredHost = GetDockHost(panelId);
                var targetHost = preferredHost?.Content is null
                    ? preferredHost
                    : new[] { ProjectPanelHost, InstrumentPanelHost, MixerPanelHost, ResultsPanelHost }.FirstOrDefault(host => host.Content is null);
                if (targetHost is not null)
                {
                    AddPanelToHost(targetHost, panelId, _dockPanels[panelId]);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Keep the default workspace if a saved layout cannot be read.
        }
    }

    private void SaveDockLayout()
    {
        var layout = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var slotId in new[] { "Project", "Instrument", "Mixer", "Results" })
        {
            var host = GetDockHost(slotId);
            if (host is not null)
            {
                layout[slotId] = GetDockPanelIds(host.Content);
            }
        }

        try
        {
            var directory = Path.GetDirectoryName(DockLayoutFilePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(DockLayoutFilePath, JsonSerializer.Serialize(layout, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AudioStatus = "패널 배치 저장 실패";
        }
    }

    private List<string> GetDockPanelIds(object? hostContent)
    {
        if (hostContent is TabControl tabs)
        {
            return tabs.Items.OfType<TabItem>()
                .Select(item => GetDockPanelId(item.Content))
                .OfType<string>()
                .ToList();
        }

        var panelId = hostContent is UIElement panel ? GetDockPanelId(panel) : null;
        return panelId is null ? [] : [panelId];
    }

    private string? GetDockPanelId(object? panel) =>
        panel is UIElement element
            ? _dockPanels.FirstOrDefault(item => ReferenceEquals(item.Value, element)).Key
            : null;

    private int SelectedNoteLengthSteps
    {
        get
        {
            if (NoteLengthPicker.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out var length))
            {
                return length;
            }

            return 4;
        }
    }

    private int SelectedAccidentalOffset
    {
        get
        {
            return AccidentalPicker?.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out var offset) ? offset : 0;
        }
    }

    private void UpdateSelectedNoteControls()
    {
        if (NoteVelocityValue is null || NoteLengthPicker is null)
        {
            return;
        }

        _isUpdatingNoteControls = true;
        try
        {
            if (SelectedNote is null)
            {
                SelectedNoteSummary.Text = SelectedTrack is null ? "악기를 선택하세요" : "음표 선택 없음";
                return;
            }

            SelectedNoteSummary.Text = $"{GetPitchName(SelectedNote.Pitch)} · {SelectedNote.StartStep + 1}단계";
            NoteVelocitySlider.Value = SelectedNote.Velocity;
            var selectedLengthIndex = Array.IndexOf(NoteLengthOptions, SelectedNote.LengthSteps);
            if (selectedLengthIndex >= 0)
            {
                NoteLengthPicker.SelectedIndex = selectedLengthIndex;
            }
        }
        finally
        {
            _isUpdatingNoteControls = false;
        }
    }

    private void RenderScore()
    {
        if (ScoreCanvas is null)
        {
            return;
        }

        var totalSteps = AudioTrack.TimelineBarCount * StepsPerBar;
        var gridWidth = totalSteps * PianoRollStepWidth;
        const double scoreLeft = 52;
        const double staffBottom = 220;
        const double staffStepHeight = 12;
        ScoreCanvas.Width = scoreLeft + gridWidth;
        ScoreCanvas.Height = 320;
        ScoreCanvas.Children.Clear();

        AddScoreLabel("높은음자리표", 4, 34, 10, Brushes.DimGray);
        AddScoreLabel("마디", 28, 4, 9, Brushes.DimGray);
        for (var lineIndex = 0; lineIndex < 5; lineIndex++)
        {
            AddScoreLine(scoreLeft, staffBottom - lineIndex * staffStepHeight * 2, gridWidth, 1,
                Color.FromRgb(95, 103, 96));
        }

        for (var step = 0; step <= totalSteps; step++)
        {
            var isBar = step % StepsPerBar == 0;
            var isBeat = step % StepsPerBeat == 0;
            AddScoreLine(scoreLeft + step * PianoRollStepWidth, 24, isBar ? 2 : 1, 224,
                isBar ? Color.FromRgb(165, 174, 166) : isBeat ? Color.FromRgb(224, 228, 223) : Color.FromRgb(241, 243, 240));

            if (isBar && step < totalSteps)
            {
                AddScoreLabel((step / StepsPerBar + 1).ToString(), scoreLeft + step * PianoRollStepWidth + 3, 4, 9, Brushes.DimGray);
            }
        }

        _scorePlayheadIndicator = new Border
        {
            Width = 2,
            Height = 120,
            Background = Brushes.IndianRed,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Canvas.SetLeft(_scorePlayheadIndicator, scoreLeft);
        Canvas.SetTop(_scorePlayheadIndicator, 112);
        ScoreCanvas.Children.Add(_scorePlayheadIndicator);

        if (SelectedTrack is null)
        {
            return;
        }

        foreach (var note in SelectedTrack.Notes)
        {
            var notePosition = MidiToDiatonicStep(note.Pitch);
            var x = scoreLeft + note.StartStep * PianoRollStepWidth + PianoRollStepWidth / 2;
            var y = staffBottom - notePosition * staffStepHeight;
            var selected = ReferenceEquals(note, SelectedNote);
            if (notePosition < 0 || notePosition > 8)
            {
                var ledger = new Border { Width = 18, Height = 1, Background = Brushes.Black, IsHitTestVisible = false };
                Canvas.SetLeft(ledger, x - 9);
                Canvas.SetTop(ledger, staffBottom - (notePosition < 0 ? -2 : 10) * staffStepHeight);
                ScoreCanvas.Children.Add(ledger);
            }
            var notehead = new Ellipse
            {
                Width = 12,
                Height = 8,
                Fill = note.LengthSteps >= 8 ? Brushes.White : selected ? Brushes.SeaGreen : Brushes.Black,
                Stroke = selected ? Brushes.SeaGreen : Brushes.Black,
                StrokeThickness = 1.5,
                RenderTransform = new RotateTransform(-20),
                RenderTransformOrigin = new Point(0.5, 0.5),
                DataContext = note,
                Cursor = Cursors.Hand,
                ToolTip = $"{GetPitchName(note.Pitch)} · {note.LengthSteps}/16 박 · 세기 {note.Velocity}"
            };
            notehead.MouseLeftButtonDown += PianoRollNote_MouseLeftButtonDown;
            Canvas.SetLeft(notehead, x - 6);
            Canvas.SetTop(notehead, y - 4);
            ScoreCanvas.Children.Add(notehead);

            var noteClass = note.Pitch % 12;
            if (noteClass is 1 or 3 or 6 or 8 or 10)
            {
                AddScoreLabel("♯", x - 17, y - 10, 12, Brushes.Black);
            }

            if (note.LengthSteps < 16)
            {
                var stem = new Border { Width = 1, Height = 34, Background = selected ? Brushes.SeaGreen : Brushes.Black, IsHitTestVisible = false };
                Canvas.SetLeft(stem, x + 5);
                Canvas.SetTop(stem, y - 32);
                ScoreCanvas.Children.Add(stem);
            }
        }

        if (_playbackStopwatch.IsRunning)
        {
            UpdateScorePlayhead(isVisible: true);
        }
    }

    private void UpdateScorePlayhead(bool isVisible)
    {
        if (_scorePlayheadIndicator is null)
        {
            return;
        }

        if (!isVisible)
        {
            _scorePlayheadIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        const double scoreLeft = 52;
        var elapsedSteps = _playbackStopwatch.Elapsed.TotalSeconds / (60d / TempoBpm / StepsPerBeat);
        Canvas.SetLeft(_scorePlayheadIndicator, scoreLeft + elapsedSteps * PianoRollStepWidth);
        _scorePlayheadIndicator.Visibility = elapsedSteps <= AudioTrack.TimelineBarCount * StepsPerBar
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AddScoreLine(double left, double top, double width, double height, Color color)
    {
        var line = new Border { Width = width, Height = height, Background = new SolidColorBrush(color), IsHitTestVisible = false };
        Canvas.SetLeft(line, left);
        Canvas.SetTop(line, top);
        ScoreCanvas.Children.Add(line);
    }

    private void AddScoreLabel(string text, double left, double top, double fontSize, Brush foreground)
    {
        var label = new TextBlock { Text = text, FontSize = fontSize, Foreground = foreground, IsHitTestVisible = false };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        ScoreCanvas.Children.Add(label);
    }

    private static int MidiToDiatonicStep(int midiPitch)
    {
        var pitchClass = midiPitch % 12;
        var octave = midiPitch / 12 - 1;
        var letter = pitchClass switch
        {
            0 or 1 => 0, // C
            2 or 3 => 1, // D
            4 => 2,      // E
            5 or 6 => 3, // F
            7 or 8 => 4, // G
            9 or 10 => 5,// A
            _ => 6       // B
        };
        if (pitchClass is 1 or 3 or 6 or 8 or 10)
        {
            letter = pitchClass switch { 1 => 0, 3 => 1, 6 => 3, 8 => 4, _ => 5 };
        }
        var diatonicIndex = octave * 7 + letter;
        var e4Index = 4 * 7 + 2;
        return diatonicIndex - e4Index;
    }

    private static int DiatonicStepToMidi(int step)
    {
        var naturalPitches = new[] { 60, 62, 64, 65, 67, 69, 71, 72, 74, 76, 77, 79, 81, 83 };
        var index = step + 2; // C4 is two diatonic steps below E4.
        return index >= 0 && index < naturalPitches.Length ? naturalPitches[index] : -1;
    }

    private static bool IsBlackKey(int pitch) => pitch % 12 is 1 or 3 or 6 or 8 or 10;

    private static string GetPitchName(int pitch)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[pitch % 12]}{pitch / 12 - 1}";
    }

    private void AudioClip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border clip || clip.DataContext is not AudioTrack track || clip.Parent is not Canvas)
        {
            return;
        }

        var timelineCanvas = (Canvas)clip.Parent;
        _draggingAudioTrack = track;
        _audioClipDragOffset = e.GetPosition(timelineCanvas).X - track.TimelineLeft;
        clip.CaptureMouse();
        e.Handled = true;
    }

    private void AudioClip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingAudioTrack is null || sender is not Border clip || clip.Parent is not Canvas timelineCanvas || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var pointerX = e.GetPosition(timelineCanvas).X - _audioClipDragOffset;
        var startBar = (int)Math.Floor((pointerX + AudioTrack.PixelsPerBar / 2) / AudioTrack.PixelsPerBar) + 1;
        _draggingAudioTrack.StartBar = Math.Clamp(startBar, 1, AudioTrack.TimelineBarCount);
        e.Handled = true;
    }

    private void AudioClip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border clip || _draggingAudioTrack is null)
        {
            return;
        }

        clip.ReleaseMouseCapture();
        _draggingAudioTrack = null;
        e.Handled = true;
    }

    private void InstrumentTracks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (InstrumentTrack track in e.OldItems)
            {
                track.PropertyChanged -= InstrumentTrack_PropertyChanged;
                track.Notes.CollectionChanged -= InstrumentNotes_CollectionChanged;
                foreach (var note in track.Notes)
                {
                    note.PropertyChanged -= MusicalNote_PropertyChanged;
                }
            }
        }

        if (e.NewItems is not null)
        {
            foreach (InstrumentTrack track in e.NewItems)
            {
                track.PropertyChanged += InstrumentTrack_PropertyChanged;
                track.Notes.CollectionChanged += InstrumentNotes_CollectionChanged;
                foreach (var note in track.Notes)
                {
                    note.PropertyChanged += MusicalNote_PropertyChanged;
                }
            }
        }

        MarkDirty();
        OnPropertyChanged(nameof(CanPlayTimeline));
        RenderScore();
    }

    private void InstrumentNotes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MusicalNote note in e.OldItems)
            {
                note.PropertyChanged -= MusicalNote_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MusicalNote note in e.NewItems)
            {
                note.PropertyChanged += MusicalNote_PropertyChanged;
            }
        }

        MarkDirty();
        if (SelectedTrack is not null && ReferenceEquals(sender, SelectedTrack.Notes))
        {
            RenderScore();
        }
        OnPropertyChanged(nameof(CanPlayTimeline));
    }

    private void MusicalNote_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        if (sender is MusicalNote note && SelectedTrack?.Notes.Contains(note) == true)
        {
            RenderScore();
            UpdateSelectedNoteControls();
        }
    }

    private void AudioTracks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (AudioTrack track in e.OldItems)
            {
                track.PropertyChanged -= AudioTrack_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (AudioTrack track in e.NewItems)
            {
                track.PropertyChanged += AudioTrack_PropertyChanged;
            }
        }

        MarkDirty();
        OnPropertyChanged(nameof(CanPlayTimeline));
    }

    private void InstrumentTrack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        OnPropertyChanged(nameof(CanPlayTimeline));
        if (_audioOutput is not null && e.PropertyName is nameof(InstrumentTrack.Volume) or nameof(InstrumentTrack.IsMuted) or nameof(InstrumentTrack.IsSolo))
        {
            StopPlayback("악보 믹서 설정 변경 · 다시 재생하세요");
        }
        if (e.PropertyName == nameof(InstrumentTrack.Role) && sender is InstrumentTrack track)
        {
            RegisterInstrumentRole(track.Role);
        }
    }

    private void AudioTrack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        OnPropertyChanged(nameof(CanPlayTimeline));

        if (sender is not AudioTrack track)
        {
            return;
        }

        if (ReferenceEquals(track, SelectedAudioTrack) && e.PropertyName == nameof(AudioTrack.AudioFilePath))
        {
            AudioStatus = track.AudioStatus;
        }

        if (_audioOutput is null)
        {
            return;
        }

        if (e.PropertyName == nameof(AudioTrack.StartBar))
        {
            StopPlayback("클립 위치 변경 · 다시 재생하세요");
            return;
        }

        var hasSoloTrack = AudioTracks.Any(item => item.IsSolo);
        var activeTrackExcluded = _readersByAudioTrack.Keys.Any(item => item.IsMuted || (hasSoloTrack && !item.IsSolo));
        if (activeTrackExcluded)
        {
            StopPlayback("믹서 설정으로 정지됨");
        }
        else if (e.PropertyName == nameof(AudioTrack.Volume) && _readersByAudioTrack.TryGetValue(track, out var reader))
        {
            reader.Volume = (float)track.Volume;
        }
    }

    private void MarkDirty()
    {
        if (!_isApplyingProject)
        {
            IsDirty = true;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class InstrumentTrack(string name, string instrumentType, string role = "") : INotifyPropertyChanged
{
    private string _name = name;
    private string _role = role;
    private double _volume = 0.8;
    private bool _isMuted;
    private bool _isSolo;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string InstrumentType { get; } = instrumentType;

    public string Role
    {
        get => _role;
        set
        {
            if (!SetField(ref _role, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TrackDescription));
        }
    }

    public string TrackDescription => string.IsNullOrWhiteSpace(Role) ? InstrumentType : $"{InstrumentType} · {Role}";

    public ObservableCollection<MusicalNote> Notes { get; } = [];

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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class ToneNoteSampleProvider : ISampleProvider
{
    private const int FadeFrames = 352;
    private readonly int _totalFrames;
    private readonly double _phaseIncrement;
    private readonly float _amplitude;
    private int _currentFrame;

    public ToneNoteSampleProvider(int sampleRate, double frequency, float amplitude, TimeSpan duration)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _totalFrames = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * sampleRate));
        _phaseIncrement = 2 * Math.PI * frequency / sampleRate;
        _amplitude = Math.Clamp(amplitude, 0, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var framesToRead = Math.Min(count / WaveFormat.Channels, _totalFrames - _currentFrame);
        for (var frame = 0; frame < framesToRead; frame++)
        {
            var frameIndex = _currentFrame + frame;
            var attack = Math.Min(1d, frameIndex / (double)FadeFrames);
            var release = Math.Min(1d, (_totalFrames - 1 - frameIndex) / (double)FadeFrames);
            var sample = (float)(Math.Sin(frameIndex * _phaseIncrement) * _amplitude * Math.Max(0, Math.Min(attack, release)));
            var sampleIndex = offset + frame * 2;
            buffer[sampleIndex] = sample;
            buffer[sampleIndex + 1] = sample;
        }

        _currentFrame += framesToRead;
        return framesToRead * WaveFormat.Channels;
    }
}
