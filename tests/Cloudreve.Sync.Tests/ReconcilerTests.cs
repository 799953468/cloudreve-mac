using Cloudreve.Desktop;
using Cloudreve.Sync;
using Xunit;

namespace Cloudreve.Sync.Tests;

// The reconcilers are pure: every case here is built in memory, with no files or network.
public sealed class ReconcilerTests
{
    private const string Volume = "AAAA";
    private readonly Dictionary<string, FileBaseline> baseline = [];
    private readonly PairInventory local = new();
    private readonly PairInventory remote = new();
    private readonly ConflictSet conflicts = new();
    private readonly HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);

    private ReconcileView View(string rules = "")
    {
        return new ReconcileView(baseline, local, remote, new PairIgnoreRules(rules), conflicts, blocked);
    }

    private static string LocalId(int n)
    {
        return $"{Volume}:{n:X16}:0000000000000001:00000000";
    }

    private void LocalDir(string key, int id)
    {
        local.Nodes[key] = new PairNode(key, NodeKind.Directory, LocalId: LocalId(id));
    }

    private void RemoteDir(string key, string id)
    {
        remote.Nodes[key] = new PairNode(key, NodeKind.Directory, Remote: new DriveEntry(Path.GetFileName(key), "cloudreve://my/" + key, true, 0, Id: id));
    }

    private void LocalFile(string key, string hash, int id)
    {
        local.Nodes[key] = new PairNode(key, NodeKind.File, hash, LocalId: LocalId(id));
    }

    private void RemoteFile(string key, string version, string id)
    {
        remote.Nodes[key] = new PairNode(key, NodeKind.File, Remote: new DriveEntry(Path.GetFileName(key), "cloudreve://my/" + key, false, 1, version, Id: id));
    }

    private DirectoryDecision Directory(string key, bool adopt = true)
    {
        var view = View();
        var scanned = local.Nodes.Values.Where(node => node.LocalId != null).Select(node => node.LocalId!).ToHashSet();
        var tracked = baseline.Values.Where(entry => entry.LocalId != null).Select(entry => entry.LocalId!).ToHashSet();
        return DirectoryReconciler.Decide(key, view, adopt, scanned, tracked);
    }

    [Fact]
    public void NewFoldersAreCreatedOnTheMissingSide()
    {
        LocalDir("here", 1);
        RemoteDir("there", "r1");
        LocalDir("both", 2);
        RemoteDir("both", "r2");
        Assert.Equal(DirectoryAction.CreateRemote, Directory("here").Action);
        Assert.Equal(DirectoryAction.CreateLocal, Directory("there").Action);
        Assert.Equal(DirectoryAction.Record, Directory("both").Action);
        Assert.Equal(["both", "here", "there"], DirectoryReconciler.Keys(View()));
    }

    [Fact]
    public void DeletedFoldersPropagateOnlyWithVerifiedIdentities()
    {
        baseline["gone-here"] = new FileBaseline("", "", LocalId(1), "r1", true);
        RemoteDir("gone-here", "r1");
        baseline["gone-there"] = new FileBaseline("", "", LocalId(2), "r2", true);
        LocalDir("gone-there", 2);
        baseline["legacy"] = new FileBaseline("", "", null, null, true);
        LocalDir("legacy", 3);
        Assert.Equal(DirectoryAction.TrashWhenVerified, Directory("gone-here").Action);
        Assert.Equal(DirectoryAction.RemoveLocal, Directory("gone-there").Action);
        Assert.Equal(DirectoryDecision.Conflict(ConflictCause.UnverifiedIdentity), Directory("legacy"));
    }

    [Fact]
    public void ConflictCausesNameWhatBlocksAFolder()
    {
        LocalDir("mixed", 1);
        RemoteFile("mixed", "v1", "r1");
        local.Nodes["link"] = new PairNode("link", NodeKind.Unsupported);
        baseline["swapped"] = new FileBaseline("", "", LocalId(3), "r3", true);
        LocalDir("swapped", 3);
        RemoteDir("swapped", "other");
        Assert.Equal(DirectoryDecision.Conflict(ConflictCause.TypeMismatch), Directory("mixed"));
        RemoteDir("link", "r2");
        Assert.Equal(DirectoryDecision.Conflict(ConflictCause.UnsupportedItem), Directory("link"));
        Assert.Equal(DirectoryDecision.Conflict(ConflictCause.CloudFolderReplaced), Directory("swapped"));
    }

    [Fact]
    public void RecreatedFolderIsAdoptedOnlyWhenNothingElseExplainsIt()
    {
        baseline["build"] = new FileBaseline("", "", LocalId(1), "r1", true);
        LocalDir("build", 2);
        RemoteDir("build", "r1");
        Assert.Equal(DirectoryAction.Record, Directory("build").Action);
        Assert.Equal(DirectoryDecision.Conflict(ConflictCause.DirectoryReplaced), Directory("build", adopt: false));

        // The old folder still exists elsewhere: that is a move, not a rebuild.
        LocalDir("archive", 1);
        Assert.Equal(DirectoryDecision.Conflict(ConflictCause.DirectoryReplaced), Directory("build"));
        local.Nodes.Remove("archive");

        // Another volume cannot vouch for the old folder.
        local.Nodes["build"] = new PairNode("build", NodeKind.Directory, LocalId: "BBBB:0000000000000002:0000000000000001:00000000");
        Assert.Equal(DirectoryDecision.Conflict(ConflictCause.DirectoryReplaced), Directory("build"));
    }

    [Fact]
    public void BlockedAndIgnoredFoldersAreLeftAlone()
    {
        LocalDir("parent", 1);
        LocalDir("parent/child", 2);
        blocked.Add("parent");
        baseline["ignored"] = new FileBaseline("", "", LocalId(3), "r3", true);
        Assert.Equal(DirectoryAction.Skip, Directory("parent/child").Action);
        Assert.Equal(DirectoryAction.Forget, DirectoryReconciler.Decide("ignored", View("ignored/"), true, [], []).Action);
    }

    [Fact]
    public void FileDecisionsFollowTheThreeWayState()
    {
        LocalFile("new-here", "H1", 1);
        RemoteFile("new-there", "v1", "r1");
        LocalFile("both", "H2", 2);
        RemoteFile("both", "v2", "r2");
        baseline["deleted-there"] = new FileBaseline("H3", "v3", LocalId(3), "r3");
        LocalFile("deleted-there", "H3", 3);
        baseline["edited-both"] = new FileBaseline("H4", "v4", LocalId(4), "r4");
        LocalFile("edited-both", "H4b", 4);
        RemoteFile("edited-both", "v4b", "r4");
        baseline["deleted-here-edited-there"] = new FileBaseline("H5", "v5", LocalId(5), "r5");
        RemoteFile("deleted-here-edited-there", "v5b", "r5");
        var view = View();
        Assert.Equal((FileStep.Transfer, SyncAction.Upload), Step(FileReconciler.Decide("new-here", view)));
        Assert.Equal((FileStep.Transfer, SyncAction.Download), Step(FileReconciler.Decide("new-there", view)));
        Assert.Equal((FileStep.Transfer, SyncAction.CompareContents), Step(FileReconciler.Decide("both", view)));
        Assert.Equal(FileStep.RecoverLocal, FileReconciler.Decide("deleted-there", view).Step);
        var edited = FileReconciler.Decide("edited-both", view);
        Assert.Equal((FileStep.Transfer, SyncAction.CompareContents), Step(edited));
        var deleted = FileReconciler.Decide("deleted-here-edited-there", view);
        Assert.Equal((FileStep.ConflictWithChoice, ConflictCause.DeletedLocally), (deleted.Step, deleted.Cause));

        static (FileStep, SyncAction) Step(FileDecision decision)
        {
            return (decision.Step, decision.Action);
        }
    }

    [Fact]
    public void FileConflictsNameTheirCause()
    {
        local.Nodes["link"] = new PairNode("link", NodeKind.Unsupported);
        remote.Nodes["Case"] = new PairNode("case", NodeKind.File, Remote: new DriveEntry("case", "cloudreve://my/case", false, 1, "v1"));
        LocalFile("Case", "H1", 1);
        baseline["was-file"] = new FileBaseline("H2", "v2", LocalId(2), "r2");
        LocalDir("was-file", 3);
        var view = View();
        Assert.Equal(ConflictCause.UnsupportedItem, FileReconciler.Decide("link", view).Cause);
        Assert.Equal(ConflictCause.NameCollision, FileReconciler.Decide("Case", view).Cause);
        Assert.Equal(ConflictCause.TypeMismatch, FileReconciler.Decide("was-file", view).Cause);
        Assert.Equal(ConflictCause.DeletedInCloud, FileReconciler.ChoiceCause("H", null));
        Assert.Equal(ConflictCause.ContentsDiffer, FileReconciler.ChoiceCause("H", new DriveEntry("a", "cloudreve://my/a", false, 1)));
    }

    [Fact]
    public void ADuplicateNameIsReportedAsACollision()
    {
        remote.Nodes["doc"] = new PairNode("doc", NodeKind.Unsupported);
        remote.Collisions.Add("doc");
        Assert.Equal(ConflictCause.NameCollision, FileReconciler.Decide("doc", View()).Cause);
    }

    [Fact]
    public void OwnedUploadPlaceholdersAreCheckedBeforeOtherDecisions()
    {
        remote.Nodes["doc"] = new PairNode("doc", NodeKind.Unsupported,
            Remote: new DriveEntry("doc", "cloudreve://my/doc", false, 1, SkipSync: true, UploadSessionId: "s1"));
        Assert.Equal(FileStep.CheckOwnedUpload, FileReconciler.Decide("doc", View()).Step);
        Assert.Equal(FileStep.Conflict, FileReconciler.Decide("doc", View(), ownedUploadChecked: true).Step);
    }

    [Fact]
    public void MovesNeedOneSourceOneDestinationAndUnchangedContent()
    {
        baseline["old"] = new FileBaseline("H", "v1", LocalId(1), "r1");
        LocalFile("new", "H", 1);
        RemoteFile("old", "v1", "r1");
        var view = View();
        var candidates = MoveReconciler.Candidates(view, CancellationToken.None);
        var move = MoveReconciler.Decide("old", baseline["old"], view, MoveReconciler.Index(view, candidates));
        Assert.NotNull(move);
        Assert.True(move.LocalMoved);
        Assert.False(move.RemoteMoved);
        Assert.Equal("new", move.Target);
        Assert.True(move.Rename);
        Assert.Empty(conflicts.Keys);
    }

    [Fact]
    public void AmbiguousOrNonAtomicMovesBecomeUncertainMoves()
    {
        baseline["a/old"] = new FileBaseline("H", "v1", LocalId(1), "r1");
        LocalFile("b/renamed", "H", 1);
        RemoteFile("a/old", "v1", "r1");
        var view = View();
        // Moving to another folder under another name needs two cloud requests, so it is not applied.
        Assert.Null(MoveReconciler.Decide("a/old", baseline["a/old"], view, MoveReconciler.Index(view, MoveReconciler.Candidates(view, CancellationToken.None))));
        Assert.Equal(ConflictCause.UncertainMove, conflicts.Causes["a/old"]);
        Assert.Equal(ConflictCause.UncertainMove, conflicts.Causes["b/renamed"]);
    }

    [Fact]
    public void MovedOutOfAMissingFolderProtectsBoth()
    {
        baseline["notes"] = new FileBaseline("", "", LocalId(1), "r1", true);
        baseline["notes/doc"] = new FileBaseline("H", "v1", LocalId(2), "r2");
        RemoteDir("notes", "r1");
        LocalFile("moved", "H", 2);
        Assert.Equal(["notes", "moved"], DirectoryReconciler.MovedOutOfMissingFolders(View()).Distinct());
    }
}
