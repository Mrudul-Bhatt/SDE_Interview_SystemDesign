// VideoMetadata — the row that lives in our metadata DB (Cassandra in production).
//
// ManifestUrl points at the master M3U8 in object storage / CDN; players fetch
// this first to discover all available renditions. ViewCount is approximate
// (batch-flushed from the ViewCounter buffer), which is the standard trade-off
// for view counters at YouTube scale.

using System;
using System.Collections.Generic;

public class VideoMetadata
{
    public string VideoId       { get; set; }
    public string UploaderId    { get; set; }
    public string Title         { get; set; }
    public List<string> Tags    { get; set; } = new List<string>();
    public VideoStatus Status   { get; set; }
    public int DurationSeconds  { get; set; }
    public long ViewCount       { get; set; }
    public DateTime CreatedAt   { get; set; }
    public string ManifestUrl   { get; set; }  // CDN path to master M3U8
}
