/* Web-only controls. Original documents stay on disk; conversion runs on the C# worker. */
(() => {
  const runtime = window.finplanRuntime;
  const byId = id => document.getElementById(id);
  const bytes = value => {
    const n = Number(value), unit = n >= 1e9 ? [1e9, 'ГБ'] : n >= 1e6 ? [1e6, 'МБ'] : n >= 1e3 ? [1e3, 'КБ'] : [1, 'байт'];
    return new Intl.NumberFormat('ru-RU', {maximumFractionDigits: 3}).format(n / unit[0]) + ' ' + unit[1];
  };
  const datasetId = location.pathname?.match(/\/datasets\/([^/]+)\//)?.[1] || 'initial';
  const headers = () => ({'X-FinPlan-Token': runtime.token});
  const savedKey = 'finplan-document-jobs';
  let remembered = [], volumeOffset = 0, volumeCount = 0, generationToOpen = null;
  try { remembered = JSON.parse(localStorage.getItem(savedKey) || '[]').filter(x => /^[a-f0-9]{32}$/.test(x.id)).slice(-8); } catch {}
  function remember(id, kind) {
    remembered = [...remembered.filter(x => x.id !== id), {id, kind}].slice(-8);
    try { localStorage.setItem(savedKey, JSON.stringify(remembered)); } catch {}
  }
  function forget(id) {
    remembered = remembered.filter(job => job.id !== id);
    try { localStorage.setItem(savedKey, JSON.stringify(remembered)); } catch {}
  }
  function saveTarget() {
    try { localStorage.setItem('finplan-volume-target', JSON.stringify({value: byId('volume-target').value, unit: byId('volume-unit').value, operation: byId('volume-operation').value})); } catch {}
  }
  function targetBytes(value, unit, minimum = 20000000n) {
    if (!/^\d+(?:[.,]\d{1,3})?$/.test(String(value))) throw Error('Укажите положительное число, до трёх знаков после запятой.');
    const [whole, fraction = ''] = String(value).replace(',', '.').split('.');
    const factor = unit === 'MB' ? 1000000n : 1000000000n;
    const result = BigInt(whole) * factor + BigInt(fraction.padEnd(3, '0')) * factor / 1000n;
    if (result < minimum || result > BigInt(Number.MAX_SAFE_INTEGER)) throw Error(`Минимальный объём ${minimum / 1000000n} МБ. Укажите объём в пределах свободного места.`);
    return Number(result);
  }
  // Exposed only to make unit conversion independently testable without running the server.
  window.finplanDocumentTools = {targetBytes};
  function setSourceKind() {
    const generated = byId('conversion-source-kind').value === 'generated';
    byId('conversion-upload-field').hidden = generated;
    byId('conversion-generated-field').hidden = !generated;
    updateWarning();
  }
  function updateWarning() {
    const name = byId('conversion-source-kind').value === 'generated' ? byId('conversion-generated').value : byId('conversion-upload').files?.[0]?.name || '';
    const source = name.split('.').pop().toLowerCase(); const target = byId('conversion-target').value;
    byId('conversion-warning').textContent = source === target ? 'Выберите формат, отличный от исходного.' : source === 'pdf'
      ? 'Для PDF структура таблиц восстанавливается по расположению текста. OCR может ошибаться в суммах и датах. Изображения и исходная вёрстка не переносятся; проверьте результат.'
      : target === 'pdf' ? 'PDF создаётся через LibreOffice. Переносы страниц и шрифты могут отличаться от Microsoft Office.'
      : 'Сохраняются текст и значения таблиц. Оформление, изображения и объединения ячеек теряются; формулы XLSX заменяются сохранёнными результатами. Все листы попадут в результат с названиями разделов.';
  }
  byId('conversion-generated').innerHTML = BASE.documents.map(d => `<option value="${esc(d.path)}">${esc(d.path.split('/').pop())}</option>`).join('');
  byId('conversion-source-kind').onchange = setSourceKind;
  for (const id of ['conversion-target', 'conversion-generated', 'conversion-upload']) byId(id).onchange = updateWarning;
  setSourceKind();
  try {
    const saved = JSON.parse(localStorage.getItem('finplan-volume-target') || 'null');
    if (saved && /^(MB|GB)$/.test(saved.unit)) {
      byId('volume-target').value = saved.value; byId('volume-unit').value = saved.unit;
      if (['replace', 'add', 'remove'].includes(saved.operation)) byId('volume-operation').value = saved.operation;
    }
  } catch {}
  byId('volume-target').onchange = saveTarget; byId('volume-unit').onchange = saveTarget;
  function updateOperation() {
    const action = byId('volume-operation').value || 'replace';
    byId('volume-label').textContent = action === 'replace' ? 'Целевой объём' : action === 'add' ? 'Добавить объём' : 'Удалить объём';
    byId('volume-submit').textContent = action === 'replace' ? 'Сгенерировать и заменить' : action === 'add' ? 'Догенерировать данные' : 'Уменьшить датасет';
    byId('volume-action-help').textContent = action === 'replace' ? 'Создать другой набор заданного суммарного размера. Нужно свободное место на весь новый объём и ещё 5 ГБ.'
      : action === 'add' ? 'Добавить указанный объём к текущему набору. Существующие записи сохраняются, новые получают уникальные номера. Нужно свободное место на добавляемый объём и ещё 5 ГБ.'
      : 'Удалить примерно указанный объём из конца корпуса целыми связанными частями. Отклонение до 100 МБ. Контрольные документы, справочник контрагентов и календарь сохраняются.';
    saveTarget();
  }
  byId('volume-operation').onchange = updateOperation; updateOperation();
  function showChange(total, change) {
    const node = byId('dataset-change');
    node.textContent = change ? `${change.operation === 'add' ? 'Добавлено' : 'Удалено'} ${bytes(Math.abs(total - change.before_bytes))} при запросе ${bytes(change.requested_bytes)}. Было ${bytes(change.before_bytes)}, стало ${bytes(total)}.` : '';
  }
  showChange(BASE.volume?.total_bytes, BASE.volume?.last_change);
  async function loadDatasetSize() {
    const value = byId('dataset-size'), details = byId('dataset-size-details'), button = byId('dataset-size-refresh');
    if (!runtime) {
      button.disabled = true;
      value.textContent = BASE.volume?.total_bytes ? bytes(BASE.volume.total_bytes) : 'Недоступен';
      details.textContent = BASE.volume?.total_bytes
        ? 'Размер сохранён при генерации. Для измерения файлов на диске запустите веб-приложение.'
        : 'Для измерения размера файлов запустите веб-приложение.';
      return;
    }
    button.disabled = true;
    try {
      const response = await fetch(`/api/datasets/${datasetId}/size`, {cache: 'no-store'});
      if (!response.ok) throw Error('Не удалось измерить размер открытого набора. Повторите попытку.');
      const data = await response.json();
      if (!Number.isSafeInteger(data.total_bytes) || !Number.isSafeInteger(data.files)) throw Error('Не удалось получить размер файлов. Повторите попытку.');
      value.textContent = bytes(data.total_bytes);
      details.textContent = `${Number(data.files).toLocaleString('ru-RU')} файлов на диске: документы, таблицы и файлы календаря открытого набора.`;
      if (Number.isSafeInteger(data.removable_bytes)) details.textContent += ` Из корпуса можно удалить до ${bytes(Math.floor(data.removable_bytes / 1000000) * 1000000)}.`;
      if (data.cleanup_warning) details.textContent += ' ' + data.cleanup_warning;
      showChange(data.total_bytes, data.last_change);
    } catch (error) { value.textContent = 'Недоступен'; details.textContent = error.message; }
    finally { button.disabled = false; }
  }
  byId('dataset-size-refresh').onclick = loadDatasetSize;
  loadDatasetSize();
  function offline(status) { byId(status).textContent = 'Для обработки файлов запустите приложение через ▶ в Rider или как локальное веб-приложение .NET.'; }
  function jobUi(job) {
    const generation = job.kind === 'generation';
    const status = byId(generation ? 'volume-status' : 'conversion-status');
    const active = job.state === 'running' || job.state === 'queued';
    if (generation && active) generationToOpen = job.id;
    byId(generation ? 'volume-submit' : 'conversion-submit').disabled = active;
    if (generation) byId('volume-operation').disabled = active;
    const cancel = byId(generation ? 'volume-cancel' : 'conversion-cancel'); cancel.hidden = !active;
    cancel.onclick = async () => {
      const response = await fetch('/api/jobs/' + job.id, {method: 'DELETE', headers: headers()});
      if (response.ok) { cancel.disabled = true; status.textContent = 'Отменяем задание и убираем незавершённый набор…'; }
    };
    cancel.disabled = false;
    status.textContent = job.error || job.message;
    if (generation && job.target_bytes) {
      byId('volume-progress').hidden = false; byId('volume-progress').value = Math.min(100, job.bytes / job.target_bytes * 100);
      if (active) status.textContent += ' · ' + bytes(job.bytes) + ' из ' + bytes(job.target_bytes) + ' · обязательств: ' + Number(job.obligations).toLocaleString('ru-RU');
    }
    const result = byId(generation ? 'volume-result' : 'conversion-result');
    if (job.state === 'succeeded' && job.result) {
      const r = job.result;
      if (generation) {
        byId('volume-progress').value = 100;
        forget(job.id); result.innerHTML = '';
        if (generationToOpen === job.id) {
          status.textContent = 'Новый набор готов. Открываем…';
          location.assign(r.url);
        } else status.textContent = 'Размер текущего набора указан выше.';
      } else {
        result.innerHTML = `<p class="positive">Готово. <a href="${esc(r.url)}" download>${esc('Скачать ' + r.file_name)}</a> · ${esc(bytes(r.bytes))}</p>` +
          (r.warnings?.length ? `<div class="notice"><strong>Что нужно проверить</strong><ul>${r.warnings.map(w => `<li>${esc(w)}</li>`).join('')}</ul></div>` : '');
      }
    }
    return active;
  }
  async function poll(id, kind) {
    try {
      const response = await fetch('/api/jobs/' + id, {cache: 'no-store'});
      if (!response.ok) throw Error('Задание не найдено. Повторите обработку.');
      const job = await response.json();
      if (jobUi(job)) setTimeout(() => poll(id, kind), 1000);
    } catch (error) {
      byId(kind === 'generation' ? 'volume-status' : 'conversion-status').textContent = error.message + ' Статус проверится повторно после восстановления соединения.';
      setTimeout(() => poll(id, kind), 5000);
    }
  }
  byId('conversion-form').onsubmit = async event => {
    event.preventDefault(); if (!runtime) { offline('conversion-status'); return; }
    const button = byId('conversion-submit'); button.disabled = true; byId('conversion-result').innerHTML = '';
    byId('conversion-status').textContent = 'Загружаем и проверяем документ…';
    try {
      const form = new FormData();
      if (byId('conversion-source-kind').value === 'upload') {
        const file = byId('conversion-upload').files[0];
        if (!file) throw Error('Выберите исходный файл.');
        if (file.size > 100000000) throw Error('Размер входного файла ограничен 100 МБ.');
        form.append('file', file);
      } else { form.append('datasetId', datasetId); form.append('source', byId('conversion-generated').value); }
      form.append('target', byId('conversion-target').value); form.append('ocr', byId('conversion-ocr').value); form.append('languages', byId('conversion-languages').value);
      const response = await fetch('/api/conversions', {method: 'POST', headers: headers(), body: form});
      const job = await response.json(); if (!response.ok) throw Error(job.error || 'Не удалось принять документ.');
      remember(job.id, 'conversion'); jobUi(job); poll(job.id, 'conversion');
    } catch (error) { byId('conversion-status').textContent = error.message; button.disabled = false; }
  };
  byId('volume-form').onsubmit = async event => {
    event.preventDefault(); if (!runtime) { offline('volume-status'); return; }
    const button = byId('volume-submit'); button.disabled = true; byId('volume-result').innerHTML = '';
    try {
      const operation = byId('volume-operation').value || 'replace';
      const target = targetBytes(byId('volume-target').value, byId('volume-unit').value, operation === 'replace' ? 20000000n : 1000000n); saveTarget();
      const response = await fetch(operation === 'replace' ? '/api/jobs/generate' : '/api/jobs/resize', {method: 'POST', headers: {...headers(), 'Content-Type': 'application/json'},
        body: JSON.stringify(operation === 'replace' ? {targetBytes: target} : {datasetId, operation, bytes: target})});
      const job = await response.json(); if (!response.ok) throw Error(job.error || 'Не удалось начать генерацию.');
      remember(job.id, 'generation'); jobUi(job); poll(job.id, 'generation');
    } catch (error) { byId('volume-status').textContent = error.message; button.disabled = false; }
  };
  async function loadVolume() {
    if (!BASE.volume || !runtime) return;
    const response = await fetch(`/api/datasets/${datasetId}/volume?offset=${volumeOffset}&limit=25`);
    if (!response.ok) { byId('volume-summary').textContent = 'Не удалось прочитать манифест корпуса.'; return; }
    const data = await response.json(); volumeCount = data.total;
    byId('volume-summary').textContent = `Цель: ${bytes(data.summary.target_bytes)} · фактически: ${bytes(data.summary.total_bytes)} · файлов: ${data.summary.files} · связанных обязательств: ${Number(data.summary.obligations).toLocaleString('ru-RU')}. ${data.scope}`;
    byId('volume-manifest').href = 'volume-manifest.json';
    byId('volume-files').innerHTML = data.files.map(f => `<tr><td><a href="${esc(f.path)}" download>${esc(f.path)}</a></td><td>${esc(f.kind)}</td><td>${Number(f.rows).toLocaleString('ru-RU')}</td><td>${esc(bytes(f.bytes))}</td><td><button type="button" data-convert-source="${esc(f.path)}">Преобразовать</button></td></tr>`).join('');
    byId('volume-page').textContent = `${volumeOffset + 1}–${Math.min(volumeOffset + 25, volumeCount)} из ${volumeCount}`;
    byId('volume-prev').disabled = volumeOffset === 0; byId('volume-next').disabled = volumeOffset + 25 >= volumeCount;
    document.querySelectorAll('[data-convert-source]').forEach(button => button.onclick = () => {
      const path = button.dataset.convertSource;
      if (![...byId('conversion-generated').options].some(o => o.value === path)) byId('conversion-generated').add(new Option(path, path));
      byId('conversion-source-kind').value = 'generated'; byId('conversion-generated').value = path; setSourceKind(); navigate('converter');
    });
  }
  byId('volume-prev').onclick = () => { volumeOffset = Math.max(0, volumeOffset - 25); loadVolume().catch(e => byId('volume-summary').textContent = e.message); };
  byId('volume-next').onclick = () => { volumeOffset += 25; loadVolume().catch(e => byId('volume-summary').textContent = e.message); };
  if (runtime) {
    fetch('/api/document-tools').then(r => r.json()).then(info => {
      byId('conversion-capabilities').textContent = `PDF через LibreOffice: ${info.libreOffice ? 'доступен' : 'нужно установить LibreOffice'}. OCR: ${info.ocr ? 'доступен' : 'нужны Tesseract и Poppler'}. Языки: ${info.languages.join(', ') || 'не установлены'}.`;
    }).catch(() => byId('conversion-capabilities').textContent = 'Не удалось проверить доступность движков.');
    // Restore at most the last job of each kind after a reload.
    for (const kind of ['conversion', 'generation']) { const job = remembered.filter(x => x.kind === kind).at(-1); if (job) poll(job.id, kind); }
    loadVolume().catch(e => byId('volume-summary').textContent = e.message);
  } else byId('conversion-capabilities').textContent = 'Загрузка, конвертация, OCR и генерация доступны при запуске локального веб-приложения.';
})();
