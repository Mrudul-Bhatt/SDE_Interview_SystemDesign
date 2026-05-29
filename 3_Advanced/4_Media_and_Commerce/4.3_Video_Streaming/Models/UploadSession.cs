// UploadSession — tracks state for one chunked resumable upload.
//
// ReceivedChunks is a Set so duplicate chunk receives (from retries) are
// idempotent — the client can safely re-send any chunk after a network hiccup
// without corrupting the assembled file. IsComplete checks the count rather
// than range to defend against clients sending chunks out of order.

using System.Collections.Generic;
using System.Linq;

public class UploadSession
{
    public string UploadId     { get; set; }
    public string VideoId      { get; set; }
    public string UploaderId   { get; set; }
    public string OriginalName { get; set; }
    public long   TotalSize    { get; set; }
    public int    TotalChunks  { get; set; }
    public HashSet<int> ReceivedChunks { get; } = new HashSet<int>();
    public bool IsComplete => ReceivedChunks.Count == TotalChunks;
    public int LastReceivedChunk => ReceivedChunks.Count == 0 ? -1 : ReceivedChunks.Max();
}
