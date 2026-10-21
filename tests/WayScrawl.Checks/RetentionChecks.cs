using System.Text.Json;
using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Native;

namespace WayScrawl.Checks;

internal static class RetentionChecks
{
    public static async Task RunAsync(Harness h, string fixtureVideo)
    {
        h.Section = "retention";
        string root = h.TempDirectory("retention");
        var paths = new WayScrawlPaths(
            Path.Combine(root, "run"), Path.Combine(root, "state"), Path.Combine(root, "data/wayscrawl"), Path.Combine(root, "config"));
        var config = new WayScrawlConfig(Notifications: false, CopyPathToClipboard: false);
        var now = DateTimeOffset.UtcNow;
        async Task<(string Bundle, string Raw)> Make(string id, double days, bool linked = true)
        {
            string directory = Path.Combine(paths.Bundles, id + ".wayscrawl"), raw = Path.Combine(paths.Sessions, id);
            await Demo.CreateAsync(directory); Directory.CreateDirectory(raw);
            string original = Path.Combine(raw, "recording.mkv");
            File.Copy(fixtureVideo, original); File.Copy(fixtureVideo, Path.Combine(raw, "recording.recovered.mkv"));
            await File.WriteAllTextAsync(Path.Combine(raw, ".transcript.json"), "private narration");
            await File.WriteAllTextAsync(Path.Combine(raw, SessionRecord.JournalName), "private title");
            Directory.CreateDirectory(Path.Combine(directory, "media"));
            string media = Path.Combine(directory, "media/recording.mkv");
            if (linked) h.Check(Libc.TryHardLink(original, media), "Retention fixture uses a real raw/bundle hard link.");
            else File.Copy(original, media);
            var (bytes, hash) = await BundleStore.HashAsync(original, CancellationToken.None);
            var demo = await BundleStore.ReadAsync(directory);
            var bundle = demo with { Id = id, CreatedUtc = now.AddDays(-days),
                Media = [CoreChecks.SampleBundle().Media[0] with { Bytes = bytes, Sha256 = hash }] };
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(bundle, BundleStore.Json));
            EvidenceBuilder.WriteRecord(raw, new(id, now.AddDays(-days), "published", "fixture",
                                                 new("fixture", bundle.Geometries[0], CompositionMode.CapturedInk, "fixture"), 0,
                                                 "recording.mkv", null, bundle.DurationUs, null, [], [], directory));
            Age(directory, now.AddDays(-days).UtcDateTime); Age(raw, now.AddDays(-days).UtcDateTime);
            return (directory, raw);
        }
        await InterruptionProtection(h, root, now, fixtureVideo);
        await OrphanRecovery(h, root, now, fixtureVideo);
        await OwnerInventory(h, root, now, fixtureVideo);
        var media = await Make("media", 10); var old = await Make("old whole", 40);
        var unindexed = await Make("unindexed", 40);
        EvidenceBuilder.WriteRecord(unindexed.Raw, EvidenceBuilder.ReadRecord(unindexed.Raw) with { State = "recorded", Bundle = null });
        Age(unindexed.Raw, now.AddDays(-40).UtcDateTime);
        var pinned = await Make("pinned", 40);
        await File.WriteAllTextAsync(Path.Combine(pinned.Bundle, ".pinned"), "protected"); Age(pinned.Bundle, now.AddDays(-40).UtcDateTime);
        var recent = await Make("recent", 40);
        File.SetLastWriteTimeUtc(Path.Combine(recent.Bundle, "frames/frame-00.png"), now.AddHours(-12).UtcDateTime);
        var baby = await Make("baby", 0.5); Age(baby.Bundle, now.AddDays(-10).UtcDateTime); Age(baby.Raw, now.AddDays(-10).UtcDateTime);
        var active = await Make("active", 40);
        EvidenceBuilder.WriteRecord(active.Raw, EvidenceBuilder.ReadRecord(active.Raw) with { State = "recording" });
        Age(active.Raw, now.AddDays(-40).UtcDateTime);
        var busy = await Make("busy", 40);
        using var processing = OperationLease.Acquire(busy.Raw + Path.DirectorySeparatorChar, paths.Runtime);
        h.Check(!OperationLease.Available(busy.Raw, paths.Runtime), "Trailing slash aliases share the processing lock identity.");
        var linked = await Make("linked", 40);
        string victim = Path.Combine(root, "outside.txt"); await File.WriteAllTextAsync(victim, "keep outside data");
        File.CreateSymbolicLink(Path.Combine(linked.Bundle, "link"), victim);
        Directory.SetLastWriteTimeUtc(linked.Bundle, now.AddDays(-40).UtcDateTime);
        string outside = Path.Combine(root, "outside.wayscrawl"); await Demo.CreateAsync(outside);
        Directory.CreateSymbolicLink(Path.Combine(paths.Bundles, "foreign.wayscrawl"), outside);
        var cleaner = new RetentionCleaner(paths, config, Path.Combine(root, "Trash"));
        h.Check((await new RetentionCleaner(paths, config with { Retention = null }, Path.Combine(root, "Trash")).RunAsync(now))
                        .Actions.Length == 0,
            "Null retention keeps all captures.");
        var dry = await cleaner.RunAsync(now, dryRun: true);
        h.Check(dry.Actions.Length == 3 && Directory.Exists(old.Bundle) && Directory.Exists(media.Raw) &&
                    !Directory.Exists(cleaner.TrashDirectory),
            "Dry run plans only old eligible units and performs no Trash writes.");
        long retainedBlocks = StorageAccounting.AllocatedBytes(Path.Combine(media.Bundle, "media"));
        long rawUnique = await AllocatedExceptOriginal(media.Raw);
        var result = await cleaner.RunAsync(now);
        var mediaAction = result.Actions.Single(a => a.Bundle == media.Bundle);
        h.Check(result.Actions.Length == 3 && result.Actions.All(a => a.Complete) && Directory.Exists(media.Bundle) &&
                    !Directory.Exists(Path.Combine(media.Bundle, "media")) && !Directory.Exists(media.Raw) &&
                    File.Exists(Path.Combine(media.Bundle, MediaFiles.RemovedNote)),
            "Media expiry deletes the entire raw session, including recovered media, journal and transcription, while evidence stays.");
        h.Check(mediaAction.FreedBytes == rawUnique && mediaAction.TrashedBytes == retainedBlocks &&
                    File.Exists(Path.Combine(mediaAction.TrashPath!, "recording.mkv")),
            "Inode accounting keeps the linked recording allocated in Trash and counts only raw-exclusive files as freed.");
        using (var mediaState = JsonDocument.Parse(File.ReadAllText(Path.Combine(media.Bundle, MediaFiles.RemovedNote))))
        using (var bundleState = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            result.Actions.Single(a => a.Bundle == old.Bundle).TrashPath!, MediaFiles.RemovedNote))))
        using (var transferState = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.State, "retention",
            bundleState.RootElement.GetProperty("id").GetString() + ".json"))))
        {
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(mediaState.RootElement.GetRawText())!;
            legacy["stage"] = "media-retired";
            h.Check(mediaState.RootElement.GetProperty("stage").GetString() == "complete" &&
                bundleState.RootElement.GetProperty("stage").GetString() == "bundle-pending" &&
                transferState.RootElement.GetProperty("stage").GetString() == "ready" &&
                JsonSerializer.Deserialize<MediaRemoval>(
                    legacy.ToJsonString(), WayScrawlJson.Options)?.Stage == RetentionStage.MediaRetired,
                "Retention stages preserve on-disk strings and read legacy hyphenated checkpoints.");
        }
        h.Check(!Directory.Exists(old.Bundle) && !Directory.Exists(old.Raw) && !Directory.Exists(unindexed.Bundle) &&
                    !Directory.Exists(unindexed.Raw),
            "Bundle expiry includes raw data even when the post-publication record update never happened.");
        h.Check(
            new[] { pinned.Bundle, recent.Bundle, baby.Bundle, active.Bundle, busy.Bundle, linked.Bundle, outside }.All(Directory.Exists) &&
                await File.ReadAllTextAsync(victim) == "keep outside data",
            "Pins, recent changes, new/active/processing captures, symlinks and outside data are protected.");
        var info = Directory.GetFiles(Path.Combine(cleaner.TrashDirectory, "info"), "*.trashinfo");
        h.Check(
            info.Length == 5 && info.Any(p => File.ReadAllText(p).Contains("old%20whole.wayscrawl")) &&
                info.All(p => File.ReadAllText(p).StartsWith("[Trash Info]\nPath=/") && File.ReadAllText(p).Contains("\nDeletionDate=")),
            "Trash info records escaped absolute paths and local deletion dates.");
        // A file manager restores the directory and removes its Trash receipt. The old
        // bundle-pending marker must not be treated as permission to throw it away again.
        string oldTrash = result.Actions.Single(a => a.Bundle == old.Bundle).TrashPath!;
        Directory.Move(oldTrash, old.Bundle);
        File.Delete(Path.Combine(cleaner.TrashDirectory, "info", Path.GetFileName(oldTrash) + ".trashinfo"));
        var restoredPass = await cleaner.RunAsync(now.AddDays(2));
        h.Check(!restoredPass.Actions.Any(a => a.Bundle == old.Bundle) && Directory.Exists(old.Bundle),
            "Restoring a whole bundle protects it from automatic retirement, even after the Trash receipt is removed.");
        var retiredBundle = await BundleStore.ReadAsync(media.Bundle);
        h.Reject(() => MediaFiles.Require(media.Bundle, retiredBundle), "Retirement marker is authoritative for media readers.");
        h.Check(BundleStore.Summary(retiredBundle, media.Bundle).Contains("media-removed.json"),
            "Inspect/summary readers disclose media retirement.");

        var interrupted = await Make("interrupted", 10);
        string unavailable = Path.Combine(root, "unavailable-trash"); await File.WriteAllTextAsync(unavailable, "blocked");
        var resuming = new RetentionCleaner(paths, config, unavailable);
        var failed = await resuming.RunAsync(now);
        h.Check(failed.Actions.Any(a => a.Bundle == interrupted.Bundle && !a.Complete) && Directory.Exists(interrupted.Raw) &&
                    File.Exists(Path.Combine(interrupted.Bundle, MediaFiles.RemovedNote)),
            "Marker is durable before a failed move and raw data stays recoverable.");
        File.Delete(unavailable);
        var marker = JsonSerializer.Deserialize<MediaRemoval>(
            await File.ReadAllTextAsync(Path.Combine(interrupted.Bundle, MediaFiles.RemovedNote)), WayScrawlJson.Options)!;
        // Simulate process exit after the public Trash move but before its receipt is journaled.
        Trash.Move(Path.Combine(interrupted.Bundle, "media"), unavailable, now, marker.Id);
        var resumed = await new RetentionCleaner(paths, config with { Retention = new(100, 130, true) }, unavailable).RunAsync(now);
        h.Check(resumed.Actions.Any(a => a.Bundle == interrupted.Bundle && a.Complete) && !Directory.Exists(interrupted.Raw),
            "The next pass resumes marker+move with the original Trash policy, even if both the expiry threshold and purge configuration " +
            "changed, and finishes raw deletion.");
        foreach (bool nested in new[] { false, true })
        {
            var retainedLink = await Make(nested ? "retained-nested-link" : "retained-root-link", 10, linked: false);
            if (nested)
            {
                var linkedBundle = await BundleStore.ReadAsync(retainedLink.Bundle);
                Directory.CreateDirectory(Path.Combine(retainedLink.Bundle, "media/nested"));
                File.Move(Path.Combine(retainedLink.Bundle, "media/recording.mkv"),
                    Path.Combine(retainedLink.Bundle, "media/nested/recording.mkv"));
                await File.WriteAllTextAsync(Path.Combine(retainedLink.Bundle, "manifest.json"), JsonSerializer.Serialize(
                    linkedBundle with { Media = [linkedBundle.Media[0] with { Path = "media/nested/recording.mkv" }] }, BundleStore.Json));
                Age(retainedLink.Bundle, now.AddDays(-10).UtcDateTime);
            }
            string blockedTrash = Path.Combine(root, nested ? "nested-linked-trash" : "root-linked-trash");
            await File.WriteAllTextAsync(blockedTrash, "block initial move");
            var linkCleaner = new RetentionCleaner(paths, config, blockedTrash);
            await linkCleaner.RunAsync(now);
            string linkNote = Path.Combine(retainedLink.Bundle, MediaFiles.RemovedNote);
            var linkMarker = JsonSerializer.Deserialize<MediaRemoval>(await File.ReadAllTextAsync(linkNote), WayScrawlJson.Options)!;
            File.Delete(blockedTrash);
            var retained = Trash.Move(Path.Combine(retainedLink.Bundle, "media"), blockedTrash, now, linkMarker.Id);
            await File.WriteAllTextAsync(
                linkNote, JsonSerializer.Serialize(
                              linkMarker with { Stage = RetentionStage.MediaRetired, TrashAccounted = true }, WayScrawlJson.Options));
            string link = nested ? Path.Combine(retained.Path, "nested") : retained.Path;
            Directory.Delete(link, recursive: true); Directory.CreateSymbolicLink(link, retainedLink.Raw);
            Age(retainedLink.Bundle, now.AddDays(-10).UtcDateTime);
            try
            {
                var unsafeRetained = await linkCleaner.RunAsync(now);
                h.Check(Directory.Exists(retainedLink.Raw) && File.Exists(Path.Combine(retainedLink.Raw, "recording.mkv")) &&
                            unsafeRetained.Actions.Any(a => a.Bundle == retainedLink.Bundle && !a.Complete),
                    "An interrupted media-retired cleanup rejects a linked " +
                        (nested ? "retained-media descendant" : "retained-media root") +
                        " instead of verifying raw media against itself and deleting it.");
            }
            finally { if (PathSafety.IsLink(link)) File.Delete(link); }
        }
        var preparing = await Make("preparing-transfer-crash", 10);
        await cleaner.RunAsync(now);
        string preparingNote = Path.Combine(preparing.Bundle, MediaFiles.RemovedNote);
        var preparingMarker = JsonSerializer.Deserialize<MediaRemoval>(await File.ReadAllTextAsync(preparingNote), WayScrawlJson.Options)!;
        string wholeId = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(preparingMarker.Id)))[..32];
        string transferDirectory = Path.Combine(paths.State, "retention"); Directory.CreateDirectory(transferDirectory);
        // Persist the crash boundary: preparing is durable, then the bundle marker
        // rename changes directory ctime, but ready has not yet been written.
        await File.WriteAllTextAsync(Path.Combine(transferDirectory, preparingMarker.Id + ".json"), JsonSerializer.Serialize(new
        {
            Schema = "wayscrawl.bundle-transfer/1", BundleId = preparingMarker.BundleId, preparingMarker.CreatedUtc,
            Source = PathSafety.CanonicalPath(preparing.Bundle),
            Destination = PathSafety.CanonicalPath(Trash.Destination(preparing.Bundle, cleaner.TrashDirectory, wholeId)),
            Identity = FileAllocation.Identity(preparing.Bundle), Stage = "preparing"
        }, WayScrawlJson.Options));
        await File.WriteAllTextAsync(preparingNote + ".tmp",
            JsonSerializer.Serialize(preparingMarker with { Stage = RetentionStage.BundlePending }, WayScrawlJson.Options));
        File.Move(preparingNote + ".tmp", preparingNote, overwrite: true);
        var preparedResume =
            await new RetentionCleaner(paths, config with { Retention = new(100, 130) }, Path.Combine(root, "Trash")).RunAsync(now);
        h.Check(preparedResume.Actions.Any(a => a.Bundle == preparing.Bundle && a.Complete) && !Directory.Exists(preparing.Bundle),
            "A crash after preparing and the pending-marker rename resumes with the same inode, even after retention thresholds increase.");
        var copied = await Make("copied", 10, linked: false);
        long copiedRawBytes = StorageAccounting.AllocatedBytes(copied.Raw);
        var copyResult = (await cleaner.RunAsync(now)).Actions.Single(a => a.Bundle == copied.Bundle);
        h.Check(copyResult.FreedBytes == copiedRawBytes && copyResult.TrashedBytes > 0 && !Directory.Exists(copied.Raw),
            "Copied media frees its independent raw recording as well as raw metadata.");
        var purge = await Make("purged", 10);
        long purgeBytes = StorageAccounting.AllocatedBytes([purge.Raw, Path.Combine(purge.Bundle, "media")]);
        var purging = new RetentionCleaner(paths, config with { Retention = new(7, 30, true) }, Path.Combine(root, "Trash"));
        var purged = (await purging.RunAsync(now)).Actions.Single(a => a.Bundle == purge.Bundle);
        h.Check(purged.FreedBytes == purgeBytes && purged.TrashedBytes == 0 && !Directory.Exists(purge.Raw) &&
                    !Directory.Exists(Path.Combine(purge.Bundle, "media")),
            "Explicit purge unlinks both recording references and counts their inode once.");
        var wholePurge = await Make("purged-bundle", 40);
        var allPurged = (await purging.RunAsync(now)).Actions.Single(a => a.Bundle == wholePurge.Bundle);
        h.Check(allPurged.Complete && !Directory.Exists(wholePurge.Bundle) && !Directory.Exists(wholePurge.Raw),
            "Explicit purge also expires whole bundles.");
        var later = await cleaner.RunAsync(now.AddDays(25));
        h.Check(later.Actions.Any(a => a.Bundle == media.Bundle && a.Kind == "bundle") && !Directory.Exists(media.Bundle),
            "Evidence-only bundles still expire at bundleDays.");
        var restored = await Make("restored", 10);
        await cleaner.RunAsync(now);
        Directory.CreateDirectory(Path.Combine(restored.Bundle, "media"));
        string restoredFile = Path.Combine(restored.Bundle, "media", "restored.txt");
        await File.WriteAllTextAsync(restoredFile, "new material");
        File.SetLastWriteTimeUtc(restoredFile, now.AddDays(25).UtcDateTime);
        var restorePass = await cleaner.RunAsync(now.AddDays(25));
        h.Check(!restorePass.Actions.Any(a => a.Bundle == restored.Bundle) && File.Exists(restoredFile),
            "A completed marker does not authorize deleting newly restored media.");
        var restoredMedia = await Make("restored-source-media", 10);
        var retiredMedia = (await cleaner.RunAsync(now)).Actions.Single(a => a.Bundle == restoredMedia.Bundle);
        Directory.Move(retiredMedia.TrashPath!, Path.Combine(restoredMedia.Bundle, "media"));
        File.Delete(Path.Combine(cleaner.TrashDirectory, "info", Path.GetFileName(retiredMedia.TrashPath!) + ".trashinfo"));
        var oldRestorePass = await cleaner.RunAsync(now.AddDays(25));
        h.Check(Directory.Exists(Path.Combine(restoredMedia.Bundle, "media")) &&
                    !oldRestorePass.Actions.Any(a => a.Bundle == restoredMedia.Bundle),
            "Restored source media stays protected even after its old preserved mtime and whole-bundle expiry threshold pass.");
        await AncestorAliases(h, root, config, now, fixtureVideo);
        Directory.Delete(Path.Combine(paths.Bundles, "foreign.wayscrawl"));
        var orphanFailed = await Make("orphan-failed", 10);
        var orphanDeleted = await Make("orphan-deleted-bundle", 10);
        var orphanActive = await Make("orphan-active", 10);
        var orphanFuture = await Make("orphan-future", -2);
        var orphanPinned = await Make("orphan-pinned", 10);
        foreach (var orphan in new[] { orphanFailed, orphanDeleted, orphanActive, orphanFuture, orphanPinned })
            Directory.Delete(orphan.Bundle, recursive: true);
        EvidenceBuilder.WriteRecord(
            orphanFailed.Raw, EvidenceBuilder.ReadRecord(orphanFailed.Raw) with { State = "failed", Bundle = null });
        EvidenceBuilder.WriteRecord(orphanActive.Raw, EvidenceBuilder.ReadRecord(orphanActive.Raw) with { State = "recording" });
        await File.WriteAllTextAsync(Path.Combine(orphanPinned.Raw, ".pinned"), "keep");
        foreach (var orphan in new[] { orphanFailed, orphanDeleted, orphanActive, orphanFuture, orphanPinned })
            Age(orphan.Raw, now.AddDays(-10).UtcDateTime);
        var oldLocation = await Make("old-custom-directory", 10);
        var oldLocationPinned = await Make("old-custom-pinned", 10);
        string historical = Path.Combine(root, "historical-bundles"); Directory.CreateDirectory(historical);
        foreach (var moved in new[] { oldLocation, oldLocationPinned })
        {
            string target = Path.Combine(historical, Path.GetFileName(moved.Bundle)); Directory.Move(moved.Bundle, target);
            EvidenceBuilder.WriteRecord(moved.Raw, EvidenceBuilder.ReadRecord(moved.Raw) with { Bundle = target });
            Age(moved.Raw, now.AddDays(-10).UtcDateTime);
        }
        string pinnedHistorical = Path.Combine(historical, Path.GetFileName(oldLocationPinned.Bundle));
        await File.WriteAllTextAsync(Path.Combine(pinnedHistorical, ".pinned"), "keep");
        Age(pinnedHistorical, now.AddDays(-10).UtcDateTime);
        var orphans = await new RetentionCleaner(
            paths, config with { BundleDirectory = Path.Combine(root, "never-created-bundle-root") }, Path.Combine(root, "Trash"))
                          .RunAsync(now);
        h.Check(Directory.Exists(orphanFailed.Raw) && Directory.Exists(orphanDeleted.Raw) && !Directory.Exists(oldLocation.Raw) &&
            orphans.Actions.Any(a => a.Bundle == oldLocation.Raw && a.Complete),
            "Unprocessed sessions keep their grace period; verified historical media authorizes raw retirement.");
        h.Check(new[] { orphanActive.Raw, orphanFuture.Raw, orphanPinned.Raw, oldLocationPinned.Raw }.All(Directory.Exists) &&
            Directory.Exists(Path.Combine(historical, Path.GetFileName(oldLocation.Bundle))),
            "Orphan cleanup preserves active/future sessions, raw pins and historical bundle pins; historical bundles remain untouched.");
        await CrossFilesystem(h, root, now);
        if (Directory.Exists("/dev/shm") && Libc.GetUid() != 0)
            await CrossBundle(h, paths, config, now, await Make("whole-cross", 40));
        foreach (string problem in new[] { "missing", "empty", "changed" })
        {
            var damaged = await Make("media-" + problem, 10, linked: false);
            string video = Path.Combine(damaged.Bundle, "media", "recording.mkv");
            if (problem == "changed") await File.WriteAllTextAsync(video, "damaged");
            else if (problem == "missing") Directory.Delete(Path.GetDirectoryName(video)!, true);
            else File.Delete(video);
            Age(damaged.Bundle, now.AddDays(-10).UtcDateTime);
            var damagedPass = await cleaner.RunAsync(now);
            h.Check(damagedPass.Actions.Any(a => a.Bundle == damaged.Bundle && !a.Complete) &&
                        File.Exists(Path.Combine(damaged.Raw, "recording.mkv")),
                "Default Trash policy preserves the last raw copy when published media is " + problem + ".");
        }
        if (Libc.GetUid() != 0)
        {
            var blocked = await Make("empty-directory-failure", 40);
            string parent = Path.Combine(blocked.Bundle, "blocked");
            Directory.CreateDirectory(Path.Combine(parent, "empty")); Age(blocked.Bundle, now.AddDays(-40).UtcDateTime);
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var blockedPass = await purging.RunAsync(now);
            h.Check(blockedPass.Actions.Any(a => a.Bundle == blocked.Bundle && !a.Complete) &&
                        File.Exists(Path.Combine(blocked.Bundle, "manifest.json")) &&
                        File.Exists(Path.Combine(blocked.Bundle, MediaFiles.RemovedNote)),
                "Empty-directory deletion failure preserves discoverable manifest and recovery state.");
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var finished = await purging.RunAsync(now);
            h.Check(finished.Actions.Any(a => a.Bundle == blocked.Bundle && a.Complete) && !Directory.Exists(blocked.Bundle),
                "Retry finishes whole-bundle purge after the directory permission is repaired.");
        }
        var renamedPinned = await Make("renamed-pinned-owner", 40);
        var renamedRecent = await Make("renamed-recent-owner", 40);
        string pinnedOwner = Path.Combine(paths.Bundles, "renamed-and-pinned.wayscrawl");
        string recentOwner = Path.Combine(paths.Bundles, "renamed-and-recent.wayscrawl");
        Directory.Move(renamedPinned.Bundle, pinnedOwner); Directory.Move(renamedRecent.Bundle, recentOwner);
        await File.WriteAllTextAsync(Path.Combine(pinnedOwner, ".pinned"), "keep raw session too");
        Age(pinnedOwner, now.AddDays(-40).UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(recentOwner, "frames/frame-00.png"), now.AddHours(-12).UtcDateTime);
        var renamedPass = await cleaner.RunAsync(now);
        h.Check(Directory.Exists(renamedPinned.Raw) && !renamedPass.Actions.Any(a => a.Bundle == renamedPinned.Raw),
            "A renamed pinned bundle still owns and protects its raw session by manifest identity.");
        h.Check(Directory.Exists(renamedRecent.Raw) && !renamedPass.Actions.Any(a => a.Bundle == renamedRecent.Raw) &&
                    Directory.Exists(Path.Combine(recentOwner, "media")),
            "A renamed unpinned bundle with recent changes keeps orphan cleanup from bypassing its retention safeguards.");
        await BindMount(h, root);
    }

    private static async Task InterruptionProtection(Harness h, string root, DateTimeOffset now, string fixtureVideo)
    {
        string area = Path.Combine(root, "interruption-protection");
        var paths = new WayScrawlPaths(Path.Combine(area, "run"), Path.Combine(area, "state"),
            Path.Combine(area, "data"), Path.Combine(area, "config"));
        string raw = Path.Combine(paths.Sessions, "interrupted-orphan"), trash = Path.Combine(area, "Trash");
        Directory.CreateDirectory(raw); File.Copy(fixtureVideo, Path.Combine(raw, "recording.mkv"));
        EvidenceBuilder.WriteRecord(raw, EvidenceBuilder.ReadRecord(Path.GetDirectoryName(fixtureVideo)!) with
        { Id = "interrupted-orphan", State = "failed", Bundle = null, CreatedUtc = now.AddDays(-40) });
        Age(raw, now.AddDays(-40).UtcDateTime);
        var config = new WayScrawlConfig(Notifications: false, CopyPathToClipboard: false);
        File.WriteAllText(trash, "interrupt Trash publication");
        var interrupted = await new RetentionCleaner(paths, config, trash).RunAsync(now);
        h.Check(Directory.Exists(raw) && interrupted.Actions.Any(a => a.Bundle == raw && !a.Complete),
            "An interrupted orphan transfer checkpoints its recovery policy before attempting Trash publication.");
        File.Delete(trash);
        var changedPolicy = config with { Retention = new(100, 130, true) };
        var cleaner = new RetentionCleaner(paths, changedPolicy, trash);
        var resumed = await cleaner.RunAsync(now);
        var transfer = resumed.Actions.Single(a => a.Bundle == raw);
        h.Check(transfer.Complete && transfer.TrashPath is not null && !Directory.Exists(raw),
            "A policy change resumes the orphan transfer with its original Trash policy.");
        Directory.Move(transfer.TrashPath!, raw);
        string pin = Path.Combine(raw, ".pinned"); File.WriteAllText(pin, "protect restored recording");
        for (int pass = 0; pass < 3; pass++)
        {
            if (pass == 1) File.Delete(pin);
            if (pass == 2) File.WriteAllText(pin, "pin again");
            var repeated = await cleaner.RunAsync(now.AddDays(200 + pass));
            h.Check(File.ReadAllBytes(Path.Combine(raw, "recording.mkv")).AsSpan().SequenceEqual(File.ReadAllBytes(fixtureVideo)) &&
                !repeated.Actions.Any(a => a.Bundle == raw) && Directory.GetFiles(Path.Combine(trash, "info")).Length == 1 &&
                Directory.GetDirectories(Path.Combine(trash, "files")).Length == 0,
                "Restoration and pin changes across repeated policy-changed passes preserve bytes without a second transfer: " + pass);
        }
    }

    private static async Task OwnerInventory(Harness h, string root, DateTimeOffset now, string fixtureVideo)
    {
        foreach (string kind in new[] { "malformed", "linked", "unreadable", "historical", "missing" })
        {
            if (kind == "unreadable" && Libc.GetUid() == 0) continue;
            string area = Path.Combine(root, "inventory-" + kind);
            var paths = new WayScrawlPaths(Path.Combine(area, "run"), Path.Combine(area, "state"),
                Path.Combine(area, "data"), Path.Combine(area, "config"));
            string bundle = Path.Combine(kind == "historical" ? Path.Combine(area, "previous") : paths.Bundles, "original.wayscrawl");
            await Demo.CreateAsync(bundle);
            var manifest = (await BundleStore.ReadAsync(bundle)) with { Id = "owner", CreatedUtc = now.AddDays(-40) };
            await File.WriteAllTextAsync(Path.Combine(bundle, "manifest.json"), JsonSerializer.Serialize(manifest, BundleStore.Json));
            string raw = Path.Combine(paths.Sessions, "owner"); Directory.CreateDirectory(raw);
            File.Copy(fixtureVideo, Path.Combine(raw, "recording.mkv"));
            await File.WriteAllTextAsync(Path.Combine(raw, "notes.txt"), "all raw files must survive");
            EvidenceBuilder.WriteRecord(raw, EvidenceBuilder.ReadRecord(Path.GetDirectoryName(fixtureVideo)!) with
            { Id = "owner", CreatedUtc = manifest.CreatedUtc, Bundle = bundle });
            Age(raw, now.AddDays(-40).UtcDateTime);
            string renamed = Path.Combine(Path.GetDirectoryName(bundle)!, "renamed.wayscrawl"); Directory.Move(bundle, renamed);
            await File.WriteAllTextAsync(Path.Combine(renamed, ".pinned"), "keep");
            string file = Path.Combine(renamed, "manifest.json");
            if (kind == "malformed") await File.WriteAllTextAsync(file, "{");
            if (kind == "linked")
            {
                string target = Path.Combine(area, "linked-manifest.json"); File.Move(file, target); File.CreateSymbolicLink(file, target);
            }
            if (kind == "unreadable") File.SetUnixFileMode(file, 0);
            Age(renamed, now.AddDays(-40).UtcDateTime);
            if (kind == "missing") Directory.Delete(renamed, recursive: true);
            var before = Directory.GetFiles(raw).ToDictionary(f => f, File.ReadAllBytes);
            try
            {
                var report = await new RetentionCleaner(paths, new() { Retention = new(7, 30, true) },
                    Path.Combine(area, "Trash")).RunAsync(now);
                if (kind == "missing")
                {
                    string? retained = report.Actions.Single(a => a.Bundle == raw).TrashPath;
                    h.Check(retained is not null && before.All(f =>
                        File.ReadAllBytes(Path.Combine(retained, Path.GetFileName(f.Key))).AsSpan().SequenceEqual(f.Value)),
                        "An unresolved recorded bundle path permits only recoverable Trash retirement, even under purge.");
                }
                else
                {
                    h.Check(before.All(f => File.Exists(f.Key) && File.ReadAllBytes(f.Key).AsSpan().SequenceEqual(f.Value)) &&
                        !report.Actions.Any(a => a.Bundle == raw),
                        "Unknown or pinned " + kind + " renamed ownership preserves every raw file, including under purge.");
                    if (kind != "historical")
                        h.Check(report.Skipped.Any(s => s.Contains("incomplete bundle inventory", StringComparison.OrdinalIgnoreCase)),
                            "An incomplete " + kind + " owner inventory reports why the orphan lane was disabled.");
                }
            }
            finally { if (kind == "unreadable") File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }
    }

    private static async Task OrphanRecovery(Harness h, string root, DateTimeOffset now, string fixtureVideo)
    {
        string area = Path.Combine(root, "orphan recovery's");
        var paths = new WayScrawlPaths(Path.Combine(area, "run"), Path.Combine(area, "state"),
            Path.Combine(area, "data"), Path.Combine(area, "config"));
        var config = new WayScrawlConfig(Notifications: false, CopyPathToClipboard: false);
        string trash = Path.Combine(area, "Trash");
        var cleaner = new RetentionCleaner(paths, config, trash);
        foreach (string state in new[] { "recorded", "failed" })
        {
            string raw = Path.Combine(paths.Sessions, state); Directory.CreateDirectory(raw);
            File.Copy(fixtureVideo, Path.Combine(raw, "recording.mkv"));
            File.Copy(Path.Combine(Path.GetDirectoryName(fixtureVideo)!, SessionRecord.JournalName),
                Path.Combine(raw, SessionRecord.JournalName));
            var record = EvidenceBuilder.ReadRecord(Path.GetDirectoryName(fixtureVideo)!) with
            { Id = state, State = state, CreatedUtc = now.AddDays(-10), Bundle = null };
            EvidenceBuilder.WriteRecord(raw, record); Age(raw, now.AddDays(-10).UtcDateTime);
            var young = await cleaner.RunAsync(now);
            h.Check(Directory.Exists(raw) && young.Actions.Length == 0,
                "Unprocessed " + state + " session is untouched before orphanDays.");
            var retired = await cleaner.RunAsync(now.AddDays(21));
            var action = retired.Actions.Single(a => a.Bundle == raw);
            h.Check(action.Complete && action.TrashPath is not null &&
                        File.ReadAllBytes(Path.Combine(action.TrashPath, "recording.mkv"))
                            .AsSpan()
                            .SequenceEqual(File.ReadAllBytes(fixtureVideo)) &&
                        File.Exists(Path.Combine(trash, "info", Path.GetFileName(action.TrashPath) + ".trashinfo")),
                "Expired " + state + " orphan retains recording bytes and a restore receipt in explicit Trash.");
            string warning = action.Warning ?? "";
            int commandStart = warning.IndexOf("wayscrawl process ", StringComparison.Ordinal);
            string[] arguments = commandStart < 0 ? [] : ShellWords(warning[commandStart..^" to rebuild.".Length]);
            h.Check(arguments.SequenceEqual(new[] { "wayscrawl", "process", raw }),
                "The recovery command preserves spaces and single quotes as one session-directory argument.");
            Directory.Move(action.TrashPath!, raw);
            var restored = await cleaner.RunAsync(now.AddDays(22));
            h.Check(Directory.Exists(raw) && !restored.Actions.Any(a => a.Bundle == raw),
                "Restored orphan is protected against another automatic transfer.");
            string bundle = await new EvidenceBuilder(paths, config, null).BuildAsync(raw, CancellationToken.None);
            h.Check((await BundleStore.ReadAsync(bundle)).Media.Length > 0,
                "Restored " + state + " orphan successfully reprocesses through the real pipeline.");
            File.WriteAllText(Path.Combine(bundle, ".pinned"), "retain processed fixture");
        }
        string purge = Path.Combine(paths.Sessions, "purge"); Directory.CreateDirectory(purge);
        File.Copy(fixtureVideo, Path.Combine(purge, "recording.mkv"));
        EvidenceBuilder.WriteRecord(purge, EvidenceBuilder.ReadRecord(Path.GetDirectoryName(fixtureVideo)!) with
        { Id = "purge", State = "failed", CreatedUtc = now.AddDays(-40), Bundle = null });
        Age(purge, now.AddDays(-40).UtcDateTime);
        var purged = await new RetentionCleaner(paths, config with { Retention = new(7, 30, true) }, trash).RunAsync(now);
        h.Check(!Directory.Exists(purge) && purged.Actions.Any(a => a.Bundle == purge && a.TrashPath is null && a.Complete),
            "Explicit purge permanently deletes an expired orphan.");
    }

    private static string[] ShellWords(string command)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        bool quoted = false, started = false;
        for (int i = 0; i < command.Length; i++)
        {
            char value = command[i];
            if (value == '\'') { quoted = !quoted; started = true; }
            else if (value == '\\' && !quoted)
            {
                if (++i == command.Length) return [];
                word.Append(command[i]); started = true;
            }
            else if (char.IsWhiteSpace(value) && !quoted)
            {
                if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
            }
            else { word.Append(value); started = true; }
        }
        if (quoted) return [];
        if (started) words.Add(word.ToString());
        return words.ToArray();
    }

    private static async Task AncestorAliases(Harness h, string root, WayScrawlConfig config, DateTimeOffset now, string fixtureVideo)
    {
        string real = Path.Combine(root, "real-xdg"), alias = Path.Combine(root, "xdg-alias");
        Directory.CreateDirectory(real); Directory.CreateSymbolicLink(alias, real);
        var paths = new WayScrawlPaths(
            Path.Combine(alias, "run"), Path.Combine(alias, "state"), Path.Combine(alias, "data/wayscrawl"), Path.Combine(alias, "config"));
        string raw = Path.Combine(paths.Sessions, "alias-session"); Directory.CreateDirectory(raw);
        var sample = CoreChecks.SampleBundle();
        EvidenceBuilder.WriteRecord(raw, new("alias-session", now.AddDays(-40), "recorded", "fixture",
                                             new("fixture", sample.Geometries[0], CompositionMode.CapturedInk, "fixture"), 0,
                                             "recording.mkv", null, sample.DurationUs, null, [], []));
        File.Copy(fixtureVideo, Path.Combine(raw, "recording.mkv")); Age(raw, now.AddDays(-10).UtcDateTime);
        using (OperationLease.Acquire(raw, paths.Runtime))
            h.Check(!OperationLease.Available(Path.Combine(real, "state/sessions/alias-session"), paths.Runtime),
                "Resolved symlinked ancestors share the real directory's operation lease.");
        string direct = Path.Combine(paths.Sessions, "direct-link"); Directory.CreateSymbolicLink(direct, raw);
        await h.RejectAsync<IOException>(() => Task.Run(() => { using var ignored = OperationLease.Acquire(direct, paths.Runtime); }),
            "An operation directory that is itself a symlink remains rejected.");
        File.CreateSymbolicLink(Path.Combine(raw, "linked-payload"), Path.Combine(root, "outside.txt"));
        var linkedPass = await new RetentionCleaner(paths, config, Path.Combine(root, "Trash")).RunAsync(now);
        h.Check(Directory.Exists(raw) && linkedPass.Actions.Length == 0,
            "Symlinks inside a managed tree remain protected under aliased ancestors.");
        File.Delete(Path.Combine(raw, "linked-payload")); Age(raw, now.AddDays(-10).UtcDateTime);
        var pass = await new RetentionCleaner(paths, config, Path.Combine(root, "Trash")).RunAsync(now);
        h.Check(pass.Actions.Any(a => a.Kind == "raw" && a.Complete) && !Directory.Exists(raw),
            "Retention works with legitimate symlinked XDG ancestors even without a published bundle directory.");
        DurableTrash(h, real, now);
        FreshTrash(h, real, now);
        SyncFailures(h, real, now);
        HardLinkAliases(h, real, now);
        ChangedAlias(h, real, now);
        CancelRehash(h, real, now);
        await VerifiedChanges(h, real, now);
    }

    private static void DurableTrash(Harness h, string root, DateTimeOffset now)
    {
        string source = Path.Combine(root, "durable-source"), trash = Path.Combine(root, "durable-trash");
        string id = Guid.NewGuid().ToString("N"), destination = Trash.Destination(source, trash, id);
        // Resume a published copy so this check runs without a second filesystem or mount privileges.
        foreach (string tree in new[] { source, destination })
        {
            Directory.CreateDirectory(Path.Combine(tree, "nested", "empty"));
            File.WriteAllText(Path.Combine(tree, "data.txt"), "retained root bytes");
            File.WriteAllText(Path.Combine(tree, "nested", "data.txt"), "retained nested bytes");
        }
        string metadata = Path.Combine(trash, "info", Path.GetFileName(destination) + ".trashinfo");
        string[] expected = [destination, Path.Combine(destination, "nested"), Path.Combine(destination, "nested", "empty"),
            Path.Combine(destination, "data.txt"), Path.Combine(destination, "nested", "data.txt"), metadata,
            Path.Combine(trash, "files"), Path.Combine(trash, "info"), trash];
        var synced = new List<string>();
        var item = Trash.Move(source, trash, now, id, beforeDelete: () =>
        {
            h.Check(expected.ToHashSet(StringComparer.Ordinal).IsSubsetOf(synced) &&
                        File.Exists(Path.Combine(source, "data.txt")) && File.Exists(Path.Combine(source, "nested", "data.txt")),
                "Every retained file, directory and Trash receipt is synced before the first source unlink.");
        }, afterSync: synced.Add);
        h.Check(item.Problem is null && !Directory.Exists(source) && File.Exists(metadata),
            "A durably retained Trash copy completes source removal and keeps its restore receipt.");
    }

    private static void FreshTrash(Harness h, string root, DateTimeOffset now)
    {
        string parent = Path.Combine(root, "fresh-source-parent"), source = Path.Combine(parent, "capture");
        string missing = Path.Combine(root, "fresh-missing"), ancestor = Path.Combine(missing, "nested");
        string trash = Path.Combine(ancestor, "Trash"), id = Guid.NewGuid().ToString("N");
        string destination = Trash.Destination(source, trash, id);
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        File.WriteAllText(Path.Combine(source, "data.txt"), "fresh retained bytes");
        var synced = new List<string>();
        string[] created = [missing, ancestor, trash, Path.Combine(trash, "files"), Path.Combine(trash, "info")];
        const UnixFileMode writable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        // Deny rename at the source parent to exercise the real copy fallback without another mount.
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var item = Trash.Move(source, trash, now, id, beforeDelete: () =>
            {
                h.Check(created.All(directory => synced.Contains(directory) && synced.Contains(Path.GetDirectoryName(directory)!)) &&
                            synced.Contains(Path.Combine(destination, "data.txt")) &&
                            File.Exists(Path.Combine(source, "data.txt")),
                    "Fresh Trash syncs every created directory and its parent before the first source unlink.");
                File.SetUnixFileMode(parent, writable);
            }, afterSync: synced.Add);
            h.Check(item.Problem is null && !Directory.Exists(source) &&
                        File.ReadAllText(Path.Combine(destination, "data.txt")) == "fresh retained bytes",
                "A fresh nested Trash copy completes only after its directory entries and retained bytes are synced.");
        }
        finally { File.SetUnixFileMode(parent, writable); }
    }

    private static void SyncFailures(Harness h, string root, DateTimeOffset now)
    {
        foreach (var (kind, errno) in new[] { ("file", 22), ("file", 5), ("directory", 22), ("directory", 95) })
        {
            string source = Path.Combine(root, $"fsync-{kind}-{errno}-source");
            string trash = Path.Combine(root, $"fsync-{kind}-{errno}-trash"), id = Guid.NewGuid().ToString("N");
            string destination = Trash.Destination(source, trash, id);
            foreach (string tree in new[] { source, destination })
            {
                Directory.CreateDirectory(tree);
                File.WriteAllText(Path.Combine(tree, "data.txt"), "recoverable bytes");
                File.WriteAllText(Path.Combine(tree, SessionRecord.FileName), "recovery identity");
            }
            string failed = kind == "file" ? Path.Combine(destination, "data.txt") : destination;
            bool injected = false, deleting = false;
            string? problem = null;
            try
            {
                Trash.Move(source, trash, now, id, [SessionRecord.FileName], beforeDelete: () => deleting = true,
                    syncError: path =>
                    {
                        if (path != failed) return 0;
                        injected = true; return errno;
                    });
            }
            catch (IOException error) { problem = error.Message; }
            h.Check(injected && !deleting && problem is not null && problem.Contains($"errno {errno}") &&
                        File.ReadAllText(Path.Combine(source, "data.txt")) == "recoverable bytes" &&
                        File.Exists(Path.Combine(source, SessionRecord.FileName)),
                $"An fsync {kind} failure (errno {errno}) is reported and preserves every source file pending retry.");
            var retry = Trash.Move(source, trash, now, id, [SessionRecord.FileName]);
            h.Check(retry.Problem is null && !Directory.Exists(source),
                $"A pending fsync {kind} failure (errno {errno}) resumes safely with the same Trash item on retry.");
        }
    }

    private static void HardLinkAliases(Harness h, string root, DateTimeOffset now)
    {
        string source = Path.Combine(root, "alias-source"), trash = Path.Combine(root, "alias-trash");
        string id = Guid.NewGuid().ToString("N"), destination = Trash.Destination(source, trash, id);
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        string first = Path.Combine(source, "first.txt"), second = Path.Combine(source, "second.txt");
        File.WriteAllText(first, "shared inode bytes");
        h.Check(Libc.TryHardLink(first, second), "Trash alias regression uses two names of the same real inode.");
        foreach (string name in new[] { "first.txt", "second.txt" })
            File.Copy(Path.Combine(source, name), Path.Combine(destination, name));
        var item = Trash.Move(source, trash, now, id);
        h.Check(item.Problem is null && !Directory.Exists(source) &&
                    File.ReadAllText(Path.Combine(destination, "first.txt")) == "shared inode bytes" &&
                    File.ReadAllText(Path.Combine(destination, "second.txt")) == "shared inode bytes",
            "Both verified hard-link aliases retire in one pass despite the first unlink changing inode ctime and link count.");
    }

    private static void ChangedAlias(Harness h, string root, DateTimeOffset now)
    {
        string source = Path.Combine(root, "changed-alias-source"), trash = Path.Combine(root, "changed-alias-trash");
        string id = Guid.NewGuid().ToString("N"), destination = Trash.Destination(source, trash, id);
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        string first = Path.Combine(source, "first.txt"), second = Path.Combine(source, "second.txt");
        string metadata = Path.Combine(source, SessionRecord.FileName);
        File.WriteAllText(first, "verified"); File.WriteAllText(metadata, "recovery identity");
        File.SetLastWriteTimeUtc(first, now.UtcDateTime);
        h.Check(Libc.TryHardLink(first, second), "Changed-alias regression uses two names of the same real inode.");
        foreach (string name in new[] { "first.txt", "second.txt", SessionRecord.FileName })
            File.Copy(Path.Combine(source, name), Path.Combine(destination, name));
        string? changed = null;
        var item = Trash.Move(source, trash, now, id, [SessionRecord.FileName], afterUnlink: removed =>
        {
            if (changed is not null) return;
            string survivor = removed == first ? second : first;
            h.Check(!File.Exists(removed) && File.Exists(survivor),
                "Alias mutation occurs after the first unlink and before the surviving alias's final check.");
            DateTime modified = File.GetLastWriteTimeUtc(survivor);
            File.WriteAllText(survivor, "replaced"); File.SetLastWriteTimeUtc(survivor, modified);
            changed = survivor;
        });
        h.Check(changed is not null && item.Problem is not null && File.Exists(changed) &&
                    File.ReadAllText(changed) == "replaced" && File.Exists(metadata),
            "Changed bytes on a surviving alias with restored mtime remain recoverable and the transfer stays pending.");
        h.Check(File.ReadAllText(Path.Combine(destination, "first.txt")) == "verified" &&
                    File.ReadAllText(Path.Combine(destination, "second.txt")) == "verified",
            "The verified Trash copies survive a same-size alias edit between unlinks.");
    }

    private static void CancelRehash(Harness h, string root, DateTimeOffset now)
    {
        string source = Path.Combine(root, "cancel-source"), trash = Path.Combine(root, "cancel-trash");
        string id = Guid.NewGuid().ToString("N"), destination = Trash.Destination(source, trash, id);
        byte[] payload = new byte[192 * 1024]; new Random(39).NextBytes(payload);
        foreach (string tree in new[] { source, destination })
        {
            Directory.CreateDirectory(tree);
            foreach (string name in new[] { "first.bin", "second.bin" }) File.WriteAllBytes(Path.Combine(tree, name), payload);
            File.WriteAllText(Path.Combine(tree, SessionRecord.FileName), "recovery identity");
        }
        using var cancellation = new CancellationTokenSource();
        var chunks = new Dictionary<string, int>(StringComparer.Ordinal);
        var item = Trash.Move(source, trash, now, id, [SessionRecord.FileName], cancellationToken: cancellation.Token,
            afterRehashChunk: path =>
            {
                if (!path.EndsWith(".bin", StringComparison.Ordinal)) return;
                chunks[path] = chunks.GetValueOrDefault(path) + 1;
                if (chunks.Count == 2 && chunks[path] == 2) cancellation.Cancel();
            });
        h.Check(cancellation.IsCancellationRequested && chunks.Count == 2 && chunks.Values.Min() == 2 && item.Problem is not null &&
                    new[] { "first.bin", "second.bin" }.All(name => File.Exists(Path.Combine(source, name)) &&
                        File.ReadAllBytes(Path.Combine(source, name)).AsSpan().SequenceEqual(payload)) &&
                    File.Exists(Path.Combine(source, SessionRecord.FileName)),
            "Cancelling inside a later file's chunked rehash leaves all source bytes and recovery identity intact and pending.");
        h.Check(File.Exists(Path.Combine(trash, "info", Path.GetFileName(destination) + ".trashinfo")) &&
                    File.ReadAllBytes(Path.Combine(destination, "second.bin")).AsSpan().SequenceEqual(payload),
            "Cancellation keeps the published Trash copy and its restore receipt.");
        var resumed = Trash.Move(source, trash, now, id, [SessionRecord.FileName]);
        h.Check(resumed.Problem is null && !Directory.Exists(source), "A cancelled rehash completes safely on the next uncancelled pass.");
    }

    private static async Task VerifiedChanges(Harness h, string root, DateTimeOffset now)
    {
        foreach (string change in new[] { "replace", "append", "new-filename", "same-size-restored-mtime" })
        {
            string source = Path.Combine(root, "verified-" + change), trash = Path.Combine(root, "verified-trash-" + change);
            string id = Guid.NewGuid().ToString("N"), destination = Trash.Destination(source, trash, id);
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            string file = Path.Combine(source, "data.txt"), metadata = Path.Combine(source, SessionRecord.FileName);
            File.WriteAllText(file, "verified"); File.WriteAllText(metadata, "recovery identity");
            File.Copy(file, Path.Combine(destination, "data.txt")); File.Copy(metadata, Path.Combine(destination, SessionRecord.FileName));
            string changed = change == "new-filename" ? Path.Combine(source, "new.txt") : file;
            string expected = change == "append" ? "verified appended bytes" :
                change == "same-size-restored-mtime" ? "replaced" : "new unverified bytes";
            var item = Trash.Move(source, trash, now, id, [SessionRecord.FileName], beforeDelete: () =>
            {
                if (change == "replace")
                {
                    string replacement = Path.Combine(root, "replacement.txt"); File.WriteAllText(replacement, expected);
                    File.Move(replacement, file, overwrite: true);
                }
                else if (change == "append") File.AppendAllText(file, " appended bytes");
                else if (change == "same-size-restored-mtime")
                {
                    DateTime modified = File.GetLastWriteTimeUtc(file);
                    File.WriteAllText(file, expected); File.SetLastWriteTimeUtc(file, modified);
                }
                else File.WriteAllText(changed, expected);
            });
            h.Check(item.Problem is not null && File.Exists(changed) && File.ReadAllText(changed) == expected && File.Exists(metadata),
                "A " + change + " after verification preserves changed bytes and recovery metadata and leaves the transfer pending.");
            h.Check(File.ReadAllText(Path.Combine(destination, "data.txt")) == "verified",
                "The published verified copy remains intact after a source " + change + ".");
        }
    }

    private static async Task BindMount(Harness h, string root)
    {
        var (executable, prefix) = SelfCommand.Resolve();
        string fixture = Path.Combine(root, "mount-fixture"); Directory.CreateDirectory(fixture);
        var result = await ProcessRunner.RunAsync("unshare", ["--user", "--map-root-user", "--mount", "--propagation", "private",
            executable, .. prefix, "--trash-mount-fixture", fixture], CancellationToken.None, environment: new Dictionary<string, string>
            { ["WAYSCRAWL_TEST_PARENT_MOUNT"] = new FileInfo("/proc/self/ns/mnt").LinkTarget! });
        if (result.ExitCode != 0 && result.Error.Contains("unshare failed"))
        { Console.WriteLine("Skipped private bind-mount Trash fixture: " + result.Error.Trim()); return; }
        h.Check(result.ExitCode == 0 && result.Text.Contains("bind-mount Trash passed"),
            "Same-device EXDEV rename falls back to verified copy in a private mount namespace: " + result.Error);
        h.Check(result.ExitCode == 0 && result.Text.Contains("bounded ENOSPC Trash passed"),
            "A real ENOSPC on a private 64 KiB tmpfs discards the partial Trash copy while preserving its source: " + result.Error);
    }

    internal static async Task<int> MountFixtureAsync(string root)
    {
        string? parentMount = Environment.GetEnvironmentVariable("WAYSCRAWL_TEST_PARENT_MOUNT");
        if (Libc.GetUid() != 0 || parentMount is null || parentMount == new FileInfo("/proc/self/ns/mnt").LinkTarget ||
            !Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "wayscrawl-checks-retention-"), StringComparison.Ordinal) ||
            PathSafety.IsLink(root))
            return 64;
        string left = Path.Combine(root, "left"), right = Path.Combine(root, "right");
        string backingLeft = Path.Combine(root, "backing-left"), backingRight = Path.Combine(root, "backing-right");
        foreach (string directory in new[] { left, right, backingLeft, backingRight }) Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(backingLeft, "capture", "nested", "empty"));
        await File.WriteAllTextAsync(Path.Combine(backingLeft, "capture", "data.txt"), "bind-mount data");
        await ProcessRunner.RunCheckedAsync("mount", ["--bind", backingLeft, left], CancellationToken.None);
        await ProcessRunner.RunCheckedAsync("mount", ["--bind", backingRight, right], CancellationToken.None);
        string a = (await ProcessRunner.RunCheckedAsync("stat", ["-c", "%d", left], CancellationToken.None)).Text;
        string b = (await ProcessRunner.RunCheckedAsync("stat", ["-c", "%d", right], CancellationToken.None)).Text;
        if (a != b) return 1;
        string source = Path.Combine(left, "capture");
        try { Directory.Move(source, Path.Combine(right, "rename-probe")); return 1; } catch (IOException) { }
        string trash = Path.Combine(right, "Trash"), id = Guid.NewGuid().ToString("N");
        string destination = Trash.Destination(source, trash, id);
        string[] expectedSyncs = [destination, Path.Combine(destination, "nested"), Path.Combine(destination, "nested", "empty"),
            Path.Combine(destination, "data.txt"), Path.Combine(trash, "info", Path.GetFileName(destination) + ".trashinfo"),
            Path.Combine(trash, "files"), Path.Combine(trash, "info"), trash];
        var synced = new List<string>();
        var moved = Trash.Move(source, trash, DateTimeOffset.UtcNow, id, beforeDelete: () =>
        {
            if (!expectedSyncs.ToHashSet(StringComparer.Ordinal).IsSubsetOf(synced) || !File.Exists(Path.Combine(source, "data.txt")))
                throw new IOException("Copied Trash files and directories were not synced before source unlink.");
        }, afterSync: synced.Add);
        if (moved.Problem is not null || Directory.Exists(source) ||
            await File.ReadAllTextAsync(Path.Combine(moved.Path, "data.txt")) != "bind-mount data")
            return 1;
        Console.WriteLine("bind-mount Trash passed");
        string tiny = Path.Combine(root, "tiny-trash-mount"), fullSource = Path.Combine(root, "space-limited-source");
        Directory.CreateDirectory(tiny); Directory.CreateDirectory(fullSource);
        await ProcessRunner.RunCheckedAsync("mount", ["-t", "tmpfs", "-o", "size=64k", "tmpfs", tiny], CancellationToken.None);
        byte[] payload = new byte[256 * 1024]; new Random(42).NextBytes(payload);
        string payloadFile = Path.Combine(fullSource, "payload.bin"); await File.WriteAllBytesAsync(payloadFile, payload);
        try { Trash.Move(fullSource, Path.Combine(tiny, "Trash"), DateTimeOffset.UtcNow); return 1; }
        catch (IOException error)
        {
            if ((error.HResult & 0xffff) != 28 && !error.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return 1;
            if (!File.ReadAllBytes(payloadFile).AsSpan().SequenceEqual(payload) ||
                Directory.GetDirectories(Path.Combine(tiny, "Trash/files"), ".*.partial").Length != 0) return 1;
        }
        Console.WriteLine("bounded ENOSPC Trash passed"); return 0;
    }

    private static async Task CrossBundle(Harness h, WayScrawlPaths paths, WayScrawlConfig config,
        DateTimeOffset now, (string Bundle, string Raw) capture)
    {
        string trash = Path.Combine("/dev/shm", "wayscrawl-bundle-check-" + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(capture.Bundle, "frames");
        try
        {
            File.SetUnixFileMode(frames, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var pending = (await new RetentionCleaner(paths, config, trash).RunAsync(now)).Actions.Single(a => a.Bundle == capture.Bundle);
            h.Check(!pending.Complete && !File.Exists(Path.Combine(capture.Bundle, "summary.md")) &&
                        File.Exists(Path.Combine(capture.Bundle, "manifest.json")),
                "Cross-device whole-bundle interruption keeps its marker/manifest after partial payload deletion.");
            File.SetUnixFileMode(frames, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var resumed = await new RetentionCleaner(paths, config with { Retention = new(7, 100) }, trash).RunAsync(now);
            h.Check(resumed.Actions.Any(a => a.Bundle == capture.Bundle && a.Complete) && !Directory.Exists(capture.Bundle),
                "Whole-bundle deletion resumes against its stable verified copy, even after the expiry threshold changes.");
        }
        finally
        {
            if (Directory.Exists(frames))
                File.SetUnixFileMode(frames, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (Directory.Exists(trash))
            {
                foreach (string directory in Directory.GetDirectories(trash, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(trash, true);
            }
        }
    }

    private static async Task<long> AllocatedExceptOriginal(string raw)
    {
        long sum = 0;
        foreach (string file in Directory.GetFiles(raw).Where(f => Path.GetFileName(f) != "recording.mkv"))
        {
            var result = await ProcessRunner.RunCheckedAsync("stat", ["-c", "%b", file], CancellationToken.None);
            sum += long.Parse(result.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture) * 512;
        }
        return sum;
    }

    private static async Task CrossFilesystem(Harness h, string root, DateTimeOffset now)
    {
        if (!Directory.Exists("/dev/shm")) { Console.WriteLine("Skipped cross-filesystem Trash fixture: /dev/shm absent."); return; }
        string source = Path.Combine(root, "cross-filesystem"),
               trash = Path.Combine("/dev/shm", "wayscrawl-trash-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "data 猫.txt"); await File.WriteAllTextAsync(file, "cross-volume recoverable data");
        string id = Guid.NewGuid().ToString("N");
        try
        {
            bool canDenyDeletion = Libc.GetUid() != 0;
            if (canDenyDeletion) File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var item = Trash.Move(source, trash, now, id);
            h.Check(await File.ReadAllTextAsync(Path.Combine(item.Path, "data 猫.txt")) == "cross-volume recoverable data",
                "Cross-filesystem Trash publishes a verified recoverable copy.");
            if (canDenyDeletion)
            {
                h.Check(item.Problem is not null && Directory.Exists(source),
                    "Failed source deletion keeps both source and committed Trash copy.");
                File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                item = Trash.Move(source, trash, now, id);
            }
            h.Check(
                item.Problem is null && !Directory.Exists(source), "Cross-filesystem retry safely finishes deleting the verified source.");
            if (canDenyDeletion)
            {
                Directory.CreateDirectory(source);
                string readable = Path.Combine(source, "already-copied.txt"), denied = Path.Combine(source, "unreadable.txt");
                await File.WriteAllTextAsync(readable, new string('x', 8192)); await File.WriteAllTextAsync(denied, "unreadable");
                File.SetUnixFileMode(denied, 0);
                try
                {
                    bool failed = false;
                    try { Trash.Move(source, trash, now, Guid.NewGuid().ToString("N")); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
                    h.Check(failed && File.Exists(readable) && File.Exists(denied) &&
                        Directory.GetDirectories(Path.Combine(trash, "files"), ".*.partial").Length == 0,
                        "A failed cross-volume copy releases its unpublished partial allocation and leaves all source data intact.");
                }
                finally { File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            }
        }
        finally
        {
            if (Directory.Exists(source))
                File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (Directory.Exists(trash))
            {
                foreach (string directory in Directory.GetDirectories(trash, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(trash, true);
            }
        }
    }

    private static void Age(string root, DateTime time)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, time);
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(directory, time);
        Directory.SetLastWriteTimeUtc(root, time);
    }
}
