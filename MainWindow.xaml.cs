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
using Rectangle = System.Windows.Shapes.Rectangle;
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
    private const int PianoRollLowestPitch = 48;
    private const int PianoRollHighestPitch = 83;
    private const int StepsPerBeat = 4;
    private const int StepsPerBar = 16;
    private const double PianoRollStepWidth = 12;
    private const double PianoRollPitchLabelWidth = 48;
    private const int BarsPerScorePage = 4;
    private const int SystemsPerScorePage = 6;
    private const int BarsPerScoreSheet = BarsPerScorePage * SystemsPerScorePage;
    private const int StepsPerScoreSheet = BarsPerScoreSheet * StepsPerBar;
    private const double ScoreSystemHeight = 250;
    private const double GrandStaffGap = 140;
    private const int ScorePageCount = (AudioTrack.TimelineBarCount + BarsPerScoreSheet - 1) / BarsPerScoreSheet;
    private const double ScorePageLeft = 48;
    private const double ScorePageStepWidth = 36;
    private const int AudioOutputSampleRate = 44100;
    private static readonly int[] NoteLengthOptions = [1, 2, 4, 8, 16, 32, 64];

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
    private int _scorePageIndex;
    private bool UsesGrandStaff => SelectedTrack?.InstrumentType == "피아노";
    private string PrimaryClef => SelectedTrack?.InstrumentType is "베이스" or "첼로" ? "Bass" : "Treble";
    private int VisibleSystemsOnPage => Math.Clamp(
        (AudioTrack.TimelineBarCount - _scorePageIndex * BarsPerScoreSheet + BarsPerScorePage - 1) / BarsPerScorePage,
        1,
        SystemsPerScorePage);
    private readonly UIElement? _scoreEditorContent;
    private readonly HashSet<MusicalNote> _selectedNotes = [];
    private Point _scoreDragStart;
    private bool _isSelectingNotes;
    private bool _isMovingNotes;
    private bool _scoreDragMoved;
    private bool _isUpdatingScorePageSlider;
    private MusicalNote? _draggedNote;
    private Rectangle? _noteSelectionRectangle;
    private Dictionary<MusicalNote, (int StartStep, int Pitch)> _noteDragOrigins = [];
    private bool _pendingNoteEntry;
    private int _pendingNotePitch;
    private int _pendingNoteStartStep;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        ScorePageSlider.Maximum = ScorePageCount - 1;
        _scoreEditorContent = ScoreDockHost.Content as UIElement;
        _dockPanels["Project"] = (UIElement)ProjectPanelHost.Content;
        _dockPanels["Instrument"] = (UIElement)InstrumentPanelHost.Content;
        _dockPanels["Mixer"] = (UIElement)MixerPanelHost.Content;
        _dockPanels["Results"] = (UIElement)ResultsPanelHost.Content;
        _transportTimer.Tick += TransportTimer_Tick;
        InstrumentTracks.CollectionChanged += InstrumentTracks_CollectionChanged;
        AudioTracks.CollectionChanged += AudioTracks_CollectionChanged;

        InstrumentTracks.Add(new InstrumentTrack("피아노", "피아노"));
        SelectedTrack = InstrumentTracks[0];
        IsDirty = false;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        RestoreLastProject();
    }

    private void RestoreLastProject()
    {
        string path;
        try
        {
            if (!File.Exists(LastProjectFilePath))
            {
                return;
            }

            path = File.ReadAllText(LastProjectFilePath).Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                return;
            }

            var document = ReadProject(path);
            ApplyProject(document, path);
            CurrentProjectPath = path;
            IsDirty = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, $"마지막 프로젝트를 열 수 없습니다.\n{exception.Message}", "프로젝트 열기 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            _selectedNotes.Clear();
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
            _selectedNotes.Clear();
            if (value is not null)
            {
                _selectedNotes.Add(value);
            }
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
            document = ReadProject(dialog.FileName);
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
        RememberLastProject(dialog.FileName);
        IsDirty = false;
    }

    private static ProjectDocument ReadProject(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".mmproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("프로젝트 파일은 .mmproj 형식만 열 수 있습니다.");
        }

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("프로젝트 파일에 내용이 없습니다.");
        ValidateProject(document, path);
        return document;
    }

    private static void RememberLastProject(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastProjectFilePath)!);
            File.WriteAllText(LastProjectFilePath, Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The project remains usable even if the recent-project preference cannot be written.
        }
    }

    private static string LastProjectFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MakeMusic",
        "last-project.txt");

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
                        Velocity = note.Velocity,
                        Articulation = note.Articulation,
                        IsSlurredToNext = note.IsSlurredToNext
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
            RememberLastProject(path);
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
                    instrumentTrack.Notes.Add(new MusicalNote(noteDocument.Pitch, noteDocument.StartStep, noteDocument.LengthSteps, noteDocument.Velocity)
                    {
                        Articulation = noteDocument.Articulation,
                        IsSlurredToNext = noteDocument.IsSlurredToNext
                    });
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
                if (note is null || note.Pitch is < 48 or > 83 || note.StartStep is < 0 or >= AudioTrack.TimelineBarCount * 16 ||
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

        _scorePageIndex = Math.Clamp((PlayheadBar - 1) / BarsPerScoreSheet, 0, ScorePageCount - 1);
        RenderScore();
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

                var orderedNotes = track.Notes.OrderBy(note => note.StartStep).ToList();
                for (var noteIndex = 0; noteIndex < orderedNotes.Count; noteIndex++)
                {
                    var note = orderedNotes[noteIndex];
                    var articulationScale = note.Articulation == "Staccato" ? 0.4 : note.Articulation == "Tenuto" ? 2d : 1d;
                    var sustainedSteps = note.LengthSteps;
                    var lastTiedIndex = noteIndex;
                    while (orderedNotes[lastTiedIndex].IsSlurredToNext && lastTiedIndex + 1 < orderedNotes.Count)
                    {
                        var nextNote = orderedNotes[lastTiedIndex + 1];
                        if (nextNote.Pitch != note.Pitch || nextNote.StartStep != note.StartStep + sustainedSteps)
                        {
                            break;
                        }

                        sustainedSteps += nextNote.LengthSteps;
                        lastTiedIndex++;
                    }

                    var overlapSeconds = lastTiedIndex == noteIndex && note.IsSlurredToNext ? 0.035 : 0;
                    var duration = TimeSpan.FromSeconds(sustainedSteps * secondsPerStep * articulationScale + overlapSeconds);
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
                    noteIndex = lastTiedIndex;
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
        _pendingNoteEntry = false;
        if (e.OriginalSource is Canvas && Keyboard.Modifiers == ModifierKeys.None)
        {
            var visibleSteps = BarsPerScorePage * StepsPerBar;
            var localStep = (int)((position.X - ScorePageLeft) / ScorePageStepWidth);
            var systemIndex = (int)(position.Y / ScoreSystemHeight);
            if (localStep >= 0 && localStep < visibleSteps && systemIndex >= 0 && systemIndex < VisibleSystemsOnPage)
            {
                var startStep = _scorePageIndex * StepsPerScoreSheet + systemIndex * visibleSteps + localStep;
                const double staffBottom = 145;
                const double staffStepHeight = 10;
                var systemY = systemIndex * ScoreSystemHeight;
                var localY = position.Y - systemY;
                var inputClef = UsesGrandStaff && localY >= 175 ? "Bass" : PrimaryClef;
                var inputStaffBottom = (inputClef == "Bass" && UsesGrandStaff ? staffBottom + GrandStaffGap : staffBottom) + systemY;
                var diatonicStep = (int)Math.Round((inputStaffBottom - position.Y) / staffStepHeight);
                var pitch = DiatonicStepToMidi(diatonicStep, inputClef) + SelectedAccidentalOffset;
                var totalSteps = AudioTrack.TimelineBarCount * StepsPerBar;
                var minimumPitch = PianoRollLowestPitch;
                var validStaff = position.Y >= inputStaffBottom - 80 - 8 && position.Y <= inputStaffBottom + 8;
                if (validStaff && pitch >= minimumPitch && pitch <= PianoRollHighestPitch && startStep < totalSteps)
                {
                    _pendingNoteEntry = true;
                    _pendingNotePitch = pitch;
                    _pendingNoteStartStep = startStep;
                }
            }
        }

        _scoreDragStart = position;
        _scoreDragMoved = false;
        _isSelectingNotes = true;
        _noteSelectionRectangle = new Rectangle { Stroke = Brushes.SeaGreen, StrokeThickness = 1, Fill = new SolidColorBrush(Color.FromArgb(35, 46, 139, 87)), IsHitTestVisible = false };
        Canvas.SetLeft(_noteSelectionRectangle, position.X);
        Canvas.SetTop(_noteSelectionRectangle, position.Y);
        ScoreCanvas.Children.Add(_noteSelectionRectangle);
        ScoreCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void PianoRollNote_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MusicalNote note })
        {
            if (SelectedTrack is null)
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Right)
            {
                SelectedTrack.Notes.Remove(note);
                if (ReferenceEquals(SelectedNote, note))
                {
                    SelectedNote = null;
                }
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (!_selectedNotes.Remove(note))
                {
                    _selectedNotes.Add(note);
                    _selectedNote = note;
                }
                else if (ReferenceEquals(_selectedNote, note))
                {
                    _selectedNote = _selectedNotes.LastOrDefault();
                }
            }
            else if (!_selectedNotes.Contains(note))
            {
                _selectedNotes.Clear();
                _selectedNotes.Add(note);
                _selectedNote = note;
            }

            OnPropertyChanged(nameof(HasSelectedNote));
            UpdateSelectedNoteControls();
            RenderScore();
            ScoreCanvas.Focus();
            _draggedNote = note;
            _scoreDragStart = e.GetPosition(ScoreCanvas);
            _scoreDragMoved = false;
            _isMovingNotes = true;
            ScoreCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void ScoreCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || SelectedTrack is null)
        {
            return;
        }

        var position = e.GetPosition(ScoreCanvas);
        var movedHorizontally = Math.Abs(position.X - _scoreDragStart.X) > 4;
        var movedVertically = Math.Abs(position.Y - _scoreDragStart.Y) > 4;
        if (!_scoreDragMoved && (_isMovingNotes ? movedHorizontally : movedHorizontally || movedVertically))
        {
            _scoreDragMoved = true;
            if (_isMovingNotes && SelectedTrack is not null)
            {
                _noteDragOrigins = _selectedNotes.ToDictionary(note => note, note => (note.StartStep, note.Pitch));
            }
        }

        if (!_scoreDragMoved)
        {
            return;
        }

        if (_isMovingNotes && _draggedNote is not null)
        {
            var horizontalSteps = (int)Math.Round((position.X - _scoreDragStart.X) / ScorePageStepWidth);
            var totalSteps = AudioTrack.TimelineBarCount * StepsPerBar;
            foreach (var entry in _noteDragOrigins)
            {
                entry.Key.StartStep = Math.Clamp(entry.Value.StartStep + horizontalSteps, 0, totalSteps - entry.Key.LengthSteps);
            }
        }
        else if (_isSelectingNotes && _noteSelectionRectangle is not null)
        {
            var left = Math.Min(_scoreDragStart.X, position.X);
            var top = Math.Min(_scoreDragStart.Y, position.Y);
            _noteSelectionRectangle.Width = Math.Abs(position.X - _scoreDragStart.X);
            _noteSelectionRectangle.Height = Math.Abs(position.Y - _scoreDragStart.Y);
            Canvas.SetLeft(_noteSelectionRectangle, left);
            Canvas.SetTop(_noteSelectionRectangle, top);
        }
    }

    private void ScoreCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSelectingNotes && _noteSelectionRectangle is not null && _scoreDragMoved && SelectedTrack is not null)
        {
            var rectangle = _noteSelectionRectangle;
            var left = Canvas.GetLeft(rectangle);
            var top = Canvas.GetTop(rectangle);
            var right = left + rectangle.Width;
            var bottom = top + rectangle.Height;
            var pageStartStep = _scorePageIndex * StepsPerScoreSheet;
            _selectedNotes.Clear();
            foreach (var note in SelectedTrack.Notes)
            {
                if (note.StartStep < pageStartStep || note.StartStep >= pageStartStep + StepsPerScoreSheet)
                {
                    continue;
                }

                var pageLocalStep = note.StartStep - pageStartStep;
                var systemIndex = pageLocalStep / (BarsPerScorePage * StepsPerBar);
                var localStep = pageLocalStep % (BarsPerScorePage * StepsPerBar);
                var noteX = ScorePageLeft + (localStep + Math.Min(note.LengthSteps, 2) / 2d) * ScorePageStepWidth;
                var noteClef = UsesGrandStaff ? (note.Pitch >= 60 ? "Treble" : "Bass") : PrimaryClef;
                var staffBottom = 145 + systemIndex * ScoreSystemHeight;
                var noteBottom = UsesGrandStaff && noteClef == "Bass" ? staffBottom + GrandStaffGap : staffBottom;
                var noteY = noteBottom - MidiToDiatonicStep(note.Pitch, noteClef) * 10;
                if (noteX >= left && noteX <= right && noteY >= top && noteY <= bottom)
                {
                    _selectedNotes.Add(note);
                }
            }

            _selectedNote = _selectedNotes.LastOrDefault();
            OnPropertyChanged(nameof(HasSelectedNote));
            ScoreCanvas.Focus();
        }
        else if (_pendingNoteEntry && !_scoreDragMoved && SelectedTrack is not null)
        {
            var totalSteps = AudioTrack.TimelineBarCount * StepsPerBar;
            var note = new MusicalNote(_pendingNotePitch, _pendingNoteStartStep,
                Math.Min(SelectedNoteLengthSteps, totalSteps - _pendingNoteStartStep), (int)Math.Round(NoteVelocitySlider.Value));
            SelectedTrack.Notes.Add(note);
            SelectedNote = note;
        }

        if (_noteSelectionRectangle is not null)
        {
            ScoreCanvas.Children.Remove(_noteSelectionRectangle);
            _noteSelectionRectangle = null;
        }

        _isSelectingNotes = false;
        _isMovingNotes = false;
        _draggedNote = null;
        _noteDragOrigins.Clear();
        _pendingNoteEntry = false;
        ScoreCanvas.ReleaseMouseCapture();
        UpdateSelectedNoteControls();
        RenderScore();
        e.Handled = true;
    }

    private void ScorePageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingScorePageSlider)
        {
            return;
        }

        var pageIndex = Math.Clamp((int)Math.Round(e.NewValue), 0, ScorePageCount - 1);
        if (pageIndex != _scorePageIndex)
        {
            _scorePageIndex = pageIndex;
            RenderScore();
        }
    }

    private void NoteLengthPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingNoteControls || SelectedNote is null || SelectedTrack is null)
        {
            return;
        }

        var length = SelectedNoteLengthSteps;
        foreach (var note in GetSelectedNotes())
        {
            note.LengthSteps = Math.Min(length, AudioTrack.TimelineBarCount * StepsPerBar - note.StartStep);
        }
    }

    private IEnumerable<MusicalNote> GetSelectedNotes() =>
        SelectedTrack is null ? [] : _selectedNotes.Where(SelectedTrack.Notes.Contains).ToList();

    private void AccidentalPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingNoteControls || SelectedTrack is null || _selectedNotes.Count == 0)
        {
            return;
        }

        var accidental = SelectedAccidentalOffset;
        foreach (var note in GetSelectedNotes())
        {
            var naturalPitch = note.Pitch - (note.Pitch % 12 is 1 or 3 or 6 or 8 or 10 ? 1 : 0);
            note.Pitch = Math.Clamp(naturalPitch + accidental, PianoRollLowestPitch, PianoRollHighestPitch);
        }
    }

    private void ArticulationPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingNoteControls || SelectedTrack is null || _selectedNotes.Count == 0 || ArticulationPicker.SelectedItem is not ComboBoxItem { Tag: string articulation })
        {
            return;
        }

        foreach (var note in GetSelectedNotes())
        {
            note.Articulation = articulation;
        }
    }

    private void SlurSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingNoteControls || SelectedTrack is null)
        {
            return;
        }

        var selected = GetSelectedNotes().OrderBy(note => note.StartStep).ToList();
        if (SlurSelectionToggle.IsChecked == true && selected.Count < 2)
        {
            _isUpdatingNoteControls = true;
            SlurSelectionToggle.IsChecked = false;
            _isUpdatingNoteControls = false;
            AudioStatus = "연결할 음표를 두 개 이상 선택하세요";
            return;
        }

        var selectedSet = selected.ToHashSet();
        var orderedTrackNotes = SelectedTrack.Notes.OrderBy(note => note.StartStep).ToList();
        foreach (var note in selected)
        {
            var noteIndex = orderedTrackNotes.IndexOf(note);
            note.IsSlurredToNext = SlurSelectionToggle.IsChecked == true &&
                noteIndex >= 0 && noteIndex + 1 < orderedTrackNotes.Count && selectedSet.Contains(orderedTrackNotes[noteIndex + 1]);
        }

        if (SlurSelectionToggle.IsChecked == true && selected.All(note => !note.IsSlurredToNext))
        {
            _isUpdatingNoteControls = true;
            SlurSelectionToggle.IsChecked = false;
            _isUpdatingNoteControls = false;
            AudioStatus = "연음으로 연결할 음표를 시간 순서대로 함께 선택하세요";
        }
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

        var notesToDelete = GetSelectedNotes().ToList();
        if (notesToDelete.Count == 0)
        {
            notesToDelete.Add(SelectedNote);
        }

        foreach (var note in notesToDelete)
        {
            SelectedTrack.Notes.Remove(note);
        }
        SelectedNote = null;
    }

    private void ScoreCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Delete or Key.Back) && SelectedTrack is not null && HasSelectedNote)
        {
            DeleteSelectedNote_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
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
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ScoreEditor_PreviewDragOver(object sender, DragEventArgs e)
    {
        var panelId = e.Data.GetData("MakeMusic.DockPanel") as string;
        var sourceHost = panelId is null ? null : FindDockHost(panelId);
        e.Effects = panelId is not null && _dockPanels.ContainsKey(panelId) && sourceHost is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ScoreEditor_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("MakeMusic.DockPanel") is not string panelId ||
            !_dockPanels.TryGetValue(panelId, out var panel))
        {
            return;
        }

        var sourceHost = FindDockHost(panelId);
        if (sourceHost is null)
        {
            return;
        }

        RemovePanelFromHost(sourceHost, panel);
        var destination = ScoreEditorTabItem;
        if (destination.Content is not ContentControl contentHost)
        {
            contentHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            destination.Content = contentHost;
        }

        AddPanelToHost(contentHost, panelId, panel);
        ScoreEditorTabs.SelectedItem = destination;
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
            if (NoteVelocityValue is null || NoteLengthPicker is null || ArticulationPicker is null || SlurSelectionToggle is null)
        {
            return;
        }

        _isUpdatingNoteControls = true;
        try
        {
            if (SelectedNote is null)
            {
                SelectedNoteSummary.Text = SelectedTrack is null ? "악기를 선택하세요" : "음표 선택 없음";
            SlurSelectionToggle.IsChecked = GetSelectedNotes().Any(note => note.IsSlurredToNext);
                return;
            }

            SelectedNoteSummary.Text = $"{GetPitchName(SelectedNote.Pitch)} · {SelectedNote.StartStep + 1}단계";
            NoteVelocitySlider.Value = SelectedNote.Velocity;
            var selectedLengthIndex = Array.IndexOf(NoteLengthOptions, SelectedNote.LengthSteps);
            if (selectedLengthIndex >= 0)
            {
                NoteLengthPicker.SelectedIndex = selectedLengthIndex;
            }
            ArticulationPicker.SelectedIndex = SelectedNote.Articulation switch { "Tenuto" => 1, "Staccato" => 2, _ => 0 };
            SlurSelectionToggle.IsChecked = GetSelectedNotes().Any(note => note.IsSlurredToNext);
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

        const double staffStepHeight = 10;
        var visibleSteps = BarsPerScorePage * StepsPerBar;
        var pageStartStep = _scorePageIndex * StepsPerScoreSheet;
        var pageEndStep = Math.Min(AudioTrack.TimelineBarCount * StepsPerBar, pageStartStep + StepsPerScoreSheet);
        var pageNumberStart = _scorePageIndex * BarsPerScoreSheet + 1;
        ScoreCanvas.Width = ScorePageLeft + visibleSteps * ScorePageStepWidth + 12;
        var visibleSystems = VisibleSystemsOnPage;
        ScoreCanvas.Height = visibleSystems * ScoreSystemHeight + 24;
        ScoreCanvas.Children.Clear();
        ScoreDockHost.Content = _scoreEditorContent;
        var pageNumberEnd = Math.Min(AudioTrack.TimelineBarCount, pageNumberStart + BarsPerScoreSheet - 1);
        ScorePageLabel.Text = $"마디 {pageNumberStart}–{pageNumberEnd} / {AudioTrack.TimelineBarCount}";
        _isUpdatingScorePageSlider = true;
        ScorePageSlider.Value = _scorePageIndex;
        _isUpdatingScorePageSlider = false;

        for (var systemIndex = 0; systemIndex < visibleSystems; systemIndex++)
        {
            var systemY = systemIndex * ScoreSystemHeight;
            var staffBottom = 145 + systemY;
            var lowerStaffBottom = staffBottom + GrandStaffGap;
            var firstBar = pageNumberStart + systemIndex * BarsPerScorePage;
            DrawStaff(staffBottom, PrimaryClef, firstBar, BarsPerScorePage, visibleSteps);
            if (UsesGrandStaff)
            {
                DrawStaff(lowerStaffBottom, "Bass", firstBar, BarsPerScorePage, visibleSteps);
                AddScoreLine(ScorePageLeft - 2, staffBottom - 80, 2, lowerStaffBottom - staffBottom + 80, Color.FromRgb(110, 118, 111));
                AddScoreLine(ScorePageLeft + visibleSteps * ScorePageStepWidth, staffBottom - 80, 2, lowerStaffBottom - staffBottom + 80, Color.FromRgb(110, 118, 111));
                for (var bar = 0; bar <= BarsPerScorePage; bar++)
                {
                    var barX = ScorePageLeft + bar * StepsPerBar * ScorePageStepWidth;
                    AddScoreLine(barX, staffBottom - 80, bar == BarsPerScorePage ? 2 : 1, lowerStaffBottom - staffBottom + 80, Color.FromRgb(165, 174, 166));
                }
            }
        }
        AddScoreLabel("♩ =", 5, 8, 9, Brushes.DimGray);
        AddScoreLabel(TempoBpm.ToString(), 31, 8, 9, Brushes.DimGray);

        _scorePlayheadIndicator = new Border
        {
            Width = 2,
            Height = UsesGrandStaff ? GrandStaffGap + 80 : 80,
            Background = Brushes.IndianRed,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Canvas.SetLeft(_scorePlayheadIndicator, ScorePageLeft);
        Canvas.SetTop(_scorePlayheadIndicator, 65);
        ScoreCanvas.Children.Add(_scorePlayheadIndicator);

        if (SelectedTrack is null)
        {
            return;
        }

        var notePositions = new Dictionary<MusicalNote, (double X, double Y, int System)>();
        foreach (var note in SelectedTrack.Notes)
        {
            if (note.StartStep < pageStartStep || note.StartStep >= pageEndStep)
            {
                continue;
            }

            var noteClef = UsesGrandStaff ? (note.Pitch >= 60 ? "Treble" : "Bass") : PrimaryClef;
            var pageLocalStep = note.StartStep - pageStartStep;
            var systemIndex = pageLocalStep / visibleSteps;
            var localStart = pageLocalStep % visibleSteps;
            var systemY = systemIndex * ScoreSystemHeight;
            var staffBottom = 145 + systemY;
            var lowerStaffBottom = staffBottom + GrandStaffGap;
            var noteBottom = UsesGrandStaff && noteClef == "Bass" ? lowerStaffBottom : staffBottom;
            var notePosition = MidiToDiatonicStep(note.Pitch, noteClef);
            var x = ScorePageLeft + (localStart + Math.Min(note.LengthSteps, 2) / 2d) * ScorePageStepWidth;
            var y = noteBottom - notePosition * staffStepHeight;
            notePositions[note] = (x, y, systemIndex);
            var selected = _selectedNotes.Contains(note);
            if (notePosition < -4 || notePosition > 8)
            {
                for (var ledgerStep = notePosition < 0 ? -2 : 10;
                     notePosition < 0 ? ledgerStep >= notePosition : ledgerStep <= notePosition;
                     ledgerStep += notePosition < 0 ? -2 : 2)
                {
                    var ledger = new Border { Width = 22, Height = 1, Background = Brushes.Black, IsHitTestVisible = false };
                    Canvas.SetLeft(ledger, x - 11);
                    Canvas.SetTop(ledger, noteBottom - ledgerStep * staffStepHeight);
                    ScoreCanvas.Children.Add(ledger);
                }
            }
            var noteheadFill = note.LengthSteps >= 16 ? Brushes.White : note.LengthSteps >= 8 ? Brushes.White : selected ? Brushes.SeaGreen : Brushes.Black;
            var notehead = new Ellipse
            {
                Width = 15,
                Height = 10,
                Fill = noteheadFill,
                Stroke = selected ? Brushes.SeaGreen : Brushes.Black,
                StrokeThickness = 1.5,
                RenderTransform = new RotateTransform(-20),
                RenderTransformOrigin = new Point(0.5, 0.5),
                DataContext = note,
                Cursor = Cursors.Hand,
                ToolTip = $"{GetPitchName(note.Pitch)} · {GetNoteValueName(note.LengthSteps)} · 세기 {note.Velocity}"
            };
            notehead.MouseLeftButtonDown += PianoRollNote_MouseLeftButtonDown;
            notehead.MouseRightButtonDown += PianoRollNote_MouseRightButtonDown;
            Canvas.SetLeft(notehead, x - 7.5);
            Canvas.SetTop(notehead, y - 5);
            ScoreCanvas.Children.Add(notehead);

            var pitchClass = note.Pitch % 12;
            if (pitchClass is 1 or 3 or 6 or 8 or 10)
            {
                var preferFlat = AccidentalPicker?.SelectedItem is ComboBoxItem { Tag: "-1" };
                var accidental = preferFlat ? "♭" : "♯";
                AddScoreLabel(accidental, x - 19, y - 10, 12, Brushes.Black);
            }

            if (note.Articulation == "Staccato")
            {
                var dot = new Ellipse { Width = 4, Height = 4, Fill = Brushes.Black, IsHitTestVisible = false };
                Canvas.SetLeft(dot, x + 10);
                Canvas.SetTop(dot, y + 1);
                ScoreCanvas.Children.Add(dot);
            }
            else if (note.Articulation == "Tenuto")
            {
                var tenutoMark = new Border
                {
                    Width = 11,
                    Height = 1.5,
                    Background = selected ? Brushes.SeaGreen : Brushes.Black,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(tenutoMark, x - 5.5);
                Canvas.SetTop(tenutoMark, y + 9);
                ScoreCanvas.Children.Add(tenutoMark);
            }

            if (note.LengthSteps < 16)
            {
                var stemHeight = 36d;
                var stemX = x + 6;
                var stemTop = y - stemHeight;
                var stem = new Border { Width = 1.4, Height = stemHeight, Background = selected ? Brushes.SeaGreen : Brushes.Black, IsHitTestVisible = false };
                Canvas.SetLeft(stem, stemX);
                Canvas.SetTop(stem, stemTop);
                ScoreCanvas.Children.Add(stem);

                var flagCount = note.LengthSteps <= 2 ? 2 : note.LengthSteps <= 4 ? 1 : 0;
                for (var flagIndex = 0; flagIndex < flagCount; flagIndex++)
                {
                    var flag = new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse("M 0,0 C 13,5 13,13 2,18"),
                        Stroke = selected ? Brushes.SeaGreen : Brushes.Black,
                        StrokeThickness = 2,
                        IsHitTestVisible = false,
                        RenderTransform = new TranslateTransform(stemX, stemTop + flagIndex * 7)
                    };
                    ScoreCanvas.Children.Add(flag);
                }
            }
        }

        var orderedScoreNotes = SelectedTrack.Notes.OrderBy(note => note.StartStep).ToList();
        foreach (var note in orderedScoreNotes.Where(note => note.IsSlurredToNext && notePositions.ContainsKey(note)))
        {
            var noteIndex = orderedScoreNotes.IndexOf(note);
            var nextNote = noteIndex >= 0 && noteIndex + 1 < orderedScoreNotes.Count ? orderedScoreNotes[noteIndex + 1] : null;
            if (nextNote is null || !notePositions.TryGetValue(nextNote, out var nextPosition))
            {
                continue;
            }

            var startPosition = notePositions[note];
            if (nextPosition.X - startPosition.X < 16)
            {
                continue;
            }

            var goesToNextSystem = startPosition.System != nextPosition.System;
            var start = new Point(startPosition.X + 5, startPosition.Y - 4);
            var end = goesToNextSystem
                ? new Point(ScorePageLeft - 6, nextPosition.Y - 4)
                : new Point(nextPosition.X - 5, nextPosition.Y - 4);
            var archY = Math.Min(start.Y, end.Y) - 22;
            var curve = new System.Windows.Media.PathGeometry();
            var figure = new System.Windows.Media.PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new System.Windows.Media.BezierSegment(
                new Point(start.X + (end.X - start.X) / 3, archY),
                new Point(start.X + (end.X - start.X) * 2 / 3, archY),
                end,
                true));
            curve.Figures.Add(figure);
            ScoreCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = curve,
                Stroke = Brushes.Black,
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            });

            if (goesToNextSystem)
            {
                var continuationStart = new Point(ScorePageLeft - 2, nextPosition.Y - 4);
                var continuationEnd = new Point(nextPosition.X - 5, nextPosition.Y - 4);
                var continuationCurve = new System.Windows.Media.PathGeometry();
                var continuationFigure = new System.Windows.Media.PathFigure { StartPoint = continuationStart, IsClosed = false };
                continuationFigure.Segments.Add(new System.Windows.Media.BezierSegment(
                    new Point(continuationStart.X + (continuationEnd.X - continuationStart.X) / 3, continuationStart.Y - 22),
                    new Point(continuationStart.X + (continuationEnd.X - continuationStart.X) * 2 / 3, continuationStart.Y - 22),
                    continuationEnd,
                    true));
                continuationCurve.Figures.Add(continuationFigure);
                ScoreCanvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = continuationCurve,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.5,
                    IsHitTestVisible = false
                });
            }
        }

        if (_playbackStopwatch.IsRunning)
        {
            UpdateScorePlayhead(isVisible: true);
        }
    }

    private void PianoRollNote_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedTrack is not null && sender is FrameworkElement { DataContext: MusicalNote note })
        {
            SelectedTrack.Notes.Remove(note);
            _selectedNotes.Remove(note);
            if (ReferenceEquals(SelectedNote, note))
            {
                _selectedNote = _selectedNotes.LastOrDefault();
                OnPropertyChanged(nameof(SelectedNote));
                OnPropertyChanged(nameof(HasSelectedNote));
                UpdateSelectedNoteControls();
            }
            RenderScore();
            e.Handled = true;
        }
    }

    private void DrawStaff(double staffBottom, string clef, int pageNumberStart, int barsPerPage, int visibleSteps)
    {
        const double staffStepHeight = 10;
        var staffTop = staffBottom - 8 * staffStepHeight;
        AddScoreLabel(clef == "Bass" ? "𝄢" : "𝄞", 8, staffBottom - 91, 58, Brushes.Black, "Segoe UI Symbol");
        AddScoreLabel("4", 31, staffTop + 7, 18, Brushes.Black);
        AddScoreLabel("4", 31, staffTop + 26, 18, Brushes.Black);
        for (var lineIndex = 0; lineIndex < 5; lineIndex++)
        {
            AddScoreLine(ScorePageLeft, staffBottom - lineIndex * staffStepHeight * 2, visibleSteps * ScorePageStepWidth, 1, Color.FromRgb(95, 103, 96));
        }

        for (var measure = 0; measure <= barsPerPage; measure++)
        {
            var x = ScorePageLeft + measure * StepsPerBar * ScorePageStepWidth;
            AddScoreLine(x, staffTop, measure == barsPerPage ? 2 : 1, staffBottom - staffTop, Color.FromRgb(165, 174, 166));
            if (measure < barsPerPage)
            {
                AddScoreLabel((pageNumberStart + measure).ToString(), x + 4, staffTop - 18, 9, Brushes.DimGray);
                for (var beat = 1; beat < 4; beat++)
                {
                    var beatX = x + beat * StepsPerBeat * ScorePageStepWidth;
                    AddScoreLine(beatX, staffTop, 1, staffBottom - staffTop, Color.FromRgb(230, 233, 230));
                }
            }
        }
    }

    private string GetNoteClef(int pitch) => UsesGrandStaff ? (pitch >= 60 ? "Treble" : "Bass") : PrimaryClef;

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

        var visibleSteps = BarsPerScorePage * StepsPerBar;
        var elapsedSteps = _playbackStopwatch.Elapsed.TotalSeconds / (60d / TempoBpm / StepsPerBeat);
        var currentPage = (int)(elapsedSteps / StepsPerScoreSheet);
        if (currentPage != _scorePageIndex && currentPage >= 0 && currentPage < ScorePageCount)
        {
            _scorePageIndex = currentPage;
            RenderScore();
            return;
        }

        if (elapsedSteps >= AudioTrack.TimelineBarCount * StepsPerBar)
        {
            _scorePlayheadIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var pageLocalStep = elapsedSteps - _scorePageIndex * StepsPerScoreSheet;
        var systemIndex = (int)(pageLocalStep / visibleSteps);
        var localStep = pageLocalStep % visibleSteps;
        Canvas.SetLeft(_scorePlayheadIndicator, ScorePageLeft + localStep * ScorePageStepWidth);
        Canvas.SetTop(_scorePlayheadIndicator, systemIndex * ScoreSystemHeight + 65);
        _scorePlayheadIndicator.Visibility = pageLocalStep <= StepsPerScoreSheet
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string GetNoteValueName(int lengthSteps) => lengthSteps switch
    {
        >= 64 => "4마디 장음",
        >= 32 => "2마디 장음",
        >= 16 => "온음표",
        >= 8 => "2분음표",
        >= 4 => "4분음표",
        >= 2 => "8분음표",
        _ => "16분음표"
    };

    private void AddScoreLine(double left, double top, double width, double height, Color color)
    {
        var line = new Border { Width = width, Height = height, Background = new SolidColorBrush(color), IsHitTestVisible = false };
        Canvas.SetLeft(line, left);
        Canvas.SetTop(line, top);
        ScoreCanvas.Children.Add(line);
    }

    private void AddScoreLabel(string text, double left, double top, double fontSize, Brush foreground, string? fontFamily = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = foreground,
            FontFamily = fontFamily is null ? SystemFonts.MessageFontFamily : new System.Windows.Media.FontFamily(fontFamily),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        ScoreCanvas.Children.Add(label);
    }

    private static int MidiToDiatonicStep(int midiPitch, string clef)
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
        var referenceIndex = clef == "Bass" ? 2 * 7 + 4 : 4 * 7 + 2;
        return diatonicIndex - referenceIndex;
    }

    private static int DiatonicStepToMidi(int step, string clef)
    {
        var referenceDiatonic = clef == "Bass" ? 2 * 7 + 4 : 4 * 7 + 2;
        var targetDiatonic = referenceDiatonic + step;
        var octave = targetDiatonic / 7;
        var letter = targetDiatonic % 7;
        var naturalPitch = (octave + 1) * 12 + new[] { 0, 2, 4, 5, 7, 9, 11 }[letter];
        return naturalPitch;
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
