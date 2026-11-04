<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue';

import type { Dashboard, Device, IpSequenceGap } from './types';

import SeatLayout from './SeatLayout.vue';
import StudentSetup from './StudentSetup.vue';
import {useGridFit} from './useGridFit';
const data=ref<Dashboard|null>(null),page=ref('live'),busy=ref(false),connected=ref(false),notice=ref('');
const loading=ref(true),loginError=ref('');
const local=['127.0.0.1','localhost','[::1]'].includes(location.hostname);
let socket:WebSocket|null=null,retry:ReturnType<typeof setTimeout>|undefined,toastTimer:ReturnType<typeof setTimeout>|undefined;
const online=computed(()=>data.value?.devices.filter(d=>d.status!=='offline').length??0);
const offline=computed(()=>data.value?.devices.filter(d=>d.status==='offline').length??0);
const ipGaps=computed(()=>data.value?.ipGaps??[]);
const missingIpCount=computed(()=>ipGaps.value.reduce((sum,gap)=>sum+gap.count,0));
const deviceSearch=ref(''),deviceFilter=ref('all');
const seatLabel=(id:string|null)=>data.value?.seats.find(s=>s.id===id)?.label??'';
const seatOrder=new Intl.Collator('zh-CN',{numeric:true});
const deviceSort=ref<'ip'|'seat'>('seat'),sortDescending=ref(false);
function sortDevices(column:'ip'|'seat'){
 if(deviceSort.value===column)sortDescending.value=!sortDescending.value;
 else{deviceSort.value=column;sortDescending.value=false;}
}
const sortIndicator=(column:'ip'|'seat')=>deviceSort.value===column?(sortDescending.value?'↓':'↑'):'↕';
const visibleDevices=computed(()=>[...(data.value?.devices??[])].filter(d=>
 (deviceFilter.value==='all'||(deviceFilter.value==='offline'?d.status==='offline':d.status!=='offline'))&&
 `${d.name} ${seatLabel(d.seatId)}`.toLowerCase().includes(deviceSearch.value.trim().toLowerCase()))
 .sort((a,b)=>{
  const bySeat=seatOrder.compare(seatLabel(a.seatId)||'\uffff',seatLabel(b.seatId)||'\uffff');
  const byIp=seatOrder.compare(a.name,b.name);
  const order=deviceSort.value==='ip'?byIp||bySeat:bySeat||byIp;
  return (sortDescending.value?-1:1)*order;
 }));
const rows=computed(()=>Array.from({length:data.value?.layoutRows??6},(_,i)=>i+1));
const columns=computed(()=>data.value?.layoutColumns??6);
const seatGrid=ref<HTMLElement|null>(null),gridRowCount=computed(()=>rows.value.length);
const gridFit=useGridFit(seatGrid,gridRowCount,columns);
const editingLayout=ref(false);
const studentSetup=ref<'package'|'password'|null>(null);
const removingDevice=ref<Device|null>(null),removeCancel=ref<HTMLButtonElement|null>(null);
const removingSeat=computed(()=>data.value?.seats.find(s=>s.id===removingDevice.value?.seatId)?.label);
interface SeatShiftChoice {deviceId:string;seatId:string;ip:string;from:string;to:string;followingCount:number;expectedState:string;canShift:boolean;error:string|null;examples:{ip:string;from:string;to:string}[]}
const seatShift=ref<SeatShiftChoice|null>(null),shiftCancel=ref<HTMLButtonElement|null>(null);
interface MatchReview {students:number;seats:number;checkedPairs:number;addressGroups:number;uncheckedAddresses:number;gaps:IpSequenceGap[];offlineIps:string[];canMatch:boolean;error:string|null;expectedState:string}
const matchReview=ref<MatchReview|null>(null),matchCancel=ref<HTMLButtonElement|null>(null);
let removeTrigger:HTMLButtonElement|null=null;
const seatMap=computed(()=>new Map(data.value?.seats.map(s=>[`${s.row}:${s.column}`,s])));
const seatAt=(row:number,col:number)=>seatMap.value.get(`${row}:${col}`);
const statuses:Record<string,string>={duplicate:'座位重复',ok:'在线',unbound:'未分配座位',offline:'离线',stale:'连接中断',idle:'连接中',error:'麦克风异常',uncalibrated:'连接中',blocked:'请更新客户端',no_signal:'无输入',calibrating:'连接中'};
const score=(n:number|null|undefined)=>n==null?'—':n>0&&n<10?n.toFixed(1):Math.round(n);
const volumeGain=computed(()=>data.value?.volumeGain??1);
const tone=(n:number|null)=>n===null?'none':n>=70*volumeGain.value?'loud':n>=40*volumeGain.value?'medium':'quiet';
function toast(text:string){notice.value=text;clearTimeout(toastTimer);toastTimer=setTimeout(()=>notice.value='',4500);}
async function request(path:string,method='GET',body?:unknown){
 const response=await fetch(path,{method,credentials:'same-origin',headers:{'Content-Type':'application/json','X-RoomScope':'1'},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(10000)});
 if(!response.ok){let message=`操作失败（${response.status}）`;try{message=(await response.json()).error??message;}catch{}throw new Error(message);}
 const text=await response.text();return text?JSON.parse(text):null;
}
const api=(path:string,method='GET',body?:unknown)=>request('/api/teacher'+path,method,body);
async function refresh(){data.value=await api('/dashboard');}
async function perform(fn:()=>Promise<void>){if(busy.value)return false;busy.value=true;try{await fn();return true;}catch(e){toast(e instanceof Error?e.message:'操作失败，请重试。');return false;}finally{busy.value=false;}}
function connect(){
 clearTimeout(retry);if(socket){socket.onclose=null;socket.close();}
 socket=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/ws/dashboard`);
 socket.onmessage=e=>{try{data.value=JSON.parse(e.data);connected.value=true;}catch{}};
 socket.onclose=()=>{connected.value=false;retry=setTimeout(connect,3000);};socket.onerror=()=>connected.value=false;
}
async function bind(device:Device,event:Event){
 const input=event.target as HTMLSelectElement,seatId=input.value;if(seatId===device.seatId)return;
 input.value=device.seatId??'';
 await perform(async()=>{
  let expectedState: string|undefined;
  if(data.value?.sequentialMatching){
   const preview=await api(`/devices/${device.id}/seat-shift-preview`,'POST',{seatId});expectedState=preview.expectedState;
   if(preview.offerShift){seatShift.value={...preview,deviceId:device.id,seatId,ip:device.name};return;}
  }
  await api(`/devices/${device.id}/seat`,'PUT',{seatId,expectedState});await refresh();
 });
 if(seatShift.value){await nextTick();shiftCancel.value?.focus();}
}
function cancelSeatShift(){if(!busy.value)seatShift.value=null;}
async function applySeatShift(following:boolean){
 const choice=seatShift.value;if(!choice||following&&!choice.canShift)return;
 await perform(async()=>{
  await api(`/devices/${choice.deviceId}/${following?'shift-seats':'seat'}`,following?'POST':'PUT',{seatId:choice.seatId,expectedState:choice.expectedState});
  seatShift.value=null;await refresh();toast(following?`已修改本机，并顺延后续 ${choice.followingCount} 台学生机。`:'已修改本机座位。');
 });
}
async function matchSeats(){
 await perform(async()=>{matchReview.value=await api('/devices/match-preview','POST');});
 if(matchReview.value){await nextTick();matchCancel.value?.focus();}
}
function cancelMatching(){if(!busy.value)matchReview.value=null;}
async function confirmMatching(){
 const review=matchReview.value;if(!review?.canMatch)return;
 await perform(async()=>{
 const result=await api('/devices/match-seats','POST',{expectedState:review.expectedState});matchReview.value=null;await refresh();deviceSort.value='ip';sortDescending.value=false;
 toast(result.changed?`已按 IP 顺序匹配 ${result.matched} 台学生机。`:'当前座位已按 IP 顺序匹配。');
 });
}
async function remove(device:Device){
 if(busy.value)return;
 removeTrigger=document.activeElement instanceof HTMLButtonElement?document.activeElement:null;
 removingDevice.value=device;await nextTick();removeCancel.value?.focus();
}
function cancelRemove(){if(busy.value)return;removingDevice.value=null;removeTrigger?.focus();}
async function confirmRemove(){
 const device=removingDevice.value;if(!device)return;
 await perform(async()=>{await api(`/devices/${device.id}`,'DELETE');removingDevice.value=null;await refresh();toast('已移除学生机。');});
}
async function saveLayout(cells:string[][]){if(await perform(async()=>{data.value=await api('/layout','PUT',{cells});}))editingLayout.value=false;}
function reload(){location.reload();}
onMounted(async()=>{try{await refresh();connect();}catch(e){loginError.value='暂时无法连接教师端，请检查网络后重试。';}finally{loading.value=false;}});
onUnmounted(()=>{clearTimeout(retry);clearTimeout(toastTimer);if(socket){socket.onclose=null;socket.close();}});
</script>

<template>
 <div v-if="loading" class="center-state">正在连接教师端……</div>
 <div v-else-if="!data" class="center-state"><h1>机房音量管理</h1><p>{{loginError}}</p><button class="button primary" @click="reload">重新连接</button></div>
 <template v-else>
  <header class="app-header">
   <nav aria-label="主导航"><button :class="{active:page==='live'}" :aria-current="page==='live'?'page':undefined" @click="page='live'">音量监控</button><button :class="{active:page==='devices'}" :aria-current="page==='devices'?'page':undefined" @click="page='devices'">学生机管理</button></nav>
   <span class="connection" :class="{disconnected:!connected}"><i aria-hidden="true"></i>{{connected?`在线 ${online} / ${data.devices.length}`:'正在重连'}}</span>
  </header>
  <main :class="{'monitor-page':page==='live'}">
   <div v-if="!connected" class="connection-warning" role="status">连接已中断，正在自动重连。当前显示的是上次收到的数据。</div>
   <template v-if="page==='live'">
    <section class="toolbar"><div class="page-heading"><h1>实时音量</h1><span>音量指数</span><span v-if="offline" class="offline-count">{{offline}} 台离线</span></div><button class="button" @click="editingLayout=true">座位布局</button></section>
    <section class="panel seat-panel" :class="{'outdated':!connected}"><div class="classroom"><div ref="seatGrid" class="seat-rows" :style="{...gridFit,'--rows':rows.length}"><div v-for="row in rows" :key="row" class="seat-row" :style="{'--columns':columns}">
     <template v-for="col in columns" :key="col"><div v-if="seatAt(row,col)" class="seat" :class="[tone(seatAt(row,col)!.score),{'seat-error':['error','blocked','stale','duplicate'].includes(seatAt(row,col)!.status)}]" :aria-label="`座位 ${seatAt(row,col)!.label}，${statuses[seatAt(row,col)!.status]}${seatAt(row,col)!.status==='duplicate'?'': '，音量 '+score(seatAt(row,col)!.score)}`" :title="seatAt(row,col)!.deviceName??statuses[seatAt(row,col)!.status]">
      <span class="seat-label">{{seatAt(row,col)!.label}}<small>号</small></span><strong v-if="seatAt(row,col)!.status==='duplicate'" class="seat-duplicate-label">座位重复</strong><template v-else><strong :class="{'volume-large':(seatAt(row,col)!.score??0)>=1000}">{{score(seatAt(row,col)!.score)}}</strong><small v-if="seatAt(row,col)!.score===null" class="seat-state">{{statuses[seatAt(row,col)!.status]}}</small><div v-else class="seat-meter" aria-hidden="true"><i :style="{width:`${Math.min(100,seatAt(row,col)!.score!/volumeGain)}%`}"></i></div></template>
     </div><div v-else class="aisle-cell" aria-hidden="true"></div></template>
    </div></div><div class="teacher-desk"><span>讲台</span></div></div></section>
   </template>
   <template v-else>
    <section class="toolbar"><div class="page-heading"><h1>学生机管理</h1><span>{{data.devices.length}} 台已接入</span></div><div v-if="local" class="actions"><button class="button primary" @click="studentSetup='package'">生成学生端</button><button class="button" @click="studentSetup='password'">维护密码</button></div></section>

    <p v-if="!data.devices.length" class="empty">等待学生机自动连接</p>
    <section v-if="ipGaps.length" class="ip-warning" aria-labelledby="ip-warning-title">
     <header><h2 id="ip-warning-title">IP 跳号异常</h2><span>{{ipGaps.length}} 处跳号 · 缺少 {{missingIpCount}} 个 IP</span></header>
     <ul><li v-for="gap in ipGaps" :key="gap.before"><span class="ip-gap-range">{{gap.before}} → {{gap.after}}</span><span>缺少 IP：<strong>{{gap.missingFrom}}{{gap.count>1?' ～ '+gap.missingTo:''}}</strong></span></li></ul>
     <p>请检查对应学生机是否已连接。</p>
    </section>
    <section v-if="data.devices.length" class="panel devices-panel"><div class="panel-heading"><h2>已接入学生机</h2><div class="device-filters"><button class="button" @click="matchSeats" :disabled="busy||!connected" title="先检查 IP 跳号，再确认按 IP 顺序重新分配全部学生机（含离线）的座位">顺序匹配</button><input v-model="deviceSearch" type="search" placeholder="搜索 IP 或座位" aria-label="搜索学生机" @keydown.esc="deviceSearch=''"/><select v-model="deviceFilter" aria-label="筛选学生机状态"><option value="all">全部状态</option><option value="online">在线 {{online}}</option><option value="offline">离线 {{offline}}</option></select></div></div>
     <div class="table-scroll"><table><thead><tr><th :aria-sort="deviceSort==='ip'?(sortDescending?'descending':'ascending'):'none'"><button class="sort-button" @click="sortDevices('ip')" :title="deviceSort==='ip'&&!sortDescending?'按 IP 从大到小排序':'按 IP 从小到大排序'">学生机 IP <span aria-hidden="true">{{sortIndicator('ip')}}</span></button></th><th :aria-sort="deviceSort==='seat'?(sortDescending?'descending':'ascending'):'none'"><button class="sort-button" @click="sortDevices('seat')" :title="deviceSort==='seat'&&!sortDescending?'按座位从大到小排序':'按座位从小到大排序'">座位 <span aria-hidden="true">{{sortIndicator('seat')}}</span></button></th><th>状态</th><th class="operation-heading">操作</th></tr></thead><tbody><tr v-for="device in visibleDevices" :key="device.id"><td class="device-ip">{{device.name}}</td><td><select :value="device.seatId??''" :aria-label="`${device.name} 的座位`" @change="bind(device,$event)" :disabled="busy||!connected"><option value="" disabled>分配座位</option><option v-for="seat in data.seats" :key="seat.id" :value="seat.id">{{seat.label}} 号</option></select></td><td><span class="device-status" :class="device.status"><i aria-hidden="true"></i>{{statuses[device.status]??device.status}}</span></td><td class="operation-cell"><button class="text-button danger" @click="remove(device)" :disabled="busy||!connected">移除</button></td></tr><tr v-if="!visibleDevices.length"><td colspan="4" class="empty">没有匹配的学生机</td></tr></tbody></table></div>
    </section>
   </template>
  </main>
 </template>
 <SeatLayout v-if="editingLayout&&data" :seats="data.seats" :rows="data.layoutRows" :columns="data.layoutColumns" :busy="busy" @close="editingLayout=false" @save="saveLayout"/>
 <StudentSetup v-if="studentSetup" :mode="studentSetup" :api="api" @close="studentSetup=null" @notice="toast"/>
 <div v-if="matchReview" class="layout-overlay" @keydown.esc.prevent="cancelMatching"><section class="layout-editor matching-dialog" role="dialog" aria-modal="true" aria-labelledby="match-review-title"><header class="layout-tools"><strong id="match-review-title">顺序匹配前检查</strong></header><div class="matching-review"><p>{{matchReview.students}} 台学生机 · {{matchReview.seats}} 个座位</p><template v-if="matchReview.gaps.length"><strong class="danger">发现 IP 跳号，请核对是否有机器未连接</strong><ul class="ip-gaps"><li v-for="gap in matchReview.gaps" :key="gap.before"><span>{{gap.before}} → {{gap.after}}</span><small>缺少：{{gap.missingFrom}}{{gap.count>1?' ～ '+gap.missingTo:''}}（{{gap.count}} 个）</small></li></ul></template><p v-else>{{matchReview.checkedPairs?'未发现已登记 IP 之间有跳号。':'现有 IP 不足以判断是否连号。'}}</p><p v-if="matchReview.offlineIps.length" class="danger">已登记但离线 {{matchReview.offlineIps.length}} 台（仍参与匹配）：<span class="ip-list">{{matchReview.offlineIps.join('、')}}</span></p><p v-if="matchReview.uncheckedAddresses">另有 {{matchReview.uncheckedAddresses}} 个地址未进行连号判断。</p><p class="matching-note">仅检查同一号段内已登记 IP 之间的缺号，不推测首尾缺机。继续后会覆盖原安排，按现有学生机依次分配座位。</p><p v-if="matchReview.error" class="danger">{{matchReview.error}}</p></div><footer class="layout-footer"><button ref="matchCancel" class="button" @click="cancelMatching" :disabled="busy">取消</button><button class="text-button" @click="matchSeats" :disabled="busy||!connected">重新检查</button><button class="button primary" @click="confirmMatching" :disabled="busy||!connected||!matchReview.canMatch">确认匹配</button></footer></section></div>
 <div v-if="seatShift" class="layout-overlay" @keydown.esc.prevent="cancelSeatShift"><section class="layout-editor password-dialog" role="dialog" aria-modal="true" aria-labelledby="seat-shift-title"><header class="layout-tools"><strong id="seat-shift-title">后续座位一起顺延？</strong></header><div class="password-fields"><strong>{{seatShift.ip}} · {{seatShift.from}} → {{seatShift.to}}</strong><p>已使用顺序匹配。可将后续 {{seatShift.followingCount}} 台学生机一起往后移，此前座位保持原样。</p><p v-if="seatShift.examples.length">{{seatShift.examples.map(e=>`${e.from} → ${e.to}`).join('，')}}</p><p v-if="seatShift.error" class="password-error">{{seatShift.error}}</p></div><footer class="layout-footer"><button ref="shiftCancel" class="button" @click="cancelSeatShift" :disabled="busy">取消</button><button class="button" @click="applySeatShift(false)" :disabled="busy||!connected">仅修改本机</button><button class="button primary" @click="applySeatShift(true)" :disabled="busy||!connected||!seatShift.canShift">后续一起顺延</button></footer></section></div>
 <div v-if="removingDevice" class="layout-overlay" @keydown.esc.prevent="cancelRemove"><section class="layout-editor password-dialog" role="alertdialog" aria-modal="true" aria-labelledby="remove-device-title" aria-describedby="remove-device-detail"><header class="layout-tools"><strong id="remove-device-title">确认移除学生机？</strong></header><div id="remove-device-detail" class="password-fields"><strong>{{removingDevice.name}} · {{removingSeat?removingSeat+' 号座位':'未分配座位'}}</strong><p>仅从列表移除，学生机继续连接时会自动出现。</p></div><footer class="layout-footer"><button ref="removeCancel" class="button" @click="cancelRemove" :disabled="busy">取消</button><button class="button danger" @click="confirmRemove" :disabled="busy">{{busy?'正在移除……':'确认移除'}}</button></footer></section></div>
 <div v-if="notice" class="toast" role="status">{{notice}}<button aria-label="关闭提示" @click="notice=''">关闭</button></div>
</template>
