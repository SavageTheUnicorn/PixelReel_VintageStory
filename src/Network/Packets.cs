using ProtoBuf;

namespace PixelReel.Network
{
    /// <summary>
    /// Network payloads. Display state itself is NOT sent here: it lives in the block
    /// entity's tree attributes, which Vintage Story already syncs to every client and
    /// replays for late joiners. That removes the entire hand-written codec layer the
    /// Fabric version needed (~1,500 lines) for free.
    ///
    /// These packets are only for things block entity sync can't express: browsing a
    /// remote library, and requests travelling client -> server.
    /// </summary>
    public static class Channel
    {
        public const string Name = "pixelreel";
    }

    public enum BrowseKind
    {
        /// <summary>Top level: the user's Jellyfin libraries.</summary>
        Libraries = 0,
        /// <summary>Movies and series inside a library.</summary>
        LibraryItems = 1,
        /// <summary>Seasons of a series.</summary>
        Seasons = 2,
        /// <summary>Episodes of a season.</summary>
        Episodes = 3,
        /// <summary>Flat list of everything recently added.</summary>
        Recent = 4
    }

    public enum EntryKind
    {
        Library = 0,
        Movie = 1,
        Series = 2,
        Season = 3,
        Episode = 4
    }

    [ProtoContract]
    public class RequestBrowse
    {
        [ProtoMember(1)] public BrowseKind Kind;
        [ProtoMember(2)] public string ParentId;
        [ProtoMember(3)] public bool ForceRefresh;
    }

    [ProtoContract]
    public class BrowseEntry
    {
        [ProtoMember(1)] public string Id;
        [ProtoMember(2)] public string Title;
        [ProtoMember(3)] public EntryKind Kind;

        /// <summary>Subtitle line: year, episode number, runtime — whatever fits.</summary>
        [ProtoMember(4)] public string Detail;

        /// <summary>Resume position in seconds, 0 when unwatched.</summary>
        [ProtoMember(5)] public long ResumeSeconds;
    }

    [ProtoContract]
    public class BrowseResult
    {
        [ProtoMember(1)] public BrowseKind Kind;
        [ProtoMember(2)] public string ParentId;
        [ProtoMember(3)] public string ParentTitle;
        [ProtoMember(4)] public BrowseEntry[] Entries;
        [ProtoMember(5)] public string Error;
    }

    [ProtoContract]
    public class PlayItem
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
        [ProtoMember(4)] public string ItemId;
        [ProtoMember(5)] public long StartSeconds;
    }

    /// <summary>
    /// Move the playhead of already-playing media. Distinct from PlayItem: re-issuing
    /// the stream would restart it, because Jellyfin ignores startTimeTicks on a
    /// static direct-play URL and just serves the file from the top.
    /// </summary>
    [ProtoContract]
    public class SeekTo
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
        [ProtoMember(4)] public long Seconds;
    }

    [ProtoContract]
    public class SetPower
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
        [ProtoMember(4)] public bool On;
    }

    [ProtoContract]
    public class SetVolume
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
        [ProtoMember(4)] public float Volume;
    }

    [ProtoContract]
    public class PlaybackControl
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;

        /// <summary>0 = pause, 1 = resume, 2 = stop, 3 = retry.</summary>
        [ProtoMember(4)] public int Action;
    }

    /// <summary>
    /// Client tells the server its stream finished, so the server can pick the next
    /// episode. Epoch guards against a stale client reporting the end of media that
    /// has since been replaced.
    /// </summary>
    [ProtoContract]
    public class ReportEnded
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
        [ProtoMember(4)] public int Epoch;
    }

    [ProtoContract]
    public class Notice
    {
        [ProtoMember(1)] public string Message;
    }

    /// <summary>Non-secret provider status for the client UI. Never carries the API key.</summary>
    [ProtoContract]
    public class ProviderStatus
    {
        [ProtoMember(1)] public bool Configured;
        [ProtoMember(2)] public bool Reachable;
        [ProtoMember(3)] public string ServerName;
        [ProtoMember(4)] public string Error;
    }

    [ProtoContract]
    public class RequestProviderStatus
    {
    }
}
