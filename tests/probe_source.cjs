const fs = require('node:fs');
const assert = require('node:assert/strict');
const create = require('./dom-harness.cjs');
const env = create(process.argv[2]);
const input = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
if (!env.sandbox.C30SuperLogin) { console.log('FEATURE_ABSENT bridge_calls=0'); process.exit(2); }
const result = env.sandbox.C30SuperLogin.run(input);
const messages = env.calls.filter(x => x.command === 'mirco.login');
assert.equal(result, 'submitted'); assert.equal(messages.length, 1);
const data = JSON.parse(messages[0].args[0]);
assert.equal(data.username, input.username); assert.equal(data.password, Buffer.from(input.password).toString('base64'));
assert.equal(data.logintype, 'account'); assert.equal(data.isrecord, 'false');
console.log('SUBMITTED bridge_calls=1 quoted_username=valid password=redacted authentication=unconfirmed');
