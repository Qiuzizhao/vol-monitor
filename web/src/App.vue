<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue';

import type { Dashboard, Device } from './types';
import DeviceEnrollment from './DeviceEnrollment.vue';
import SeatLayout from './SeatLayout.vue';
import StudentSetup from './StudentSetup.vue';
const data=ref<Dashboard|null>(null),page=ref('live'),busy=ref(false),connected=ref(false),notice=ref('');
const loading=ref(true),password=ref(''),loginError=ref('');
const local=['127.0.0.1','localhost','[::1]'].includes(location.hostname);
let socket:WebSocket|null=null,retry:ReturnType<typeof setTimeout>|undefined,toastTimer:ReturnType<typeof setTimeout>|undefined;
const online=computed(()=>data.value?.devices.filter(d=>d.status!=='offline').length??0);
const rows=computed(()=>Array.from({length:data.value?.layoutRows??6},(_,i)=>i+1));
const columns=computed(()=>data.value?.layoutColumns??6);
const editingLayout=ref(false);
const studentSetup=ref<'package'|'password'|null>(null);
const removingDevice=ref<Device|null>(null),removeCancel=ref<HTMLButtonElement|null>(null);
const removingSeat=computed(()=>data.value?.seats.find(s=>s.id===removingDevice.value?.seatId)?.label);
let removeTrigger:HTMLButtonElement|null=null;
const seatMap=computed(()=>new Map(data.value?.seats.map(s=>[`${s.row}:${s.column}`,s])));
const seatAt=(row:number,col:number)=>seatMap.value.get(`${row}:${col}`);
const calibrating=computed(()=>data.value?.devices.some(d=>d.status==='calibrating')??false);
const statuses:Record<string,string>={ok:'采集中',unbound:'未分配座位',offline:'离线',stale:'连接中断',idle:'待机',error:'麦克风异常',uncalibrated:'待校准',blocked:'请更新客户端',no_signal:'麦克风无输入',calibrating:'校准中'};
const score=(n:number|null|undefined)=>n==null?'—':Math.round(n);
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
async function login(){await perform(async()=>{await request('/api/login','POST',{password:password.value});password.value='';await refresh();connect();});}
async function logout(){await api('/logout','POST');location.reload();}
async function calibrate(){await perform(async()=>{await api('/calibrations','POST');await refresh();toast('请保持安静约 10 秒。');});}
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
onMounted(async()=>{try{await refresh();connect();}catch(e){loginError.value=local?'请重新启动教师端。':'';}finally{loading.value=false;}});
onUnmounted(()=>{clearTimeout(retry);clearTimeout(toastTimer);if(socket){socket.onclose=null;socket.close();}});
</script>

<template>
 <div v-if="loading" class="center-state">正在连接……</div>
 <div v-else-if="!data" class="center-state"><h1>机房音量管理</h1><template v-if="local"><p>{{loginError}}</p><button class="button primary" @click="reload">重新连接</button></template><form v-else @submit.prevent="login"><input v-model="password" type="password" placeholder="远程访问密码" required/><button class="button primary" :disabled="busy">进入</button></form></div>
 <template v-else>
  <header class="app-header"><nav aria-label="主导航"><button :class="{active:page==='live'}" @click="page='live'">监控</button><button :class="{active:page==='devices'}" @click="page='devices'">学生机</button></nav><span class="connection">{{connected?`在线 ${online} / ${data.devices.length}`:'连接中断'}}</span><button v-if="!local" class="text-button" @click="logout">退出登录</button></header>
  <main>
   <template v-if="page==='live'">
    <section class="toolbar"><strong>{{calibrating?'校准中':'监测中'}}</strong><div class="actions"><button class="button" @click="editingLayout=true">布局</button><button class="button" @click="calibrate" :disabled="busy||!connected||calibrating">校准</button></div></section>
    <div class="monitor-grid"><section class="panel seat-panel"><div class="classroom"><div class="teacher-desk">讲台</div><div class="seat-rows"><div v-for="row in rows" :key="row" class="seat-row" :style="{'--columns':columns}"><template v-for="col in columns" :key="col"><div v-if="seatAt(row,col)" class="seat" :class="tone(seatAt(row,col)!.score)" :aria-label="`座位 ${seatAt(row,col)!.label}，${statuses[seatAt(row,col)!.status]}，音量 ${score(seatAt(row,col)!.score)}`" :title="seatAt(row,col)!.deviceName??statuses[seatAt(row,col)!.status]"><span>{{seatAt(row,col)!.label}}</span><strong>{{score(seatAt(row,col)!.score)}}</strong><small v-if="seatAt(row,col)!.score===null">{{statuses[seatAt(row,col)!.status]}}</small></div><div v-else class="aisle-cell" aria-hidden="true"></div></template></div></div></div></section></div>
   </template>
   <template v-else><section v-if="local" class="toolbar"><div class="actions"><button class="button primary" @click="studentSetup='package'">生成学生端</button><button class="button" @click="studentSetup='password'">维护密码</button></div></section><DeviceEnrollment :api="api" :seats="data.seats" :devices="data.devices" @notice="toast"/><section v-if="data.devices.length" class="panel"><div class="table-scroll"><table><thead><tr><th>IP</th><th>座位</th><th>状态</th><th></th></tr></thead><tbody><tr v-for="device in data.devices" :key="device.id"><td>{{device.name}}</td><td><select :value="device.seatId??''" :aria-label="`${device.name} 的座位`" @change="bind(device,$event)" :disabled="busy"><option value="" disabled>选择座位</option><option v-for="seat in data.seats" :key="seat.id" :value="seat.id" :disabled="data.devices.some(d=>d.id!==device.id&&d.seatId===seat.id)">{{seat.label}} 号</option></select></td><td><span class="device-status" :class="{offline:device.status==='offline'}">{{statuses[device.status]??device.status}}</span></td><td><button class="text-button danger" @click="remove(device)" :disabled="busy">移除</button></td></tr></tbody></table></div></section></template>
  </main>
 </template>
 <SeatLayout v-if="editingLayout&&data" :seats="data.seats" :rows="data.layoutRows" :columns="data.layoutColumns" :busy="busy" @close="editingLayout=false" @save="saveLayout"/>
 <StudentSetup v-if="studentSetup" :mode="studentSetup" :api="api" @close="studentSetup=null" @notice="toast"/>
 <div v-if="removingDevice" class="layout-overlay" @keydown.esc.prevent="cancelRemove"><section class="layout-editor password-dialog" role="alertdialog" aria-modal="true" aria-labelledby="remove-device-title" aria-describedby="remove-device-detail"><header class="layout-tools"><strong id="remove-device-title">确认移除学生机？</strong></header><div id="remove-device-detail" class="password-fields"><strong>{{removingDevice.name}} · {{removingSeat?removingSeat+' 号座位':'未分配座位'}}</strong><p>移除后停止采集，重新接入需教师确认。</p></div><footer class="layout-footer"><button ref="removeCancel" class="button" @click="cancelRemove" :disabled="busy">取消</button><button class="button danger" @click="confirmRemove" :disabled="busy">{{busy?'正在移除……':'确认移除'}}</button></footer></section></div>
 <div v-if="notice" class="toast" role="status">{{notice}}<button aria-label="关闭提示" @click="notice=''">关闭</button></div>
</template>
