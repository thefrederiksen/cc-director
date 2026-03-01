import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  scoreToGrade,
  calculateScores,
  calculateOverallGrade,
  getQuickWins,
} from '../src/scoring.mjs';

// ---------------------------------------------------------------------------
// scoreToGrade
// ---------------------------------------------------------------------------
describe('scoreToGrade', () => {
  it('returns A+ for score >= 97', () => {
    assert.equal(scoreToGrade(97), 'A+');
    assert.equal(scoreToGrade(100), 'A+');
  });

  it('returns A for score >= 93 and < 97', () => {
    assert.equal(scoreToGrade(93), 'A');
    assert.equal(scoreToGrade(96), 'A');
  });

  it('returns A- for score >= 90 and < 93', () => {
    assert.equal(scoreToGrade(90), 'A-');
    assert.equal(scoreToGrade(92), 'A-');
  });

  it('returns B for score >= 83 and < 87', () => {
    assert.equal(scoreToGrade(83), 'B');
    assert.equal(scoreToGrade(86), 'B');
  });

  it('returns C for score >= 73 and < 77', () => {
    assert.equal(scoreToGrade(73), 'C');
    assert.equal(scoreToGrade(76), 'C');
  });

  it('returns D for score >= 63 and < 67', () => {
    assert.equal(scoreToGrade(63), 'D');
    assert.equal(scoreToGrade(66), 'D');
  });

  it('returns F for score < 60', () => {
    assert.equal(scoreToGrade(50), 'F');
    assert.equal(scoreToGrade(0), 'F');
    assert.equal(scoreToGrade(59), 'F');
  });

  it('handles exact boundary at 0', () => {
    assert.equal(scoreToGrade(0), 'F');
  });
});

// ---------------------------------------------------------------------------
// calculateScores
// ---------------------------------------------------------------------------
describe('calculateScores', () => {
  it('returns score 100 when all checks PASS', () => {
    const results = {
      'technical-seo': {
        name: 'Technical SEO',
        checks: [
          { id: 'a', status: 'PASS' },
          { id: 'b', status: 'PASS' },
          { id: 'c', status: 'PASS' },
        ],
      },
    };

    const scores = calculateScores(results);
    assert.equal(scores['technical-seo'].score, 100);
    assert.equal(scores['technical-seo'].grade, 'A+');
  });

  it('returns score 0 when all checks FAIL', () => {
    const results = {
      'security': {
        name: 'Security',
        checks: [
          { id: 'a', status: 'FAIL' },
          { id: 'b', status: 'FAIL' },
        ],
      },
    };

    const scores = calculateScores(results);
    assert.equal(scores['security'].score, 0);
    assert.equal(scores['security'].grade, 'F');
  });

  it('returns correct score with mixed PASS/WARN/FAIL', () => {
    const results = {
      'on-page-seo': {
        name: 'On-Page SEO',
        checks: [
          { id: 'a', status: 'PASS' },  // 100
          { id: 'b', status: 'WARN' },  // 50
          { id: 'c', status: 'FAIL' },  // 0
        ],
      },
    };

    const scores = calculateScores(results);
    // (100 + 50 + 0) / 3 = 50
    assert.equal(scores['on-page-seo'].score, 50);
  });

  it('skips checks with SKIP status', () => {
    const results = {
      'performance': {
        name: 'Performance',
        checks: [
          { id: 'a', status: 'PASS' },
          { id: 'b', status: 'PASS' },
          { id: 'c', status: 'SKIP' },
        ],
      },
    };

    const scores = calculateScores(results);
    // Only 2 scorable checks, both PASS -> (100 + 100) / 2 = 100
    assert.equal(scores['performance'].score, 100);
  });

  it('returns score 0 when all checks are SKIP', () => {
    const results = {
      'ai-readiness': {
        name: 'AI Readiness',
        checks: [
          { id: 'a', status: 'SKIP' },
          { id: 'b', status: 'SKIP' },
        ],
      },
    };

    const scores = calculateScores(results);
    assert.equal(scores['ai-readiness'].score, 0);
  });

  it('uses known weight for recognized categories', () => {
    const results = {
      'technical-seo': {
        name: 'Technical SEO',
        checks: [{ id: 'a', status: 'PASS' }],
      },
    };

    const scores = calculateScores(results);
    assert.equal(scores['technical-seo'].weight, 0.20);
  });

  it('uses default weight 0.10 for unknown categories', () => {
    const results = {
      'custom-category': {
        name: 'Custom',
        checks: [{ id: 'a', status: 'PASS' }],
      },
    };

    const scores = calculateScores(results);
    assert.equal(scores['custom-category'].weight, 0.10);
  });

  it('includes checks in the output', () => {
    const checks = [
      { id: 'a', status: 'PASS' },
      { id: 'b', status: 'FAIL' },
    ];
    const results = {
      'security': { name: 'Security', checks },
    };

    const scores = calculateScores(results);
    assert.deepEqual(scores['security'].checks, checks);
  });
});

// ---------------------------------------------------------------------------
// calculateOverallGrade
// ---------------------------------------------------------------------------
describe('calculateOverallGrade', () => {
  it('returns weighted average across categories', () => {
    const scores = {
      'technical-seo': { score: 100, weight: 0.50 },
      'security': { score: 0, weight: 0.50 },
    };

    const overall = calculateOverallGrade(scores);
    // (100 * 0.50 + 0 * 0.50) / (0.50 + 0.50) = 50
    assert.equal(overall.score, 50);
    assert.equal(overall.grade, 'F');
  });

  it('normalizes weights that do not sum to 1.0', () => {
    const scores = {
      'technical-seo': { score: 80, weight: 0.20 },
    };

    const overall = calculateOverallGrade(scores);
    // (80 * 0.20) / 0.20 = 80
    assert.equal(overall.score, 80);
    assert.equal(overall.grade, 'B-');
  });

  it('returns score 0 and grade F when no categories exist', () => {
    const overall = calculateOverallGrade({});
    assert.equal(overall.score, 0);
    assert.equal(overall.grade, 'F');
  });

  it('returns correct grade for a high-scoring set', () => {
    const scores = {
      'a': { score: 100, weight: 0.50 },
      'b': { score: 96, weight: 0.50 },
    };

    const overall = calculateOverallGrade(scores);
    // (100 * 0.50 + 96 * 0.50) / 1.0 = 98
    assert.equal(overall.score, 98);
    assert.equal(overall.grade, 'A+');
  });
});

// ---------------------------------------------------------------------------
// getQuickWins
// ---------------------------------------------------------------------------
describe('getQuickWins', () => {
  it('returns FAIL and WARN items only', () => {
    const scores = {
      'seo': {
        name: 'SEO',
        checks: [
          { id: 'a', status: 'PASS', impact: 5, effort: 1 },
          { id: 'b', status: 'FAIL', impact: 5, effort: 1 },
          { id: 'c', status: 'WARN', impact: 3, effort: 2 },
          { id: 'd', status: 'SKIP', impact: 5, effort: 1 },
        ],
      },
    };

    const wins = getQuickWins(scores);
    assert.equal(wins.length, 2);
    const ids = wins.map(w => w.id);
    assert.ok(ids.includes('b'));
    assert.ok(ids.includes('c'));
  });

  it('sorts by priority (impact/effort) descending', () => {
    const scores = {
      'seo': {
        name: 'SEO',
        checks: [
          { id: 'low', status: 'FAIL', impact: 1, effort: 5 },   // priority 0.2
          { id: 'high', status: 'FAIL', impact: 5, effort: 1 },  // priority 5.0
          { id: 'mid', status: 'WARN', impact: 3, effort: 1 },   // priority 3.0
        ],
      },
    };

    const wins = getQuickWins(scores);
    assert.equal(wins[0].id, 'high');
    assert.equal(wins[1].id, 'mid');
    assert.equal(wins[2].id, 'low');
  });

  it('breaks priority ties by impact descending', () => {
    const scores = {
      'seo': {
        name: 'SEO',
        checks: [
          { id: 'a', status: 'FAIL', impact: 4, effort: 2 },  // priority 2.0
          { id: 'b', status: 'FAIL', impact: 6, effort: 3 },  // priority 2.0, higher impact
        ],
      },
    };

    const wins = getQuickWins(scores);
    assert.equal(wins[0].id, 'b');
    assert.equal(wins[1].id, 'a');
  });

  it('respects count limit', () => {
    const scores = {
      'seo': {
        name: 'SEO',
        checks: [
          { id: 'a', status: 'FAIL', impact: 5, effort: 1 },
          { id: 'b', status: 'FAIL', impact: 4, effort: 1 },
          { id: 'c', status: 'FAIL', impact: 3, effort: 1 },
          { id: 'd', status: 'FAIL', impact: 2, effort: 1 },
          { id: 'e', status: 'FAIL', impact: 1, effort: 1 },
        ],
      },
    };

    const wins = getQuickWins(scores, 2);
    assert.equal(wins.length, 2);
  });

  it('defaults count limit to 5', () => {
    const checks = [];
    for (let i = 0; i < 10; i++) {
      checks.push({ id: `check-${i}`, status: 'FAIL', impact: 5, effort: 1 });
    }
    const scores = {
      'seo': { name: 'SEO', checks },
    };

    const wins = getQuickWins(scores);
    assert.equal(wins.length, 5);
  });

  it('uses default impact=3 and effort=2 when not specified', () => {
    const scores = {
      'seo': {
        name: 'SEO',
        checks: [
          { id: 'no-vals', status: 'FAIL' },   // priority 3/2 = 1.5
          { id: 'hi', status: 'FAIL', impact: 5, effort: 1 },  // priority 5
        ],
      },
    };

    const wins = getQuickWins(scores);
    // 'hi' should come first with higher priority
    assert.equal(wins[0].id, 'hi');
    assert.equal(wins[1].id, 'no-vals');
  });

  it('attaches category name to each finding', () => {
    const scores = {
      'seo': {
        name: 'SEO Category',
        checks: [
          { id: 'a', status: 'FAIL', impact: 5, effort: 1 },
        ],
      },
    };

    const wins = getQuickWins(scores);
    assert.equal(wins[0].category, 'SEO Category');
  });

  it('returns empty array when no FAIL or WARN checks exist', () => {
    const scores = {
      'seo': {
        name: 'SEO',
        checks: [
          { id: 'a', status: 'PASS' },
          { id: 'b', status: 'SKIP' },
        ],
      },
    };

    const wins = getQuickWins(scores);
    assert.equal(wins.length, 0);
  });
});
