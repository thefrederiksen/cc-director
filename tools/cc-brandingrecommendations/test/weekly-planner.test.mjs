import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createWeeklyPlan, effortToHours } from '../src/weekly-planner.mjs';

describe('effortToHours', () => {
  it('should map effort scores to hours', () => {
    assert.equal(effortToHours(1), 1);
    assert.equal(effortToHours(2), 2.5);
    assert.equal(effortToHours(3), 5);
    assert.equal(effortToHours(4), 10);
    assert.equal(effortToHours(5), 18);
  });

  it('should default to effort 3 for unknown values', () => {
    assert.equal(effortToHours(99), 5);
  });
});

describe('createWeeklyPlan', () => {
  it('should create a weekly plan from recommendations', () => {
    const recs = [
      { id: 'a', priority: 'Quick Win', impact: 5, effort: 1, sourceStatus: 'FAIL' },
      { id: 'b', priority: 'Strategic', impact: 4, effort: 3, sourceStatus: 'FAIL' },
      { id: 'c', priority: 'Easy Fill', impact: 2, effort: 1, sourceStatus: 'WARN' },
      { id: 'd', priority: 'Deprioritize', impact: 2, effort: 4, sourceStatus: 'WARN' },
    ];

    const plan = createWeeklyPlan(recs, 'medium');

    assert.equal(plan.budget, 'medium');
    assert.equal(plan.weeklyCapacity, 10);
    assert.equal(plan.totalRecs, 4);
    assert.ok(plan.weeks);
    assert.ok(plan.phases);
    assert.equal(plan.phases.length, 5); // 4 phases + maintenance
  });

  it('should separate PASS items into maintenance', () => {
    const recs = [
      { id: 'pass-item', priority: 'Quick Win', impact: 4, effort: 1, sourceStatus: 'PASS' },
      { id: 'fail-item', priority: 'Quick Win', impact: 4, effort: 1, sourceStatus: 'FAIL' },
    ];

    const plan = createWeeklyPlan(recs, 'medium');

    assert.equal(plan.maintenance.length, 1);
    assert.equal(plan.maintenance[0].id, 'pass-item');
  });

  it('should place Quick Wins in early weeks', () => {
    const recs = [
      { id: 'qw1', priority: 'Quick Win', impact: 5, effort: 1, sourceStatus: 'FAIL' },
      { id: 'qw2', priority: 'Quick Win', impact: 4, effort: 1, sourceStatus: 'FAIL' },
    ];

    const plan = createWeeklyPlan(recs, 'medium');

    // Quick wins should be in weeks 1-2
    const qwPhase = plan.phases.find(p => p.name === 'Quick Wins');
    assert.ok(qwPhase);
    assert.ok(qwPhase.recCount > 0);
  });

  it('should respect budget capacity', () => {
    // Low budget = 5 hrs/week
    // 3 items at effort 2 (2.5h each = 7.5h total) should need 2 weeks
    const recs = [
      { id: 'a', priority: 'Quick Win', impact: 5, effort: 2, sourceStatus: 'FAIL' },
      { id: 'b', priority: 'Quick Win', impact: 5, effort: 2, sourceStatus: 'FAIL' },
      { id: 'c', priority: 'Quick Win', impact: 4, effort: 2, sourceStatus: 'FAIL' },
    ];

    const planLow = createWeeklyPlan(recs, 'low');
    const planHigh = createWeeklyPlan(recs, 'high');

    assert.equal(planLow.weeklyCapacity, 5);
    assert.equal(planHigh.weeklyCapacity, 20);

    // Low budget should spread across more weeks than high budget
    const lowWeeks = Object.keys(planLow.weeks).length;
    const highWeeks = Object.keys(planHigh.weeks).length;
    assert.ok(lowWeeks >= highWeeks, 'Low budget should use >= weeks than high budget');
  });

  it('should handle empty recommendations', () => {
    const plan = createWeeklyPlan([], 'medium');
    assert.equal(plan.totalRecs, 0);
    assert.equal(plan.totalHours, 0);
    assert.equal(Object.keys(plan.weeks).length, 0);
    assert.equal(plan.maintenance.length, 0);
  });

  it('should calculate totalHours correctly', () => {
    const recs = [
      { id: 'a', priority: 'Quick Win', impact: 5, effort: 1, sourceStatus: 'FAIL' },  // 1h
      { id: 'b', priority: 'Quick Win', impact: 4, effort: 2, sourceStatus: 'FAIL' },  // 2.5h
      { id: 'c', priority: 'Strategic', impact: 4, effort: 3, sourceStatus: 'FAIL' },   // 5h
    ];

    const plan = createWeeklyPlan(recs, 'high');
    assert.equal(plan.totalHours, 8.5);
  });
});
