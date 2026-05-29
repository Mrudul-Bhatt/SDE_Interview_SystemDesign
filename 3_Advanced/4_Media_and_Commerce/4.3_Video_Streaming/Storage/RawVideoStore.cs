// RawVideoStore — stands in for the S3 bucket holding original uploads.
//
// Production: originals stay in S3 Standard for 30 days then move to Glacier
// — cheap cold storage we can re-pull if we ever need to re-transcode (new
// codec, fixed bug). Never delete originals; they're the only loss-free source.

using System.Collections.Generic;

public class RawVideoStore
{
    private readonly Dictionary<string, byte[]> _store = new Dictionary<string, byte[]>();

    public void Store(string videoId, byte[] data) => _store[videoId] = data;
    public bool Exists(string videoId)             => _store.ContainsKey(videoId);
    public byte[] Fetch(string videoId)            => _store[videoId];
}
