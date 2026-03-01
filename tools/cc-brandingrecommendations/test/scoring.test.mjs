import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { classifyPriority, priorityScore, sortRecommendations, classifyAndSort } from '../src/scoring.mjs';

describe('classifyPriority', () => {
  it('should classify high impact + low effort as Quick Win', () => {
    assert.equal(classifyPriority(5, 1), 'Quick Win');
    assert.equal(classifyPriority(4, 2), 'Quick Win');
  });

  it('should classify high impact + high effort as Strategic', () => {
    assert.equal(classifyPriority(5, 3), 'Strategic');
    assert.equal(classifyPriority(4, 5), 'Strategic');
  });

  it('should classify low impact + low effort as Easy Fill', () => {
    assert.equal(classifyPriority(3, 1), 'Easy Fill');
    assert.equal(classifyPriority(2, 2), 'Easy Fill');
    assert.equal(classifyPriority(1, 1), 'Easy Fill');
  });

  it('should classify low impact + high effort as Deprioritize', () => {
    assert.equal(classifyPriority(3, 3), 'Deprioritize');
    assert.equal(classifyPriority(2, 4), 'Deprioritize');
    assert.equal(classifyPriority(1, 5), 'Deprioritize');
  });
});

describe('priorityScore', () => {
  it('should give Quick Win the lowest score (highest priority)', () => {
    const qw = { priority: 'Quick Win', impact: 5, effort: 1 };
    const strat = { priority: 'Strategic', impact: 5, effort: 3 };
    assert.ok(priorityScore(qw) < priorityScore(strat));
  });

  it('should rank higher impact/effort ratio better within same bucket', () => {
    const a = { priority: 'Quick Win', impact: 5, effort: 1 };
    const b = { priority: 'Quick Win', impact: 4, effort: 2 };
    assert.ok(priorityScore(a) < priorityScore(b));
  });
});

describe('sortRecommendations', () => {
  it('should sort Quick Wins before Strategic', () => {
    const recs = [
      { id: 'strat', priority: 'Strategic', impact: 5, effort: 3 },
      { id: 'qw', priority: 'Quick Win', impact: 4, effort: 1 },
    ];
    const sorted = sortRecommendations(recs);
    assert.equal(sorted[0].id, 'qw');
    assert.equal(sorted[1].id, 'strat');
  });

  it('should sort all four priorities in correct order', () => {
    const recs = [
      { id: 'dep', priority: 'Deprioritize', impact: 2, effort: 4 },
      { id: 'qw', priority: 'Quick Win', impact: 5, effort: 1 },
      { id: 'ef', priority: 'Easy Fill', impact: 2, effort: 1 },
      { id: 'strat', priority: 'Strategic', impact: 5, effort: 4 },
    ];
    const sorted = sortRecommendations(recs);
    assert.equal(sorted[0].id, 'qw');
    assert.equal(sorted[1].id, 'strat');
    assert.equal(sorted[2].id, 'ef');
    assert.equal(sorted[3].id, 'dep');
  });

  it('should not mutate the original array', () => {
    const recs = [
      { id: 'b', priority: 'Strategic', impact: 5, effort: 3 },
      { id: 'a', priority: 'Quick Win', impact: 4, effort: 1 },
    ];
    const sorted = sortRecommendations(recs);
    assert.equal(recs[0].id, 'b'); // original unchanged
    assert.equal(sorted[0].id, 'a');
  });
});

describe('classifyAndSort', () => {
  it('should classify and sort recommendations', () => {
    const recs = [
      { id: 'a', impact: 2, effort: 4 },
      { id: 'b', impact: 5, effort: 1 },
      { id: 'c', impact: 4, effort: 3 },
      { id: 'd', impact: 3, effort: 1 },
    ];
    const sorted = classifyAndSort(recs);
    assert.equal(sorted[0].id, 'b');
    assert.equal(sorted[0].priority, 'Quick Win');
    assert.equal(sorted[1].id, 'c');
    assert.equal(sorted[1].priority, 'Strategic');
    assert.equal(sorted[2].id, 'd');
    assert.equal(sorted[2].priority, 'Easy Fill');
    assert.equal(sorted[3].id, 'a');
    assert.equal(sorted[3].priority, 'Deprioritize');
  });
});
