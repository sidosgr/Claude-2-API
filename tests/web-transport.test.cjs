const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const { webcrypto } = require('node:crypto');
const source = fs.readFileSync(require.resolve('../WebBridge.js'), 'utf8');
const requests = [];
const context = vm.createContext({
 location: { origin: 'https://claude.ai' }, navigator: { language: 'en-US' },
 crypto: webcrypto, TextDecoder, TextEncoder, Intl,
 fetch: async (path, options) => {
  requests.push({ path, options });
  if (path === '/api/organizations') return new Response(JSON.stringify([{uuid:'11111111-1111-1111-1111-111111111111',name:'Personal'}]), {headers:{'content-type':'application/json'}});
  return new Response('data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"OK"}}\n\ndata: {"type":"message_stop"}\n\n', {headers:{'content-type':'text/event-stream'}});
 }
});
vm.runInContext(source, context);
(async () => {
 const orgs = await context.localClaudeBridgeRun({operation:'session'});
 assert.equal(orgs.length,1); assert.equal(orgs[0].name,'Personal');
 const prompt = 'Quotes " and backticks ` and </script> and Greek Ελληνικά';
 const result = await context.localClaudeBridgeRun({operation:'complete',organization:orgs[0].id,prompt,model:'claude-sonnet-5'});
 assert.equal(result.text,'OK');
 const request = requests[1];
 assert.match(request.path,/^\/api\/organizations\/11111111-1111-1111-1111-111111111111\/chat_conversations\/[a-f0-9-]+\/completion$/);
 assert.equal(request.options.credentials,'same-origin');
 assert.equal(request.options.redirect,'error');
 assert.equal(JSON.parse(request.options.body).prompt,prompt);
 assert.equal(Object.keys(request.options.headers).some(k => /cookie|authorization|user-agent/i.test(k)),false);
 context.location.origin='https://example.com';
 await assert.rejects(() => context.localClaudeBridgeRun({operation:'session'}),/wrong_origin/);
 assert.equal(requests.length,2);
 context.location.origin='https://claude.ai';
 const thought=await context.localClaudeBridgeRun({operation:'complete',organization:orgs[0].id,prompt:'OK',model:'claude-sonnet-5',thinking_mode:'extended',effort:'high'});
 assert.equal(thought.text,'OK');
 const thoughtPayload=JSON.parse(requests[2].options.body);
 assert.equal(thoughtPayload.thinking_mode,'extended');assert.equal(thoughtPayload.effort,'high');
 context.location.origin='https://claude.ai';
 context.fetch=async()=>new Response('',{status:401});
 await assert.rejects(() => context.localClaudeBridgeRun({operation:'session'}),/http_401/);
 context.fetch=async()=>new Response(JSON.stringify({error:{type:'permission_error',message:'This model is unavailable. private@example.com'}}),{status:403,headers:{'content-type':'application/json'}});
 await assert.rejects(() => context.localClaudeBridgeRun({operation:'session'}),error=>error.message==='http_403|permission_error|model_restriction');
 context.fetch=async()=>new Response('<html>challenge</html>',{status:403,headers:{'content-type':'text/html','cf-mitigated':'challenge'}});
 await assert.rejects(() => context.localClaudeBridgeRun({operation:'session'}),/http_403\|browser_challenge\|unspecified/);
 context.fetch=async()=>new Response('<html>verification</html>',{headers:{'content-type':'text/html'}});
 await assert.rejects(() => context.localClaudeBridgeRun({operation:'session'}),/browser_verification/);
 context.fetch=async()=>new Response('data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"partial"}}\n\n',{headers:{'content-type':'text/event-stream'}});
 await assert.rejects(() => context.localClaudeBridgeRun({operation:'complete',organization:'test',prompt:'test',model:'test'}),/incomplete_stream/);
 console.log('PASS: web-session discovery, same-origin transport, payload escaping, no credential extraction, origin guard, 401, browser verification, truncated upstream tests. Synthetic fetch only.');
})().catch(error=>{console.error(error);process.exitCode=1;});
