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
