const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function app(runtime = null, fetch = async () => { throw Error('Unexpected fetch'); }, extraBase = {}, savedJobs = []) {
  const nodes = new Map();
  function node(id) {
    if (!nodes.has(id)) nodes.set(id, {id, value: '', files: [], innerHTML: '', textContent: '', hidden: false, disabled: false});
    return nodes.get(id);
  }
  node('conversion-source-kind').value = 'upload'; node('conversion-target').value = 'pdf';
  node('conversion-ocr').value = 'auto'; node('conversion-languages').value = 'rus+eng';
  node('volume-target').value = '50'; node('volume-unit').value = 'GB';
  node('volume-corpus').hidden = true;
  const store = new Map(), timers = []; let target = null;
  store.set('finplan-document-jobs', JSON.stringify(savedJobs));
  const context = vm.createContext({document: {getElementById: node, querySelectorAll: () => []}, window: {finplanRuntime: runtime},
    BASE: {documents: [{path: 'documents/user.docx'}], ...extraBase}, esc: s => String(s).replace(/</g, '&lt;'),
    localStorage: {getItem: key => store.get(key) ?? null, setItem: (key, value) => store.set(key, value)},
    location: {pathname: '/datasets/initial/calendar_view.html', assign: url => {target = url;}}, fetch, console,
    setTimeout: fn => timers.push(fn), Intl, FormData: class {append() {}}});
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../demo/document-tools.js'), 'utf8'), context);
  return {nodes, timers, target: () => target, run: code => vm.runInContext(code, context)};
}
test('Volume units are decimal and respect the requested target without floating point drift', () => {
  const a = app();
  assert.equal(a.run("window.finplanDocumentTools.targetBytes('50','GB')"), 50_000_000_000);
  assert.equal(a.run("window.finplanDocumentTools.targetBytes('0,025','GB')"), 25_000_000);
  assert.equal(a.run("window.finplanDocumentTools.targetBytes('20.001','MB')"), 20_001_000);
  for (const value of ['-50', 'NaN', '0', '0.001', '1.00001', '1e9', '999999999999999999999'])
    assert.throws(() => a.run(`window.finplanDocumentTools.targetBytes('${value}','GB')`));
});
test('Offline forms explain how to launch the web app and never claim a conversion succeeded', async () => {
  const a = app();
  await a.nodes.get('conversion-form').onsubmit({preventDefault() {}});
  assert.match(a.nodes.get('conversion-status').textContent, /Rider/);
  await a.nodes.get('volume-form').onsubmit({preventDefault() {}});
  assert.match(a.nodes.get('volume-status').textContent, /Rider/);
  assert.ok(!a.nodes.get('conversion-result')?.innerHTML);
});
test('Generated source and target changes show loss warnings before submission', () => {
  const a = app();
  a.nodes.get('conversion-source-kind').value = 'generated'; a.nodes.get('conversion-generated').value = 'documents/scanned.pdf';
  a.nodes.get('conversion-target').value = 'xlsx'; a.nodes.get('conversion-source-kind').onchange();
  assert.equal(a.nodes.get('conversion-upload-field').hidden, true);
  assert.match(a.nodes.get('conversion-warning').textContent, /OCR может ошибаться/);
  a.nodes.get('conversion-target').value = 'pdf'; a.nodes.get('conversion-target').onchange();
  assert.match(a.nodes.get('conversion-warning').textContent, /отличный/);
});
test('Web generation sends 50 GB to the background job API and displays progress', async () => {
  const requests = [];
  const a = app({token: 'test-token'}, async (url, options) => {
    requests.push({url, options});
    if (url === '/api/document-tools') return {json: async () => ({libreOffice: true, ocr: true, languages: ['rus', 'eng']})};
    if (url === '/api/jobs/generate') {
      assert.equal(JSON.parse(options.body).targetBytes, 50_000_000_000);
      assert.equal(options.headers['X-FinPlan-Token'], 'test-token');
      return {ok: true, json: async () => ({id: 'a'.repeat(32), kind: 'generation', state: 'running', target_bytes: 50_000_000_000, bytes: 25_000_000_000, obligations: 100, message: 'Запись реестров'})};
    }
    return {ok: true, json: async () => ({id: 'a'.repeat(32), kind: 'generation', state: 'running', target_bytes: 50_000_000_000, bytes: 25_000_000_000, obligations: 100, message: 'Запись реестров'})};
  });
  await a.nodes.get('volume-form').onsubmit({preventDefault() {}});
  assert.equal(a.nodes.get('volume-submit').disabled, true);
  assert.equal(a.nodes.get('volume-progress').value, 50);
  assert.match(a.nodes.get('volume-status').textContent, /25/);
});

test('Loading the corpus asynchronously does not reveal its section on another page', async () => {
  const a = app({token: 'test-token'}, async url => url === '/api/document-tools'
    ? {json: async () => ({libreOffice: true, ocr: true, languages: ['rus', 'eng']})}
    : {ok: true, json: async () => ({summary: {target_bytes: 50e9, total_bytes: 50e9, files: 1, obligations: 20}, scope: 'Корпус для импорта', total: 1, files: []})}, {volume: {target_bytes: 50e9}});
  await new Promise(resolve => setImmediate(resolve));
  assert.match(a.nodes.get('volume-summary').textContent, /Корпус для импорта/);
  assert.equal(a.nodes.get('volume-corpus').hidden, true);
});

test('Dataset size comes from the opened dataset files and can be refreshed independently of the target', async () => {
  let current = 50_002_841_615, failed = false, requests = 0;
  const a = app({token: 'test-token'}, async url => {
    if (url === '/api/document-tools') return {json: async () => ({libreOffice: true, ocr: true, languages: ['rus', 'eng']})};
    assert.equal(url, '/api/datasets/initial/size'); requests++;
    return {ok: !failed, json: async () => ({total_bytes: current, files: 13282})};
  });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(a.nodes.get('dataset-size').textContent, '50,003 ГБ');
  assert.match(a.nodes.get('dataset-size-details').textContent, /13\s282 файлов/);
  a.nodes.get('volume-target').value = '200';
  assert.equal(a.nodes.get('dataset-size').textContent, '50,003 ГБ');
  current = 10_123_456;
  await a.nodes.get('dataset-size-refresh').onclick();
  assert.equal(a.nodes.get('dataset-size').textContent, '10,123 МБ'); assert.equal(requests, 2);
  failed = true;
  await a.nodes.get('dataset-size-refresh').onclick();
  assert.equal(a.nodes.get('dataset-size').textContent, 'Недоступен');
  assert.equal(a.nodes.get('dataset-size-refresh').disabled, false);
});

test('Offline size is explicitly marked as saved metadata', () => {
  const a = app(null, undefined, {volume: {total_bytes: 50_002_841_615}});
  assert.equal(a.nodes.get('dataset-size').textContent, '50,003 ГБ');
  assert.match(a.nodes.get('dataset-size-details').textContent, /сохранён при генерации/);
  assert.equal(a.nodes.get('dataset-size-refresh').disabled, true);
});

test('Successful generation opens the replacement and removes the old completion from browser history', async () => {
  const id = 'b'.repeat(32); let finish;
  const a = app({token: 'test-token'}, async url => {
    if (url === '/api/document-tools') return {json: async () => ({libreOffice: true, ocr: true, languages: ['eng']})};
    if (url.endsWith('/size')) return {ok: true, json: async () => ({total_bytes: 1000, files: 1})};
    if (url === '/api/jobs/generate') return {ok: true, json: async () => ({id, kind: 'generation', state: 'queued', target_bytes: 50e9, bytes: 0})};
    return await new Promise(resolve => {finish = resolve;});
  });
  await a.nodes.get('volume-form').onsubmit({preventDefault() {}});
  finish({ok: true, json: async () => ({id, kind: 'generation', state: 'succeeded', result: {url: '/datasets/replacement/calendar_view.html#/generation', bytes: 50e9}})});
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(a.target(), '/datasets/replacement/calendar_view.html#/generation');
});

test('Restoring an old successful generation never redirects to a retired dataset', async () => {
  const id = 'c'.repeat(32);
  const a = app({token: 'test-token'}, async url => {
    if (url === '/api/document-tools') return {json: async () => ({libreOffice: true, ocr: true, languages: ['eng']})};
    if (url.endsWith('/size')) return {ok: true, json: async () => ({total_bytes: 1000, files: 1})};
    return {ok: true, json: async () => ({id, kind: 'generation', state: 'succeeded', result: {url: '/datasets/retired/calendar_view.html', bytes: 50e9}})};
  }, {}, [{id, kind: 'generation'}]);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(a.target(), null);
  assert.equal(a.nodes.get('volume-result').innerHTML, '');
});
