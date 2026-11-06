import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';

const script=await readFile(new URL('../src/RutonyOda/web/control.js',import.meta.url),'utf8');
const html=await readFile(new URL('../src/RutonyOda/web/control.html',import.meta.url),'utf8');
class Element {
  constructor(tag='div') {this.tag=tag;this.children=[];this.events={};this.value='';this.textContent='';this.disabled=false;this.hidden=false;this.checked=false;}
  append(...children) {this.children.push(...children);}
  replaceChildren(...children) {this.children=[...children];}
  addEventListener(name,handler) {this.events[name]=handler;}
  fire(name) {return this.events[name]?.({preventDefault(){}});}
  querySelectorAll(selector) {
    return this.children.flatMap(child=>[...(selector==='input:checked'?child.tag==='input'&&child.checked:child.tag===selector)?[child]:[],...child.querySelectorAll(selector)]);
  }
}
const base={hostMode:'rutony',configured:true,authorized:true,enabled:false,connected:false,delivered:2,clientId:'test-client',phase:'Пауза',error:null,uncertain:[],blockedMessage:null,storageFault:false,availableSources:['ODA','DonationAlerts'],sourceSystems:['ODA']};
const response=(body={},status=200)=>({ok:status>=200&&status<300,status,headers:{get:()=> 'application/json'},json:async()=>structuredClone(body)});
const deferred=()=>{let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};};
const settle=async()=>{await new Promise(resolve=>setImmediate(resolve));};
async function panel({state={},search=''}={}) {
  const elements=Object.fromEntries([...html.matchAll(/<([a-z0-9]+)[^>]*\bid="([^"]+)"/g)].map(([,tag,id])=>[id,new Element(tag)]));
  const heading=new Element('h1'),calls=[],replaced=[],assigned=[],prompts=[];
  let interval,confirmResult=true,current={...base,...state},stateFailure=false,stateFetch=null,post=async()=>response();
  const context={URLSearchParams,document:{getElementById:id=>{assert.ok(elements[id],id);return elements[id];},querySelector:selector=>selector==='header h1'?heading:{content:'synthetic-local-token'},createElement:tag=>new Element(tag),createTextNode:text=>Object.assign(new Element('#text'),{textContent:text})},
    location:{origin:'http://127.0.0.1:12345',search,assign:url=>assigned.push(url)},history:{replaceState:(_a,_b,url)=>replaced.push(url)},confirm:text=>{prompts.push(text);return confirmResult;},setInterval:callback=>{interval=callback;},
    fetch:async(path,options)=>{calls.push({path,...options});if(path==='/api/state'){if(stateFailure)throw Error('synthetic unavailable');return stateFetch?stateFetch():response(current);}return post(path,options);}};
  vm.runInNewContext(script,context,{filename:'control.js'});await settle();
  return {elements,calls,replaced,assigned,prompts,heading,poll:()=>interval(),setState:value=>{current={...current,...value};},failState:value=>{stateFailure=value;},post:handler=>{post=handler;},stateRequest:handler=>{stateFetch=handler;},confirm:value=>{confirmResult=value;},text:()=>Object.values(elements).map(e=>e.textContent).join('\n')};
}

test('OAuth feedback removes callback query without echoing code/state/provider details',async()=>{
  const p=await panel({search:'?login=failed&code=private-code&state=private-state&error_description=private-secret'});
  assert.match(p.elements.notice.textContent,/Вход не завершён/);assert.deepEqual(p.replaced,['/control']);assert.doesNotMatch(p.text(),/private-/);
  const success=await panel({search:'?login=ok'});assert.match(success.elements.notice.textContent,/Вход выполнен/);
});
test('busy action immediately disables mutations and cannot send twice',async()=>{
  const p=await panel(),pending=deferred();p.post(()=>pending.promise);
  const first=p.elements.enable.onclick();
  try {assert.equal(p.elements.enable.disabled,true);assert.equal(p.elements.authorize.disabled,true);assert.equal(p.elements['source-options'].disabled,true);await p.elements.enable.onclick();assert.equal(p.calls.filter(c=>c.method==='POST').length,1);}
  finally {pending.resolve(response());await first;}
});
test('busy configure does not discard credentials that were never submitted',async()=>{
  const p=await panel(),pending=deferred();p.post(()=>pending.promise);const first=p.elements.enable.onclick();
  p.elements.clientId.value='other-client';p.elements.secret.value='not-submitted-secret';
  try {await p.elements.config.fire('submit');assert.equal(p.elements.secret.value,'not-submitted-secret');assert.equal(p.calls.filter(c=>c.path==='/api/configure').length,0);}
  finally {pending.resolve(response());await first;}
});
test('configuration clears sent secret and uses guarded JSON POST without echo',async()=>{
  const p=await panel();p.elements.clientId.value=' test-client ';p.elements.secret.value='synthetic-private-secret';
  p.post(async()=>response({error:'Проверьте приложение'},400));await p.elements.config.fire('submit');
  const sent=p.calls.find(c=>c.path==='/api/configure');assert.equal(sent.headers['X-Bridge-Token'],'synthetic-local-token');assert.equal(sent.headers['Content-Type'],'application/json');assert.deepEqual(JSON.parse(sent.body),{clientId:'test-client',clientSecret:'synthetic-private-secret'});
  assert.equal(p.elements.secret.value,'');assert.doesNotMatch(p.text(),/synthetic-private-secret/);assert.equal(p.elements.notice.textContent,'Проверьте приложение');
});
test('failed state refresh disables stale enable and recovers after reconnect',async()=>{
  const p=await panel();assert.equal(p.elements.enable.disabled,false);p.failState(true);await p.poll();
  assert.match(p.elements.phase.textContent,/Нет связи/);assert.equal(p.elements.enable.disabled,true);assert.equal(p.elements.authorize.disabled,true);
  p.failState(false);await p.poll();assert.equal(p.elements.enable.disabled,false);
});
test('dirty source selections survive polling, failed save and retry',async()=>{
  const p=await panel();const inputs=p.elements['source-list'].querySelectorAll('input');inputs[0].checked=false;inputs[1].checked=true;p.elements.sources.fire('change');
  await p.poll();assert.deepEqual(inputs.map(x=>x.checked),[false,true]);
  p.post(async()=>response({error:'synthetic storage failure'},400));await p.elements.sources.fire('submit');assert.deepEqual(inputs.map(x=>x.checked),[false,true]);assert.equal(p.elements['source-options'].disabled,false);
  p.post(async()=>{p.setState({sourceSystems:['DonationAlerts'],enabled:false});return response();});await p.elements.sources.fire('submit');
  assert.deepEqual(JSON.parse(p.calls.filter(c=>c.path==='/api/sources').at(-1).body),{systems:['DonationAlerts']});assert.match(p.elements.notice.textContent,/Источники сохранены/);
  p.setState({sourceSystems:['ODA']});await p.poll();assert.deepEqual(inputs.map(x=>x.checked),[true,false]);
});
test('enable and pause reflect refreshed server state',async()=>{
  const p=await panel();p.post(async(_path,options)=>{p.setState({enabled:JSON.parse(options.body).enabled});return response();});
  await p.elements.enable.onclick();assert.equal(p.elements.enable.disabled,true);assert.equal(p.elements.pause.disabled,false);
  await p.elements.pause.onclick();assert.equal(p.elements.pause.disabled,true);assert.equal(p.elements.enable.disabled,false);
  assert.deepEqual(p.calls.filter(c=>c.path==='/api/enabled').map(c=>JSON.parse(c.body)),[{enabled:true},{enabled:false}]);
});
test('uncertain delivery needs explicit resolution and retry confirmation',async()=>{
  const p=await panel({state:{uncertain:['recipient/one']}});assert.equal(p.elements.enable.disabled,true);assert.equal(p.elements.uncertain.hidden,false);
  const [delivered,retry]=p.elements.items.querySelectorAll('button');p.confirm(false);retry.onclick();await settle();assert.equal(p.calls.filter(c=>c.path==='/api/resolve').length,0);
  p.confirm(true);retry.onclick();await settle();assert.deepEqual(JSON.parse(p.calls.find(c=>c.path==='/api/resolve').body),{key:'recipient/one',decision:'retry'});
  delivered.onclick();await settle();assert.deepEqual(JSON.parse(p.calls.filter(c=>c.path==='/api/resolve').at(-1).body),{key:'recipient/one',decision:'delivered'});
});
test('storage fault disables enable resolve and skip while retaining the explanation',async()=>{
  const p=await panel({state:{storageFault:true,uncertain:['recipient/one'],blockedMessage:'invalid:test'}});
  assert.equal(p.elements.enable.disabled,true);assert.ok(p.elements.items.querySelectorAll('button').every(b=>b.disabled));assert.equal(p.elements.skip.disabled,true);assert.match(p.elements.error.textContent,/Не удаляйте журнал/);
});
test('authorize navigates only after an accepted response; standalone hides credentials',async()=>{
  const p=await panel();p.post(async()=>response({url:'https://example.invalid/authorize'}));await p.elements.authorize.onclick();assert.deepEqual(p.assigned,['https://example.invalid/authorize']);
  const standalone=await panel({state:{hostMode:'standalone'}});assert.equal(standalone.elements.configuration.hidden,true);assert.equal(standalone.elements.authorization.hidden,true);assert.match(standalone.heading.textContent,/тестовые данные/);
});

test('older overlapping state response cannot overwrite newer enabled state',async()=>{
  const p=await panel(),old=deferred();let count=0;p.stateRequest(()=>++count===1?old.promise:response({...base,enabled:true}));
  const first=p.poll();await p.poll();assert.equal(p.elements.enable.disabled,true);
  old.resolve(response({...base,enabled:false}));await first;assert.equal(p.elements.enable.disabled,true);assert.equal(p.elements.pause.disabled,false);
});
