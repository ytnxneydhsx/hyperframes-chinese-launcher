(() => {
  'use strict';
  const VERSION = '1.1.0';
  if (window.__HFZH__?.version === VERSION) {
    window.__HFZH__.updateDictionary(window.__HFZH_TRANSLATIONS__ || {});
    return window.__HFZH__.enable();
  }
  window.__HFZH__?.disable?.();

  const excluded = [
    'script', 'style', 'noscript', 'pre', 'code', 'textarea', 'svg', 'iframe',
    '[contenteditable]:not([contenteditable="false"])', '[role="textbox"]',
    '.cm-editor', '.cm-content', '.monaco-editor', '.xterm', '.prose',
    '[data-hfzh-ignore]', '[data-message-role]', '[data-message-id]', '[role="log"]',
    '.session-title', '.session-project-name', '[data-project-name]',
    '[aria-label^="Open composition "] .truncate',
    '[aria-label^="Caption word "]',
    '.timeline-clip',
    'button[draggable] > span.truncate', 'button[style*="padding-left"] > span.truncate',
    'span.truncate[title]', 'p.text-neutral-300 > span.font-medium.text-neutral-100',
    '[data-sent-words]', '[data-sent-pills]', '[data-thinking-body]',
    '[data-run-prose="text"]', '[data-answered-questions]', '.pe-req', '.pe-where',
    '.chat-message', '.message-content'
  ].join(',');
  // HyperFrames itself locates controls by English aria-label; keep these identifiers intact.
  const attributes = ['title', 'placeholder'];
  const textRecords = new Map();
  const attributeRecords = new Map();
  let dictionary = new Map();
  let caseDictionary = new Map();
  const translationCache = new Map();
  const templateRules = [];
  let enabled = false;
  let observer = null;
  let scheduled = false;
  let frame = null;
  let replacements = 0;
  let lastPassAt = null;
  let namedRoots = new WeakSet();

  const normalize = text => String(text).replace(/\s+/g, ' ').trim();
  const protectedElement = element => {
    // CodeMirror renders its empty-input hint as a read-only span inside the editor.
    // This is application UI; editable lines and all typed input remain excluded.
    if (element?.closest('.cm-placeholder[contenteditable="false"][aria-hidden="true"]')) return false;
    if (!element || element.closest(excluded)) return true;
    for (let ancestor = element; ancestor && ancestor !== document.body; ancestor = ancestor.parentElement) {
      if (namedRoots.has(ancestor)) return true;
    }
    return false;
  };
  const visible = element => {
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0 && rect.bottom >= 0 && rect.top <= innerHeight &&
      getComputedStyle(element).visibility !== 'hidden';
  };
  function slots(template) {
    const expression = /\$\{([^}]+)\}/g;
    return [...template.matchAll(expression)];
  }
  const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  for (const template of window.__HFZH_PATTERNS__ || []) {
    if (typeof template.english !== 'string' || typeof template.chinese !== 'string') continue;
    const english = normalize(template.english);
    const chinese = normalize(template.chinese);
    const matches = slots(english);
    const keys = matches.map(match => match[1]);
    if (!matches.length || slots(chinese).some(match => !keys.includes(match[1]))) continue;
    const literalLength = english.replace(/\$\{[^}]+\}/g, '').replace(/[^A-Za-z]/g, '').length;
    if (literalLength < 4) continue;
    let position = 0;
    let expression = '^';
    for (const match of matches) {
      expression += escapeRegex(english.slice(position, match.index)) + '(.+?)';
      position = match.index + match[0].length;
    }
    expression += escapeRegex(english.slice(position)) + '$';
    try {
      templateRules.push({ regex: new RegExp(expression), chinese, keys,
        attributesOnly: !!template.attributesOnly || literalLength < 8 });
    } catch { /* Unsupported source interpolation stays in its original form. */ }
  }
  function updateDictionary(input) {
    translationCache.clear();
    dictionary = new Map();
    caseDictionary = new Map();
    const ambiguous = new Set();
    for (const [english, chinese] of Object.entries(input || {})) {
      if (typeof chinese !== 'string' || !chinese.trim()) continue;
      const key = normalize(english);
      if (!key || key === chinese || /^[\d\W]+$/.test(key)) continue;
      dictionary.set(key, chinese);
      const lower = key.toLowerCase();
      if (caseDictionary.has(lower) && caseDictionary.get(lower) !== chinese) ambiguous.add(lower);
      else caseDictionary.set(lower, chinese);
    }
    for (const key of ambiguous) caseDictionary.delete(key);
    if (enabled) schedule();
    return status();
  }
  function translateUncached(input, isAttribute = false) {
    const text = normalize(input);
    if (!text || !/[A-Za-z]/.test(text)) return null;
    const exact = dictionary.get(text) ?? caseDictionary.get(text.toLowerCase());
    if (exact) return exact;
    const ellipsis = text.match(/^(.*?)(?:…|\.\.\.)$/);
    if (ellipsis) {
      const base = dictionary.get(ellipsis[1]) ?? caseDictionary.get(ellipsis[1].toLowerCase());
      if (base) return `${base}…`;
    }
    const shortcut = text.match(/^(.+?)\s*(\((?:Enter|Escape|Esc|Tab|Space|Cmd|Ctrl|Shift|Alt|Option|⌘)[^)]*\))$/);
    if (shortcut) {
      const base = dictionary.get(shortcut[1]) ?? caseDictionary.get(shortcut[1].toLowerCase());
      if (base) return `${base} ${shortcut[2]}`;
    }
    const patterns = [
      [/^Archived \((\d+)\)$/i, m => `已归档 (${m[1]})`],
      [/^All projects \((\d+)\)$/i, m => `所有项目 (${m[1]})`],
      [/^(\d+) projects?$/i, m => `${m[1]} 个项目`],
      [/^(\d+) files?$/i, m => `${m[1]} 个文件`],
      [/^(\d+) clips?$/i, m => `${m[1]} 个片段`],
      [/^(\d+) errors?$/i, m => `${m[1]} 个错误`],
      [/^(\d+) warnings?$/i, m => `${m[1]} 个警告`],
      [/^(\d+) selected$/i, m => `已选择 ${m[1]} 项`],
      [/^Exporting(?:\.\.\.|…)?\s*(\d+)%$/i, m => `正在导出 ${m[1]}%`],
      [/^Loading(?:\.\.\.|…)?\s*(\d+)%$/i, m => `正在加载 ${m[1]}%`]
    ];
    for (const [pattern, replacement] of patterns) {
      const match = text.match(pattern);
      if (match) return replacement(match);
    }
    if (isAttribute) {
      const named = text.match(/^(Actions for|Pin|Unpin|Archive|Unarchive|Rename|Delete|Open|Add|Use):?\s+(.+)$/);
      if (named) {
        const prefix = dictionary.get(named[1]) ?? caseDictionary.get(named[1].toLowerCase());
        if (prefix) return `${prefix}：${named[2]}`;
      }
    }
    for (const rule of templateRules) {
      if (rule.attributesOnly && !isAttribute) continue;
      const match = text.match(rule.regex);
      if (!match) continue;
      return rule.chinese.replace(/\$\{([^}]+)\}/g, (_, key) => match[rule.keys.indexOf(key) + 1]);
    }
    return null;
  }
  function translate(input, isAttribute = false) {
    const key = `${isAttribute ? 'a' : 't'}:${input}`;
    if (translationCache.has(key)) return translationCache.get(key);
    const translated = translateUncached(input, isAttribute);
    if (translationCache.size >= 20000) translationCache.clear();
    translationCache.set(key, translated);
    return translated;
  }
  function processText(node) {
    if (!node.isConnected || protectedElement(node.parentElement)) return;
    const prior = textRecords.get(node);
    if (prior && node.nodeValue === prior.translated) return;
    const original = node.nodeValue;
    const result = translate(original);
    if (!result) return;
    const leading = original.match(/^\s*/)[0];
    const trailing = original.match(/\s*$/)[0];
    const translated = `${leading}${result}${trailing}`;
    if (translated === original) return;
    textRecords.set(node, { original, translated });
    node.nodeValue = translated;
    replacements++;
  }
  function processAttributes(element) {
    if (element.matches('button') && element.querySelector('.session-title')) return;
    // Input values stay untouched; their own UI placeholders and labels can be translated.
    const ownInput = element.matches('input,textarea,[role="textbox"]');
    if (protectedElement(element) && !(ownInput && !protectedElement(element.parentElement))) return;
    for (const name of attributes) {
      if (!element.hasAttribute(name)) continue;
      const original = element.getAttribute(name);
      const records = attributeRecords.get(element);
      if (records?.get(name)?.translated === original) continue;
      const translated = translate(original, true);
      if (!translated || translated === original) continue;
      const next = records ?? new Map();
      next.set(name, { original, translated });
      attributeRecords.set(element, next);
      element.setAttribute(name, translated);
      replacements++;
    }
  }
  function pass() {
    if (!enabled || !document.body) return;
    namedRoots = new WeakSet();
    for (const icon of document.querySelectorAll('[data-flat-header-icon]')) {
      if (icon.nextElementSibling) namedRoots.add(icon.nextElementSibling);
    }
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
      acceptNode: node => protectedElement(node.parentElement) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT
    });
    let node;
    while ((node = walker.nextNode())) processText(node);
    for (const element of document.body.querySelectorAll('[aria-label],[title],[placeholder]')) processAttributes(element);
    for (const node of textRecords.keys()) if (!node.isConnected) textRecords.delete(node);
    for (const element of attributeRecords.keys()) if (!element.isConnected) attributeRecords.delete(element);
    lastPassAt = new Date().toISOString();
  }
  function schedule() {
    if (!enabled || scheduled) return;
    scheduled = true;
    frame = requestAnimationFrame(() => {
      scheduled = false;
      frame = null;
      pass();
    });
  }
  function samples() {
    const result = new Set();
    for (const el of document.querySelectorAll('button,[role="button"],[role="menuitem"],label,h1,h2,h3,h4,[role="tooltip"]')) {
      if (protectedElement(el) || !visible(el)) continue;
      const value = normalize(el.textContent);
      if (value.length < 2 || value.length > 200 || !/[A-Za-z]{2}/.test(value) || /[\u3400-\u9fff@]|https?:\/\/|[\\/]/.test(value)) continue;
      if (!translate(value)) result.add(value);
      if (result.size >= 60) break;
    }
    return [...result];
  }
  function status() {
    return {
      version: VERSION, enabled, dictionarySize: dictionary.size,
      dynamicTemplateCount: templateRules.length,
      translatedTextNodes: textRecords.size,
      translatedAttributes: [...attributeRecords.values()].reduce((sum, records) => sum + records.size, 0),
      replacements, lastPassAt, untranslatedSamples: samples()
    };
  }
  function enable() {
    if (!enabled) {
      enabled = true;
      observer = new MutationObserver(schedule);
      observer.observe(document.documentElement, {
        subtree: true, childList: true, characterData: true,
        attributes: true, attributeFilter: attributes
      });
    }
    pass();
    return status();
  }
  function disable() {
    enabled = false;
    observer?.disconnect();
    observer = null;
    if (frame !== null) cancelAnimationFrame(frame);
    frame = null;
    scheduled = false;
    for (const [node, record] of textRecords) {
      if (node.isConnected && node.nodeValue === record.translated) node.nodeValue = record.original;
    }
    for (const [element, records] of attributeRecords) {
      if (!element.isConnected) continue;
      for (const [name, record] of records) {
        if (element.getAttribute(name) === record.translated) element.setAttribute(name, record.original);
      }
    }
    textRecords.clear();
    attributeRecords.clear();
    return status();
  }
  window.__HFZH_VERSION__ = VERSION;
  window.__HFZH__ = { version: VERSION, enable, disable, status, updateDictionary };
  updateDictionary(window.__HFZH_TRANSLATIONS__ || {});
  return enable();
})();
