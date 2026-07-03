// Splitting a long upload into bounded pieces. The mobile dictation upload used to send the whole
// WAV as one request body, which fails for a long recording: the Gateway (Kestrel) rejects a request
// body over its limit (roughly 30 megabytes by default), and a twenty-minute clip is larger than
// that. The upload endpoint already accepts a recording in numbered chunks and reassembles them in
// order, so the fix is to send the bytes as several bounded chunks instead of one.
//
// This module is deliberately dependency-free (no browser APIs) so the range math can be unit-tested
// on its own.

/** One contiguous slice of an upload: its chunk index and the half-open byte range [start, end). */
export interface UploadChunkRange {
  index: number;
  start: number;
  end: number;
}

/**
 * Plan the contiguous, ordered chunks for an upload of <paramref name="totalBytes"/> bytes, each no
 * larger than <paramref name="maxChunkBytes"/>. The ranges cover the whole payload exactly once with
 * no gaps or overlaps, indexed from zero, so the server reassembles them into the original bytes. A
 * zero-length payload yields no ranges (the caller guards empty audio upstream).
 */
export function planUploadChunks(totalBytes: number, maxChunkBytes: number): UploadChunkRange[] {
  if (!Number.isInteger(totalBytes) || totalBytes < 0) {
    throw new Error(`totalBytes must be a non-negative integer, got ${totalBytes}`);
  }
  if (!Number.isInteger(maxChunkBytes) || maxChunkBytes <= 0) {
    throw new Error(`maxChunkBytes must be a positive integer, got ${maxChunkBytes}`);
  }

  const ranges: UploadChunkRange[] = [];
  let start = 0;
  let index = 0;
  while (start < totalBytes) {
    const end = Math.min(start + maxChunkBytes, totalBytes);
    ranges.push({ index, start, end });
    start = end;
    index += 1;
  }
  return ranges;
}
