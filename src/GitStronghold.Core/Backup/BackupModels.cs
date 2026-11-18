namespace GitStronghold.Core.Backup;

public sealed record BackupRequest
{
    public required string Target { get; init; }

    public required string VaultRoot { get; init; }

    public bool Incremental { get; init; } = true;
}

public sealed record BackupProgress(string Phase, string Detail, int Saved, int Failed, int Skipped);

public sealed class BackupResult
{
    public bool Succeeded { get; init; }

    public bool Cancelled { get; init; }

    public string Owner { get; init; } = "";

    public string? Repository { get; init; }

    public int Saved { get; init; }

    public int Failed { get; init; }

    public int Skipped { get; init; }

    public int RepoCount { get; init; }

    public int GistCount { get; init; }

    public string? LogFile { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed class IndexedFile
{
    public string Path { get; set; } = "";

    public string Disk { get; set; } = "";

    public string Sha { get; set; } = "";

    public long Bytes { get; set; }

    public string Mode { get; set; } = "";
}

public sealed class FileIndex
{
    public List<IndexedFile> Files { get; set; } = [];
}

public sealed class ProfileSummary
{
    public string Login { get; init; } = "";

    public string Name { get; init; } = "";

    public string Kind { get; init; } = "";

    public string BackedUpAt { get; init; } = "";

    public int Saved { get; init; }

    public int Failed { get; init; }

    public int Skipped { get; init; }

    public int RepoCount { get; init; }

    public int GistCount { get; init; }

    public string Target { get; init; } = "";

    public string AvatarPath { get; init; } = "";
}

public sealed class RepoSummary
{
    public string Owner { get; init; } = "";

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public string DefaultBranch { get; init; } = "";

    public string Language { get; init; } = "";

    public bool Private { get; init; }

    public int Stars { get; init; }

    public int Saved { get; init; }

    public int Failed { get; init; }

    public int Skipped { get; init; }

    public string HtmlUrl { get; init; } = "";
}

public sealed class BranchSummary
{
    public string Name { get; init; } = "";

    public string Sha { get; init; } = "";

    public string Folder { get; init; } = "";
}

public sealed class LogDocument
{
    public string Name { get; init; } = "";

    public string Path { get; init; } = "";

    public string Text { get; init; } = "";
}
