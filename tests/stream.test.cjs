const assert = require('node:assert/strict');
const { LocalClaudeSseParser: Parser } = require('../WebBridge.js');
const frame = value => 'data: ' + JSON.stringify(value) + '\n\n';
const text = frame({ type: 'message_start', message: { model: 'test-model' } })
 + frame({ type: 'content_block_delta', delta: { type: 'text_delta', text: 'Γειά 🌍' } })
 + frame({ type: 'message_stop' });
for (let split = 0; split <= text.length; split++) {
 const parser = new Parser(); parser.feed(text.slice(0, split)); parser.feed(text.slice(split));
 assert.equal(parser.text, 'Γειά 🌍'); assert.equal(parser.model, 'test-model'); assert.equal(parser.stopped, true);
}
const crlf = new Parser(); crlf.feed(text.replaceAll('\n', '\r\n')); assert.equal(crlf.stopped, true);
const legacy = new Parser(); legacy.feed(frame({ completion: 'OK', stop_reason: 'stop_sequence' })); assert.equal(legacy.text, 'OK'); assert.equal(legacy.stopped, true);
assert.throws(() => new Parser().feed('data: broken\n\n'), /invalid_stream_json/);
assert.throws(() => new Parser().feed(frame({ type: 'error', error: {} })), /upstream_stream_error/);
assert.throws(() => new Parser().feed('data: {', true), /incomplete_stream/);
assert.throws(() => new Parser().feed('x'.repeat(1024 * 1024 + 1)), /event_too_large/);
const incomplete = new Parser(); incomplete.feed(frame({ type: 'content_block_delta', delta: { type: 'text_delta', text: 'partial' } }), true); assert.equal(incomplete.stopped, false);
// The source is injected repeatedly into the same document; it must remain redeclarable.
const vm = require('node:vm'); const fs = require('node:fs'); const source = fs.readFileSync(require.resolve('../WebBridge.js'), 'utf8');
const sandbox = vm.createContext({}); vm.runInContext(source, sandbox); vm.runInContext(source, sandbox);
console.log('PASS: split-frame, CRLF, Unicode, legacy stream, malformed/error/truncated stream, size limit, repeated injection tests.');
