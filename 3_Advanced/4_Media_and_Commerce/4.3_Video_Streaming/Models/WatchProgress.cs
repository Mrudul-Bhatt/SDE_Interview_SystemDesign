// WatchProgress — one user's playhead in one video.
//
// Updated by the ABR player every segment so "resume where you left off" works
// even if the user closes the app abruptly. Writes are debounced in production
// (every ~10s) to avoid hammering Cassandra on every frame.

using System;

public class WatchProgress
{
    public string UserId           { get; set; }
    public string VideoId          { get; set; }
    public int    PositionSeconds  { get; set; }
    public bool   Completed        { get; set; }
    public DateTime LastUpdated    { get; set; }
}
