// Domain enums shared across the streaming pipeline.
//
// VideoStatus: lifecycle from upload to playable to deleted. ABR players only
// surface Ready videos; the transcoder marks Ready once all segments + manifest
// have been written so we never serve a half-transcoded video.
//
// Rendition: the discrete quality levels we offer. Real systems often add an
// audio-only rendition for very poor networks; omitted here for brevity.

public enum VideoStatus { Uploading, Transcoding, Ready, Deleted }
public enum Rendition  { R360p, R480p, R720p, R1080p, R4K }
