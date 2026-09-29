using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;

namespace MakeMusic;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] BuiltInInstrumentRoles = ["주선율", "보조선율", "화음", "저음", "리듬"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string _projectName = "새 프로젝트";
    private InstrumentTrack? _selectedTrack;
    private AudioTrack? _selectedAudioTrack;
    private double _masterReferencePitch = 440;
    private double _masterFineTuneCents;
    private bool _isApplyingProject;
    private bool _isDirty;
    private string? _currentProjectPath;
    private string _audioStatus = "WAV 음원을 불러오세요";
    private WaveOutEvent? _audioOutput;
    private AudioFileReader? _audioFileReader;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
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
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedTrack));
        }
    }

    public bool HasSelectedTrack => SelectedTrack is not null;

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
            OnPropertyChanged(nameof(CanPlaySelectedAudio));
            StopPlayback("대기 중");
            AudioStatus = value?.AudioStatus ?? "오디오 트랙을 선택하세요";
        }
    }

    public bool HasSelectedAudioTrack => SelectedAudioTrack is not null;

    public bool CanPlaySelectedAudio
    {
        get
        {
            if (SelectedAudioTrack?.AudioFilePath is not string audioFilePath || !File.Exists(audioFilePath) || SelectedAudioTrack.IsMuted)
            {
                return false;
            }

            return !AudioTracks.Any(track => track.IsSolo) || SelectedAudioTrack.IsSolo;
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
        ReleaseAudio(stopOutput: true);
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

        _audioFileReader?.Dispose();
        _audioFileReader = null;
    }

    private void AudioOutput_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _audioOutput))
            {
                return;
            }

            ReleaseAudio(stopOutput: false);
            AudioStatus = e.Exception is null ? "재생 완료" : $"재생 오류: {e.Exception.Message}";
        });
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
                    IsSolo = track.IsSolo
                }).ToList(),
                AudioTracks = AudioTracks.Select(track => new AudioTrackDocument
                {
                    Name = track.Name,
                    AudioAssetPath = PrepareAudioAsset(track, path),
                    DurationSeconds = track.Duration.TotalSeconds,
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
            MasterReferencePitch = document.MasterReferencePitch;
            MasterFineTuneCents = document.MasterFineTuneCents;
            ResetInstrumentRoles(document.InstrumentRoles ?? []);
            SelectedTrack = null;
            SelectedAudioTrack = null;
            InstrumentTracks.Clear();
            AudioTracks.Clear();

            foreach (var trackDocument in document.Tracks)
            {
                InstrumentTracks.Add(new InstrumentTrack(trackDocument.Name, RestoreInstrumentType(trackDocument), trackDocument.Role ?? string.Empty)
                {
                    Volume = trackDocument.Volume,
                    IsMuted = trackDocument.IsMuted,
                    IsSolo = trackDocument.IsSolo
                });

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
                    Volume = audioTrackDocument.Volume,
                    IsMuted = audioTrackDocument.IsMuted,
                    IsSolo = audioTrackDocument.IsSolo
                });
            }

            SelectedTrack = InstrumentTracks.FirstOrDefault();
            SelectedAudioTrack = AudioTracks.FirstOrDefault();
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

    private void PlaySelectedAudio_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAudioTrack?.AudioFilePath is not string audioFilePath || !File.Exists(audioFilePath))
        {
            AudioStatus = "재생할 오디오 트랙을 선택하세요";
            return;
        }

        if (!CanPlaySelectedAudio)
        {
            AudioStatus = "현재 악기는 음소거 또는 솔로 제외 상태입니다";
            return;
        }

        StopPlayback("준비 중");
        try
        {
            ValidateWaveFile(audioFilePath);
            _audioFileReader = new AudioFileReader(audioFilePath)
            {
                Volume = (float)SelectedAudioTrack.Volume
            };
            _audioOutput = new WaveOutEvent();
            _audioOutput.PlaybackStopped += AudioOutput_PlaybackStopped;
            _audioOutput.Init(_audioFileReader);
            _audioOutput.Play();
            AudioStatus = $"재생 중: {SelectedAudioTrack.AudioFileName}";
        }
        catch (Exception exception)
        {
            ReleaseAudio(stopOutput: true);
            AudioStatus = "재생할 수 없습니다";
            MessageBox.Show(this, $"WAV 재생에 실패했습니다.\n{exception.Message}", "오디오 재생 오류", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void InstrumentTracks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (InstrumentTrack track in e.OldItems)
            {
                track.PropertyChanged -= InstrumentTrack_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (InstrumentTrack track in e.NewItems)
            {
                track.PropertyChanged += InstrumentTrack_PropertyChanged;
            }
        }

        MarkDirty();
        OnPropertyChanged(nameof(CanPlaySelectedAudio));
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
        OnPropertyChanged(nameof(CanPlaySelectedAudio));
    }

    private void InstrumentTrack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        if (e.PropertyName == nameof(InstrumentTrack.Role) && sender is InstrumentTrack track)
        {
            RegisterInstrumentRole(track.Role);
        }
    }

    private void AudioTrack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        OnPropertyChanged(nameof(CanPlaySelectedAudio));

        if (sender is not AudioTrack track || !ReferenceEquals(track, SelectedAudioTrack))
        {
            return;
        }

        if (e.PropertyName == nameof(AudioTrack.AudioFilePath))
        {
            AudioStatus = track.AudioStatus;
        }

        if (_audioFileReader is null)
        {
            return;
        }

        if (track.IsMuted || (AudioTracks.Any(item => item.IsSolo) && !track.IsSolo))
        {
            StopPlayback("믹서 설정으로 정지됨");
        }
        else if (e.PropertyName == nameof(AudioTrack.Volume))
        {
            _audioFileReader.Volume = (float)track.Volume;
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