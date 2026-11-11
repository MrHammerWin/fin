// Run with: node --test tests/test_finance.cjs
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Finance = require('../demo/finance.js');
const base = JSON.parse(fs.readFileSync(path.join(__dirname, '../output/dataset.json'), 'utf8'));

test('Whole-corpus browser aggregates match C# and an expanded source ledger at several cutoffs', () => {
  const file = path.join(__dirname, '../.test-artifacts/corpus-parity.json');
  if (!fs.existsSync(file)) throw Error('Run dotnet test first to build the mixed-format source fixture.');
  const data = JSON.parse(fs.readFileSync(file, 'utf8'));
  const result = Finance.calculate(data);
  assert.deepEqual(result.calendar, data.calendar);
  assert.deepEqual(result.reports, data.reports);
  const expanded = structuredClone(data); expanded.corpus = null;
  expanded.operations.push(...data.corpus.cash.map((t,i) => ({...t,id:'CORPUS-CASH-'+i,obligation:null})));
  expanded.accruals.push(...data.corpus.pnl.map((a,i) => ({...a,id:'CORPUS-PNL-'+i})));
  for (const cutoff of ['2026-10-15','2026-10-29','2026-11-15','2026-12-01','2026-12-31']) {
    const actual = Finance.calculate(data, [], cutoff), cash = Finance.calculate(expanded, [], cutoff);
    assert.deepEqual(actual.reports, cash.reports);
    for (const c of Object.keys(data.rates)) assert.equal(actual.calendar[0].currencies[c].opening, actual.reports.currencies[c].closing);
    for (const day of actual.calendar) for (const v of Object.values(day.currencies)) assert.equal(Finance.units(v.closing), Finance.units(v.opening)+Finance.units(v.in)-Finance.units(v.out));
  }
});

test('Browser calculations match C# baseline for every currency, day and report', () => {
  const current = Finance.calculate(base);
  assert.deepEqual(current.calendar, base.calendar);
  assert.deepEqual(current.reports, base.reports);
  assert.deepEqual(current.obligations.map(o => [o.id, o.paid, o.outstanding, o.status]), base.obligations.map(o => [o.id, o.paid, o.outstanding, o.status]));
});
test('Partial and full payment update status, balance and DDS but leave PnL unchanged', () => {
  const original = JSON.stringify(base);
  const o = base.obligations.find(o => o.status === 'overdue');
  const first = Finance.registerPayment(base, [], base.cutoff, o.id, '100.00', base.cutoff);
  assert.equal(first.cutoff, '2026-10-16');
  const partial = first.data.obligations.find(x => x.id === o.id);
  assert.equal(partial.status, 'overdue');
  assert.equal(Finance.units(partial.outstanding), Finance.units(o.outstanding) - 10000n);
  const result = Finance.registerPayment(base, first.edits, first.cutoff, o.id, partial.outstanding, base.cutoff);
  const paid = result.data.obligations.find(x => x.id === o.id);
  assert.equal(paid.status, 'paid');
  assert.equal(paid.outstanding, '0.00');
  const c = o.currency;
  assert.equal(Finance.units(result.data.reports.currencies[c].closing), Finance.units(base.reports.currencies[c].closing) - Finance.units(o.outstanding));
  assert.equal(result.data.reports.currencies[c].closing, result.data.calendar[0].currencies[c].opening);
  assert.equal(result.data.reports.currencies[c].operating_profit, base.reports.currencies[c].operating_profit);
  assert.equal(JSON.stringify(base), original);
});
test('Received payment completes incoming obligation without creating additional revenue', () => {
  const o = base.obligations.find(o => o.direction === 'in' && o.status === 'in_progress');
  const result = Finance.registerPayment(base, [], base.cutoff, o.id, o.outstanding, base.cutoff);
  assert.equal(result.data.obligations.find(x => x.id === o.id).status, 'paid');
  assert.equal(Finance.units(result.data.reports.currencies[o.currency].closing), Finance.units(base.reports.currencies[o.currency].closing) + Finance.units(o.outstanding));
  assert.equal(result.data.reports.currencies[o.currency].revenue, base.reports.currencies[o.currency].revenue);
});
test('Statuses change with the chosen date and cancellation restores outstanding balance', () => {
  const o = base.obligations.find(o => o.due === '2026-10-16' && o.status === 'in_progress');
  assert.equal(Finance.calculate(base, [], '2026-10-16').obligations.find(x => x.id === o.id).status, 'in_progress');
  assert.equal(Finance.calculate(base, [], '2026-10-17').obligations.find(x => x.id === o.id).status, 'overdue');
  const payment = Finance.registerPayment(base, [], base.cutoff, o.id, o.outstanding, '2026-10-16');
  assert.equal(payment.data.obligations.find(x => x.id === o.id).status, 'paid');
  assert.equal(Finance.calculate(base, [], payment.cutoff).obligations.find(x => x.id === o.id).status, 'overdue');
  assert.equal(Finance.calculate(base, payment.edits, '2026-10-15').obligations.find(x => x.id === o.id).status, 'in_progress');
});
test('Invalid amounts, dates and duplicate payment IDs are rejected', () => {
  const o = base.obligations.find(o => o.status === 'overdue');
  for (const value of ['0', '-1', '1.001', 'abc', '999999999']) assert.throws(() => Finance.registerPayment(base, [], base.cutoff, o.id, value, base.cutoff));
  for (const day of ['2026-02-30', '2026-09-30', base.end]) assert.throws(() => Finance.registerPayment(base, [], base.cutoff, o.id, '1', day));
  const result = Finance.registerPayment(base, [], base.cutoff, o.id, '1', base.cutoff);
  assert.throws(() => Finance.calculate(base, [...result.edits, ...result.edits], result.cutoff));
});
