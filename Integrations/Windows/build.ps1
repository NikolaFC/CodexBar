#Requires -Version 5.1
<#
.SYNOPSIS
    Builds, tests and packages the CodexBar Windows tray app.

.DESCRIPTION
    Restores and builds every project under src/, runs the custom checks in
    tests/, then publishes the WPF app as a self-contained single-file win-x64
    portable build into artifacts/<rid>, copies README.md, LICENSE and
    THIRD_PARTY_NOTICES.md into it and zips it. A whitelisted source archive is
    written next to it (artifacts/CodexBar-source-<version>.zip).

    The script uses whichever dotnet it finds in this order: -DotNetPath,
    $env:DOTNET_ROOT, <repo>\.dotnet, a work\dotnet folder up to three levels
    above the repository, and finally dotnet from PATH.

    Every directory this script deletes is validated first. It must be a
    directory strictly inside the repository (or an output directory that was
    explicitly approved), it may not be the repository root, src/, tests/ or
    any directory that contains them, and an existing non-empty output
    directory must carry the marker file written by an earlier run.

.PARAMETER DotNetPath
    Path to dotnet.exe, or to a directory containing it.

.PARAMETER OutputDirectory
    Publish output. Defaults to artifacts\<RuntimeIdentifier>. A custom path
    must be a directory strictly inside the repository unless
    -AllowOutsideRepositoryOutput is given.

.PARAMETER Version
    Version used in the archive names. Defaults to <Version> in the app
    project, then to 0.1.0.

.PARAMETER AllowOutsideRepositoryOutput
    Approve an -OutputDirectory that is outside the repository. Off by default.

.PARAMETER SkipSourceArchive
    Do not write the whitelisted source archive.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -DotNetPath C:\dotnet\dotnet.exe -NoPackage
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $RuntimeIdentifier = 'win-x64',

    [string] $DotNetPath,

    [string] $OutputDirectory,

    [string] $Version,

    [switch] $AllowOutsideRepositoryOutput,

    [switch] $NoRestore,

    [switch] $NoTests,

    [switch] $NoPublish,

    [switch] $NoPackage,

    [switch] $SkipSourceArchive,

    [switch] $Clean,

    [switch] $SkipSdkCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:DefaultVersion = '0.1.0'
$script:OutputMarkerName = '.codexbar-output.json'
$script:Root = $PSScriptRoot
$script:DotNetExe = $null
$script:SourceProjects = @()
$script:TestProjects = @()
$script:SolutionFile = $null
$artifactsRoot = Join-Path $script:Root 'artifacts'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $artifactsRoot $RuntimeIdentifier
}

# Keep the CLI from doing unrelated first-run work on a fresh machine or a CI
# image: no telemetry, no first-time banner, no PATH edits and no dev
# certificate. Only set when the caller has not chosen a value.
$script:DotNetDefaults = @{
    DOTNET_CLI_TELEMETRY_OPTOUT         = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE   = '1'
    DOTNET_NOLOGO                       = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH     = '0'
    DOTNET_GENERATE_ASPNET_CERTIFICATE  = 'false'
}
foreach ($name in $script:DotNetDefaults.Keys) {
    if (-not [Environment]::GetEnvironmentVariable($name, 'Process')) {
        [Environment]::SetEnvironmentVariable($name, $script:DotNetDefaults[$name], 'Process')
    }
}

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-CanonicalPath {
    param([Parameter(Mandatory)][string] $Path)

    # Relative paths are anchored at the repository root, so the script behaves
    # the same whether it is called from the repository or from anywhere else.
    # Trailing separators are dropped so 'C:\repo\artifacts\' and
    # 'C:\repo\artifacts' compare as the same place; drive roots keep theirs.
    $candidate = $Path
    if (-not [System.IO.Path]::IsPathRooted($candidate) -and $script:Root) {
        $candidate = Join-Path $script:Root $candidate
    }
    $full = [System.IO.Path]::GetFullPath($candidate)
    $volumeRoot = [System.IO.Path]::GetPathRoot($full)
    if ($volumeRoot -and $full.Length -gt $volumeRoot.Length) {
        $full = $full.TrimEnd([char[]] @('\', '/'))
    }
    return $full
}

function Get-DescendantPrefix {
    param([Parameter(Mandatory)][string] $CanonicalPath)

    if ($CanonicalPath.EndsWith([System.IO.Path]::DirectorySeparatorChar) -or $CanonicalPath.EndsWith('/')) {
        return $CanonicalPath
    }
    return $CanonicalPath + [System.IO.Path]::DirectorySeparatorChar
}

function Test-IsStrictDescendant {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Root
    )

    # True only for paths *below* the root: the boundary separator matters, so
    # 'C:\repo-other' is not treated as being inside 'C:\repo'.
    $candidate = Get-CanonicalPath $Path
    $parent = Get-CanonicalPath $Root
    if ($candidate -ieq $parent) { return $false }
    return $candidate.StartsWith((Get-DescendantPrefix $parent), [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-IsAtOrAbove {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Descendant
    )

    # True when $Path is $Descendant itself or one of its ancestors.
    $candidate = Get-CanonicalPath $Path
    $target = Get-CanonicalPath $Descendant
    if ($candidate -ieq $target) { return $true }
    return $target.StartsWith((Get-DescendantPrefix $candidate), [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-ProtectedDirectories {
    # Places that must never be deleted or used as a publish destination: the
    # repository itself, its VCS metadata, and src/ plus tests/ together with
    # every project directory and every parent of those inside the repository.
    $protected = New-Object System.Collections.Generic.List[string]
    $protected.Add((Get-CanonicalPath $script:Root))
    $protected.Add((Get-CanonicalPath (Join-Path $script:Root '.git')))
    $protected.Add((Get-CanonicalPath (Join-Path $script:Root 'src')))
    $protected.Add((Get-CanonicalPath (Join-Path $script:Root 'tests')))

    foreach ($project in @($script:SourceProjects) + @($script:TestProjects)) {
        $directory = Get-CanonicalPath (Split-Path -Parent $project.FullName)
        while ($directory -and (Test-IsAtOrAbove -Path $script:Root -Descendant $directory)) {
            $protected.Add($directory)
            $parent = Split-Path -Parent $directory
            if (-not $parent -or $parent -ieq $directory) { break }
            $directory = Get-CanonicalPath $parent
        }
    }
    return $protected
}

function Write-OutputMarker {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $Purpose
    )

    $sdk = ''
    if ($script:DotNetExe) { $sdk = $script:DotNetExe }
    $marker = [pscustomobject]@{
        tool       = 'CodexBar.Windows build.ps1'
        purpose    = $Purpose
        repository = Get-CanonicalPath $script:Root
        dotnet     = $sdk
        writtenUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $marker | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $Directory $script:OutputMarkerName) -Encoding utf8
}

function Assert-OutputDirectorySafe {
    param(
        [Parameter(Mandatory)][string] $Path,
        [switch] $AllowOutsideRepository
    )

    $full = Get-CanonicalPath $Path
    $root = Get-CanonicalPath $script:Root

    if ($full -ieq $root) {
        throw "Refusing to use the repository root '$full' as an output directory."
    }

    foreach ($protected in Get-ProtectedDirectories) {
        if (Test-IsAtOrAbove -Path $full -Descendant $protected) {
            throw "Refusing to use '$full' as an output directory: it is '$protected' or an ancestor that contains source, tests or VCS metadata."
        }
    }

    $gitDirectory = Get-CanonicalPath (Join-Path $root '.git')
    if (Test-IsStrictDescendant -Path $full -Root $gitDirectory) {
        throw "Refusing to use '$full' as an output directory: it is inside '$gitDirectory'."
    }
    if (Test-Path -LiteralPath (Join-Path $full '.git')) {
        throw "Refusing to use '$full' as an output directory: it contains a .git directory."
    }

    $insideRepository = Test-IsStrictDescendant -Path $full -Root $root
    if (-not $insideRepository -and -not $AllowOutsideRepository) {
        throw "Refusing to use '$full' as an output directory: it is outside the repository '$root'. Pass -AllowOutsideRepositoryOutput to approve it."
    }

    # Only clear a directory we can recognise as our own output: an existing
    # non-empty directory has to carry the marker written by an earlier run.
    if (Test-Path -LiteralPath $full -PathType Container) {
        $entries = @(Get-ChildItem -LiteralPath $full -Force -ErrorAction SilentlyContinue)
        if ($entries.Count -gt 0) {
            $marker = Join-Path $full $script:OutputMarkerName
            if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
                throw "Refusing to clear '$full': it is not empty and has no $($script:OutputMarkerName) marker from an earlier build. Delete it manually or choose another -OutputDirectory."
            }
        }
    }
}

function Resolve-DotNetExecutable {
    param([string] $Explicit)

    $candidates = New-Object System.Collections.Generic.List[string]

    if ($Explicit) {
        $item = Get-Item -LiteralPath $Explicit -ErrorAction SilentlyContinue
        if (-not $item) { throw "-DotNetPath '$Explicit' does not exist." }
        if ($item.PSIsContainer) { $candidates.Add((Join-Path $item.FullName 'dotnet.exe')) }
        else { $candidates.Add($item.FullName) }
    }

    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    $candidates.Add((Join-Path $script:Root '.dotnet\dotnet.exe'))

    # Also look for a workspace-local SDK a few levels up, so the same script
    # works whether the repository is the workspace root or nested below it.
    $ancestor = Get-Item -LiteralPath $script:Root
    for ($level = 0; $level -lt 3 -and $ancestor; $level++) {
        $candidates.Add((Join-Path $ancestor.FullName 'work\dotnet\dotnet.exe'))
        $ancestor = $ancestor.Parent
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if (-not $command) { $command = Get-Command 'dotnet' -ErrorAction SilentlyContinue }
    if ($command) { return $command.Source }

    throw 'No dotnet executable found. Pass -DotNetPath, set DOTNET_ROOT, or install the .NET SDK.'
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    Write-Host "    dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $script:DotNetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Reset-Directory {
    param(
        [Parameter(Mandatory)][string] $Path,
        [string] $Purpose = 'output'
    )

    # The safety rules live in Assert-OutputDirectorySafe: strict descendant of
    # the repository (or explicitly approved), never the repository root, src/,
    # tests/ or anything containing them, and never an unrecognised directory.
    Assert-OutputDirectorySafe -Path $Path -AllowOutsideRepository:$AllowOutsideRepositoryOutput
    $full = Get-CanonicalPath $Path

    if (Test-Path -LiteralPath $full) {
        try {
            Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
        }
        catch {
            throw "Cannot clear '$full'. Close any process using it (for example a running CodexBar.Windows.exe) and try again. $($_.Exception.Message)"
        }
    }
    New-Item -ItemType Directory -Force -Path $full | Out-Null
    Write-OutputMarker -Directory $full -Purpose $Purpose
}

function Remove-BuildOutputDirectory {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ProjectDirectory
    )

    $full = Get-CanonicalPath $Path
    $project = Get-CanonicalPath $ProjectDirectory
    $root = Get-CanonicalPath $script:Root
    $name = Split-Path -Leaf $full

    if ($name -notin @('bin', 'obj')) {
        throw "Refusing to clean '$full': only a 'bin' or 'obj' directory may be cleared here."
    }
    if (-not (Test-IsStrictDescendant -Path $full -Root $project)) {
        throw "Refusing to clean '$full': it is not inside the project directory '$project'."
    }
    if (-not (Test-IsStrictDescendant -Path $full -Root $root)) {
        throw "Refusing to clean '$full': it is outside the repository '$root'."
    }
    $sourceRoot = Join-Path $root 'src'
    $testRoot = Join-Path $root 'tests'
    $atSourceRoot = Test-IsAtOrAbove -Path $full -Descendant $sourceRoot
    $atTestRoot = Test-IsAtOrAbove -Path $full -Descendant $testRoot
    if ($atSourceRoot -or $atTestRoot) {
        throw "Refusing to clean '$full': it is the source or test root."
    }

    if (Test-Path -LiteralPath $full) {
        try {
            Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
        }
        catch {
            throw "Cannot clean '$full'. Close any process using it and try again. $($_.Exception.Message)"
        }
    }
}

function Test-WritableDirectory {
    param([Parameter(Mandatory)][string] $Path)

    $probe = Join-Path $Path ('.codexbar-write-probe-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Force -Path $probe -ErrorAction Stop | Out-Null
    }
    catch {
        return $false
    }
    Remove-Item -LiteralPath $probe -Recurse -Force -ErrorAction SilentlyContinue
    return $true
}

function Initialize-DotNetEnvironment {
    param([Parameter(Mandatory)][string] $StateRoot)

    # dotnet keeps CLI state under %USERPROFILE%\.dotnet and NuGet keeps its user
    # config under %APPDATA%\NuGet. Restricted environments (sandboxes, locked
    # build images) make those read-only, which aborts restore before it starts,
    # so point them at a repository-local directory when that happens.
    $redirected = @()

    $cliHome = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { $env:USERPROFILE }
    if ($cliHome -and -not (Test-WritableDirectory -Path (Join-Path $cliHome '.dotnet'))) {
        $env:DOTNET_CLI_HOME = Join-Path $StateRoot 'dotnet-home'
        New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
        $redirected += "DOTNET_CLI_HOME -> $($env:DOTNET_CLI_HOME)"
    }

    if ($env:APPDATA -and -not (Test-WritableDirectory -Path (Join-Path $env:APPDATA 'NuGet'))) {
        $env:APPDATA = Join-Path $StateRoot 'appdata'
        New-Item -ItemType Directory -Force -Path (Join-Path $env:APPDATA 'NuGet') | Out-Null
        $redirected += "APPDATA -> $($env:APPDATA)"
    }

    foreach ($entry in $redirected) {
        Write-Host "    unwritable user profile, redirecting $entry" -ForegroundColor Yellow
    }
}

function Get-ProjectFiles {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $Label
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return @() }
    $projects = @(Get-ChildItem -LiteralPath $Directory -Filter '*.csproj' -File -Recurse |
        # WPF writes temporary *_wpftmp.csproj files while compiling XAML. An
        # interrupted build can leave one behind, and it has no targets, so it
        # must never be treated as a real project.
        Where-Object { $_.Name -notlike '*_wpftmp.csproj' } |
        Sort-Object -Property FullName)
    if ($projects.Count -eq 0) {
        Write-Host "    no $Label projects found under $Directory" -ForegroundColor DarkGray
    }
    return $projects
}

function Get-SolutionFile {
    # A solution that references no project silently "builds" nothing, so only
    # accept one that actually lists .csproj files. .NET 10 creates .slnx.
    $found = @()
    foreach ($pattern in @('*.slnx', '*.sln')) {
        $candidates = @(Get-ChildItem -LiteralPath $script:Root -Filter $pattern -File -ErrorAction SilentlyContinue |
            Sort-Object -Property Name)
        foreach ($candidate in $candidates) {
            $found += $candidate
            if (Select-String -LiteralPath $candidate.FullName -Pattern '\.csproj' -Quiet) {
                return $candidate
            }
        }
    }
    if ($found.Count -gt 0) {
        Write-Host "    ignoring $($found[0].Name): it references no projects" -ForegroundColor Yellow
    }
    return $null
}

function Resolve-AppVersion {
    param([string] $Explicit)

    if ($Explicit) { return $Explicit.Trim() }

    $sources = New-Object System.Collections.Generic.List[string]
    $sources.Add((Join-Path $script:Root 'Directory.Build.props'))
    $sources.Add((Join-Path $script:Root 'src\CodexBar.Windows\CodexBar.Windows.csproj'))
    foreach ($project in @($script:SourceProjects)) { $sources.Add($project.FullName) }

    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $text = Get-Content -LiteralPath $source -Raw
        foreach ($pattern in @('<Version>\s*([^<\s]+)\s*</Version>', '<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>')) {
            $match = [regex]::Match($text, $pattern)
            if ($match.Success) { return $match.Groups[1].Value.Trim() }
        }
    }
    return $script:DefaultVersion
}

function Get-SourceArchiveFiles {
    # Whitelist only: project sources, checks, docs, the build entry points and
    # solution files. Everything else (SDK folders, caches, credentials, temp
    # files) stays out, and generated output directories are filtered out even
    # when they sit inside a whitelisted folder.
    $root = Get-CanonicalPath $script:Root
    $rootPrefix = Get-DescendantPrefix $root

    $excludedDirectoryNames = @(
        'bin', 'obj', 'artifacts', 'work', 'packages', 'node_modules',
        '.git', '.vs', '.vscode', '.idea', '.dotnet', '.codexbar',
        'TestResults', '__pycache__', '.pytest_cache'
    )
    $excludedFileNames = @($script:OutputMarkerName, 'Thumbs.db', 'desktop.ini', '.DS_Store')
    $excludedFileExtensions = @(
        '.pdb', '.user', '.suo', '.log', '.tmp', '.temp', '.bak', '.cache',
        '.zip', '.nupkg', '.pfx', '.snk', '.key', '.pem', '.p12', '.cer', '.crt'
    )
    $excludedFilePatterns = @(
        '.env', '.env.*', 'secrets.json', 'credentials.json', 'auth.json',
        'token*.json', '*.received.*',
        # Transient build leftovers: WPF writes *_wpftmp.csproj next to the
        # project while XAML is compiled and normally removes it again.
        '*_wpftmp.csproj', '*_wpftmp.*.csproj', '*.binlog', '*.dgspec.json'
    )

    $whitelist = New-Object System.Collections.Generic.List[string]
    foreach ($directoryName in @('src', 'tests', 'docs', '.github')) {
        $candidate = Join-Path $root $directoryName
        if (Test-Path -LiteralPath $candidate -PathType Container) { $whitelist.Add($candidate) }
    }
    foreach ($fileName in @(
        'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
        'build.ps1', 'global.json', '.gitignore', '.editorconfig',
        'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
        'nuget.config', 'NuGet.config'
    )) {
        $candidate = Join-Path $root $fileName
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $whitelist.Add($candidate) }
    }
    foreach ($solution in @(Get-ChildItem -LiteralPath $root -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.sln', '.slnx') })) {
        $whitelist.Add($solution.FullName)
    }

    $files = New-Object System.Collections.Generic.List[string]
    foreach ($item in $whitelist) {
        if (Test-Path -LiteralPath $item -PathType Leaf) {
            $files.Add((Get-CanonicalPath $item))
            continue
        }

        foreach ($entry in @(Get-ChildItem -LiteralPath $item -Recurse -File -Force -ErrorAction SilentlyContinue)) {
            $full = Get-CanonicalPath $entry.FullName
            if (-not (Test-IsStrictDescendant -Path $full -Root $root)) { continue }

            $relative = $full.Substring($rootPrefix.Length)
            $segments = @($relative -split '[\\/]')
            $directorySegments = @()
            if ($segments.Count -gt 1) { $directorySegments = $segments[0..($segments.Count - 2)] }
            $skip = $false
            foreach ($segment in $directorySegments) {
                if ($excludedDirectoryNames -contains $segment) { $skip = $true; break }
            }
            if ($skip) { continue }

            if ($excludedFileNames -contains $entry.Name) { continue }
            if ($excludedFileExtensions -contains $entry.Extension.ToLowerInvariant()) { continue }
            $excluded = $false
            foreach ($pattern in $excludedFilePatterns) {
                if ($entry.Name -like $pattern) { $excluded = $true; break }
            }
            if ($excluded) { continue }

            $files.Add($full)
        }
    }

    return @($files | Sort-Object -Unique)
}

function Initialize-ZipAssemblies {
    # Windows PowerShell 5.1 does not resolve either zip type on its own, and
    # loading only System.IO.Compression.FileSystem is not enough: in .NET
    # Framework the ZipArchiveMode enum lives in System.IO.Compression, so both
    # assemblies have to be loaded before the enum and the archive type exist.
    Add-Type -AssemblyName 'System.IO.Compression' -ErrorAction SilentlyContinue
    Add-Type -AssemblyName 'System.IO.Compression.FileSystem' -ErrorAction SilentlyContinue

    if (-not ('System.IO.Compression.ZipArchive' -as [type])) {
        throw 'System.IO.Compression.ZipArchive is unavailable, so archives cannot be written.'
    }
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        throw 'System.IO.Compression.ZipFile is unavailable, so archives cannot be verified.'
    }

    # Prove the enum resolves here, so a 5.1 caller fails with a clear message
    # instead of an "unable to find type" inside archive construction.
    $null = [System.IO.Compression.ZipArchiveMode]::Create
}

function New-ZipFromFiles {
    param(
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $BaseDirectory,
        [Parameter(Mandatory)][string[]] $Files,
        [string] $EntryPrefix = ''
    )

    Initialize-ZipAssemblies

    $base = Get-CanonicalPath $BaseDirectory
    $basePrefix = Get-DescendantPrefix $base

    $destinationDirectory = Split-Path -Parent (Get-CanonicalPath $Destination)
    if ($destinationDirectory) { New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null }
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }

    $stream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object -TypeName System.IO.Compression.ZipArchive -ArgumentList @($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $Files) {
                $full = Get-CanonicalPath $file
                if (-not (Test-IsStrictDescendant -Path $full -Root $base)) {
                    throw "Refusing to archive '$full': it is not inside '$base'."
                }
                $relative = $full.Substring($basePrefix.Length) -replace '\\', '/'
                $entryName = $relative
                if ($EntryPrefix) { $entryName = "$EntryPrefix/$relative" }
                [void] [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $full, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-ArchiveEntries {
    param(
        [Parameter(Mandatory)][string] $ArchivePath,
        [string[]] $ForbiddenDirectoryNames = @(),
        [string[]] $ForbiddenFileNames = @(),
        [string[]] $ForbiddenFileExtensions = @()
    )

    Initialize-ZipAssemblies

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $offenders = New-Object System.Collections.Generic.List[string]
        foreach ($entry in $archive.Entries) {
            $entryName = "$($entry.FullName)"
            if ($entryName.EndsWith('/')) { continue }
            $segments = @($entryName -split '/')
            $fileName = $segments[$segments.Count - 1]
            foreach ($segment in @($segments[0..([Math]::Max(0, $segments.Count - 2))])) {
                if ($ForbiddenDirectoryNames -contains $segment) { $offenders.Add($entryName) }
            }
            if ($ForbiddenFileNames -contains $fileName) { $offenders.Add($entryName) }
            foreach ($extension in $ForbiddenFileExtensions) {
                if ($fileName.ToLowerInvariant().EndsWith($extension)) { $offenders.Add($entryName) }
            }
        }
        if ($offenders.Count -gt 0) {
            throw "Archive '$ArchivePath' contains entries that must never be packaged: $((@($offenders | Sort-Object -Unique)) -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Copy-DocumentationFiles {
    param([Parameter(Mandatory)][string] $Destination)

    # Legal and orientation files travel with the portable build.
    $fileNames = @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')
    $copied = New-Object System.Collections.Generic.List[string]
    foreach ($fileName in $fileNames) {
        $source = Join-Path $script:Root $fileName
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            Write-Host "    $fileName is not present in the repository; skipping" -ForegroundColor Yellow
            continue
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $Destination $fileName) -Force
        $copied.Add($fileName)
    }
    return @($copied)
}

function Copy-RuntimeLicenseFiles {
    param([Parameter(Mandatory)][string] $Destination)

    # The portable build redistributes the .NET runtime (and the Windows Desktop
    # framework) as a self-contained app, so the runtime's own licence and
    # third-party notices have to travel with it. They sit next to dotnet.exe in
    # every official SDK/runtime layout, which is also where a CI setup-dotnet
    # installation keeps them.
    $dotnetRoot = Split-Path -Parent (Get-CanonicalPath $script:DotNetExe)
    $licenseDirectory = Join-Path $Destination 'licenses'

    $expectations = @(
        [pscustomobject]@{ Source = 'LICENSE.txt'; Target = 'dotnet-LICENSE.txt' },
        [pscustomobject]@{ Source = 'ThirdPartyNotices.txt'; Target = 'dotnet-ThirdPartyNotices.txt' }
    )

    $missing = @($expectations | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $dotnetRoot $_.Source) -PathType Leaf)
    })
    if ($missing.Count -gt 0) {
        $names = ($missing | Select-Object -ExpandProperty Source) -join ', '
        throw "The .NET runtime licence files ($names) were not found in '$dotnetRoot'. The portable build redistributes that runtime, so its notices must ship with it. Point -DotNetPath at a complete .NET SDK/runtime layout."
    }

    New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
    $copied = New-Object System.Collections.Generic.List[string]
    foreach ($expectation in $expectations) {
        $target = Join-Path $licenseDirectory $expectation.Target
        Copy-Item -LiteralPath (Join-Path $dotnetRoot $expectation.Source) -Destination $target -Force
        $copied.Add("licenses/$($expectation.Target)")
    }
    return @($copied)
}

$script:DotNetExe = Resolve-DotNetExecutable -Explicit $DotNetPath
Write-Host "dotnet: $script:DotNetExe" -ForegroundColor Green

Initialize-DotNetEnvironment -StateRoot (Join-Path $script:Root 'obj\local-state')

# Read the version without piping into Select-Object: an early-stopped pipeline
# leaves $LASTEXITCODE unset.
$versionOutput = & $script:DotNetExe --version
$versionExitCode = $LASTEXITCODE
if ($versionExitCode -ne 0) {
    throw "'$script:DotNetExe' --version failed with exit code $versionExitCode. If that dotnet has no .NET 10 SDK, pass -DotNetPath."
}
$sdkVersion = ("$(@($versionOutput)[0])").Trim()
Write-Host "SDK:    $sdkVersion" -ForegroundColor Green
if (-not $SkipSdkCheck -and ([System.Version]$sdkVersion).Major -lt 10) {
    throw "CodexBar.Windows needs the .NET 10 SDK but '$sdkVersion' was resolved. See global.json."
}

$sourceProjects = @(Get-ProjectFiles -Directory (Join-Path $script:Root 'src') -Label 'source')
$testProjects = @(Get-ProjectFiles -Directory (Join-Path $script:Root 'tests') -Label 'test')
$script:SourceProjects = @($sourceProjects)
$script:TestProjects = @($testProjects)
$solutionFile = Get-SolutionFile
$script:SolutionFile = $solutionFile

if (-not $solutionFile -and $sourceProjects.Count -eq 0) {
    throw "Nothing to build: no .slnx/.sln in '$script:Root' and no .csproj under '$script:Root\src'."
}
if ($solutionFile) {
    Write-Host "Solution: $($solutionFile.Name)" -ForegroundColor Green
}
else {
    Write-Host "Projects: $($sourceProjects.Count) source project(s)" -ForegroundColor Green
}

$script:Version = Resolve-AppVersion -Explicit $Version
Write-Host "Version: $($script:Version)" -ForegroundColor Green

# Fail fast on an unsafe destination instead of clearing it after a long build.
Assert-OutputDirectorySafe -Path $OutputDirectory -AllowOutsideRepository:$AllowOutsideRepositoryOutput
Write-Host "Output:  $(Get-CanonicalPath $OutputDirectory)" -ForegroundColor Green

if ($Clean) {
    Write-Step "Cleaning previous output"
    Reset-Directory -Path $OutputDirectory -Purpose 'publish'
    foreach ($project in @($sourceProjects) + @($testProjects)) {
        $projectDirectory = Split-Path -Parent $project.FullName
        foreach ($folder in @('bin', 'obj')) {
            Remove-BuildOutputDirectory -Path (Join-Path $projectDirectory $folder) -ProjectDirectory $projectDirectory
        }
    }
}

$buildTargets = if ($solutionFile) { @($solutionFile.FullName) } else { @($sourceProjects.FullName) }

# Multi-node MSBuild dispatch fails without a diagnostic in restricted /
# sandboxed environments, and this repository is small, so stay on one node.
$msBuildOptions = @('-m:1')

if (-not $NoRestore) {
    Write-Step "Restoring"
    foreach ($target in $buildTargets) {
        Invoke-DotNet -Arguments (@('restore', $target) + $msBuildOptions)
    }
    foreach ($project in @($testProjects)) {
        Invoke-DotNet -Arguments (@('restore', $project.FullName) + $msBuildOptions)
    }
}

Write-Step "Building ($Configuration)"
foreach ($target in $buildTargets) {
    Invoke-DotNet -Arguments (@('build', $target, '-c', $Configuration, '--nologo', '--no-restore') + $msBuildOptions)
}

if (-not $NoTests) {
    if ($testProjects.Count -eq 0) {
        Write-Host ''
        Write-Host '    no test projects to run' -ForegroundColor Yellow
    }
    else {
        Write-Step "Building tests ($Configuration)"
        foreach ($project in @($testProjects)) {
            # `dotnet run` would otherwise rebuild the project implicitly, and an
            # implicit build uses multi-node MSBuild, which fails without a
            # diagnostic in restricted environments. Build every test project
            # explicitly here (single node) and run it with --no-build below.
            Invoke-DotNet -Arguments (@('build', $project.FullName, '-c', $Configuration, '--nologo', '--no-restore') + $msBuildOptions)
        }

        Write-Step "Running tests"
        foreach ($project in @($testProjects)) {
            Invoke-DotNet -Arguments @('run', '--project', $project.FullName, '-c', $Configuration, '--no-build', '--no-restore')
        }
    }
}

$sourceArchivePath = $null
$sourceFiles = @()
if (-not $SkipSourceArchive -and -not $NoPackage) {
    Write-Step 'Packaging source archive'
    $sourceFiles = @(Get-SourceArchiveFiles)
    if ($sourceFiles.Count -eq 0) {
        throw 'The source archive whitelist matched no files under the repository.'
    }
    New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
    $sourceArchivePath = Join-Path $artifactsRoot "CodexBar-source-$($script:Version).zip"
    New-ZipFromFiles -Destination $sourceArchivePath -BaseDirectory $script:Root -Files $sourceFiles
    Write-Host "    $($sourceFiles.Count) whitelisted file(s)" -ForegroundColor DarkGray
    foreach ($file in $sourceFiles) { Write-Host "      $(Get-CanonicalPath $file)" -ForegroundColor DarkGray }
    Assert-ArchiveEntries -ArchivePath $sourceArchivePath -ForbiddenDirectoryNames @('bin', 'obj', 'artifacts', 'work', '.git', '.dotnet', '.vs', 'packages', 'node_modules', 'TestResults') -ForbiddenFileNames @($script:OutputMarkerName, 'secrets.json', 'credentials.json', 'auth.json') -ForbiddenFileExtensions @('.pdb', '.zip', '.nupkg', '.pfx', '.snk', '.key', '.pem', '.log', '.tmp')
}

if ($NoPublish) {
    Write-Host ''
    Write-Host 'Publish skipped (-NoPublish).' -ForegroundColor Yellow
    if ($sourceArchivePath) { Write-Host "  source archive: $sourceArchivePath" }
    return
}

$appProject = $null
if ($sourceProjects.Count -gt 0) {
    $appProject = $sourceProjects | Where-Object { $_.Name -eq 'CodexBar.Windows.csproj' } | Select-Object -First 1
    if (-not $appProject) { $appProject = $sourceProjects | Where-Object { $_.BaseName -eq 'CodexBar.Windows' } | Select-Object -First 1 }
    if (-not $appProject -and $sourceProjects.Count -eq 1) { $appProject = $sourceProjects[0] }
}
if (-not $appProject) {
    $names = ($sourceProjects | Select-Object -ExpandProperty Name) -join ', '
    throw "Could not pick the app project to publish. Expected src\CodexBar.Windows\CodexBar.Windows.csproj, found: $names"
}

Write-Step "Publishing $($appProject.Name) ($Configuration, $RuntimeIdentifier, self-contained)"
Reset-Directory -Path $OutputDirectory -Purpose 'publish'
Invoke-DotNet -Arguments @(
    'publish', $appProject.FullName,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--nologo',
    '-o', $OutputDirectory,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishReadyToRun=false',
    '-m:1'
)

Write-Step 'Assembling portable package'
$copiedDocuments = @(Copy-DocumentationFiles -Destination $OutputDirectory)
if ($copiedDocuments.Count -gt 0) {
    Write-Host "    documentation: $($copiedDocuments -join ', ')" -ForegroundColor DarkGray
}
$copiedRuntimeLicenses = @(Copy-RuntimeLicenseFiles -Destination $OutputDirectory)
Write-Host "    runtime notices: $($copiedRuntimeLicenses -join ', ')" -ForegroundColor DarkGray

# Symbols are not part of the portable payload.
foreach ($symbol in @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.pdb' -File -Recurse -ErrorAction SilentlyContinue)) {
    Remove-Item -LiteralPath $symbol.FullName -Force
    Write-Host "    removed symbol file $($symbol.Name)" -ForegroundColor DarkGray
}

if ($NoPackage) {
    Write-Host ''
    Write-Host 'Packaging skipped (-NoPackage).' -ForegroundColor Yellow
    Write-Host "  publish: $OutputDirectory"
    if ($sourceArchivePath) { Write-Host "  source archive: $sourceArchivePath" }
    return
}

Write-Step 'Packaging portable zip'
New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
$appExe = Join-Path $OutputDirectory 'CodexBar.Windows.exe'

if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'LICENSE') -PathType Leaf)) {
    throw "LICENSE is missing from '$OutputDirectory', so the portable package would ship without its licence. Make sure LICENSE exists in '$script:Root' and re-run."
}
foreach ($required in @('licenses\dotnet-LICENSE.txt', 'licenses\dotnet-ThirdPartyNotices.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $required) -PathType Leaf)) {
        throw "'$required' is missing from '$OutputDirectory', so the portable package would redistribute the .NET runtime without its notices. Re-run the packaging step."
    }
}

$packageFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne $script:OutputMarkerName -and $_.Extension -ne '.pdb' } |
    Select-Object -ExpandProperty FullName |
    Sort-Object)
if ($packageFiles.Count -eq 0) {
    throw "Nothing to package: '$OutputDirectory' is empty."
}

$zipPath = Join-Path $artifactsRoot "CodexBar-$RuntimeIdentifier-$($script:Version).zip"
New-ZipFromFiles -Destination $zipPath -BaseDirectory $OutputDirectory -Files $packageFiles
Assert-ArchiveEntries -ArchivePath $zipPath -ForbiddenDirectoryNames @('bin', 'obj', 'work', '.git', '.dotnet') -ForbiddenFileNames @($script:OutputMarkerName) -ForbiddenFileExtensions @('.pdb')

Write-Host ''
Write-Host 'Build complete.' -ForegroundColor Green
Write-Host "  publish: $OutputDirectory"
if (Test-Path -LiteralPath $appExe -PathType Leaf) {
    $size = [math]::Round((Get-Item -LiteralPath $appExe).Length / 1MB, 2)
    Write-Host "  app:     $appExe ($size MB)"
}
Write-Host "  portable zip:   $zipPath ($([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)) MB, $($packageFiles.Count) file(s))"
if ($sourceArchivePath) {
    Write-Host "  source archive: $sourceArchivePath ($([math]::Round((Get-Item -LiteralPath $sourceArchivePath).Length / 1KB, 1)) KB, $($sourceFiles.Count) file(s))"
}
