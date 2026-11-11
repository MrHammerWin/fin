/* Exact minor-unit calculations for the interactive, local-only demo. */
const Finance = (() => {
  const units = value => {
    const s = String(value).trim().replace(',', '.');
    if (!/^-?\d+(\.\d{1,2})?$/.test(s)) throw Error('Укажите сумму с точностью до копеек.');
    const negative = s.startsWith('-');
    const [a, b = ''] = s.replace(/^-/, '').split('.');
    return (BigInt(a) * 100n + BigInt(b.padEnd(2, '0'))) * (negative ? -1n : 1n);
  };
  const decimal = n => `${n < 0n ? '-' : ''}${(n < 0n ? -n : n) / 100n}.${String((n < 0n ? -n : n) % 100n).padStart(2, '0')}`;
  const dateValid = s => /^\d{4}-\d{2}-\d{2}$/.test(s) && !isNaN(Date.parse(s)) && new Date(s).toISOString().slice(0, 10) === s;
  const nextDay = s => new Date(Date.parse(s) + 86400000).toISOString().slice(0, 10);
  const categories = {
    customer_receipts: ['operating', 'in'], supplier_payments: ['operating', 'out'],
    operating_payments: ['operating', 'out'], equipment_purchase: ['investing', 'out'],
    loan_received: ['financing', 'in'], loan_repaid: ['financing', 'out']
  };
  function calculate(base, edits = [], cutoff = base.cutoff) {
    if (!dateValid(cutoff) || cutoff < base.cutoff || cutoff > base.end) throw Error('Дата прогноза должна быть в пределах демонстрационного периода.');
    const data = JSON.parse(JSON.stringify(base));
    data.cutoff = cutoff;
    const ids = new Set(data.operations.map(t => t.id));
    const allocated = Object.fromEntries(data.obligations.map(o => [o.id, 0n]));
    for (const t of data.operations) if (t.obligation) allocated[t.obligation] += units(t.amount);
    for (const t of edits) {
      const o = data.obligations.find(o => o.id === t.obligation);
      if (!o || ids.has(t.id) || !dateValid(t.date) || t.date < base.opening_date || t.date >= base.end || units(t.amount) <= 0n || t.currency !== o.currency || t.account !== o.account || t.direction !== o.direction || !t.manual) throw Error('Некорректная или повторная регистрация оплаты.');
      allocated[o.id] += units(t.amount);
      if (allocated[o.id] > units(o.amount)) throw Error('Сумма оплаты превышает непогашенный остаток.');
      ids.add(t.id);
      data.operations.push({...t});
    }
    const balances = Object.fromEntries(Object.keys(base.rates).map(c => [c, 0n]));
    for (const a of base.accounts) balances[a.currency] += units(a.opening);
    const paid = Object.fromEntries(data.obligations.map(o => [o.id, 0n]));
    for (const t of data.operations) if (base.opening_date <= t.date && t.date < cutoff) {
      balances[t.currency] += units(t.amount) * (t.direction === 'in' ? 1n : -1n);
      if (t.obligation) paid[t.obligation] += units(t.amount);
    }
    for (const t of base.corpus?.cash || []) if (base.opening_date <= t.date && t.date < cutoff) balances[t.currency] += units(t.amount) * (t.direction === 'in' ? 1n : -1n);
    data.obligations = data.obligations.map(o => {
      const outstanding = units(o.amount) - paid[o.id];
      if (outstanding < 0n) throw Error('Оплата превышает сумму обязательства.');
      const overdue = outstanding > 0n && o.due < cutoff;
      const scheduled = o.scheduled_date || o.expected;
      return {...o, paid: decimal(paid[o.id]), outstanding: decimal(outstanding), status: outstanding === 0n ? 'paid' : overdue ? 'overdue' : 'in_progress', overdue, expected: scheduled < cutoff ? cutoff : scheduled};
    });
    data.calendar = [];
    const convert = (values, key) => {
      let total = 0n;
      for (const c of Object.keys(base.rates)) {
        const product = units(values[c][key]) * units(base.rates[c]);
        total += (product < 0n ? -1n : 1n) * ((product < 0n ? -product : product) + 50n) / 100n;
      }
      return decimal(total);
    };
    for (let day = cutoff; day <= base.end; day = nextDay(day)) {
      const currencies = {};
      for (const c of Object.keys(base.rates)) {
        let incoming = 0n, outgoing = 0n;
        for (const o of data.obligations) if (o.currency === c && o.expected === day) {
          if (o.direction === 'in') incoming += units(o.outstanding); else outgoing += units(o.outstanding);
        }
        for (const b of base.corpus?.buckets || []) if (b.currency === c && (b.scheduled_date < cutoff ? cutoff : b.scheduled_date) === day) {
          let remaining = units(b.amount);
          for (const [date, value] of Object.entries(b.payments)) if (base.opening_date <= date && date < cutoff) remaining -= units(value);
          if (b.direction === 'in') incoming += remaining; else outgoing += remaining;
        }
        const opening = balances[c];
        balances[c] += incoming - outgoing;
        currencies[c] = {opening: decimal(opening), in: decimal(incoming), out: decimal(outgoing), closing: decimal(balances[c])};
      }
      data.calendar.push({date: day, currencies, total_rub: convert(currencies, 'closing')});
    }
    const reports = {start: base.opening_date, end_exclusive: cutoff, currencies: {}};
    for (const c of Object.keys(base.rates)) {
      const v = {opening: 0n, cash_in: 0n, cash_out: 0n, operating_flow: 0n, investing_flow: 0n, financing_flow: 0n, revenue: 0n, cost_of_sales: 0n, operating_expenses: 0n};
      for (const key of Object.keys(categories)) v[key] = 0n;
      for (const a of base.accounts) if (a.currency === c) v.opening += units(a.opening);
      for (const t of [...data.operations, ...(base.corpus?.cash || [])]) if (t.currency === c && base.opening_date <= t.date && t.date < cutoff && t.cash_category !== 'internal_transfer') {
        const classification = categories[t.cash_category];
        if (!classification || classification[1] !== t.direction) throw Error('Неизвестная статья ДДС.');
        const value = units(t.amount);
        v[t.cash_category] += value;
        v[t.direction === 'in' ? 'cash_in' : 'cash_out'] += value;
        v[classification[0] + '_flow'] += value * (t.direction === 'in' ? 1n : -1n);
      }
      for (const entry of [...base.accruals, ...(base.corpus?.pnl || [])]) if (entry.currency === c && base.opening_date <= entry.recognized_date && entry.recognized_date < cutoff) v[entry.category] += units(entry.amount);
      v.net_flow = v.cash_in - v.cash_out;
      v.closing = v.opening + v.net_flow;
      v.gross_profit = v.revenue - v.cost_of_sales;
      v.operating_profit = v.gross_profit - v.operating_expenses;
      reports.currencies[c] = Object.fromEntries(Object.entries(v).map(([k, value]) => [k, decimal(value)]));
    }
    reports.currencies.TOTAL = Object.fromEntries(Object.keys(reports.currencies[Object.keys(base.rates)[0]]).map(key => [key, convert(reports.currencies, key)]));
    data.reports = reports;
    return data;
  }
  function registerPayment(base, edits, cutoff, obligationId, value, date) {
    const current = calculate(base, edits, cutoff);
    const o = current.obligations.find(o => o.id === obligationId);
    const sum = units(value);
    if (!o || sum <= 0n || sum > units(o.outstanding)) throw Error('Укажите сумму больше нуля и не больше непогашенного остатка.');
    if (!dateValid(date) || date < base.opening_date || date >= base.end) throw Error('Дата оплаты должна быть в демонстрационном периоде и раньше его последнего дня.');
    let number = 1;
    const existing = new Set([...base.operations, ...edits].map(t => t.id));
    while (existing.has('DEMO-PAY-' + number)) number++;
    const operation = {id: 'DEMO-PAY-' + number, date, account: o.account, currency: o.currency, counterparty_id: o.counterparty_id, counterparty: o.counterparty, direction: o.direction, amount: decimal(sum), obligation: o.id, purpose: 'Ручная регистрация оплаты: ' + (o.invoice || o.purpose), cash_category: o.direction === 'in' ? 'customer_receipts' : 'supplier_payments', source: '', row: null, manual: true};
    const updated = [...edits, operation];
    const nextCutoff = date >= cutoff ? nextDay(date) : cutoff;
    return {edits: updated, cutoff: nextCutoff, data: calculate(base, updated, nextCutoff)};
  }
  return {calculate, registerPayment, units, decimal};
})();
if (typeof module !== 'undefined' && module.exports) module.exports = Finance;
