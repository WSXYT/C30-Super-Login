const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
module.exports = function create(sourceFile, options = {}) {
  const nodes = new Map(), handlers = new Map(), calls = [], beacons = [], posts = [];
  let ready;
  const node = key => { if (!nodes.has(key)) nodes.set(key, { value: '', disabled: false, innerHTML: '', style: {}, attrs: {}, dispatchEvent() {} }); return nodes.get(key); };
  function $(selector) {
    if (typeof selector === 'function') { ready = selector; return; }
    const key = typeof selector === 'string' ? selector : '__document', n = node(key);
    const q = { 0: n, on(type, ...args) { handlers.set(`${key}:${type}`, args.at(-1)); return q; },
      click(fn) { return q.on('click', fn); }, load(fn) { return q.on('load', fn); },
      attr(k, v) { if (v === undefined) return n.attrs[k]; n.attrs[k] = v; return q; },
      val(v) { if (v === undefined) return n.value; n.value = v; return q; },
      html(v) { if (v === undefined) return n.innerHTML; n.innerHTML = v; return q; },
      hide() { n.style.display = 'none'; return q; }, show() { n.style.display = 'block'; return q; },
      empty() { return q; }, append() { return q; }, css() { return q; }, focus() { return q; } };
    return q;
  }
  $.each = (values, fn) => values.forEach((v, i) => fn(i, v));
  function Hammer() { this.get = () => ({ set() {} }); this.on = () => {}; }
  const sandbox = { $, Hammer, console: { log() {} }, navigator: { userAgent: 'offline-source-probe' },
    location: { search: '', pathname: options.pathname ?? '/public_web/signin/index.html' },
    document: { getElementById: id => node('#' + id) }, localStorage: { removeItem() {} },
    cef: { message: { sendMessage: (command, args) => calls.push({ command, args }) } },
    Event: function (type) { this.type = type; },
    Image: function () { Object.defineProperty(this, 'src', { set(value) { beacons.push(value); } }); },
    XMLHttpRequest: function () { this.open = (method, url) => { this.method = method; this.url = url; }; this.setRequestHeader = () => {}; this.send = body => posts.push({ method: this.method, url: this.url, body }); },
    getQueryString: () => '1', _: { template: () => '' } };
  sandbox.window = sandbox;
  const context = vm.createContext(sandbox);
  if (options.synthetic) {
    // Independently authored contract stub for public CI; contains no vendor source.
    sandbox.vm = { base64_Encode: value => Buffer.from(value, 'utf8').toString('base64'), base64_Decode: value => Buffer.from(value, 'base64').toString('utf8') };
    vm.runInContext('var userArry = { loading: false, list: [] };', context);
  } else {
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../analysis/snapshots/public_web/assets_js/base64.js'), 'utf8'), context);
    vm.runInContext(fs.readFileSync(sourceFile, 'utf8'), context, { filename: sourceFile });
  }
  if (ready) ready(); calls.length = 0;
  return { sandbox, context, node, calls, beacons, posts };
};
