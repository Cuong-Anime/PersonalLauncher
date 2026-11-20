using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Core.Services;

namespace PersonalLauncher.Infrastructure.Games;

public sealed class GameScanService : IGameScanService
{
    private readonly IGameRepository _gameRepo;
    private readonly IFileIdentityProvider _fileIdentityProvider;
    private readonly IAppLogger _logger;

    public GameScanService(
        IGameRepository gameRepo,
        IFileIdentityProvider fileIdentityProvider,
        IAppLogger logger)
    {
        _gameRepo = gameRepo;
        _fileIdentityProvider = fileIdentityProvider;
        _logger = logger;
    }

    public async Task<ScanResult> ScanDirectoryAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.RootDirectory) || !Directory.Exists(options.RootDirectory))
            throw new DirectoryNotFoundException("Selected scan directory does not exist.");

        string rootFull = Path.GetFullPath(options.RootDirectory.Trim().Trim('"'));

        // Fetch normalized keys & identities for duplicate detection ONLY (never exposing Secret metadata!)
        HashSet<string> existingExeKeys = await _gameRepo.GetAllNormalizedExeKeysAsync(ct);
        HashSet<string> existingIdentities = await _gameRepo.GetAllFileIdentitiesAsync(ct);

        return await Task.Run(() =>
        {
            var inaccessible = new List<string>();
            var skippedReparse = new List<string>();
            var foundExesByDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(rootFull);

            int scannedDirs = 0;
            int foundCount = 0;

            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested)
                {
                    return BuildResult(foundExesByDir, existingExeKeys, existingIdentities, inaccessible, skippedReparse, wasCancelled: true);
                }

                string currentDir = stack.Pop();
                if (!visitedDirs.Add(currentDir))
                    continue;

                scannedDirs++;
                progress?.Report(new ScanProgress
                {
                    ScannedDirectories = scannedDirs,
                    FoundExecutables = foundCount,
                    CurrentDirectory = currentDir
                });

                // Enumerate .exe files read-only — NEVER execute any binary!
                try
                {
                    foreach (string exeFile in Directory.EnumerateFiles(currentDir, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        try
                        {
                            FileAttributes attrs = File.GetAttributes(exeFile);
                            if ((attrs & FileAttributes.ReparsePoint) != 0)
                            {
                                // Check symlink target does not escape root
                                var fi = new FileInfo(exeFile);
                                string? target = fi.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                                if (target is not null && !PathNormalization.IsPathInsideDirectory(target, rootFull))
                                {
                                    skippedReparse.Add(exeFile);
                                    continue;
                                }
                            }
                        }
                        catch
                        {
                            // Keep going safely
                        }

                        if (!foundExesByDir.TryGetValue(currentDir, out var list))
                        {
                            list = new List<string>();
                            foundExesByDir[currentDir] = list;
                        }
                        list.Add(exeFile);
                        foundCount++;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    inaccessible.Add(currentDir);
                    continue;
                }
                catch (IOException)
                {
                    inaccessible.Add(currentDir);
                    continue;
                }

                if (!options.IncludeSubdirectories)
                    continue;

                try
                {
                    foreach (string subDir in Directory.EnumerateDirectories(currentDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        try
                        {
                            var dirInfo = new DirectoryInfo(subDir);
                            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                // Never follow junctions/symlinks out of root or into cycles (Rule 5.2 #3)
                                skippedReparse.Add(subDir);
                                continue;
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            inaccessible.Add(subDir);
                            continue;
                        }
                        catch (IOException)
                        {
                            inaccessible.Add(subDir);
                            continue;
                        }

                        stack.Push(subDir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    inaccessible.Add(currentDir);
                }
                catch (IOException)
                {
                    inaccessible.Add(currentDir);
                }
            }

            return BuildResult(foundExesByDir, existingExeKeys, existingIdentities, inaccessible, skippedReparse, wasCancelled: ct.IsCancellationRequested);
        }, ct);
    }

    private ScanResult BuildResult(
        Dictionary<string, List<string>> foundExesByDir,
        HashSet<string> existingExeKeys,
        HashSet<string> existingIdentities,
        List<string> inaccessible,
        List<string> skippedReparse,
        bool wasCancelled)
    {
        var candidates = new List<ScanCandidate>();

        foreach (var (dir, exeList) in foundExesByDir.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string directFolderName = PathNormalization.GetDirectParentFolderName(exeList[0]);

            // Determine non-helper count to detect multi-EXE folders
            var scored = exeList.Select(exe =>
            {
                var (isHelper, reason) = ScanHeuristics.ClassifyExecutable(exe);
                string normKey = PathNormalization.NormalizeExePathKey(exe);
                string? fileId = _fileIdentityProvider.TryGetFileIdentity(exe);
                bool duplicate = existingExeKeys.Contains(normKey) ||
                                 (fileId is not null && existingIdentities.Contains(fileId));
                int score = ScanHeuristics.ScorePrimaryCandidate(exe, directFolderName);
                return (Exe: exe, NormKey: normKey, IsHelper: isHelper, Reason: reason, Duplicate: duplicate, Score: score);
            }).OrderByDescending(x => x.Score).ThenBy(x => x.Exe, StringComparer.OrdinalIgnoreCase).ToList();

            int nonHelperCount = scored.Count(x => !x.IsHelper);
            bool hasMultipleExes = scored.Count > 1;
            bool primaryPicked = false;

            foreach (var item in scored)
            {
                bool suggestSelect = false;
                if (!item.IsHelper && !item.Duplicate && !primaryPicked)
                {
                    suggestSelect = true;
                    primaryPicked = true;
                }

                candidates.Add(new ScanCandidate
                {
                    ExePath = item.Exe,
                    NormalizedExeKey = item.NormKey,
                    DirectParentDirectory = dir,
                    SuggestedName = directFolderName,
                    IsLikelyHelper = item.IsHelper,
                    HelperReason = item.Reason,
                    IsAlreadyInLibrary = item.Duplicate,
                    HasMultipleExesInFolder = hasMultipleExes && nonHelperCount > 1,
                    IsSuggestedSelected = suggestSelect
                });
            }
        }

        _logger.Info("Scanner", $"Directory scan complete: {candidates.Count} candidates, {inaccessible.Count} inaccessible, {skippedReparse.Count} skipped links, cancelled={wasCancelled}.");

        return new ScanResult
        {
            Candidates = candidates,
            InaccessibleDirectories = inaccessible,
            SkippedReparsePoints = skippedReparse,
            WasCancelled = wasCancelled
        };
    }
}
