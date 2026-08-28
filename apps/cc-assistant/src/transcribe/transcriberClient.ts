// The page's side of the speech model.
//
// Owns the worker, keeps the count of chunks sent but not yet answered, and nothing else. That count
// is the honest verdict on whether a model can be used continuously: an average that looks fine
// hides a backlog, and a backlog that grows for two minutes means the thing has been quietly falling
// further behind the room the whole time.

import type {
  DecoderPrecision,
  TranscriberDevice,
  WorkerMessage,
  WorkerRequest,
} from "./transcriber.worker";

export type { DecoderPrecision, TranscriberDevice };

export interface TranscriberEvents {
  onLoading(percent: number | null, file: string): void;
  onLoaded(modelId: string, device: TranscriberDevice, loadMs: number): void;
  onResult(text: string, transcribeMs: number, realTimeFactor: number, backlog: number): void;
  onFailure(message: string): void;
}

export interface Transcriber {
  load(modelId: string, device: TranscriberDevice, decoderPrecision: DecoderPrecision): void;
  /** Hand one chunk over. Returns the backlog depth AFTER queueing it. */
  submit(samples: Float32Array, seconds: number): number;
  readonly backlog: number;
  dispose(): void;
}

export function createTranscriber(events: TranscriberEvents): Transcriber {
  const worker = new Worker(new URL("./transcriber.worker.ts", import.meta.url), {
    type: "module",
  });

  let nextId = 1;
  let outstanding = 0;

  worker.onmessage = (event: MessageEvent<WorkerMessage>) => {
    const message = event.data;
    switch (message.kind) {
      case "loading":
        events.onLoading(message.percent, message.file);
        return;
      case "loaded":
        events.onLoaded(message.modelId, message.device, message.loadMs);
        return;
      case "result":
        outstanding = Math.max(0, outstanding - 1);
        events.onResult(message.text, message.transcribeMs, message.realTimeFactor, outstanding);
        return;
      case "failure":
        if (message.id !== null) {
          outstanding = Math.max(0, outstanding - 1);
        }
        events.onFailure(message.message);
        return;
      default:
        events.onFailure("The speech worker sent a message this page does not understand.");
    }
  };

  worker.onerror = (event: ErrorEvent) => {
    events.onFailure(`The speech worker stopped: ${event.message}`);
  };

  function send(request: WorkerRequest, transfer?: Transferable[]): void {
    if (transfer !== undefined) {
      worker.postMessage(request, transfer);
      return;
    }
    worker.postMessage(request);
  }

  return {
    load(modelId, device, decoderPrecision) {
      outstanding = 0;
      send({ kind: "load", modelId, device, decoderPrecision });
    },
    submit(samples, seconds) {
      outstanding += 1;
      // Transferred, not copied. The page has no further use for these samples and a copy per chunk
      // is real work on a phone.
      send({ kind: "transcribe", id: nextId, samples, seconds }, [samples.buffer]);
      nextId += 1;
      return outstanding;
    },
    get backlog() {
      return outstanding;
    },
    dispose() {
      worker.onmessage = null;
      worker.onerror = null;
      worker.terminate();
    },
  };
}
