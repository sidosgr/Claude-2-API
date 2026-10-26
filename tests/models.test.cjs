const assert=require('node:assert/strict');
const {LocalClaudeExtractModels:extract}=require('../WebBridge.js');
const row=(text,attrs={})=>({textContent:text,disabled:false,getAttribute:key=>attrs[key]??null,hasAttribute:key=>Object.hasOwn(attrs,key)});
const menu={querySelectorAll:()=>[
 row('Fable 5.1 Pro or Max For your toughest challenges Upgrade'),
 row('Opus 5.5 Pro For complex work Upgrade'),
 row('Opus 5.5ProFor complex work and everyday tasksUpgrade'),
 row('Sonnet 5.5 Most efficient for simpler tasks'),
 row('Haiku 5.5 Fastest for quick answers'),
 row('Haiku 5.5 duplicate'),
 row('Opus 5.5 disabled',{'aria-disabled':'true'}),
 row('Opus 4.8 disabled',{'data-disabled':''}),
 row('Effort Medium'),
]};
assert.deepEqual(extract(menu),[{id:'claude-sonnet-5-5',name:'Sonnet 5.5'},{id:'claude-haiku-5-5',name:'Haiku 5.5'}]);
assert.deepEqual(extract({querySelectorAll:()=>[]}),[]);
assert.deepEqual(extract({querySelectorAll:()=>[row('Sonnet 5.5',{'data-model-id':'claude-sonnet-5-5-current'})]}),[{id:'claude-sonnet-5-5-current',name:'Sonnet 5.5'}]);
const locked=row('Opus 5.5');
locked.parentElement={textContent:'Opus 5.5 Pro Upgrade',querySelectorAll:()=>[locked],getAttribute:()=>null,hasAttribute:()=>false};
assert.deepEqual(extract({querySelectorAll:()=>[locked]}),[]);
console.log('PASS: model picker extraction, upgrade/disabled exclusion, deduplication, explicit IDs, empty picker.');
(async()=>{
 let attempts=0,expanded=false;
 const trigger={isConnected:true,getAttribute:key=>key==='aria-label'?'Model: Sonnet 5.5':String(expanded),click:()=>{expanded=!expanded;}};
 globalThis.location={origin:'https://claude.ai'};
 menu.getBoundingClientRect=()=>({width:100});
 globalThis.document={querySelectorAll:selector=>selector==='button'?(++attempts<3?[]:[trigger]):[menu]};
 globalThis.requestAnimationFrame=callback=>setImmediate(callback);
 const models=await globalThis.localClaudeBridgeRun({operation:'models'});
 assert.equal(models.length,2);assert.ok(attempts>=3);assert.equal(expanded,false);
 await assert.rejects(()=>globalThis.localClaudeBridgeRun({operation:'models'},{aborted:true}),/request_aborted/);
 console.log('PASS: delayed model-picker hydration and cancellation.');
 let moreOpen=false;
 const more={textContent:'More models',isConnected:true,getAttribute:key=>key==='aria-expanded'?String(moreOpen):null,click:()=>{moreOpen=!moreOpen;}};
 const primary={getBoundingClientRect:()=>({width:100}),querySelectorAll:selector=>selector==='[role="menuitem"]'?[more]:[row('Sonnet 5.5 Most efficient for simpler tasks')]};
 const secondary={getBoundingClientRect:()=>({width:100}),querySelectorAll:selector=>selector==='[role="menuitem"]'?[]:[row('Haiku 5.5'),row('Opus 5.5 Pro Upgrade')]};
 globalThis.document={querySelectorAll:selector=>selector==='button'?[trigger]:moreOpen?[primary,secondary]:[primary]};
 const nested=await globalThis.localClaudeBridgeRun({operation:'models'});
 assert.deepEqual(nested.map(model=>model.id),['claude-sonnet-5-5','claude-haiku-5-5']);
 assert.equal(expanded,false);
 console.log('PASS: More models submenu merged with selected primary model; upgrade-only entry excluded.');
})().catch(error=>{console.error(error);process.exitCode=1;});
