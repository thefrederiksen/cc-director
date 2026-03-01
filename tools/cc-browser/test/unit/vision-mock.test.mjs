// Unit tests for vision.mjs with mocked fetch
import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';

// We test analyzeScreenshot by mocking global fetch
const originalFetch = globalThis.fetch;

function mockFetch(response) {
  globalThis.fetch = async (url, opts) => {
    return response;
  };
}

function restoreFetch() {
  globalThis.fetch = originalFetch;
}

describe('analyzeScreenshot', () => {
  let analyzeScreenshot;
  const originalApiKey = process.env.ANTHROPIC_API_KEY;

  beforeEach(async () => {
    // Set a fake API key for testing
    process.env.ANTHROPIC_API_KEY = 'test-api-key-123';
    // Dynamic import to get fresh module
    const mod = await import('../../src/vision.mjs');
    analyzeScreenshot = mod.analyzeScreenshot;
  });

  afterEach(() => {
    restoreFetch();
    if (originalApiKey) {
      process.env.ANTHROPIC_API_KEY = originalApiKey;
    } else {
      delete process.env.ANTHROPIC_API_KEY;
    }
  });

  it('returns text from successful API response', async () => {
    mockFetch({
      ok: true,
      json: async () => ({
        content: [{ type: 'text', text: '{"detected": true, "type": "recaptcha_v2"}' }],
      }),
    });

    const result = await analyzeScreenshot('fakeBase64', 'test prompt');
    assert.equal(result, '{"detected": true, "type": "recaptcha_v2"}');
  });

  it('throws on 401 unauthorized', async () => {
    mockFetch({
      ok: false,
      status: 401,
      text: async () => 'Unauthorized',
    });

    await assert.rejects(
      () => analyzeScreenshot('fakeBase64', 'test prompt'),
      (err) => {
        assert.ok(err.message.includes('401'));
        assert.ok(err.message.includes('Unauthorized'));
        return true;
      }
    );
  });

  it('throws on 429 rate limit', async () => {
    mockFetch({
      ok: false,
      status: 429,
      text: async () => 'Rate limit exceeded',
    });

    await assert.rejects(
      () => analyzeScreenshot('fakeBase64', 'test prompt'),
      (err) => {
        assert.ok(err.message.includes('429'));
        return true;
      }
    );
  });

  it('throws on 500 server error', async () => {
    mockFetch({
      ok: false,
      status: 500,
      text: async () => 'Internal Server Error',
    });

    await assert.rejects(
      () => analyzeScreenshot('fakeBase64', 'test prompt'),
      (err) => {
        assert.ok(err.message.includes('500'));
        return true;
      }
    );
  });

  it('throws clear error when ANTHROPIC_API_KEY is missing', async () => {
    delete process.env.ANTHROPIC_API_KEY;

    // Re-import to pick up env change - but the check is at call time
    const mod = await import('../../src/vision.mjs');

    await assert.rejects(
      () => mod.analyzeScreenshot('fakeBase64', 'test prompt'),
      (err) => {
        assert.ok(err.message.includes('ANTHROPIC_API_KEY'));
        return true;
      }
    );
  });

  it('passes correct model parameter', async () => {
    let capturedBody;
    globalThis.fetch = async (url, opts) => {
      capturedBody = JSON.parse(opts.body);
      return {
        ok: true,
        json: async () => ({
          content: [{ type: 'text', text: 'test' }],
        }),
      };
    };

    await analyzeScreenshot('fakeBase64', 'test', { model: 'claude-sonnet-4-6' });
    assert.equal(capturedBody.model, 'claude-sonnet-4-6');
  });

  it('passes correct headers including API key', async () => {
    let capturedHeaders;
    globalThis.fetch = async (url, opts) => {
      capturedHeaders = opts.headers;
      return {
        ok: true,
        json: async () => ({
          content: [{ type: 'text', text: 'test' }],
        }),
      };
    };

    await analyzeScreenshot('fakeBase64', 'test');
    assert.equal(capturedHeaders['x-api-key'], 'test-api-key-123');
    assert.equal(capturedHeaders['anthropic-version'], '2023-06-01');
    assert.equal(capturedHeaders['Content-Type'], 'application/json');
  });
});
