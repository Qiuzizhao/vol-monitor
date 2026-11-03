<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue';

import type { Dashboard, Device } from './types';
import DeviceEnrollment from './DeviceEnrollment.vue';
import SeatLayout from './SeatLayout.vue';
import StudentSetup from './StudentSetup.vue';
const data=ref<Dashboard|null>(null),page=ref('live'),busy=ref(false),connected=ref(false),notice=ref('');
const loading=ref(true),loginError=ref('');
const local=['127.0.0.1','localhost','[::1]'].includes(location.hostname);
let socket:WebSocket|null=null,retry:ReturnType<typeof setTimeout>|undefined,toastTimer:ReturnType<typeof setTimeout>|undefined;
const online=computed(()=>data.value?.devices.filter(d=>d.status!=='offline').length??0);
const offline=computed(()=>data.value?.devices.filter(d=>d.status==='offline').length??0);
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
const editingLayout=ref(false);
const studentSetup=ref<'package'|'password'|null>(null);
const removingDevice=ref<Device|null>(null),removeCancel=ref<HTMLButtonElement|null>(null);
const removingSeat=computed(()=>data.value?.seats.find(s=>s.id===removingDevice.value?.seatId)?.label);
let removeTrigger:HTMLButtonElement|null=null;
const seatMap=computed(()=>new Map(data.value?.seats.map(s=>[`${s.row}:${s.column}`,s])));
const seatAt=(row:number,col:number)=>seatMap.value.get(`${row}:${col}`);
const statuses:Record<string,string>={ok:'在线',unbound:'未分配座位',offline:'离线',stale:'连接中断',idle:'连接中',error:'麦克风异常',uncalibrated:'连接中',blocked:'请更新客户端',no_signal:'无输入',calibrating:'连接中'};
const score=(n:number|null|undefined)=>n==null?'—':n>0&&n<10?n.toFixed(1):Math.round(n);
const tone=(n:number|null)=>n===null?'none':n>=70?'loud':n>=40?'medium':'quiet';
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
async function bind(device:Device,event:Event){const seatId=(event.target as HTMLSelectElement).value;await perform(async()=>{await api(`/devices/${device.id}/seat`,'PUT',{seatId});await refresh();});}
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
  <main>
   <div v-if="!connected" class="connection-warning" role="status">连接已中断，正在自动重连。当前显示的是上次收到的数据。</div>
   <template v-if="page==='live'">
    <section class="toolbar"><div class="page-heading"><h1>实时音量</h1><span>音量指数</span><span v-if="offline" class="offline-count">{{offline}} 台离线</span></div><button class="button" @click="editingLayout=true">座位布局</button></section>
    <section class="panel seat-panel" :class="{'outdated':!connected}"><div class="classroom"><div class="teacher-desk"><span>讲台</span></div><div class="seat-rows"><div v-for="row in rows" :key="row" class="seat-row" :style="{'--columns':columns}">
     <template v-for="col in columns" :key="col"><div v-if="seatAt(row,col)" class="seat" :class="[tone(seatAt(row,col)!.score),{'seat-error':['error','blocked','stale'].includes(seatAt(row,col)!.status)}]" :aria-label="`座位 ${seatAt(row,col)!.label}，${statuses[seatAt(row,col)!.status]}，音量 ${score(seatAt(row,col)!.score)}`" :title="seatAt(row,col)!.deviceName??statuses[seatAt(row,col)!.status]">
      <span class="seat-label">{{seatAt(row,col)!.label}}<small>号</small></span><strong>{{score(seatAt(row,col)!.score)}}</strong><small v-if="seatAt(row,col)!.score===null" class="seat-state">{{statuses[seatAt(row,col)!.status]}}</small><div v-else class="seat-meter" aria-hidden="true"><i :style="{width:`${Math.min(100,seatAt(row,col)!.score!)}%`}"></i></div>
     </div><div v-else class="aisle-cell" aria-hidden="true"></div></template>
    </div></div></div></section>
   </template>
   <template v-else>
    <section class="toolbar"><div class="page-heading"><h1>学生机管理</h1><span>{{data.devices.length}} 台已接入</span></div><div v-if="local" class="actions"><button class="button primary" @click="studentSetup='package'">生成学生端</button><button class="button" @click="studentSetup='password'">维护密码</button></div></section>
    <DeviceEnrollment :api="api" :seats="data.seats" :devices="data.devices" @notice="toast"/>
    <section v-if="data.devices.length" class="panel devices-panel"><div class="panel-heading"><h2>已接入学生机</h2><div class="device-filters"><input v-model="deviceSearch" type="search" placeholder="搜索 IP 或座位" aria-label="搜索学生机" @keydown.esc="deviceSearch=''"/><select v-model="deviceFilter" aria-label="筛选学生机状态"><option value="all">全部状态</option><option value="online">在线 {{online}}</option><option value="offline">离线 {{offline}}</option></select></div></div>
     <div class="table-scroll"><table><thead><tr><th :aria-sort="deviceSort==='ip'?(sortDescending?'descending':'ascending'):'none'"><button class="sort-button" @click="sortDevices('ip')" :title="deviceSort==='ip'&&!sortDescending?'按 IP 从大到小排序':'按 IP 从小到大排序'">学生机 IP <span aria-hidden="true">{{sortIndicator('ip')}}</span></button></th><th :aria-sort="deviceSort==='seat'?(sortDescending?'descending':'ascending'):'none'"><button class="sort-button" @click="sortDevices('seat')" :title="deviceSort==='seat'&&!sortDescending?'按座位从大到小排序':'按座位从小到大排序'">座位 <span aria-hidden="true">{{sortIndicator('seat')}}</span></button></th><th>状态</th><th class="operation-heading">操作</th></tr></thead><tbody><tr v-for="device in visibleDevices" :key="device.id"><td class="device-ip">{{device.name}}</td><td><select :value="device.seatId??''" :aria-label="`${device.name} 的座位`" @change="bind(device,$event)" :disabled="busy||!connected"><option value="" disabled>分配座位</option><option v-for="seat in data.seats" :key="seat.id" :value="seat.id">{{seat.label}} 号</option></select></td><td><span class="device-status" :class="device.status"><i aria-hidden="true"></i>{{statuses[device.status]??device.status}}</span></td><td class="operation-cell"><button class="text-button danger" @click="remove(device)" :disabled="busy||!connected">移除</button></td></tr><tr v-if="!visibleDevices.length"><td colspan="4" class="empty">没有匹配的学生机</td></tr></tbody></table></div>
    </section>
   </template>
  </main>
 </template>
 <SeatLayout v-if="editingLayout&&data" :seats="data.seats" :rows="data.layoutRows" :columns="data.layoutColumns" :busy="busy" @close="editingLayout=false" @save="saveLayout"/>
 <StudentSetup v-if="studentSetup" :mode="studentSetup" :api="api" @close="studentSetup=null" @notice="toast"/>
 <div v-if="removingDevice" class="layout-overlay" @keydown.esc.prevent="cancelRemove"><section class="layout-editor password-dialog" role="alertdialog" aria-modal="true" aria-labelledby="remove-device-title" aria-describedby="remove-device-detail"><header class="layout-tools"><strong id="remove-device-title">确认移除学生机？</strong></header><div id="remove-device-detail" class="password-fields"><strong>{{removingDevice.name}} · {{removingSeat?removingSeat+' 号座位':'未分配座位'}}</strong><p>移除后停止采集，重新接入需教师确认。</p></div><footer class="layout-footer"><button ref="removeCancel" class="button" @click="cancelRemove" :disabled="busy">取消</button><button class="button danger" @click="confirmRemove" :disabled="busy">{{busy?'正在移除……':'确认移除'}}</button></footer></section></div>
 <div v-if="notice" class="toast" role="status">{{notice}}<button aria-label="关闭提示" @click="notice=''">关闭</button></div>
</template>
