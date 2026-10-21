using System.Text.Json;
using System.Text.Json.Serialization;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Native;

namespace WayScrawl.Application;

public enum RetentionStage
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("media-retired")] MediaRetired,
    [JsonStringEnumMemberName("complete")] Complete,
    [JsonStringEnumMemberName("bundle-pending")] BundlePending,
    [JsonStringEnumMemberName("preparing")] Preparing,
    [JsonStringEnumMemberName("ready")] Ready
}

public sealed record RetentionAction(string Kind, string Bundle, string[] Associated, long LogicalBytes, string? TrashPath,
    long FreedBytes = 0, long TrashedBytes = 0, bool Complete = true, string? Problem = null, string? Warning = null);
public sealed record RetentionReport(RetentionAction[] Actions, string[] Skipped)
{
    public const string SpaceNote = "Freed data bytes are counted by inode/link count; open handles can delay reclamation. Trashed bytes " +
                                    "remain allocated until Trash is emptied. Purge is opt-in.";
}

public sealed record RetentionFile(string Path, long Bytes, DateTime ModifiedUtc);
public sealed record MediaRemoval(string Schema, string Id, string BundleId, DateTimeOffset CreatedUtc, DateTimeOffset RemovedUtc,
    string[] Files, string[] RawDirectories, bool Purge, RetentionStage Stage, RetentionFile[] Evidence, RetentionFile[] RawFiles,
    RetentionFile[] MediaFiles, long FreedBytes = 0, long TrashedBytes = 0, bool TrashAccounted = false)
{
    public const string SchemaName = "wayscrawl.retention/1";
    public string Reason { get; init; } = "retention.mediaDays";
    public FileIdentity? MediaIdentity { get; init; }
}

public sealed class RetentionCleaner(WayScrawlPaths paths, WayScrawlConfig config, string? trashDirectory = null)
{
    private sealed record BundleTransfer(string Schema, string BundleId, DateTimeOffset CreatedUtc, string Source, string Destination,
        FileIdentity Identity, RetentionStage Stage = RetentionStage.Ready);
    private sealed record RawRemoval(
        string Schema, string Id, DateTimeOffset CreatedUtc, RetentionFile[] Files, string? TransferId = null, bool Purge = false);

    public string TrashDirectory => trashDirectory ?? Path.Combine(Path.GetDirectoryName(paths.Data)!, "Trash");

    private sealed record RetirementDecision(bool Purge, bool RetireBundle, RetentionFile[] Evidence);
    private sealed class BundleRetirement(string directory, Bundle bundle, MediaRemoval removal, bool purge)
    {
        public string Directory { get; } = directory;
        public string Media => Path.Combine(Directory, "media");
        public Bundle Bundle { get; } = bundle;
        public MediaRemoval Removal { get; set; } = removal;
        public bool Purge { get; } = purge;
    }

    public async Task<RetentionReport> RunAsync(DateTimeOffset now, bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<RetentionAction>(); var skipped = new List<string>();
        if (config.Retention is not { } policy) return new([], ["Retention disabled."]);
        string bundles = Path.GetFullPath(config.BundleDirectory ?? paths.Bundles);
        if (PathSafety.IsLink(bundles) || PathSafety.IsLink(paths.Sessions) || PathSafety.IsLink(TrashDirectory))
            return new([], ["Configured paths contain symlinks; no cleanup performed."]);
        foreach (string directory in Directory.Exists(bundles) ? Directory.EnumerateDirectories(bundles, "*.wayscrawl") : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leases = new List<FileStream>(); BundleRetirement? job = null; bool began = false;
            long initialFreed = 0, initialTrashed = 0;
            try
            {
                var (bundle, removal) = await LoadCapture(directory, bundles, dryRun, leases, cancellationToken);
                var decision = DecideRetirement(directory, bundle, removal, now, policy);
                if (decision is null) continue;
                var rawDirectories = LoadRawDirectories(directory, bundle, removal, now, dryRun, leases);
                if (removal is { Stage: RetentionStage.Complete } && !decision.RetireBundle) continue;
                string media = Path.Combine(directory, "media");
                if (removal is null && !Directory.Exists(media) && rawDirectories.Count == 0 && !decision.RetireBundle) continue;
                if (dryRun)
                {
                    var trashed = decision.Purge ? Array.Empty<string>() :
                        new[] { decision.RetireBundle ? directory : media }.Where(Directory.Exists).ToArray();
                    var deleted = rawDirectories.Concat(decision.Purge ? [decision.RetireBundle ? directory : media] : [])
                        .Where(Directory.Exists);
                    actions.Add(new(decision.RetireBundle ? "bundle" : "media", directory, rawDirectories.ToArray(), 0, null,
                        StorageAccounting.EstimateFreed(deleted), StorageAccounting.AllocatedBytes(trashed)));
                    continue;
                }
                removal ??= new(MediaRemoval.SchemaName, Guid.NewGuid().ToString("N"), bundle.Id, bundle.CreatedUtc, now,
                    bundle.Media.Select(m => m.Path).ToArray(), rawDirectories.ToArray(), decision.Purge, RetentionStage.Pending,
                    decision.Evidence, rawDirectories.SelectMany(Snapshot).ToArray(),
                    Snapshot(media)) { MediaIdentity = Directory.Exists(media) ? FileAllocation.Identity(media) : null };
                job = new(directory, bundle, removal, decision.Purge);
                initialFreed = removal.FreedBytes; initialTrashed = removal.TrashedBytes;
                if (removal.Stage != RetentionStage.BundlePending) await Save(directory, removal, now, cancellationToken);
                began = true;
                await RetireAndVerifyMedia(job, now, cancellationToken);
                await RetireRaw(job, now, cancellationToken);
                string? trash = job.Removal.TrashAccounted ? Trash.Destination(media, TrashDirectory, job.Removal.Id) : null;
                if (decision.RetireBundle) trash = await TransferBundle(job, now, cancellationToken);
                actions.Add(new(decision.RetireBundle ? "bundle" : "media", directory, job.Removal.RawDirectories, 0, trash,
                    job.Removal.FreedBytes - initialFreed, job.Removal.TrashedBytes - initialTrashed));
            }
            catch (Exception error) when (Unreadable(error))
            {
                if (began && job is not null)
                    actions.Add(new("pending", directory, job.Removal.RawDirectories, 0, null, job.Removal.FreedBytes - initialFreed,
                        job.Removal.TrashedBytes - initialTrashed, false, error.Message));
                else skipped.Add(directory + ": " + error.Message);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
        await RetireOrphans(now, dryRun, bundles, policy, actions, skipped, cancellationToken);
        return new(actions.ToArray(), skipped.ToArray());
    }

    private async Task<(Bundle Bundle, MediaRemoval? Removal)> LoadCapture(string directory, string bundles,
        bool dryRun, List<FileStream> leases, CancellationToken cancellationToken)
    {
        MediaRemoval? removal = null;
        if (!PathSafety.Within(bundles, directory) || directory == Path.GetFullPath(TrashDirectory) ||
            PathSafety.Within(directory, TrashDirectory) || PathSafety.Within(TrashDirectory, directory))
            throw new IOException("Unsafe path relation.");
        if (!PathSafety.InspectTree(directory).Safe || File.Exists(Path.Combine(directory, ".pinned")))
            throw new IOException("Pinned or linked capture.");
        if (!OperationLease.Available(directory, paths.Runtime)) throw new IOException("Bundle is in use.");
        if (!dryRun) leases.Add(OperationLease.Acquire(directory, paths.Runtime));
        if (File.Exists(Path.Combine(directory, ".pinned"))) throw new IOException("Pinned capture.");
        var bundle = await BundleStore.ReadAsync(directory, cancellationToken);
        string note = Path.Combine(directory, MediaFiles.RemovedNote);
        if (File.Exists(note))
        {
            removal = JsonSerializer.Deserialize<MediaRemoval>(await File.ReadAllTextAsync(note, cancellationToken), WayScrawlJson.Options)
                ?? throw new InvalidDataException("Empty retention marker.");
            ValidateMarker(removal, bundle, directory);
        }
        return (bundle, removal);
    }

    private RetirementDecision? DecideRetirement(string directory, Bundle bundle, MediaRemoval? removal,
        DateTimeOffset now, RetentionSettings policy)
    {
        double age = (now - bundle.CreatedUtc).TotalDays;
        // A started transaction keeps its policy and resumes independently of a
        // newly increased expiry threshold.
        if (age < 1 || age < policy.MediaDays && removal is not { Stage: not RetentionStage.Complete }) return null;
        bool purge = removal is { Stage: not RetentionStage.Complete } ? removal.Purge : policy.Purge;
        if (removal is not null && MediaWasRestored(directory, removal))
            throw new IOException(
                "Restored or changed media is protected from automatic retirement; pin the bundle to retain it explicitly.");
        if (removal is { Stage: RetentionStage.BundlePending, Purge: false } && IsRestored(directory, bundle, removal))
            throw new IOException("Restored whole bundle is protected from automatic retirement; pin it to retain it explicitly.");
        var evidence = Evidence(directory);
        bool recent = PathSafety.InspectTree(directory).LastChanged > now.UtcDateTime.AddDays(-1);
        if (recent && (removal is null || !Same(evidence, removal.Evidence, subset: removal.Stage == RetentionStage.BundlePending) ||
            (removal.Stage == RetentionStage.Pending ? !Same(Snapshot(Path.Combine(directory, "media")), removal.MediaFiles, subset: true)
                : Snapshot(Path.Combine(directory, "media")).Length != 0)))
            throw new IOException("Capture changed in the last day.");
        bool retireBundle = age >= policy.BundleDays || removal?.Stage == RetentionStage.BundlePending;
        return new(purge, retireBundle, evidence);
    }

    private List<string> LoadRawDirectories(string directory, Bundle bundle, MediaRemoval? removal, DateTimeOffset now,
        bool dryRun, List<FileStream> leases)
    {
        string raw = Path.Combine(paths.Sessions, bundle.Id);
        var rawDirectories = new List<string>();
        if (SafeId(bundle.Id) && Path.GetFileName(directory) == bundle.Id + ".wayscrawl" &&
            (Directory.Exists(raw) || removal?.RawDirectories.Length > 0))
        {
            if (!PathSafety.Within(paths.Sessions, raw) || PathSafety.IsLink(raw) ||
                Directory.Exists(raw) && !PathSafety.InspectTree(raw).Safe || File.Exists(Path.Combine(raw, ".pinned")) ||
                !OperationLease.Available(raw, paths.Runtime))
                throw new IOException("Raw session is linked or in use.");
            if (!dryRun) leases.Add(OperationLease.Acquire(raw, paths.Runtime));
            if (File.Exists(Path.Combine(raw, ".pinned"))) throw new IOException("Pinned raw session.");
            bool recovering = removal?.RawDirectories.Contains(Path.GetFullPath(raw), StringComparer.Ordinal) == true;
            SessionRecord? record = null;
            try { if (Directory.Exists(raw)) record = EvidenceBuilder.ReadRecord(raw); }
            catch (Exception error) when (recovering && Unreadable(error)) { }
            if (record is not null && (record.Id != bundle.Id || record.CreatedUtc != bundle.CreatedUtc ||
                                          record.State is not ("published" or "recorded" or "failed")))
                throw new IOException("Raw session identity/state does not match the published capture.");
            if (record is null && !recovering) throw new IOException("Raw session cannot be associated safely.");
            if (Directory.Exists(raw) && PathSafety.InspectTree(raw).LastChanged > now.UtcDateTime.AddDays(-1) &&
                (!recovering || !Same(Snapshot(raw), removal!.RawFiles, subset: true)))
                throw new IOException("Raw session changed in the last day.");
            if (Directory.Exists(raw)) rawDirectories.Add(Path.GetFullPath(raw));
        }
        return rawDirectories;
    }

    private async Task RetireAndVerifyMedia(BundleRetirement job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string directory = job.Directory, media = job.Media;
        var bundle = job.Bundle;
        bool purge = job.Purge;
        if (!purge && job.Removal.Stage != RetentionStage.BundlePending && job.Removal.RawDirectories.Any(Directory.Exists))
        {
            string retained = Trash.Destination(media, TrashDirectory, job.Removal.Id);
            await RequireRetainedMedia(
                bundle, job.Removal.Stage == RetentionStage.Pending && !Directory.Exists(retained) ? media : retained, cancellationToken);
        }
        if (job.Removal.Stage == RetentionStage.Pending)
        {
            if (Directory.Exists(media))
            {
                if (purge)
                {
                    var result = StorageAccounting.DeleteTree(media, cancellationToken: cancellationToken);
                    job.Removal = job.Removal with { FreedBytes = job.Removal.FreedBytes + result.FreedBytes, Purge = true };
                    await Save(directory, job.Removal, now, cancellationToken);
                    if (result.Problem is not null) throw new IOException(result.Problem);
                }
                else
                {
                    var item = Trash.Move(media, TrashDirectory, now, job.Removal.Id, cancellationToken: cancellationToken);
                    job.Removal = job.Removal with { FreedBytes = job.Removal.FreedBytes + item.FreedBytes,
                        TrashedBytes = job.Removal.TrashedBytes + (job.Removal.TrashAccounted ? 0 : item.TrashedBytes),
                        TrashAccounted = true, Purge = false };
                    await Save(directory, job.Removal, now, cancellationToken);
                    if (item.Problem is not null) throw new IOException(item.Problem);
                }
            }
            else if (!purge && !job.Removal.TrashAccounted && Directory.Exists(Trash.Destination(media, TrashDirectory, job.Removal.Id)))
                job.Removal =
                    job.Removal with { TrashedBytes = job.Removal.TrashedBytes + StorageAccounting.AllocatedBytes(Trash.Destination(
                                                                                     media, TrashDirectory, job.Removal.Id)),
                        TrashAccounted = true };
            else if (bundle.Media.Length > 0 && job.Removal.MediaFiles.Length > 0 && !job.Removal.Purge && !job.Removal.TrashAccounted)
                throw new IOException("Retained media cannot be located; preserving the raw session for recovery.");
            job.Removal = Advance(job.Removal, RetentionStage.MediaRetired);
            await Save(directory, job.Removal, now, cancellationToken);
        }
    }

    private static async Task RetireRaw(BundleRetirement job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string directory = job.Directory;
        foreach (string rawDirectory in job.Removal.Stage == RetentionStage.BundlePending ? [] : job.Removal.RawDirectories)
        {
            var result = StorageAccounting.DeleteTree(rawDirectory, [SessionRecord.FileName], cancellationToken: cancellationToken);
            job.Removal = job.Removal with { FreedBytes = job.Removal.FreedBytes + result.FreedBytes };
            await Save(directory, job.Removal, now, cancellationToken);
            if (result.Problem is not null) throw new IOException(result.Problem);
        }
        string summary = Path.Combine(directory, "summary.md");
        if (job.Removal.Stage is not (RetentionStage.Complete or RetentionStage.BundlePending) && File.Exists(summary))
            await File.AppendAllTextAsync(summary,
                "\n## Retention\n\nSource media retired; raw session deleted and reprocessing is no longer available. " +
                    RetentionReport.SpaceNote + "\n",
                cancellationToken);
        if (job.Removal.Stage != RetentionStage.BundlePending)
        {
            job.Removal = Advance(job.Removal, RetentionStage.Complete) with { Evidence = Evidence(directory) };
            await Save(directory, job.Removal, now, cancellationToken);
        }
    }

    private async Task<string?> TransferBundle(BundleRetirement job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string directory = job.Directory, media = job.Media;
        var bundle = job.Bundle;
        bool purge = job.Purge;
        string? trash = job.Removal.TrashAccounted ? Trash.Destination(media, TrashDirectory, job.Removal.Id) : null;
        if (job.Removal.Stage != RetentionStage.BundlePending)
        {
            // Preparing is durable before the marker rename changes directory
            // ctime. No transfer may start until its post-marker identity is ready.
            if (!purge) await PrepareTransfer(directory, bundle, job.Removal, ready: false, cancellationToken);
            job.Removal = Advance(job.Removal, RetentionStage.BundlePending) with { Purge = purge };
            await Save(directory, job.Removal, now, cancellationToken);
        }
        if (purge)
        {
            var result = StorageAccounting.DeleteTree(directory, ["manifest.json", MediaFiles.RemovedNote],
                cancellationToken: cancellationToken);
            job.Removal = job.Removal with { FreedBytes = job.Removal.FreedBytes + result.FreedBytes };
            if (result.Problem is not null) throw new IOException(result.Problem);
        }
        else
        {
            string id = WholeTrashId(job.Removal);
            await PrepareTransfer(directory, bundle, job.Removal, ready: true, cancellationToken);
            var item = Trash.Move(directory, TrashDirectory, now, id, ["manifest.json", MediaFiles.RemovedNote],
                cancellationToken: cancellationToken);
            trash = item.Path;
            job.Removal = job.Removal with { FreedBytes = job.Removal.FreedBytes + item.FreedBytes,
                TrashedBytes = job.Removal.TrashedBytes + item.TrashedBytes };
            if (item.Problem is not null) throw new IOException(item.Problem);
        }
        return trash;
    }

    private async Task RetireOrphans(DateTimeOffset now, bool dryRun, string bundles, RetentionSettings policy,
        List<RetentionAction> actions, List<string> skipped, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.Sessions)) return;
        var configuredOwners = new HashSet<(string Id, DateTimeOffset CreatedUtc)>();
        var historicalOwners = new Dictionary<(string Id, DateTimeOffset CreatedUtc), List<string>>();
        var roots = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(bundles), Path.GetFullPath(paths.Bundles) };
        try
        {
            foreach (string raw in Directory.EnumerateDirectories(paths.Sessions))
            {
                if (PathSafety.IsLink(raw)) continue;
                try
                {
                    string? previous = EvidenceBuilder.ReadRecord(raw).Bundle;
                    if (previous is not null && Path.IsPathRooted(previous)) roots.Add(Path.GetDirectoryName(previous)!);
                }
                catch (Exception error) when (Unreadable(error)) { } // Invalid raw records are reported by their own lane.
            }
            foreach (string root in roots)
            {
                if (PathSafety.IsLink(root)) throw new IOException("Linked bundle inventory root: " + root);
                foreach (string directory in Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root, "*.wayscrawl") : [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(directory) || PathSafety.IsLink(directory) ||
                        PathSafety.IsLink(Path.Combine(directory, "manifest.json")))
                        throw new IOException("Linked or unreadable bundle inventory candidate: " + directory);
                    var bundle = await BundleStore.ReadAsync(directory, cancellationToken);
                    var identity = (bundle.Id, bundle.CreatedUtc);
                    if (PathSafety.CanonicalPath(root) == PathSafety.CanonicalPath(bundles)) configuredOwners.Add(identity);
                    else
                    {
                        if (!historicalOwners.TryGetValue(identity, out var owners)) historicalOwners[identity] = owners = [];
                        owners.Add(directory);
                    }
                }
            }
        }
        catch (Exception error) when (Unreadable(error))
        {
            skipped.Add("Orphan cleanup disabled: incomplete bundle inventory: " + error.Message);
            return;
        }
        foreach (string raw in Directory.EnumerateDirectories(paths.Sessions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leases = new List<FileStream>();
            bool began = false;
            try
            {
                // A present configured bundle owns its own raw retirement, including
                // incomplete jobs and captures protected by its pin/recent-file checks.
                string id = Path.GetFileName(raw);
                if (Directory.Exists(Path.Combine(bundles, id + ".wayscrawl"))) continue;
                if (!PathSafety.Within(paths.Sessions, raw) || PathSafety.CanonicalPath(raw) == PathSafety.CanonicalPath(TrashDirectory) ||
                    PathSafety.Within(raw, TrashDirectory) || PathSafety.Within(TrashDirectory, raw) || !PathSafety.InspectTree(raw).Safe ||
                    !OperationLease.Available(raw, paths.Runtime)) throw new IOException("Orphan raw session is linked or in use.");
                if (!dryRun) leases.Add(OperationLease.Acquire(raw, paths.Runtime));
                if (File.Exists(Path.Combine(raw, ".pinned"))) throw new IOException("Pinned raw session.");
                var record = EvidenceBuilder.ReadRecord(raw);
                if (!SafeId(record.Id) || record.Id != id || record.State is not ("published" or "recorded" or "failed"))
                    throw new IOException("Orphan raw session identity/state is not eligible.");
                if (configuredOwners.Contains((record.Id, record.CreatedUtc))) continue;
                string note = Path.Combine(raw, "raw-retirement.json");
                RawRemoval? marker = File.Exists(note)
                    ? JsonSerializer.Deserialize<RawRemoval>(await File.ReadAllTextAsync(note, cancellationToken), WayScrawlJson.Options)
                    : null;
                if (marker is not null && (marker.Schema != "wayscrawl.raw-retention/1" || marker.Id != record.Id ||
                    marker.CreatedUtc != record.CreatedUtc || marker.Files is null ||
                    marker.TransferId is { } markerId && (markerId.Length != 32 || !markerId.All(Uri.IsHexDigit))))
                    throw new InvalidDataException("Invalid raw retirement marker.");
                double age = (now - record.CreatedUtc).TotalDays;
                if (age < 1) continue;
                var files = Snapshot(raw)
                                .Where(f => Path.GetFileName(f.Path) is not ("raw-retirement.json" or "raw-retirement.json.tmp"))
                                .ToArray();
                if (PathSafety.InspectTree(raw).LastChanged > now.UtcDateTime.AddDays(-1) &&
                    (marker is null || !Same(files, marker.Files, subset: true)))
                    throw new IOException("Orphan raw session changed in the last day.");
                historicalOwners.TryGetValue((record.Id, record.CreatedUtc), out var knownOwners);
                bool unresolvedOwner = record.Bundle is not null && !Directory.Exists(record.Bundle) && knownOwners is null;
                var associated = new[] { record.Bundle, Path.Combine(paths.Bundles, id + ".wayscrawl") }
                                     .Concat(knownOwners ?? [])
                                     .Where(p => p is not null && Path.IsPathRooted(p) && Directory.Exists(p))
                                     .Select(p => p!)
                                     .Distinct(StringComparer.Ordinal);
                bool verifiedMedia = false;
                foreach (string bundleDirectory in associated)
                {
                    var associatedTree = PathSafety.InspectTree(bundleDirectory);
                    if (!associatedTree.Safe || associatedTree.LastChanged > now.UtcDateTime.AddDays(-1) ||
                        !OperationLease.Available(bundleDirectory, paths.Runtime))
                        throw new IOException("Associated bundle is linked or in use.");
                    if (!dryRun) leases.Add(OperationLease.Acquire(bundleDirectory, paths.Runtime));
                    if (File.Exists(Path.Combine(bundleDirectory, ".pinned"))) throw new IOException("Associated bundle is pinned.");
                    try
                    {
                        var bundle = await BundleStore.ReadAsync(bundleDirectory, cancellationToken);
                        if (bundle.Id != record.Id || bundle.CreatedUtc != record.CreatedUtc)
                            throw new IOException("Associated bundle identity does not match the raw session.");
                        await RequireRetainedMedia(bundle, Path.Combine(bundleDirectory, "media"), cancellationToken);
                        verifiedMedia = true;
                    }
                    catch (Exception error) when (Unreadable(error)) { verifiedMedia = false; }
                }
                bool purge = !unresolvedOwner && (marker?.Purge ?? policy.Purge);
                bool recoverable = !purge && (!verifiedMedia || unresolvedOwner);
                if (marker is null && age < (recoverable ? policy.OrphanDays : policy.MediaDays)) continue;
                long freed = dryRun && !recoverable ? StorageAccounting.EstimateFreed([raw]) : 0;
                long trashed = dryRun && recoverable ? StorageAccounting.AllocatedBytes(raw) : 0;
                string? problem = null, destination = null, warning = null;
                if (!dryRun)
                {
                    marker ??= new("wayscrawl.raw-retention/1", record.Id, record.CreatedUtc, files,
                        Guid.NewGuid().ToString("N"), purge);
                    if (recoverable)
                    {
                        string transferId = marker.TransferId ?? throw new IOException("Legacy raw retirement requires manual recovery.");
                        string receipt = Path.Combine(paths.State, "retention", "raw-" + transferId + ".json");
                        destination = Trash.Destination(raw, TrashDirectory, transferId);
                        var transfer = File.Exists(receipt) ? ReadTransfer(receipt,
                            new BundleTransfer("wayscrawl.raw-transfer/1", record.Id, record.CreatedUtc,
                                PathSafety.CanonicalPath(raw), PathSafety.CanonicalPath(destination), FileAllocation.Identity(raw))) : null;
                        var identity = FileAllocation.Identity(raw);
                        if (transfer is not null && (!identity.SameInode(transfer.Identity) ||
                            transfer.Stage == RetentionStage.Ready && !Directory.Exists(destination) && identity != transfer.Identity))
                            throw new IOException("Restored raw session is protected from automatic retirement.");
                        if (transfer is null)
                        {
                            string parent = Path.GetDirectoryName(receipt)!;
                            if (PathSafety.IsLink(parent)) throw new IOException("Linked retention state directory.");
                            WayScrawlPaths.EnsurePrivateDirectory(parent);
                            transfer = new("wayscrawl.raw-transfer/1", record.Id, record.CreatedUtc,
                                PathSafety.CanonicalPath(raw), PathSafety.CanonicalPath(destination), identity, RetentionStage.Preparing);
                            await DurableWrite(receipt, transfer, cancellationToken);
                        }
                        if (transfer.Stage == RetentionStage.Preparing)
                        {
                            await DurableWrite(note, marker, cancellationToken);
                            transfer = transfer with { Identity = FileAllocation.Identity(raw), Stage = RetentionStage.Ready };
                            await DurableWrite(receipt, transfer, cancellationToken);
                        }
                        began = true;
                        var item = Trash.Move(raw, TrashDirectory, now, transferId, [SessionRecord.FileName, "raw-retirement.json"],
                            cancellationToken: cancellationToken);
                        freed = item.FreedBytes; trashed = item.TrashedBytes; problem = item.Problem;
                        if (problem is null)
                            warning = $"Unprocessed recording {record.Id} moved to Trash; " +
                                $"restore it and run wayscrawl process '{raw.Replace("'", "'\\''", StringComparison.Ordinal)}' to rebuild.";
                    }
                    else
                    {
                        await DurableWrite(note, marker, cancellationToken); began = true;
                        var deletion = StorageAccounting.DeleteTree(raw, [SessionRecord.FileName, "raw-retirement.json"],
                            cancellationToken: cancellationToken);
                        freed = deletion.FreedBytes; problem = deletion.Problem;
                    }
                }
                actions.Add(new("raw", raw, [raw], 0, destination, freed, trashed, problem is null, problem, warning));
            }
            catch (Exception error) when (Unreadable(error))
            {
                if (began) actions.Add(new("raw", raw, [raw], 0, null, 0, 0, false, error.Message));
                else skipped.Add(raw + ": " + error.Message);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
    }

    private bool MediaWasRestored(string directory, MediaRemoval removal)
    {
        string media = Path.Combine(directory, "media");
        if (!Directory.Exists(media)) return false;
        if (removal.Stage is RetentionStage.MediaRetired or RetentionStage.Complete) return true;
        if (removal.Purge || removal.MediaIdentity is not { } original) return false;
        var current = FileAllocation.Identity(media);
        return !original.SameInode(current) ||
               !Directory.Exists(Trash.Destination(media, TrashDirectory, removal.Id)) && original != current;
    }

    private string TransferFile(MediaRemoval removal) => Path.Combine(paths.State, "retention", removal.Id + ".json");
    private static string WholeTrashId(MediaRemoval removal) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(removal.Id)))[..32];

    private bool IsRestored(string directory, Bundle bundle, MediaRemoval removal)
    {
        string file = TransferFile(removal);
        string destination = Trash.Destination(directory, TrashDirectory, WholeTrashId(removal));
        // Legacy pending markers without a transfer receipt are ambiguous; preserve
        // them. New jobs persist preparing before the pending marker can exist.
        if (!File.Exists(file)) return true;
        var transfer = ReadTransfer(file, bundle, directory, destination);
        var current = FileAllocation.Identity(directory);
        return !current.SameInode(transfer.Identity) ||
               transfer.Stage == RetentionStage.Ready && !Directory.Exists(destination) && current != transfer.Identity;
    }

    private async Task PrepareTransfer(
        string directory, Bundle bundle, MediaRemoval removal, bool ready, CancellationToken cancellationToken)
    {
        string file = TransferFile(removal);
        string destination = PathSafety.CanonicalPath(Trash.Destination(directory, TrashDirectory, WholeTrashId(removal)));
        BundleTransfer? transfer = File.Exists(file) ? ReadTransfer(file, bundle, directory, destination) : null;
        if (transfer?.Stage == RetentionStage.Ready || transfer is not null && !ready) return;
        var current = FileAllocation.Identity(directory);
        if (transfer is not null && !transfer.Identity.SameInode(current))
            throw new IOException("Bundle directory changed while preparing retirement.");
        string parent = Path.GetDirectoryName(file)!;
        if (PathSafety.IsLink(parent)) throw new IOException("Linked retention state directory.");
        WayScrawlPaths.EnsurePrivateDirectory(parent);
        transfer = new("wayscrawl.bundle-transfer/1", bundle.Id, bundle.CreatedUtc,
            PathSafety.CanonicalPath(directory), destination, current, ready ? RetentionStage.Ready : RetentionStage.Preparing);
        await DurableWrite(file, transfer, cancellationToken);
    }

    private static BundleTransfer ReadTransfer(string file, Bundle bundle, string directory, string destination)
    {
        return ReadTransfer(file, new BundleTransfer("wayscrawl.bundle-transfer/1", bundle.Id, bundle.CreatedUtc,
            PathSafety.CanonicalPath(directory), PathSafety.CanonicalPath(destination), FileAllocation.Identity(directory)));
    }

    private static BundleTransfer ReadTransfer(string file, BundleTransfer expected)
    {
        if (PathSafety.IsLink(file)) throw new IOException("Linked retention transfer receipt.");
        var transfer = JsonSerializer.Deserialize<BundleTransfer>(File.ReadAllText(file), WayScrawlJson.Options)
            ?? throw new InvalidDataException("Empty retention transfer receipt.");
        if (transfer.Schema != expected.Schema || transfer.BundleId != expected.BundleId || transfer.CreatedUtc != expected.CreatedUtc ||
            transfer.Stage is not (RetentionStage.Preparing or RetentionStage.Ready) || transfer.Source != expected.Source ||
            transfer.Destination != expected.Destination)
            throw new InvalidDataException("Retention transfer receipt does not match this capture.");
        return transfer;
    }

    private void ValidateMarker(MediaRemoval marker, Bundle bundle, string directory)
    {
        if (marker.Schema != MediaRemoval.SchemaName || marker.Id is null || marker.Id.Length != 32 || !marker.Id.All(Uri.IsHexDigit) ||
            marker.BundleId != bundle.Id || marker.CreatedUtc != bundle.CreatedUtc ||
            marker.Stage is not (
                RetentionStage.Pending or RetentionStage.MediaRetired or RetentionStage.Complete or RetentionStage.BundlePending) ||
            marker.RawDirectories is null ||
            marker.RawDirectories.Any(d => Path.GetFileName(directory) != bundle.Id + ".wayscrawl" || !SafeId(bundle.Id) ||
                                           Path.GetFullPath(d) != Path.GetFullPath(Path.Combine(paths.Sessions, bundle.Id)) ||
                                           !PathSafety.Within(paths.Sessions, d) || PathSafety.IsLink(d)) ||
            marker.Evidence is null || marker.RawFiles is null || marker.MediaFiles is null)
            throw new InvalidDataException("Invalid retention marker; manual recovery required: " + directory);
    }

    private static bool SafeId(string id) => id == Path.GetFileName(id) && id is not ("." or "..") && !id.Any(char.IsControl);
    private static async Task RequireRetainedMedia(Bundle bundle, string root, CancellationToken cancellationToken)
    {
        // Ancestors outside this boundary may be legitimate XDG aliases. The retained
        // root and everything inside it must be independent regular media, never links
        // back to the raw tree whose deletion these hashes are about to authorize.
        if (bundle.Media.Length == 0 || !Directory.Exists(root) || !PathSafety.InspectTree(root).Safe)
            throw new IOException("Retained media directory is missing or linked; preserving the raw session for recovery: " + root);
        foreach (var asset in bundle.Media)
        {
            if (!asset.Path.StartsWith("media/", StringComparison.Ordinal))
                throw new InvalidDataException("Media lies outside the retained media directory.");
            string file = Path.Combine(root, asset.Path["media/".Length..]);
            if (!PathSafety.Within(root, file) || !File.Exists(file) || PathSafety.IsLink(file))
                throw new IOException(
                    "Retained media is missing or outside its boundary; preserving the raw session for recovery: " + file);
            var (bytes, hash) = await BundleStore.HashAsync(file, cancellationToken);
            if (bytes != asset.Bytes || !hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Retained media changed; preserving the raw session for recovery: " + file);
        }
    }
    private static RetentionFile[] Snapshot(string root) => Directory.Exists(root)
        ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => new RetentionFile(Path.GetFullPath(f), new FileInfo(f).Length, File.GetLastWriteTimeUtc(f))).ToArray() : [];
    private static RetentionFile[] Evidence(string root) => Snapshot(root).Where(f =>
        !PathSafety.Within(Path.Combine(root, "media"), f.Path) && !PathSafety.Within(Path.Combine(root, "zoom"), f.Path) &&
        Path.GetFileName(f.Path) is not (MediaFiles.RemovedNote or "media-removed.json.tmp")).ToArray();
    private static bool Same(RetentionFile[] current, RetentionFile[] stored, bool subset) =>
        (subset || current.Length == stored.Length) && current.All(file => stored.Any(s => s == file));
    private static MediaRemoval Advance(MediaRemoval removal, RetentionStage next)
    {
        bool valid = (removal.Stage, next) is
            (RetentionStage.Pending, RetentionStage.MediaRetired) or
            (RetentionStage.MediaRetired, RetentionStage.Complete) or
            (RetentionStage.Complete, RetentionStage.Complete) or
            (RetentionStage.Complete, RetentionStage.BundlePending);
        if (!valid) throw new InvalidDataException($"Invalid retention stage transition: {removal.Stage} to {next}.");
        return removal with { Stage = next };
    }

    private static async Task Save(string directory, MediaRemoval removal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string note = Path.Combine(directory, MediaFiles.RemovedNote);
        await DurableWrite(note, removal, cancellationToken);
        File.SetLastWriteTimeUtc(note, now.UtcDateTime);
        Directory.SetLastWriteTimeUtc(directory, now.UtcDateTime);
        Libc.SyncDirectory(directory);
    }
    private static async Task DurableWrite<T>(string path, T value, CancellationToken cancellationToken)
    {
        if (PathSafety.IsLink(path) || PathSafety.IsLink(path + ".tmp")) throw new IOException("Linked retention marker.");
        await using (var output = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await output.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, WayScrawlJson.Options), cancellationToken);
            output.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, overwrite: true);
        Libc.SyncDirectory(Path.GetDirectoryName(path)!);
    }
    private static bool Unreadable(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException;
}
