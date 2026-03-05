// CC Browser v2 - Native Messaging Protocol Tests
// Tests 4-byte LE encode/decode, round-trip, and size limit enforcement.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

// ---------------------------------------------------------------------------
// Protocol helpers (inline for testing - matches native-host.mjs logic)
// ---------------------------------------------------------------------------

const MAX_MESSAGE_SIZE = 1024 * 1024; // 1MB

function encodeNativeMessage(obj) {
  const json = JSON.stringify(obj);
  const jsonBytes = Buffer.from(json, 'utf8');
  if (jsonBytes.length > MAX_MESSAGE_SIZE) {
    throw new Error(`Message too large: ${jsonBytes.length} bytes (max: ${MAX_MESSAGE_SIZE})`);
  }
  const header = Buffer.alloc(4);
  header.writeUInt32LE(jsonBytes.length, 0);
  return Buffer.concat([header, jsonBytes]);
}

function decodeNativeMessages(buffer) {
  const messages = [];
  let offset = 0;

  while (offset + 4 <= buffer.length) {
    const messageLength = buffer.readUInt32LE(offset);
    if (messageLength > MAX_MESSAGE_SIZE) {
      throw new Error(`Message too large: ${messageLength} bytes`);
    }
    if (offset + 4 + messageLength > buffer.length) {
      break; // Incomplete message
    }
    const jsonBytes = buffer.subarray(offset + 4, offset + 4 + messageLength);
    messages.push(JSON.parse(jsonBytes.toString('utf8')));
    offset += 4 + messageLength;
  }

  return { messages, remaining: buffer.subarray(offset) };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('Native Messaging Protocol', () => {
  describe('encodeNativeMessage', () => {
    it('should encode a simple message with 4-byte LE header', () => {
      const msg = { id: '1', command: 'ping' };
      const encoded = encodeNativeMessage(msg);

      assert.ok(encoded.length >= 4);
      const declaredLength = encoded.readUInt32LE(0);
      const jsonBytes = encoded.subarray(4);
      assert.equal(declaredLength, jsonBytes.length);

      const decoded = JSON.parse(jsonBytes.toString('utf8'));
      assert.deepEqual(decoded, msg);
    });

    it('should handle empty object', () => {
      const encoded = encodeNativeMessage({});
      const declaredLength = encoded.readUInt32LE(0);
      assert.equal(declaredLength, 2); // "{}" is 2 bytes
    });

    it('should handle unicode text', () => {
      const msg = { text: 'Hello World - test 123' };
      const encoded = encodeNativeMessage(msg);
      const declaredLength = encoded.readUInt32LE(0);
      const jsonBytes = encoded.subarray(4);
      assert.equal(declaredLength, jsonBytes.length);

      const decoded = JSON.parse(jsonBytes.toString('utf8'));
      assert.equal(decoded.text, msg.text);
    });

    it('should reject messages over 1MB', () => {
      const bigPayload = { data: 'x'.repeat(1024 * 1024) };
      assert.throws(
        () => encodeNativeMessage(bigPayload),
        /Message too large/
      );
    });
  });

  describe('decodeNativeMessages', () => {
    it('should decode a single message', () => {
      const msg = { id: '1', result: { pong: true } };
      const encoded = encodeNativeMessage(msg);

      const { messages, remaining } = decodeNativeMessages(encoded);
      assert.equal(messages.length, 1);
      assert.deepEqual(messages[0], msg);
      assert.equal(remaining.length, 0);
    });

    it('should decode multiple concatenated messages', () => {
      const msg1 = { id: '1', command: 'ping' };
      const msg2 = { id: '2', command: 'tabs' };
      const msg3 = { id: '3', result: 'ok' };

      const combined = Buffer.concat([
        encodeNativeMessage(msg1),
        encodeNativeMessage(msg2),
        encodeNativeMessage(msg3),
      ]);

      const { messages, remaining } = decodeNativeMessages(combined);
      assert.equal(messages.length, 3);
      assert.deepEqual(messages[0], msg1);
      assert.deepEqual(messages[1], msg2);
      assert.deepEqual(messages[2], msg3);
      assert.equal(remaining.length, 0);
    });

    it('should handle incomplete message (partial header)', () => {
      const { messages, remaining } = decodeNativeMessages(Buffer.from([0x05]));
      assert.equal(messages.length, 0);
      assert.equal(remaining.length, 1);
    });

    it('should handle incomplete message (partial body)', () => {
      const header = Buffer.alloc(4);
      header.writeUInt32LE(100, 0);
      const partialBody = Buffer.from('{"id":"1"}');
      const incomplete = Buffer.concat([header, partialBody]);

      const { messages, remaining } = decodeNativeMessages(incomplete);
      assert.equal(messages.length, 0);
      assert.equal(remaining.length, incomplete.length);
    });

    it('should reject oversized messages', () => {
      const header = Buffer.alloc(4);
      header.writeUInt32LE(MAX_MESSAGE_SIZE + 1, 0);

      assert.throws(
        () => decodeNativeMessages(header),
        /Message too large/
      );
    });
  });

  describe('round-trip', () => {
    it('should encode and decode back to original', () => {
      const original = {
        id: 'req-42',
        command: 'snapshot',
        params: {
          interactive: true,
          compact: false,
          maxDepth: 5,
          tabId: 12345,
        },
      };

      const encoded = encodeNativeMessage(original);
      const { messages } = decodeNativeMessages(encoded);

      assert.equal(messages.length, 1);
      assert.deepEqual(messages[0], original);
    });

    it('should handle nested objects and arrays', () => {
      const original = {
        id: 'req-1',
        result: {
          snapshot: '- link "Home" [ref=e1]\n- button "Submit" [ref=e2]',
          refs: {
            e1: { role: 'link', name: 'Home', nth: 0 },
            e2: { role: 'button', name: 'Submit', nth: 0 },
          },
          stats: {
            chars: 52,
            lines: 2,
            refs: 2,
            interactive: 2,
          },
        },
      };

      const encoded = encodeNativeMessage(original);
      const { messages } = decodeNativeMessages(encoded);
      assert.deepEqual(messages[0], original);
    });

    it('should handle message at exactly 1MB limit', () => {
      // Calculate how much padding we need to hit exactly 1MB JSON
      const baseObj = { id: '1', data: '' };
      const baseJson = JSON.stringify(baseObj);
      const paddingNeeded = MAX_MESSAGE_SIZE - Buffer.from(baseJson, 'utf8').length;
      baseObj.data = 'a'.repeat(paddingNeeded);

      const json = JSON.stringify(baseObj);
      const jsonBytes = Buffer.from(json, 'utf8');
      assert.equal(jsonBytes.length, MAX_MESSAGE_SIZE);

      const encoded = encodeNativeMessage(baseObj);
      const { messages } = decodeNativeMessages(encoded);
      assert.equal(messages.length, 1);
      assert.equal(messages[0].id, '1');
    });
  });
});
