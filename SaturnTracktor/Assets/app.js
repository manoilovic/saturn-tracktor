const $=id=>document.getElementById(id);
const bridge=message=>window.chrome.webview.postMessage(message);
const fields=['Gain','Leveling','Q','Clarity','Ambience','Surround','Dynamics','Bass'];
const names=['Дополнительное усиление','Выравнивание громкости','Q фильтр','Чёткость','Атмосфера','3D объёмный звук','Динамическое усиление','Усиление басов'];
const frequencies=['20','25','31.5','40','50','63','80','100','125','160','200','250','315','400','500','630','800','1k','1.25k','1.6k','2k','2.5k','3.15k','4k','5k','6.3k','8k','10k','12.5k','16k','20k'];
let state={Current:{Gain:0,Leveling:0,Q:1,Clarity:0,Ambience:0,Surround:0,Dynamics:0,Bass:0,Balance:0,Eq:Array(31).fill(0)},Compact:false},profiles={},on=false,busy=false,sending,dirty=false;
window.appDriver=false;
document.querySelectorAll('.logo').forEach(e=>e.innerHTML='<img src="logo.png" alt="Saturn Tracktor" draggable="false">');
$('controls').innerHTML=names.map((n,i)=>`<div class="control" style="margin-top:${i?19:17}px"><label>${n}<output id="o${i}"></output></label><input id="c${i}" aria-label="${n}" type="range" min="${i===2?.5:0}" max="${i===0?20:i===2?4:10}" step="${i<3?.1:1}" value="0"></div>`).join('');
$('bands').innerHTML=frequencies.map((f,i)=>`<div class="band"><output></output><input aria-label="${f} Гц" type="range" min="-12" max="12" step=".5" value="0"><svg class="knob" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9.5" fill="#101216" stroke="var(--a)" stroke-width="4"/></svg><span class="freq">${f}</span></div>`).join('');
$('bands').insertAdjacentHTML('afterbegin','<svg class="eqcurve" viewBox="0 0 1000 215" preserveAspectRatio="none"><defs><linearGradient id="eqGradient" x1="0" y1="0" x2="0" y2="1"><stop stop-color="var(--a)" stop-opacity=".34"/><stop offset="1" stop-color="var(--a)" stop-opacity="0"/></linearGradient></defs><path id="eqFill" fill="url(#eqGradient)"/><path id="eqLine" fill="none" stroke="var(--a)" stroke-width="1.6" vector-effect="non-scaling-stroke"/></svg>');
const eq=[...$('bands').querySelectorAll('input')],controls=fields.map((_,i)=>$('c'+i));
function write(){fields.forEach((field,i)=>controls[i].value=state.Current[field]);eq.forEach((e,i)=>e.value=state.Current.Eq[i]);$('balance').value=state.Current.Balance;$('compactGain').value=state.Current.Gain;paint();}
function paint(){
    controls.forEach((e,i)=>$('o'+i).textContent=Number(e.value).toFixed(i<3?1:0)+(i===0?' dB':i===2?'×':''));
    eq.forEach(e=>e.previousElementSibling.textContent=(+e.value>0?'+':'')+e.value);
    const points=eq.map((e,i)=>{const y=8+(12-Number(e.value))/24*199;e.parentElement.style.setProperty('--y',(26+y)+'px');return[(i+.5)*1000/31,y]});
    const d=points.map((p,i)=>(i?'L':'M')+p.join(' ')).join(' ');$('eqLine').setAttribute('d',d);$('eqFill').setAttribute('d',d+' L'+points.at(-1)[0]+' 215 L'+points[0][0]+' 215 Z');
    [...controls,$('balance'),$('compactGain')].forEach(e=>{const ratio=(Number(e.value)-Number(e.min))/(Number(e.max)-Number(e.min));const width=e.getBoundingClientRect().width;const thumb=e===$('compactGain')?25:17;e.style.setProperty('--fill',(width>0?100*(thumb/2+(width-thumb)*ratio)/width:100*ratio)+'%')});
    $('db').textContent='+'+Number(state.Current.Gain).toFixed(1)+' dB';$('percent').textContent='+'+Math.round((10**(state.Current.Gain/20)-1)*100)+'%';
    const b=state.Current.Balance;$('balOut').textContent=b===0?'Центр':(b<0?'L ':'R ')+Math.abs(b)+'%';
    $('status').textContent=dirty?'Изменён · Сброс вернёт значения профиля':'Значения выбранного профиля';
}
function update(){fields.forEach((f,i)=>state.Current[f]=Number(controls[i].value));state.Current.Eq=eq.map(e=>Number(e.value));state.Current.Balance=Number($('balance').value);$('compactGain').value=state.Current.Gain;dirty=true;paint();clearTimeout(sending);sending=setTimeout(()=>bridge({type:'settings',settings:state.Current}),35);}
function flush(){clearTimeout(sending);bridge({type:'settings',settings:state.Current});}
[...controls,...eq,$('balance')].forEach(e=>e.addEventListener('input',update));
$('compactGain').addEventListener('input',()=>{controls[0].value=$('compactGain').value;update()});
$('plus').onclick=()=>{eq.forEach(e=>e.value=Math.min(12,Number(e.value)+1));update()};
$('minus').onclick=()=>{eq.forEach(e=>e.value=Math.max(-12,Number(e.value)-1));update()};
$('reset').onclick=()=>{clearTimeout(sending);dirty=false;bridge({type:'reset'})};
$('profile').onchange=async()=>{flush();if($('profile').value==='__create__'){let name=await askName();$('profile').value=state.Profile;if(name)bridge({type:'saveProfile',name})}else{dirty=false;bridge({type:'profile',name:$('profile').value})}};
$('outputDevice').onchange=()=>{flush();bridge({type:'device',id:$('outputDevice').value})};
$('outputDevice').onpointerdown=()=>bridge({type:'refreshDevices'});
document.querySelectorAll('[data-color]').forEach(e=>e.onclick=()=>{state.Theme=e.dataset.color;document.documentElement.style.setProperty('--a',state.Theme);bridge({type:'theme',color:state.Theme})});
document.querySelectorAll('.power').forEach(e=>e.onclick=()=>{if(busy)return;flush();bridge({type:window.appDriver?'power':'installDriver'})});
function setPower(value){on=value;document.querySelectorAll('.power').forEach(p=>{p.classList.toggle('off',!on);p.setAttribute('aria-pressed',String(on));p.title=on?'Выключить обработку':'Включить обработку'});$('audioStatus').textContent=on?'● Обработка включена':'● Обработка выключена';}
document.querySelector('.viewbtn').onclick=()=>bridge({type:'compact',value:true});document.querySelector('[aria-label="Основной режим"]').onclick=()=>bridge({type:'compact',value:false});
document.querySelectorAll('.window-controls').forEach(group=>{const buttons=group.querySelectorAll('button');['minimize','maximize','close'].forEach((type,i)=>buttons[i].onclick=()=>bridge({type}));});
document.querySelectorAll('.header').forEach(e=>{e.onpointerdown=event=>{if(event.button===0&&!event.target.closest('button,input,select'))bridge({type:'drag'})};e.ondblclick=event=>{if(!event.target.closest('button'))bridge({type:'maximize'})}});
$('installDriver').onclick=()=>{if(!busy)bridge({type:'installDriver'})};
$('toastClose').onclick=()=>{$('toast').hidden=true};
function toast(message,error=false){$('toast').hidden=false;$('toastMessage').textContent=message;$('toast').classList.toggle('error',error);}
function renderCompact(value){state.Compact=value;document.body.classList.toggle('compactonly',value);requestAnimationFrame(paint);}
async function askName(){return new Promise(resolve=>{const modal=$('profileModal');modal.hidden=false;$('profileName').value='';$('profileName').focus();const done=value=>{modal.hidden=true;resolve(value)};$('profileCancel').onclick=()=>done(null);$('profileCreate').onclick=()=>done($('profileName').value.trim());$('profileName').onkeydown=e=>{if(e.key==='Enter')done($('profileName').value.trim());if(e.key==='Escape')done(null)}})}
window.chrome.webview.addEventListener('message',event=>{
    const m=event.data;
    if(m.type==='init'){
        state=m.state;profiles=m.profiles;window.appDriver=m.driver;document.documentElement.style.setProperty('--a',state.Theme);
        $('profile').replaceChildren();Object.keys(profiles).forEach(name=>$('profile').add(new Option(name,name)));$('profile').add(new Option('Создать свой профиль…','__create__'));$('profile').value=state.Profile;
        $('outputDevice').replaceChildren();m.devices.forEach(d=>$('outputDevice').add(new Option(d.Name,d.Id)));$('outputDevice').value=state.DeviceId;
        $('setupBar').hidden=m.driver;$('driverState').textContent=m.driver?'Windows Audio · системная обработка':'Нужна однократная настройка звука';
        setPower(m.on);renderCompact(state.Compact);write();
    }
    else if(m.type==='power')setPower(m.on);
    else if(m.type==='busy'){busy=m.value;document.querySelectorAll('.power,#installDriver').forEach(e=>e.disabled=busy);$('installDriver').textContent=busy?'Установка…':'Настроить звук';}
    else if(m.type==='compact')renderCompact(m.value);
    else if(m.type==='error')toast(m.message,true);
    else if(m.type==='notice')toast(m.message);
    else if(m.type==='meter'){
        const s=m.spectrum;const points=s.map((v,i)=>[(i+.5)*1000/31,112-v*100]);const d=points.map((p,i)=>(i?'L':'M')+p.join(' ')).join(' ');
        document.querySelector('.spectrum path[fill]').setAttribute('d',d+' L1000 120 H0 Z');
        $('meterState').textContent=m.active?(m.rendering?'СИГНАЛ · '+(m.rate/1000).toFixed(1)+' kHz':'ПАУЗА · ВЫХОД ОСВОБОЖДЁН'):'ОЖИДАНИЕ';
        $('limiterState').textContent=m.limiter>.2?'Лимитер −'+m.limiter.toFixed(1)+' dB':'Лимитер';
    }
});
window.addEventListener('resize',paint);setPower(false);write();bridge({type:'ready'});
