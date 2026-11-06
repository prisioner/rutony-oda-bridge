const $=id=>document.getElementById(id);
const token=document.querySelector('meta[name=bridge-token]').content;
let busy=false,lastUncertain='',sourcesRendered=false,sourcesDirty=false,lastState=null,refreshVersion=0;
$('redirect').textContent=location.origin+'/oauth/callback';
const login=new URLSearchParams(location.search).get('login');
if(login) {$('notice').textContent=login==='ok'?'Вход выполнен. Теперь можно включить передачу.':'Вход не завершён. Причина показана в статусе выше; после проверки начните вход заново.';history.replaceState(null,'','/control');}
function updateControls() {
  const s=lastState,unavailable=busy||!s;
  $('save-sources').disabled=unavailable;$('source-options').disabled=unavailable;
  $('enable').disabled=unavailable||s.enabled||!s.authorized||s.uncertain.length>0||s.storageFault;
  $('pause').disabled=unavailable||!s.enabled;$('authorize').disabled=unavailable||!s.configured;
  for(const tag of ['button','input']) for(const control of $('config').querySelectorAll(tag)) control.disabled=unavailable;
  for(const button of $('items').querySelectorAll('button')) button.disabled=unavailable||s.storageFault;
  $('skip').disabled=unavailable||s.storageFault;
}
async function action(path,body={}) {
  if(busy||!lastState) return;busy=true;++refreshVersion;updateControls();
  try {
    const r=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Bridge-Token':token},body:JSON.stringify(body)});
    if(!r.ok) {let data;try {data=await r.json();}catch {}throw Error(data?.error||'Операция не выполнена ('+r.status+').');}
    $('notice').textContent='Готово';return r.status===200 && r.headers.get('Content-Type')?.includes('json')?await r.json():{};
  }catch(e) {$('notice').textContent=e.message;return null;}finally {await refresh();busy=false;updateControls();}
}
$('config').addEventListener('submit',async e=> {e.preventDefault();if(busy||!lastState) return;const secret=$('secret').value;const id=$('clientId').value.trim();$('secret').value='';const result=await action('configure',{clientId:id,clientSecret:secret});if(result) $('clientId').value='';});
$('authorize').onclick=async()=> {const result=await action('authorize');if(result?.url) location.assign(result.url);};
$('enable').onclick=()=>action('enabled',{enabled:true});
$('pause').onclick=()=>action('enabled',{enabled:false});
$('sources').addEventListener('change',()=> {sourcesDirty=true;});
$('sources').addEventListener('submit',async e=> {e.preventDefault();if(busy||!lastState) return;const systems=Array.from($('source-list').querySelectorAll('input:checked'),input=>input.value);$('source-options').disabled=true;const result=await action('sources',{systems});if(result) {sourcesDirty=false;await refresh();$('notice').textContent='Источники сохранены. Передача на паузе.';}});
$('skip').onclick=()=> {if(confirm('Пропустить одно проблемное сообщение? Оно не попадёт в RutonyChat.')) action('skip');};
async function refresh() {
  const version=++refreshVersion;
  try {
    const r=await fetch('/api/state');if(!r.ok) throw Error();const s=await r.json();if(version!==refreshVersion) return;lastState=s;
    if(s.hostMode==='standalone') {document.querySelector('header h1').textContent='ODA → RutonyChat: тестовые данные';$('configuration').hidden=true;$('authorization').hidden=true;}
    if(!sourcesRendered) {
      for(const system of s.availableSources) {const label=document.createElement('label');label.className='source-option';const input=document.createElement('input');input.type='checkbox';input.value=system;label.append(input,document.createTextNode(system));$('source-list').append(label);}
      sourcesRendered=true;
    }
    if(!sourcesDirty) for(const input of $('source-list').querySelectorAll('input')) input.checked=s.sourceSystems.includes(input.value);
    $('phase').textContent=s.phase;$('error').textContent=s.error||'';
    $('summary').textContent=`Вход: ${s.authorized?'выполнен':'не выполнен'} · Связь: ${s.connected?'есть':'нет'} · Доставлено: ${s.delivered}`;
    $('client').textContent=s.configured?'Сохранён Client ID: '+s.clientId:'Приложение ещё не сохранено';
    if(s.storageFault) $('error').textContent='Ошибка записи журнала. Передача заблокирована до проверки файлов и перезапуска RutonyChat. Не удаляйте журнал.';
    $('blocked').hidden=!s.blockedMessage;$('uncertain').hidden=!s.uncertain.length;
    const key=JSON.stringify(s.uncertain);
    if(key!==lastUncertain) {
      lastUncertain=key;$('items').replaceChildren();
      for(const id of s.uncertain) {
        const div=document.createElement('div');div.className='delivery';const p=document.createElement('p');p.textContent=id;div.append(p);
        for(const [label,decision] of [['Уже доставлен — не повторять','delivered'],['Разрешить повтор','retry']]) {
          const button=document.createElement('button');button.textContent=label;button.className='secondary';button.onclick=()=> {if(decision==='retry'&&!confirm('RutonyChat мог уже обработать донат. Повтор может вызвать повторное оповещение. Продолжить?')) return;action('resolve',{key:id,decision});};div.append(button);
        }$('items').append(div);
      }
    }
    updateControls();
  }catch {if(version!==refreshVersion) return;lastState=null;updateControls();$('phase').textContent='Нет связи с локальным мостом';$('error').textContent='Проверьте, что автономный скрипт ODA включён в RutonyChat.';}
}
updateControls();refresh();setInterval(refresh,2000);
