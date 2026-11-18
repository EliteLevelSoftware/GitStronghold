using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitStronghold.App.Services;
using GitStronghold.Core;
using GitStronghold.Core.Backup;
using GitStronghold.Core.GitHub;
using GitStronghold.Core.Paths;
using GitStronghold.Core.Security;

namespace GitStronghold.App.ViewModels;

public partial class ProfileRow : ObservableObject
{
    public string Login { get; init; } = "";

    public string Title { get; init; } = "";

    public string Meta { get; init; } = "";

    public string AvatarPath { get; init; } = "";
}

public partial class RepoRow
{
    public string Owner { get; init; } = "";

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public string Meta { get; init; } = "";

    public string HtmlUrl { get; init; } = "";
}

public partial class FileRow : ObservableObject
{
    public string Name { get; init; } = "";

    public string Path { get; init; } = "";

    public string Meta { get; init; } = "";

    public IndexedFile Source { get; init; } = new();

    [ObservableProperty]
    private bool _isSelected;
}

public partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private bool _loading = true;
    private CancellationTokenSource? _backup;

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        Product = AppVersion.Product;
        ProductVersion = AppVersion.ProductVersion;
        Credit = AppVersion.Credit;
        Copyright = AppVersion.Copyright;
        VersionText = AppVersion.Current;
        VaultPath = _settings.VaultPath;
        Incremental = _settings.Incremental;
        Notifications = _settings.Notifications;
        ReduceEffects = _settings.ReduceEffects;
        CheckUpdatesOnStartup = _settings.CheckUpdatesOnStartup;
        UpdateSource = _settings.UpdateSource;
        UpdateChannel = _settings.UpdateChannel;
        TokenStatus = string.IsNullOrEmpty(TokenStore.Load()) ? "No token saved" : "Token saved in the system secret store";
        _loading = false;
        Reload();
        if (CheckUpdatesOnStartup)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    public string Product { get; }

    public string ProductName => Product;

    public string VersionLabel => "v" + VersionText;

    public bool ShowCredit { get; } = true;

    public string ProductVersion { get; }

    public string Credit { get; }

    public string Copyright { get; }

    public string VersionText { get; }

    public string LicenseName { get; } = "MIT License";

    public Func<Task<string?>>? PickFolder { get; set; }

    public ObservableCollection<ProfileRow> Profiles { get; } = [];

    public ObservableCollection<RepoRow> Repositories { get; } = [];

    public ObservableCollection<FileRow> Files { get; } = [];

    public ObservableCollection<string> ProfileNames { get; } = [];

    public ObservableCollection<string> RepoNames { get; } = [];

    public ObservableCollection<string> BranchNames { get; } = [];

    public ObservableCollection<string> LogNames { get; } = [];

    [ObservableProperty]
    private string _page = "dashboard";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _target = "";

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _phase = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private int _saved;

    [ObservableProperty]
    private int _failed;

    [ObservableProperty]
    private int _skipped;

    [ObservableProperty]
    private string _backupMessage = "";

    [ObservableProperty]
    private string _vaultPath = "";

    [ObservableProperty]
    private bool _incremental = true;

    [ObservableProperty]
    private bool _notifications = true;

    [ObservableProperty]
    private bool _reduceEffects;

    [ObservableProperty]
    private bool _checkUpdatesOnStartup = true;

    [ObservableProperty]
    private string _updateSource = "";

    [ObservableProperty]
    private string _updateChannel = "stable";

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private string _tokenDraft = "";

    [ObservableProperty]
    private string _tokenStatus = "";

    [ObservableProperty]
    private string _tokenMessage = "";

    [ObservableProperty]
    private bool _showAbout;

    [ObservableProperty]
    private bool _showDelete;

    [ObservableProperty]
    private string _pendingDelete = "";

    [ObservableProperty]
    private bool _hasNotification;

    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private string? _selectedRepo;

    [ObservableProperty]
    private string? _selectedBranch;

    [ObservableProperty]
    private string _codeText = "";

    [ObservableProperty]
    private string _markdownText = "";

    [ObservableProperty]
    private string _viewerDirectory = "";

    [ObservableProperty]
    private string _imagePath = "";

    [ObservableProperty]
    private string _viewerKind = "empty";

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private string _heroTitle = "Nothing backed up";

    [ObservableProperty]
    private string _heroMeta = "The vault is empty";

    [ObservableProperty]
    private string _dashboardStats = "No snapshot yet";

    [ObservableProperty]
    private int _profileCount;

    [ObservableProperty]
    private int _repositoryCount;

    public bool IsDashboard => Page == "dashboard";

    public bool IsBackup => Page == "backup";

    public bool IsProfiles => Page == "profiles";

    public bool IsRepositories => Page == "repositories";

    public bool IsBrowse => Page == "browse";

    public bool IsLogs => Page == "logs";

    public bool IsSettings => Page == "settings";

    public bool HasProfiles => ProfileCount > 0;

    public bool ShowEmpty => !HasProfiles && !Busy;

    public bool HasProgress => Phase.Length > 0;

    public string CountsText => "Saved " + Saved + "  ·  Failed " + Failed + "  ·  Skipped " + Skipped;

    public bool ShowCode => ViewerKind == "code";

    public bool ShowMarkdown => ViewerKind == "markdown";

    public bool ShowImage => ViewerKind == "image";

    public bool ShowBinary => ViewerKind == "binary";

    public bool ShowViewerEmpty => ViewerKind == "empty";

    partial void OnPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsDashboard));
        OnPropertyChanged(nameof(IsBackup));
        OnPropertyChanged(nameof(IsProfiles));
        OnPropertyChanged(nameof(IsRepositories));
        OnPropertyChanged(nameof(IsBrowse));
        OnPropertyChanged(nameof(IsLogs));
        OnPropertyChanged(nameof(IsSettings));
        if (value == "logs")
        {
            LoadLogs();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnPhaseChanged(string value) => OnPropertyChanged(nameof(HasProgress));

    partial void OnSavedChanged(int value) => OnPropertyChanged(nameof(CountsText));

    partial void OnFailedChanged(int value) => OnPropertyChanged(nameof(CountsText));

    partial void OnSkippedChanged(int value) => OnPropertyChanged(nameof(CountsText));

    partial void OnProfileCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnViewerKindChanged(string value)
    {
        OnPropertyChanged(nameof(ShowCode));
        OnPropertyChanged(nameof(ShowMarkdown));
        OnPropertyChanged(nameof(ShowImage));
        OnPropertyChanged(nameof(ShowBinary));
        OnPropertyChanged(nameof(ShowViewerEmpty));
    }

    partial void OnIncrementalChanged(bool value) => Persist(false);

    partial void OnNotificationsChanged(bool value) => Persist(false);

    partial void OnReduceEffectsChanged(bool value) => Persist(false);

    partial void OnCheckUpdatesOnStartupChanged(bool value) => Persist(false);

    partial void OnUpdateSourceChanged(string value) => Persist(false);

    partial void OnUpdateChannelChanged(string value) => Persist(false);

    partial void OnSelectedProfileChanged(string? value) => LoadRepoNames();

    partial void OnSelectedRepoChanged(string? value) => LoadBranchNames();

    partial void OnSelectedBranchChanged(string? value) => LoadFiles();

    [RelayCommand]
    private void ShowPage(string? page)
    {
        if (!string.IsNullOrWhiteSpace(page))
        {
            Page = page;
        }
    }

    [RelayCommand]
    private void OpenAbout() => ShowAbout = true;

    [RelayCommand]
    private void CloseAbout() => ShowAbout = false;

    [RelayCommand]
    private void DismissNotice() => HasNotification = false;

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (Busy)
        {
            return;
        }

        Persist(false);
        if (!ParsedTarget.TryParse(Target, out _, out var error))
        {
            BackupMessage = error;
            Phase = "Could not start";
            return;
        }

        Busy = true;
        BackupMessage = "";
        Phase = "Starting";
        Detail = Target.Trim();
        _backup = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(report =>
        {
            Phase = report.Phase;
            Detail = report.Detail;
            Saved = report.Saved;
            Failed = report.Failed;
            Skipped = report.Skipped;
        });
        try
        {
            var token = TokenStore.Load();
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            };
            using var engine = new BackupEngine(handler, token);
            var result = await Task.Run(() => engine.RunAsync(new BackupRequest
            {
                Target = Target.Trim(),
                VaultRoot = VaultPath,
                Incremental = Incremental
            }, progress, _backup.Token));
            Saved = result.Saved;
            Failed = result.Failed;
            Skipped = result.Skipped;
            Phase = result.Cancelled ? "Cancelled" : result.Succeeded ? "Backup complete" : "Backup finished with failures";
            Detail = CountsText;
            BackupMessage = result.Errors.FirstOrDefault() ?? "";
            if (Notifications && !result.Cancelled)
            {
                Notice = Product + " saved " + result.Saved + " files" + (result.Failed > 0 ? " with " + result.Failed + " failures" : "") + ".";
                HasNotification = true;
                SystemLauncher.Notify(Product, Notice);
            }
        }
        catch (OperationCanceledException)
        {
            Phase = "Cancelled";
        }
        catch (Exception ex)
        {
            Phase = "Backup failed";
            BackupMessage = ex.Message;
        }
        finally
        {
            Busy = false;
            _backup.Dispose();
            _backup = null;
            Reload();
        }
    }

    [RelayCommand]
    private void CancelBackup() => _backup?.Cancel();

    [RelayCommand]
    private async Task BrowseVaultAsync()
    {
        if (PickFolder is null)
        {
            return;
        }

        var path = await PickFolder();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        VaultPath = path;
        Persist(true);
    }

    [RelayCommand]
    private void SaveToken()
    {
        if (!TokenStore.TrySave(TokenDraft, out var error))
        {
            TokenMessage = error;
            return;
        }

        TokenDraft = "";
        TokenMessage = "";
        TokenStatus = "Token saved in the system secret store";
    }

    [RelayCommand]
    private void ClearToken()
    {
        TokenStore.Clear();
        TokenDraft = "";
        TokenStatus = "No token saved";
        TokenMessage = "";
    }

    [RelayCommand]
    private void CreateToken() => SystemLauncher.OpenHttp(UpdateFeed.FineGrainedTokenUrl);

    [RelayCommand]
    private void OpenGitHub() => SystemLauncher.OpenHttp(UpdateFeed.RepositoryUrl(UpdateSource));

    [RelayCommand]
    private async Task CheckForUpdatesAsync() => UpdateStatus = await UpdateFeed.CheckAsync(CurrentSettings(), CancellationToken.None);

    [RelayCommand]
    private void SelectProfile(string? login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return;
        }

        SelectedProfile = login;
        Page = "browse";
    }

    [RelayCommand]
    private void AskDelete(string? login)
    {
        PendingDelete = login ?? "";
        ShowDelete = PendingDelete.Length > 0;
    }

    [RelayCommand]
    private void CancelDelete() => ShowDelete = false;

    [RelayCommand]
    private void ConfirmDelete()
    {
        try
        {
            SnapshotCatalog.DeleteProfile(VaultPath, PendingDelete);
        }
        catch (VaultException ex)
        {
            BackupMessage = ex.Message;
        }

        ShowDelete = false;
        Reload();
    }

    [RelayCommand]
    private void OpenRepository(RepoRow? repo)
    {
        if (repo is null)
        {
            return;
        }

        SelectedProfile = repo.Owner;
        SelectedRepo = repo.Name;
        Page = "browse";
    }

    [RelayCommand]
    private void OpenRepositorySite(string? url) => SystemLauncher.OpenHttp(url);

    [RelayCommand]
    private void OpenFile(FileRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(SelectedProfile) || string.IsNullOrWhiteSpace(SelectedRepo) || string.IsNullOrWhiteSpace(SelectedBranch))
        {
            return;
        }

        foreach (var file in Files)
        {
            file.IsSelected = ReferenceEquals(file, row);
        }

        var folder = BranchNames.Count == 0 ? "" : SelectedBranch;
        var branch = SnapshotCatalog.Branches(VaultPath, SelectedProfile, SelectedRepo).FirstOrDefault(item => item.Name == folder || item.Folder == folder);
        if (branch is null)
        {
            return;
        }

        var full = SnapshotCatalog.ResolveFile(VaultPath, SelectedProfile, SelectedRepo, branch.Folder, row.Source);
        if (full is null)
        {
            ViewerKind = "binary";
            CodeText = "This entry has no file in the vault.";
            return;
        }

        var extension = Path.GetExtension(full);
        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
        {
            ImagePath = full;
            ViewerKind = "image";
            return;
        }

        var text = SnapshotCatalog.ReadText(VaultPath, full);
        if (text.StartsWith("Binary file", StringComparison.Ordinal))
        {
            CodeText = text;
            ViewerKind = "binary";
            return;
        }

        if (extension is ".md" or ".markdown" or ".mdown")
        {
            MarkdownText = text;
            ViewerDirectory = Path.GetDirectoryName(full) ?? "";
            ViewerKind = "markdown";
            return;
        }

        CodeText = text;
        ViewerKind = "code";
    }

    [RelayCommand]
    private void OpenFileOutside(FileRow? row)
    {
        OpenFile(row);
        if (ShowImage && ImagePath.Length > 0)
        {
            SystemLauncher.OpenVaultFile(VaultPath, ImagePath);
        }
    }

    public void OpenRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(SelectedProfile) || string.IsNullOrWhiteSpace(SelectedRepo) || string.IsNullOrWhiteSpace(SelectedBranch))
        {
            return;
        }

        var match = Files.FirstOrDefault(file =>
        {
            var branch = SnapshotCatalog.Branches(VaultPath, SelectedProfile, SelectedRepo).FirstOrDefault(item => item.Name == SelectedBranch || item.Folder == SelectedBranch);
            if (branch is null)
            {
                return false;
            }

            var full = SnapshotCatalog.ResolveFile(VaultPath, SelectedProfile, SelectedRepo, branch.Folder, file.Source);
            return string.Equals(full, path, StringComparison.OrdinalIgnoreCase);
        });
        if (match != null)
        {
            OpenFile(match);
        }
    }

    [RelayCommand]
    private void ShowLog(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var log = SnapshotCatalog.Logs(VaultPath).FirstOrDefault(item => item.Name == name);
        LogText = log?.Text ?? "";
    }

    private void Reload()
    {
        Profiles.Clear();
        Repositories.Clear();
        ProfileSummary? latest = null;
        try
        {
            foreach (var profile in SnapshotCatalog.Profiles(VaultPath))
            {
                latest ??= profile;
                if (SearchText.Length > 0 && !profile.Login.Contains(SearchText, StringComparison.OrdinalIgnoreCase) && !profile.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var when = profile.BackedUpAt;
                if (DateTimeOffset.TryParse(profile.BackedUpAt, out var parsed))
                {
                    when = parsed.ToLocalTime().ToString("d MMM yyyy, HH:mm");
                }

                Profiles.Add(new ProfileRow
                {
                    Login = profile.Login,
                    Title = profile.Name.Length > 0 ? profile.Name : profile.Login,
                    Meta = profile.RepoCount + " repositories · " + when,
                    AvatarPath = profile.AvatarPath
                });
            }

            foreach (var repo in SnapshotCatalog.Repos(VaultPath, null))
            {
                if (SearchText.Length > 0 && !repo.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) && !repo.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Repositories.Add(new RepoRow
                {
                    Owner = repo.Owner,
                    Name = repo.Name,
                    Description = repo.Description,
                    Meta = (repo.Private ? "Private" : "Public") + " · " + repo.Language + " · " + repo.Stars + " stars",
                    HtmlUrl = repo.HtmlUrl
                });
            }
        }
        catch (VaultException ex)
        {
            BackupMessage = ex.Message;
        }

        ProfileCount = Profiles.Count;
        RepositoryCount = Repositories.Count;
        var first = Profiles.FirstOrDefault();
        HeroTitle = first is null ? "Nothing backed up" : first.Title;
        HeroMeta = first is null ? "Back up a GitHub user, organization, or repository." : first.Login + " · " + first.Meta;
        DashboardStats = latest is null
            ? "No snapshot yet"
            : "Saved " + latest.Saved + "  ·  Failed " + latest.Failed + "  ·  Skipped " + latest.Skipped;
        ProfileNames.Clear();
        foreach (var profile in Profiles)
        {
            ProfileNames.Add(profile.Login);
        }

        if (SelectedProfile is null || !ProfileNames.Contains(SelectedProfile))
        {
            SelectedProfile = ProfileNames.FirstOrDefault();
        }
        else
        {
            LoadRepoNames();
        }
    }

    private void ApplySearch() => Reload();

    private void LoadRepoNames()
    {
        RepoNames.Clear();
        if (string.IsNullOrWhiteSpace(SelectedProfile))
        {
            return;
        }

        foreach (var repo in SnapshotCatalog.Repos(VaultPath, SelectedProfile))
        {
            RepoNames.Add(repo.Name);
        }

        if (SelectedRepo is null || !RepoNames.Contains(SelectedRepo))
        {
            SelectedRepo = RepoNames.FirstOrDefault();
        }
        else
        {
            LoadBranchNames();
        }
    }

    private void LoadBranchNames()
    {
        BranchNames.Clear();
        Files.Clear();
        if (string.IsNullOrWhiteSpace(SelectedProfile) || string.IsNullOrWhiteSpace(SelectedRepo))
        {
            return;
        }

        foreach (var branch in SnapshotCatalog.Branches(VaultPath, SelectedProfile, SelectedRepo))
        {
            BranchNames.Add(branch.Name);
        }

        if (SelectedBranch is null || !BranchNames.Contains(SelectedBranch))
        {
            SelectedBranch = BranchNames.FirstOrDefault();
        }
        else
        {
            LoadFiles();
        }
    }

    private void LoadFiles()
    {
        Files.Clear();
        ViewerKind = "empty";
        if (string.IsNullOrWhiteSpace(SelectedProfile) || string.IsNullOrWhiteSpace(SelectedRepo) || string.IsNullOrWhiteSpace(SelectedBranch))
        {
            return;
        }

        var branch = SnapshotCatalog.Branches(VaultPath, SelectedProfile, SelectedRepo).FirstOrDefault(item => item.Name == SelectedBranch);
        if (branch is null)
        {
            return;
        }

        foreach (var file in SnapshotCatalog.Files(VaultPath, SelectedProfile, SelectedRepo, branch.Folder))
        {
            if (file.Path.Length == 0)
            {
                continue;
            }

            if (SearchText.Length > 0 && Page == "browse" && !file.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Files.Add(new FileRow
            {
                Name = file.Path,
                Path = file.Path,
                Meta = file.Mode.Length == 0 ? "submodule" : file.Bytes + " bytes",
                Source = file
            });
        }

        var readme = Files.FirstOrDefault(file => file.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) ?? Files.FirstOrDefault();
        if (readme != null)
        {
            OpenFile(readme);
        }
    }

    private void LoadLogs()
    {
        LogNames.Clear();
        foreach (var log in SnapshotCatalog.Logs(VaultPath))
        {
            LogNames.Add(log.Name);
        }

        LogText = SnapshotCatalog.Logs(VaultPath).FirstOrDefault()?.Text ?? "No backups have been logged yet.";
    }

    private void Persist(bool reload)
    {
        if (_loading)
        {
            return;
        }

        _settings.VaultPath = VaultPath;
        _settings.Incremental = Incremental;
        _settings.Notifications = Notifications;
        _settings.ReduceEffects = ReduceEffects;
        _settings.CheckUpdatesOnStartup = CheckUpdatesOnStartup;
        _settings.UpdateSource = UpdateSource;
        _settings.UpdateChannel = UpdateChannel;
        SettingsStore.Save(_settings);
        if (reload)
        {
            Reload();
        }
    }

    private AppSettings CurrentSettings() => new()
    {
        VaultPath = VaultPath,
        Incremental = Incremental,
        Notifications = Notifications,
        ReduceEffects = ReduceEffects,
        CheckUpdatesOnStartup = CheckUpdatesOnStartup,
        UpdateSource = UpdateSource,
        UpdateChannel = UpdateChannel
    };
}
