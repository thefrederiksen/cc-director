import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  normalizeUrl,
  isSameHost,
  countWords,
  stripHtml,
} from '../src/utils.mjs';

// ---------------------------------------------------------------------------
// normalizeUrl
// ---------------------------------------------------------------------------
describe('normalizeUrl', () => {
  it('removes trailing slash from non-root paths', () => {
    const result = normalizeUrl('https://example.com/about/');
    assert.equal(result, 'https://example.com/about');
  });

  it('keeps trailing slash for root path', () => {
    const result = normalizeUrl('https://example.com/');
    assert.equal(result, 'https://example.com/');
  });

  it('lowercases the hostname', () => {
    const result = normalizeUrl('https://EXAMPLE.COM/About');
    // URL constructor lowercases the host automatically
    assert.ok(result.includes('example.com'));
    // Path case should be preserved
    assert.ok(result.includes('/About'));
  });

  it('removes hash fragment', () => {
    const result = normalizeUrl('https://example.com/page#section');
    assert.ok(!result.includes('#section'));
    assert.ok(!result.includes('#'));
  });

  it('sorts query parameters alphabetically', () => {
    const result = normalizeUrl('https://example.com/page?z=1&a=2&m=3');
    assert.equal(result, 'https://example.com/page?a=2&m=3&z=1');
  });

  it('returns original string for invalid URLs', () => {
    const invalid = 'not-a-valid-url';
    assert.equal(normalizeUrl(invalid), invalid);
  });

  it('handles URLs with no path', () => {
    const result = normalizeUrl('https://example.com');
    // URL constructor adds trailing slash for root
    assert.equal(result, 'https://example.com/');
  });

  it('handles URL with both query and hash', () => {
    const result = normalizeUrl('https://example.com/page?b=2&a=1#top');
    assert.equal(result, 'https://example.com/page?a=1&b=2');
  });
});

// ---------------------------------------------------------------------------
// isSameHost
// ---------------------------------------------------------------------------
describe('isSameHost', () => {
  it('returns true when hostname matches', () => {
    assert.equal(isSameHost('https://example.com/page', 'example.com'), true);
  });

  it('returns true for matching hostname with port', () => {
    assert.equal(isSameHost('https://example.com:443/page', 'example.com'), true);
  });

  it('returns false when hostname differs', () => {
    assert.equal(isSameHost('https://other.com/page', 'example.com'), false);
  });

  it('returns false for subdomain mismatch', () => {
    assert.equal(isSameHost('https://www.example.com/page', 'example.com'), false);
  });

  it('returns false for invalid URL', () => {
    assert.equal(isSameHost('not-a-url', 'example.com'), false);
  });

  it('handles matching with different paths', () => {
    assert.equal(isSameHost('https://example.com/a/b/c?q=1', 'example.com'), true);
  });
});

// ---------------------------------------------------------------------------
// countWords
// ---------------------------------------------------------------------------
describe('countWords', () => {
  it('counts words in a normal sentence', () => {
    assert.equal(countWords('hello world foo bar'), 4);
  });

  it('returns 0 for empty string', () => {
    assert.equal(countWords(''), 0);
  });

  it('returns 0 for null or undefined', () => {
    assert.equal(countWords(null), 0);
    assert.equal(countWords(undefined), 0);
  });

  it('handles multiple spaces between words', () => {
    assert.equal(countWords('hello    world'), 2);
  });

  it('handles leading and trailing whitespace', () => {
    assert.equal(countWords('  hello world  '), 2);
  });

  it('handles tabs and newlines', () => {
    assert.equal(countWords('hello\tworld\nfoo'), 3);
  });

  it('returns 1 for a single word', () => {
    assert.equal(countWords('hello'), 1);
  });

  it('returns 0 for whitespace-only string', () => {
    assert.equal(countWords('   '), 0);
  });
});

// ---------------------------------------------------------------------------
// stripHtml
// ---------------------------------------------------------------------------
describe('stripHtml', () => {
  it('removes HTML tags', () => {
    assert.equal(stripHtml('<p>Hello</p>'), 'Hello');
  });

  it('removes nested tags', () => {
    assert.equal(stripHtml('<div><p>Hello <strong>world</strong></p></div>'), 'Hello world');
  });

  it('collapses whitespace from removed tags', () => {
    const result = stripHtml('<p>Hello</p>   <p>World</p>');
    assert.equal(result, 'Hello World');
  });

  it('returns empty string for null or undefined', () => {
    assert.equal(stripHtml(null), '');
    assert.equal(stripHtml(undefined), '');
  });

  it('returns empty string for empty input', () => {
    assert.equal(stripHtml(''), '');
  });

  it('handles self-closing tags', () => {
    assert.equal(stripHtml('Hello<br/>World'), 'Hello World');
  });

  it('handles tags with attributes', () => {
    assert.equal(stripHtml('<a href="https://example.com" class="link">Click</a>'), 'Click');
  });

  it('trims leading and trailing whitespace', () => {
    assert.equal(stripHtml('  <p>Hello</p>  '), 'Hello');
  });

  it('preserves text with no tags', () => {
    assert.equal(stripHtml('No tags here'), 'No tags here');
  });
});
