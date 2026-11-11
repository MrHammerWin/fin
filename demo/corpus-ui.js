/* The browser holds additive events, while source records are fetched in bounded pages. */
(() => {
  if (!BASE.corpus) return;
  $('cards').classList.add('corpus-cards');
  const online = !!window.finplanRuntime;
  const datasetId = location.pathname.match(/\/datasets\/([^/]+)\//)?.[1];
  const count = n => Number(n).toLocaleString('ru-RU');
  const all = D.obligations.length + D.corpus.obligations;
  const note = document.createElement('p'); note.id = 'corpus-accounting-summary'; note.className = 'notice';
  note.textContent = `В расчётах: ${count(all)} обязательств, ${count(D.operations.length + D.corpus.operations)} банковских операций, ${count(D.accruals.length + D.corpus.accruals)} начислений. Учтён весь корпус из ${count(D.corpus.files)} файлов. Итоги пересчитываются по выбранной дате; исходные записи открываются по страницам.`;
  $('notice').after(note);
  if (!online) note.textContent += ' Для постраничных расшифровок запустите веб-приложение.';
  const states = new Map(); let sequence = 0;
  function pagination(anchor, key) {
    let node = $(key + '-pager');
    if (!node) { node = document.createElement('div'); node.id = key + '-pager'; node.className = 'pagination'; anchor.after(node); }
    return node;
  }
  async function load(key, kind, filters, render) {
    const signature = JSON.stringify([D.cutoff, mode, filters, edits]);
    let state = states.get(key);
    if (!state || state.signature !== signature) { state = {signature, cursor: 0, history: [], next: null}; states.set(key, state); }
    const request = ++sequence; state.request = request;
    const payload = {kind, cutoff: D.cutoff, currency: mode, cursor: state.cursor, limit: 50, edits, ...filters};
    const pager = pagination(key === 'registry' ? $('payments').parentElement.parentElement : $(key + '-sources').parentElement, key);
    pager.textContent = 'Читаем страницу исходных записей…';
    try {
      const response = await fetch(`/api/datasets/${datasetId}/records`, {method: 'POST', headers: {'Content-Type': 'application/json', 'X-FinPlan-Token': window.finplanRuntime.token}, body: JSON.stringify(payload)});
      const page = await response.json();
      if (!response.ok) throw Error(page.error || 'Не удалось прочитать записи.');
      if (states.get(key) !== state || state.request !== request) return;
      state.next = page.next_cursor; render(page);
      pager.innerHTML = `<button data-back ${state.history.length ? '' : 'disabled'}>Назад</button><span>${page.total == null ? 'Поиск в исходных записях' : 'Найдено: ' + count(page.total)} · страница до 50 записей</span><button data-next ${state.next == null ? 'disabled' : ''}>Далее</button>`;
      pager.querySelector('[data-back]').onclick = () => { state.cursor = state.history.pop() ?? 0; load(key, kind, filters, render); };
      pager.querySelector('[data-next]').onclick = () => { state.history.push(state.cursor); state.cursor = state.next; load(key, kind, filters, render); };
    } catch (error) { if (state.request === request) pager.textContent = error.message; }
  }
  const originalRegistry = renderRegistry;
  renderRegistry = function () {
    if (!online) return originalRegistry();
    const status = $('status-filter').value || 'all', query = $('obligation-search').value.trim();
    $('payment-detail').textContent = 'Выберите обязательство, чтобы посмотреть суммы и источники.';
    load('registry', 'obligations', {status, day: selected, query}, page => {
      const items = page.obligations.map(o => D.obligations.find(core => core.id === o.id) || o);
      $('registry-count').textContent = `Всего в наборе: ${count(all)} обязательств. ${page.total == null ? page.next_cursor == null ? 'Поиск завершён.' : 'Поиск продолжится на следующих страницах.' : 'По фильтрам: ' + count(page.total) + '.'}`;
      $('payments').innerHTML = items.length ? items.map(o => `<tr><td><button data-id="${esc(o.id)}">${esc(o.invoice || o.purpose)}</button><div class="muted">${esc(o.id)}${o.invoice_id ? ' · ' + esc(o.invoice_id) : ''}</div></td><td>${esc(o.counterparty)}</td><td>${o.direction === 'in' ? 'К получению' : 'К оплате'}</td><td>${fmt(o.amount, o.currency)}</td><td>${fmt(o.paid, o.currency)}</td><td>${fmt(o.outstanding, o.currency)}</td><td>${dateLabel(o.due)}</td><td>${o.status === 'paid' ? '—' : dateLabel(o.expected)}</td><td><span class="tag ${o.status}">${statusNames[o.status]}</span></td></tr>`).join('') : '<tr><td colspan="9">В этой части совпадений нет. Продолжите поиск кнопкой «Далее».</td></tr>';
      document.querySelectorAll('[data-id]').forEach(button => button.onclick = () => openObligation(button.dataset.id));
    });
  };
  let searchTimer;
  $('obligation-search').oninput = () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => renderRegistry(), 250); };
  const originalOpen = openObligation;
  openObligation = async function (id) {
    if (!id.startsWith('BO-')) return originalOpen(id);
    $('payment-detail').textContent = 'Читаем документы и оплаты…';
    try {
      const response = await fetch(`/api/datasets/${datasetId}/obligations/${encodeURIComponent(id)}?cutoff=${D.cutoff}`);
      const result = await response.json(); if (!response.ok) throw Error(result.error || 'Запись не найдена.');
      const o = result.obligation;
      $('payment-detail').innerHTML = `<h3>${esc(o.id)} · ${esc(o.counterparty)}</h3><p>Счёт: ${esc(o.invoice_id)} / ${esc(o.invoice)}<br>Договор: ${esc(o.contract_id)} / ${esc(o.contract)}<br>Акт: ${esc(o.act_id)} / ${esc(o.act)}</p><p>Сумма: ${fmt(o.amount, o.currency)} · Оплачено: ${fmt(o.paid, o.currency)} · Остаток: ${fmt(o.outstanding, o.currency)}</p><p>Срок: ${esc(o.due)} · Дата в календаре: ${esc(o.expected)} · ${statusNames[o.status]}</p><p>Источники: ${o.sources.map(link).join(' · ')}</p><h4>Фактические оплаты</h4>${result.payments.map(t => `<div>${esc(t.date)} · ${esc(t.id)} · ${fmt(t.amount, t.currency)} · ${link(t.source)} · строка ${t.row}</div>`).join('') || 'Оплат на выбранную дату нет.'}<p class="muted">Это запись исходного корпуса. Демонстрационная регистрация ручной оплаты доступна для основных документов.</p>`;
    } catch (error) { $('payment-detail').textContent = error.message; }
  };
  const originalReports = renderReports;
  renderReports = function () {
    originalReports(); if (!online) return;
    load('pnl', 'pnl', {}, page => { $('pnl-sources').innerHTML = page.accruals.map(a => `<tr><td>${esc(a.recognized_date)}</td><td>${esc(a.counterparty)}<br>${esc(a.category)}<br>${esc(a.id)} · ${esc(a.obligation)}</td><td>${fmt(a.amount, a.currency)}</td><td>${link(a.source)}</td></tr>`).join(''); });
    load('cashflow', 'cash', {}, page => { $('cashflow-sources').innerHTML = page.operations.map(t => `<tr><td>${esc(t.date)}<br>${esc(t.id)}</td><td>${esc(t.counterparty)}<br>${esc(t.cash_category)}</td><td>${t.direction === 'in' ? 'Поступление' : 'Выплата'}</td><td>${fmt(t.amount, t.currency)}</td><td>${t.source ? link(t.source) + ' · строка ' + t.row : 'Демонстрационная ручная оплата'}</td></tr>`).join(''); });
  };
  const originalRefresh = refresh;
  refresh = function () {
    originalRefresh();
    const parties = [...new Map([...D.corpus.counterparties, ...D.counterparties].map(cp => [cp.id, cp])).values()];
    $('counterparty-list').innerHTML = parties.map(cp => `<tr><td>${esc(cp.id)}</td><td>${esc(cp.name)}</td><td>${count(D.operations.filter(t => t.counterparty_id === cp.id).length + (D.corpus.counterparty_operations[cp.id] || 0))}</td><td>${count(D.obligations.filter(o => o.counterparty_id === cp.id).length + (D.corpus.counterparty_obligations[cp.id] || 0))}</td></tr>`).join('');
    const dates = D.calendar.filter(day => Object.values(day.currencies).some(c => cents(c.in) > 0n || cents(c.out) > 0n)).map(day => day.date);
    $('date-filter').innerHTML = '<option value="all">Все даты</option>' + dates.map(day => `<option value="${day}">${dateLabel(day)}</option>`).join(''); $('date-filter').value = selected;
  };
  refresh();
})();
