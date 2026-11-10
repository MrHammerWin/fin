// Lightweight DOM harness for offline navigation and form handlers; not a visual browser test.
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function viewer(hash = '', runtime = null, fetchImpl = async()=>{throw Error('Unexpected fetch')}) {
  const html = fs.readFileSync(path.join(__dirname, '../demo/viewer.html'), 'utf8')
    .replace('__DATA__', fs.readFileSync(path.join(__dirname, '../output/dataset.json'), 'utf8').replace(/</g, '\\u003c'))
    .replace('__FINANCE__', fs.readFileSync(path.join(__dirname, '../demo/finance.js'), 'utf8'));
  const scripts = [...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)];
  const nodes = new Map();
  const node = (id, extra = {}) => {
    if (!nodes.has(id)) nodes.set(id, {id, innerHTML: '', textContent: '', value: '', hidden: false, classList: {toggle() {}}, scrollIntoView() {}, ...extra});
    return nodes.get(id);
  };
  for (const match of html.split('<script')[0].matchAll(/id="([^"]+)"/g)) node(match[1]);
  node('dataset').textContent = scripts[0][1];
  node('currency').value = 'TOTAL';
  const sections = [...html.matchAll(/data-page="([^"]+)"/g)].map(m => ({dataset: {page: m[1]}, hidden: false}));
  const nav = [...html.matchAll(/<a href="#\/([^"]+)"/g)].map(m => ({classList: {toggle() {}}, getAttribute() {return '#/' + m[1]}, setAttribute() {}, removeAttribute() {}}));
  const document = {
    getElementById(id) {
      if (nodes.has(id)) return nodes.get(id);
      const detail = node('payment-detail').innerHTML;
      if (!detail.includes('id="' + id + '"')) return null;
      const match = detail.match(new RegExp('id="' + id + '"[^>]*value="([^"]*)"'));
      return node(id, {value: match ? match[1] : ''});
    },
    querySelectorAll(selector) {
      if (selector === '[data-page]') return sections;
      if (selector === 'nav a') return nav;
      if (selector === '[data-id]') return [...node('payments').innerHTML.matchAll(/data-id="([^"]+)"/g)].map(m => ({dataset: {id: m[1]}}));
      if (selector === '[data-revert]') return [...node('payment-detail').innerHTML.matchAll(/data-revert="([^"]+)"/g)].map(m => ({dataset: {revert: m[1]}}));
      return [];
    }
  };
  let target = null;
  const location = {assign(url){target=url},get hash(){return hash},set hash(value){hash=value.startsWith('#')?value:'#'+value}};
  const context = vm.createContext({document, location, window: {finplanRuntime:runtime,addEventListener() {}, scrollTo() {}}, fetch:fetchImpl,console, Intl});
  vm.runInContext(scripts[1][1], context);
  return {run: code => vm.runInContext(code, context), nodes, sections,target:()=>target};
}

test('Home is separate; navigation, unknown route and deep links work', () => {
  const app = viewer();
  assert.equal(app.nodes.get('page-title').textContent, 'Главная');
  assert.ok(app.sections.filter(x => !x.hidden).every(x => x.dataset.page === 'home'));
  app.run("navigate('documents')");
  assert.ok(app.sections.filter(x => !x.hidden).every(x => x.dataset.page === 'documents'));
  app.run("navigate('unknown')");
  assert.equal(app.nodes.get('page-title').textContent, 'Главная');
  const direct = viewer('#/obligations');
  assert.equal(direct.nodes.get('page-title').textContent, 'Реестр обязательств');
  assert.equal((direct.nodes.get('payments').innerHTML.match(/data-id=/g) || []).length, 420);
});

test('Register payment form refreshes status and exposes manual payment in DDS', () => {
  const app = viewer('#/obligations');
  app.run("openObligation('O-001')");
  app.run("document.getElementById('payment-form').onsubmit({preventDefault(){}})");
  assert.equal(app.run("D.obligations.find(o=>o.id==='O-001').status"), 'paid');
  assert.equal(app.run('edits.length'), 1);
  assert.ok(app.nodes.get('cashflow-sources').innerHTML.includes('Зарегистрировано вручную'));
  assert.ok(app.nodes.get('payment-detail').innerHTML.includes('Обязательство полностью исполнено'));
  app.run("edits=[];D=Finance.calculate(BASE,edits,D.cutoff);refresh()");
  assert.equal(app.run("D.obligations.find(o=>o.id==='O-001').status"), 'overdue');
});

test('Calendar day opens obligations page and status filters still work', () => {
  const app = viewer();
  app.run("showDay('2026-10-15',true)");
  assert.equal(app.nodes.get('page-title').textContent, 'Реестр обязательств');
  assert.ok(app.nodes.get('payments').innerHTML.includes('data-id="O-001"'));
  assert.equal(app.nodes.get('date-filter').value, '2026-10-15');
  app.run("document.getElementById('clear-filters').onclick();document.getElementById('status-filter').value='paid';renderRegistry()");
  assert.equal((app.nodes.get('payments').innerHTML.match(/data-id=/g) || []).length, app.run("D.obligations.filter(o=>o.status==='paid').length"));
});


test('Currency curves are dashed, converted to the selected scale and finite on the last day', () => {
  const app = viewer();
  assert.ok(!app.nodes.has('reset-demo'));
  assert.equal(app.run('chartSeries().series.length'), 4);
  assert.equal(app.run('chartSeries().series.filter(s=>s.dashed).length'), 3);
  for (const c of ['RUB','CHF','GBP']) {
    app.run(`document.getElementById('currency').value='${c}';render()`);
    assert.equal(app.run('chartSeries().display'), c);
    assert.equal(app.run('chartSeries().series.length'), 3);
    assert.equal(app.run('chartSeries().series.filter(s=>s.dashed).length'), 2);
    assert.ok(app.nodes.get('chart').innerHTML.includes('stroke-dasharray="7 5"'));
  }
  app.run("document.getElementById('currency').value='CHF';render()");
  assert.equal(app.run("chartConversion('110.00','RUB','CHF')"), '1.00');
  assert.equal(app.run("chartConversion('-125.00','GBP','RUB')"), '-15625.00');
  assert.equal(app.run("chartConversion('1.00','GBP','CHF')"), '1.14');
  assert.ok(app.nodes.get('chart-unit').textContent.includes('CHF'));
  app.run("D=Finance.calculate(BASE,[],BASE.end);refresh()");
  assert.ok(!/NaN|Infinity/.test(app.nodes.get('chart').innerHTML));
});


test('Regenerate button waits for C# generation and opens the completed snapshot', async () => {
  let finish;
  const app=viewer('',{regenerateUrl:'/api/regenerate',token:'test-token'},async(url,options)=>{
    assert.equal(url,'/api/regenerate');assert.equal(options.method,'POST');assert.equal(options.headers['X-FinPlan-Token'],'test-token');
    return await new Promise(resolve=>{finish=resolve});
  });
  const task=app.run("document.getElementById('regenerate-data').onclick()");
  assert.equal(app.nodes.get('regenerate-data').disabled,true);
  finish({ok:true,json:async()=>({url:'/datasets/new/calendar_view.html#/home'})});
  await task;
  assert.equal(app.target(),'/datasets/new/calendar_view.html#/home');
});

test('Generation errors preserve the page and restore the button; offline mode explains launch', async () => {
  const app=viewer('',{regenerateUrl:'/api/regenerate',token:'test-token'},async()=>({ok:false,json:async()=>({error:'Test generation failure'})}));
  const initial=app.run('D.seed');
  await app.run("document.getElementById('regenerate-data').onclick()");
  assert.equal(app.nodes.get('regenerate-data').disabled,false);
  assert.equal(app.nodes.get('generation-status').textContent,'Test generation failure');
  assert.equal(app.run('D.seed'),initial);assert.equal(app.target(),null);
  const offline=viewer();
  await offline.run("document.getElementById('regenerate-data').onclick()");
  assert.ok(offline.nodes.get('generation-status').textContent.includes('Rider'));
});
