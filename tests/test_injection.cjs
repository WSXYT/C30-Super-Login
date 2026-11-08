const fs = require('node:fs'), vm = require('node:vm'), path = require('node:path'), assert = require('node:assert/strict');
const create = require('./dom-harness.cjs');
const original = path.join(__dirname, '../analysis/snapshots/public_web/signin/signin.js');
const extension = fs.readFileSync(path.join(__dirname, '../src/C30SuperLogin.Core/Injection/login.js'), 'utf8');
const input = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixture-input.json'), 'utf8'));
let count = 0;
function env(options) { const e = create(original, { ...options, synthetic: process.argv.includes('--synthetic') }); vm.runInContext(extension, e.context); return e; }
function test(name, run) { run(); count++; console.log('PASS ' + name); }
test('probe returns ready without submitting credentials', () => { const e = env(); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, operation: 'probe' }), 'ready'); assert.equal(e.calls.length, 0); });
test('JSON serialization supports quotes in supplied username', () => { const e = env(); e.sandbox.C30SuperLogin.run(input); const payload = JSON.parse(e.calls.at(-1).args[0]); assert.equal(payload.username, input.username); });
test('duplicate request never resubmits', () => { const e = env(); e.sandbox.C30SuperLogin.run(input); e.node('#loginbtn').disabled = false; assert.equal(e.sandbox.C30SuperLogin.run(input), 'duplicate'); assert.equal(e.calls.filter(x => x.command === 'mirco.login').length, 1); });
test('other CEF pages never receive a login', () => { const e = env({ pathname: '/other/index.html' }); assert.equal(e.sandbox.C30SuperLogin.run(input), 'page_mismatch'); assert.equal(e.calls.length, 0); });
test('busy original button prevents duplicate native requests', () => { const e = env(); e.node('#loginbtn').disabled = true; assert.equal(e.sandbox.C30SuperLogin.run(input), 'busy'); assert.equal(e.calls.length, 0); });
test('empty password rejected before the bridge', () => { const e = env(); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, password: '' }), 'invalid_credentials'); assert.equal(e.calls.length, 0); });
test('receipt refuses a non-loopback destination', () => { const e = env(); e.sandbox.C30SuperLogin.run({ ...input, receipt: 'https://example.invalid/collect' }); assert.equal(e.beacons.length, 0); });
test('receipt carries only event code, not credentials', () => { const e = env(); e.sandbox.C30SuperLogin.run(input); assert.equal(e.beacons.length, 1); assert.ok(e.beacons[0].endsWith('?event=submitted')); assert.ok(!e.beacons.join().includes(input.username)); assert.ok(!e.beacons.join().includes(input.password)); });
test('missing bridge reports not_ready and does not throw', () => { const e = env(); e.sandbox.cef = undefined; assert.equal(e.sandbox.C30SuperLogin.run(input), 'not_ready'); });
test('username event runs before password assignment', () => { const e = env(); e.node('#loginname').dispatchEvent = event => { if (event.type === 'keyup') e.node('#loginpwd').value = 'OLD_HISTORY_PASSWORD'; }; e.sandbox.C30SuperLogin.run(input); assert.equal(e.node('#loginpwd').value, input.password); });
function saved(e, entries) { vm.runInContext('userArry.loading = true; userArry.list = ' + JSON.stringify(entries) + ';', e.context); }
const nativeRecord = { uid: 'LOCAL_UID', uname: input.username, nickname: 'Local Saved Fixture', pwd: Buffer.from(input.password).toString('base64'), isrecord: 'true', token: 'MUST_NEVER_LEAVE_C30' };
test('saved-account list projects only local metadata, never password or token', () => {
  const e = env(); saved(e, [nativeRecord]); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, operation: 'list_saved' }), 'accounts');
  assert.equal(e.posts.length, 1); const record = JSON.parse(e.posts[0].body)[0];
  assert.deepEqual(Object.keys(record).sort(), ['displayName', 'hasSavedPassword', 'uid', 'username']);
  assert.equal(record.hasSavedPassword, true); assert.equal(record.uid, nativeRecord.uid);
  assert.ok(!e.posts[0].body.includes(nativeRecord.pwd)); assert.ok(!e.posts[0].body.includes(nativeRecord.token)); assert.equal(e.calls.length, 0);
});
test('saved-account metadata refuses external destination', () => { const e = env(); saved(e, [nativeRecord]); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, operation: 'list_saved', receipt: 'https://example.invalid' }), 'invalid_receipt'); assert.equal(e.posts.length, 0); });
test('unloaded saved-account list never reports an empty successful import', () => { const e = env(); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, operation: 'list_saved' }), 'not_ready'); assert.equal(e.posts.length, 0); });
test('login by saved reference preserves original autologin encryption flags', () => {
  const e = env(); saved(e, [nativeRecord]); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, password: undefined, operation: 'login_saved', savedUid: nativeRecord.uid }), 'submitted');
  const data = JSON.parse(e.calls.at(-1).args[0]); assert.equal(data.logintype, 'autologin'); assert.equal(data.needEncrypt, 'true'); assert.equal(data.issetclass, '0'); assert.equal(data.password, nativeRecord.pwd);
  assert.equal(e.posts.length, 0); assert.ok(!e.beacons.join().includes(input.password));
});
test('missing saved password requires user input instead of inventing a login', () => { const e = env(); saved(e, [{ ...nativeRecord, isrecord: 'false' }]); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, operation: 'login_saved', savedUid: nativeRecord.uid }), 'password_required'); assert.equal(e.calls.length, 0); });
test('stale saved account ID never selects another account', () => { const e = env(); saved(e, [nativeRecord]); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, operation: 'login_saved', savedUid: 'MISSING_UID' }), 'saved_missing'); assert.equal(e.calls.length, 0); });
test('ambiguous duplicate C30 records are not submitted twice', () => { const e = env(); saved(e, [nativeRecord, nativeRecord]); assert.equal(e.sandbox.C30SuperLogin.run({ ...input, operation: 'login_saved', savedUid: nativeRecord.uid }), 'saved_ambiguous'); assert.equal(e.calls.length, 0); });
console.log(`RESULT ${count}/${count} PASS; live_network=none; fixture=${process.argv.includes('--synthetic') ? 'owned-synthetic' : 'local-vendor-snapshot'}`);
