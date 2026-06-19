// UploadSession — the server-side checklist for one resumable, chunked upload.
//
// THE BIG IDEA:
// Like a jigsaw puzzle arrival sheet: it ticks off which chunks have safely arrived so a
// dropped connection can resume from the first missing one instead of restarting. Once every
// box is ticked (IsComplete), UploadService assembles the file and queues it for transcoding.
//
// WHY VideoId IS MINTED UP FRONT (before any bytes): the client gets its permanent public URL
// immediately and resumes a dropped upload against the SAME id — no orphaned partial uploads.
//
// WHY ReceivedChunks IS A HASHSET: retries are idempotent (re-adding an index is a no-op),
// chunks may arrive out of order, and IsComplete / Contains are O(1). A list or counter would
// double-count retries.
//
// HOW IT BEHAVES AT RUNTIME (15 MB file, 3 chunks):
//
//   After                  | ReceivedChunks | IsComplete | LastReceivedChunk
//   -----------------------|----------------|------------|------------------
//   Init                   | {}             | false      | -1
//   chunks 0, 1 arrive     | {0,1}          | false      | 1
//   [drop] chunk 1 retry+2 | {0,1,2}        | true       | 2   (retry was a no-op)

using System.Collections.Generic;
using System.Linq;

public class UploadSession
{
    // Ephemeral session token the client sends on every chunk + resume. Discarded after Complete.
    public string UploadId { get; set; }

    // Permanent public id (URL key), minted at Init before any bytes arrive.
    public string VideoId { get; set; }

    // Who started the upload. Carried through to VideoMetadata for attribution.
    public string UploaderId { get; set; }

    // Client's original filename — for display only; VideoId is the storage key.
    public string OriginalName { get; set; }

    // Total bytes. Drives TotalChunks and progress bars.
    public long TotalSize { get; set; }

    // Chunk count: Math.Ceiling(TotalSize / chunkSize). The client must send all of these indices.
    public int TotalChunks { get; set; }

    // Which chunk indices have arrived. HashSet => idempotent retries, order-independent, O(1).
    public HashSet<int> ReceivedChunks { get; } = [];

    // All chunks in? Count == TotalChunks is O(1) (pigeonhole: N valid distinct indices = complete).
    public bool IsComplete => ReceivedChunks.Count == TotalChunks;

    // Highest index seen (rough progress); -1 sentinel when nothing has arrived yet.
    public int LastReceivedChunk => ReceivedChunks.Count == 0 ? -1 : ReceivedChunks.Max();
}
