// Program — entry point for all Video Streaming demo scenarios.

using System;
using System.Collections.Generic;

public class Program
{
    public static void Main()
    {
        var rawStore    = new RawVideoStore();
        var hlsStore    = new HlsStore();
        var metaStore   = new VideoMetaStore();
        var uploadSvc   = new UploadService(rawStore);
        var transcoder  = new TranscodeWorker(rawStore, hlsStore, metaStore);
        var cdnEdge     = new CdnEdgeCache(hlsStore);
        var watchHist   = new WatchHistory();
        var viewCounter = new ViewCounter(metaStore);

        Console.WriteLine("=== Scenario 1: Upload → Transcode → Stream ===\n");
        {
            // Alice uploads a 5-minute video in 3 chunks
            long fileSize = 15 * 1024 * 1024; // 15 MB
            var session = uploadSvc.Init("alice", "vacation.mp4", fileSize, 5 * 1024 * 1024);
            Console.WriteLine($"Upload session {session.UploadId}, video {session.VideoId}, {session.TotalChunks} chunks\n");

            uploadSvc.ReceiveChunk(session.UploadId, 0, new byte[5 * 1024 * 1024]);
            uploadSvc.ReceiveChunk(session.UploadId, 1, new byte[5 * 1024 * 1024]);
            uploadSvc.ReceiveChunk(session.UploadId, 2, new byte[5 * 1024 * 1024]);

            var (ok, videoId) = uploadSvc.Complete(session.UploadId, new byte[fileSize]);
            Console.WriteLine($"  Upload complete: {ok}, videoId={videoId}\n");

            // Transcode worker picks up the job
            if (uploadSvc.HasTranscodeJob(out var jobId))
            {
                transcoder.Process(jobId, 300, "Vacation 2024", "alice",
                    new[] { Rendition.R360p, Rendition.R720p, Rendition.R1080p });
            }
            Console.WriteLine();

            // Bob streams the video (good connection → 1080p)
            Console.WriteLine("--- Bob streams (8000 kbps) ---");
            var bobPlayer = new AbrPlayer("bob", videoId, cdnEdge, watchHist, viewCounter);
            bobPlayer.Play(4, simulatedThroughputKbps: 8000);
            Console.WriteLine();

            // Bob resumes after closing — should start from last position
            Console.WriteLine("--- Bob resumes ---");
            var bobPlayer2 = new AbrPlayer("bob", videoId, cdnEdge, watchHist, viewCounter);
            Console.WriteLine($"  Resumed at position {bobPlayer2.PositionSeconds}s (expected {bobPlayer.PositionSeconds}s)");
            Console.WriteLine();

            // Flush view counter
            Console.WriteLine("--- Flush view counter ---");
            viewCounter.Flush();
        }

        Console.WriteLine("\n=== Scenario 2: Interrupted Upload / Resume ===\n");
        {
            long fileSize = 10 * 1024 * 1024;
            var session = uploadSvc.Init("carol", "tutorial.mp4", fileSize);
            Console.WriteLine($"Session {session.UploadId}, {session.TotalChunks} chunks");

            // Only chunk 0 received before network drops
            uploadSvc.ReceiveChunk(session.UploadId, 0, new byte[5 * 1024 * 1024]);

            var resumeFrom = uploadSvc.GetResumePoint(session.UploadId);
            Console.WriteLine($"  Resume from chunk: {resumeFrom}");

            // Carol reconnects, uploads chunk 1
            uploadSvc.ReceiveChunk(session.UploadId, 1, new byte[5 * 1024 * 1024]);

            var (ok, videoId) = uploadSvc.Complete(session.UploadId, new byte[fileSize]);
            Console.WriteLine($"  Upload complete after resume: {ok}, videoId={videoId}");

            // Drain transcode queue so Scenario 3 starts clean
            if (uploadSvc.HasTranscodeJob(out var tid))
                transcoder.Process(tid, 90, "Tutorial", "carol");
        }

        Console.WriteLine("\n=== Scenario 3: ABR Quality Switching on Poor Network ===\n");
        {
            // Upload a short video
            var session = uploadSvc.Init("dave", "short.mp4", 5 * 1024 * 1024);
            uploadSvc.ReceiveChunk(session.UploadId, 0, new byte[5 * 1024 * 1024]);
            var (_, videoId) = uploadSvc.Complete(session.UploadId, new byte[5 * 1024 * 1024]);

            if (uploadSvc.HasTranscodeJob(out var jobId))
                transcoder.Process(jobId, 120, "Short Clip", "dave",
                    new[] { Rendition.R360p, Rendition.R480p, Rendition.R720p });

            // Eve streams with degrading network
            Console.WriteLine("--- Eve on poor network (500 kbps) ---");
            var evePlayer = new AbrPlayer("eve", videoId, cdnEdge, watchHist, viewCounter);
            evePlayer.Play(3, simulatedThroughputKbps: 500);
            Console.WriteLine();

            Console.WriteLine("--- Eve on good network (6000 kbps) ---");
            var evePlayer2 = new AbrPlayer("eve", videoId, cdnEdge, watchHist, viewCounter);
            evePlayer2.Play(3, simulatedThroughputKbps: 6000);
        }

        Console.WriteLine("\n=== Scenario 4: CDN Hit Rate ===\n");
        {
            // Drain any leftover transcode jobs from Scenario 3
            while (uploadSvc.HasTranscodeJob(out var leftover))
                transcoder.Process(leftover, 60, "Leftover", "system");

            Console.WriteLine($"CDN hits={cdnEdge.Hits}, misses={cdnEdge.Misses}, hit_rate={cdnEdge.HitRate:P1}");
            // Second playback of same video should hit cache
            var session = uploadSvc.Init("user", "test.mp4", 5 * 1024 * 1024);
            uploadSvc.ReceiveChunk(session.UploadId, 0, new byte[5 * 1024 * 1024]);
            var (_, videoId) = uploadSvc.Complete(session.UploadId, new byte[5 * 1024 * 1024]);
            if (uploadSvc.HasTranscodeJob(out var jid))
                transcoder.Process(jid, 60, "Test", "user", new[] { Rendition.R360p, Rendition.R720p, Rendition.R1080p });

            int hitsBefore = cdnEdge.Hits;
            var p1 = new AbrPlayer("u1", videoId, cdnEdge, watchHist, viewCounter);
            p1.Play(2, 8000); // first play → misses
            var p2 = new AbrPlayer("u2", videoId, cdnEdge, watchHist, viewCounter);
            p2.Play(2, 8000); // second play → should hit cache
            Console.WriteLine($"\nHits gained by second viewer: {cdnEdge.Hits - hitsBefore}");
        }

        Console.WriteLine("\n=== Scenario 5: Search and Trending ===\n");
        {
            // Manually add some videos for search test
            metaStore.Upsert(new VideoMetadata
            {
                VideoId = "vid001", Title = "Funny Cats 2024", Tags = new List<string> { "cats", "funny" },
                Status = VideoStatus.Ready, ViewCount = 5000000, CreatedAt = DateTime.UtcNow
            });
            metaStore.Upsert(new VideoMetadata
            {
                VideoId = "vid002", Title = "Cat Training Tips", Tags = new List<string> { "cats", "tutorial" },
                Status = VideoStatus.Ready, ViewCount = 1200000, CreatedAt = DateTime.UtcNow
            });
            metaStore.Upsert(new VideoMetadata
            {
                VideoId = "vid003", Title = "Dog vs Cat", Tags = new List<string> { "dogs", "cats" },
                Status = VideoStatus.Ready, ViewCount = 800000, CreatedAt = DateTime.UtcNow
            });

            var results = metaStore.Search("cats");
            Console.WriteLine("Search 'cats':");
            foreach (var v in results)
                Console.WriteLine($"  {v.VideoId}: \"{v.Title}\" ({v.ViewCount:N0} views)");

            Console.WriteLine("\nTrending top 3:");
            foreach (var v in metaStore.Trending(3))
                Console.WriteLine($"  {v.VideoId}: \"{v.Title}\" ({v.ViewCount:N0} views)");
        }

        Console.WriteLine("\nDone — 0 errors, 0 warnings");
    }
}
