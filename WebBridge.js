// Runs in the app-owned claude.ai document. Cookies are never read or exported.
globalThis.localClaudeBridgeRun = async function (input, signal) {
  if (location.origin !== 'https://claude.ai') throw new Error('wrong_origin');
  const failure = async response => {
    let kind = 'unknown', reason = 'unspecified';
    if (response.headers.get('cf-mitigated') === 'challenge') kind = 'browser_challenge';
    else if ((response.headers.get('content-type') || '').includes('text/html')) kind = 'html_response';
    else if ((response.headers.get('content-type') || '').includes('application/json') && response.body) {
      const reader = response.body.getReader(); const decoder = new TextDecoder(); let body = '', size = 0;
      try {
        while (size < 32768) { const next = await reader.read(); if (next.done) break; size += next.value.length; body += decoder.decode(next.value, { stream: true }); }
        if (size <= 32768) {
          const data = JSON.parse(body + decoder.decode()), error = data.error || data;
          if (typeof error.type === 'string' && /^[a-z_]{1,64}$/.test(error.type)) kind = error.type;
          const message = typeof error.message === 'string' ? error.message.toLowerCase() : '';
          if (/subscription|upgrade|paid plan|free plan/.test(message)) reason = 'plan_restriction';
          else if (/model/.test(message)) reason = 'model_restriction';
          else if (/organization|workspace/.test(message)) reason = 'workspace_restriction';
          else if (/csrf/.test(message)) reason = 'csrf_validation';
          else if (/verification|challenge/.test(message)) reason = 'verification';
          else if (/permission|access|not allowed|not authorized/.test(message)) reason = 'access_restriction';
        }
      } catch {} finally { await reader.cancel().catch(() => {}); }
    }
    // Only classifications leave the page; raw server errors may contain private data.
    return new Error('http_' + response.status + '|' + kind + '|' + reason);
  };
  if (input.operation === 'session') {
    const response = await fetch('/api/organizations', { credentials: 'same-origin', redirect: 'error', signal });
    if (!response.ok) throw await failure(response);
    if (!(response.headers.get('content-type') || '').includes('application/json')) throw new Error('browser_verification');
    const data = await response.json();
    if (!Array.isArray(data)) throw new Error('unexpected_organizations');
    return data.filter(o => typeof o.uuid === 'string').map((o, i) => ({ id: o.uuid, name: typeof o.name === 'string' ? o.name : 'Workspace ' + (i + 1) }));
  }
  if (input.operation === 'models') {
    // NavigationCompleted can precede the site's client-side model picker.
    let trigger;
    const readyDeadline = Date.now() + 6000;
    do {
      trigger = [...document.querySelectorAll('button')].find(button => /^Model:\s*(Sonnet|Haiku|Opus|Fable)/i.test(button.getAttribute('aria-label') || ''));
      if (trigger || signal?.aborted) break;
      await new Promise(resolve => setTimeout(resolve, 150));
    } while (Date.now() < readyDeadline);
    if (signal?.aborted) throw new Error('request_aborted');
    if (!trigger) throw new Error('model_picker_missing');
    const opened = trigger.getAttribute('aria-expanded') !== 'true';
    if (opened) trigger.click();
    let moreTrigger, openedMore = false;
    try {
      const deadline = Date.now() + 3000;
      const found = new Map(); let lastChange = Date.now();
      do {
        const menus = [...document.querySelectorAll('[role="menu"]')].filter(menu => menu.getBoundingClientRect().width > 0);
        for (const menu of menus) {
          const models = globalThis.LocalClaudeExtractModels(menu);
          for (const model of models) if (!found.has(model.id)) { found.set(model.id, model); lastChange = Date.now(); }
          // Current Claude UI nests alternatives under More models. Opening a
          // submenu does not choose a model or change the user's preference.
          const more = [...menu.querySelectorAll('[role="menuitem"]')].find(row => /^More models\b/i.test(row.textContent?.trim() || ''));
          if (more && !moreTrigger) {
            moreTrigger = more;
            if (more.getAttribute('aria-expanded') !== 'true') { more.click(); openedMore = true; lastChange = Date.now(); }
          }
        }
        if (found.size && Date.now() - lastChange >= 650) return [...found.values()];
        await new Promise(resolve => setTimeout(resolve, 100));
      } while (Date.now() < deadline && !signal?.aborted);
      if (signal?.aborted) throw new Error('request_aborted');
      if (found.size) return [...found.values()];
      throw new Error('no_available_models');
    } finally {
      if (opened && trigger.isConnected && trigger.getAttribute('aria-expanded') === 'true') trigger.click();
      else if (openedMore && moreTrigger?.isConnected && moreTrigger.getAttribute('aria-expanded') === 'true') moreTrigger.click();
    }
  }
  if (input.operation !== 'complete') throw new Error('unsupported_operation');
  const conversation = crypto.randomUUID();
  const payload = {
    prompt: input.prompt, model: input.model,
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC', locale: navigator.language || 'en-US',
    attachments: [], files: [], sync_sources: [], tools: [], personalized_styles: [],
    rendering_mode: 'messages', thinking_mode: input.thinking_mode || 'off', effort: input.effort || 'low',
    turn_message_uuids: { human_message_uuid: crypto.randomUUID(), assistant_message_uuid: crypto.randomUUID() },
    create_conversation_params: { name: 'Local Bridge request', model: input.model, include_conversation_preferences: true,
      paprika_mode: null, compass_mode: null, is_temporary: false, enabled_imagine: false, tool_search_mode: 'auto' }
  };
  const path = '/api/organizations/' + encodeURIComponent(input.organization) + '/chat_conversations/' + conversation + '/completion';
  const response = await fetch(path, { method: 'POST', credentials: 'same-origin', redirect: 'error', signal,
    headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' }, body: JSON.stringify(payload) });
  if (!response.ok) throw await failure(response);
  if (!(response.headers.get('content-type') || '').includes('text/event-stream')) throw new Error('browser_verification');
  if (!response.body) throw new Error('empty_response');
  const parser = new LocalClaudeSseParser();
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let size = 0;
  try {
    while (!parser.stopped) {
      const { value, done } = await reader.read();
      if (done) { parser.feed(decoder.decode(), true); break; }
      size += value.length;
      if (size > 4 * 1024 * 1024) throw new Error('response_too_large');
      parser.feed(decoder.decode(value, { stream: true }));
    }
    if (!parser.stopped) throw new Error('incomplete_stream');
    if (!parser.text) throw new Error('no_text_output');
    return { text: parser.text, model: parser.model || input.model };
  } finally { await reader.cancel().catch(() => {}); }
};

globalThis.LocalClaudeSseParser = class {
  constructor() { this.buffer = ''; this.text = ''; this.stopped = false; this.model = null; }
  feed(chunk, eof = false) {
    this.buffer += chunk;
    if (this.buffer.length > 1024 * 1024) throw new Error('event_too_large');
    let match;
    while (!this.stopped && (match = /\r?\n\r?\n/.exec(this.buffer))) {
      const frame = this.buffer.slice(0, match.index);
      this.buffer = this.buffer.slice(match.index + match[0].length);
      const lines = frame.split(/\r?\n/);
      const data = lines.filter(l => l.startsWith('data:')).map(l => l.slice(5).replace(/^ /, '')).join('\n');
      if (!data) continue;
      if (data === '[DONE]') { this.stopped = true; continue; }
      let value;
      try { value = JSON.parse(data); } catch { throw new Error('invalid_stream_json'); }
      const type = value.type || lines.find(l => l.startsWith('event:'))?.slice(6).trim();
      if (type === 'error' || value.error) throw new Error('upstream_stream_error');
      if (type === 'message_start' && typeof value.message?.model === 'string') this.model = value.message.model;
      if (type === 'content_block_delta' && value.delta?.type === 'text_delta' && typeof value.delta.text === 'string') this.text += value.delta.text;
      if (typeof value.completion === 'string') this.text += value.completion;
      if (type === 'message_stop' || (typeof value.completion === 'string' && value.stop_reason)) this.stopped = true;
    }
    if (eof && !this.stopped && this.buffer.trim()) throw new Error('incomplete_stream');
  }
};
globalThis.LocalClaudeExtractModels = function (menu) {
  const models = new Map();
  for (const row of menu.querySelectorAll('[role="menuitemradio"], [role="menuitem"], [role="option"]')) {
    let label = (row.textContent || '').replace(/\s+/g, ' ').trim();
    let restricted = row.disabled || row.getAttribute('aria-disabled') === 'true' || row.hasAttribute('data-disabled');
    // Upgrade badges can sit beside a menu item rather than inside it. Include
    // its owning wrapper, stopping before a shared group containing other models.
    for (let parent = row.parentElement, depth = 0; parent && parent !== menu && depth < 4; parent = parent.parentElement, depth++) {
      const peers = [...parent.querySelectorAll('[role="menuitemradio"], [role="menuitem"], [role="option"]')].filter(item => /\b(Sonnet|Haiku|Opus|Fable)\s+\d/i.test(item.textContent || ''));
      if (peers.length > 1) break;
      label += ' ' + (parent.textContent || '');
      restricted ||= parent.getAttribute('aria-disabled') === 'true' || parent.hasAttribute('data-disabled');
    }
    for (const attribute of ['aria-label', 'aria-description']) label += ' ' + (row.getAttribute(attribute) || '');
    // DOM text can join badge and description words ("5.5ProFor...Upgrade").
    if (restricted || /upgrade|paid plan/i.test(label) || /\b(pro|max)\b/i.test(label)) continue;
    const match = /\b(Sonnet|Haiku|Opus|Fable)\s+(\d+(?:\.\d+)*)/i.exec(label);
    if (!match) continue;
    const declared = row.getAttribute('data-model-id');
    const id = declared && /^claude-[a-z0-9-]+$/.test(declared) ? declared : 'claude-' + match[1].toLowerCase() + '-' + match[2].replace(/\./g, '-');
    models.set(id, { id, name: match[1][0].toUpperCase() + match[1].slice(1).toLowerCase() + ' ' + match[2] });
  }
  return [...models.values()];
};
if (typeof module !== 'undefined') module.exports = { LocalClaudeSseParser: globalThis.LocalClaudeSseParser, LocalClaudeExtractModels: globalThis.LocalClaudeExtractModels };
