// Pulls raw audio out of the microphone graph and hands it to the page.
//
// This runs on the audio rendering thread, which is the only place allowed to see the samples, and
// it is the reason the transcription model can run in a worker without the microphone stuttering.
// It does no processing at all: it batches the tiny 128-sample blocks the browser delivers into
// something worth posting, and posts them. Everything else, including changing the sample rate,
// happens on the other side.
class PcmCollector extends AudioWorkletProcessor {
  constructor() {
    super();
    this.batchSize = 4096;
    this.buffer = new Float32Array(this.batchSize);
    this.filled = 0;
  }

  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (!channel) {
      // No input connected yet. Keep the processor alive; a returned false would remove it for good.
      return true;
    }
    for (let i = 0; i < channel.length; i += 1) {
      this.buffer[this.filled] = channel[i];
      this.filled += 1;
      if (this.filled === this.batchSize) {
        // Copied, not transferred, because the buffer is reused on the next block.
        this.port.postMessage(this.buffer.slice(0));
        this.filled = 0;
      }
    }
    return true;
  }
}

registerProcessor("pcm-collector", PcmCollector);
