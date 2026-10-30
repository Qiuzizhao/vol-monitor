<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { AudioLines, Monitor, Play, Pause, Square, RefreshCw, Wifi, WifiOff, X, Volume2, LogOut } from '@lucide/vue';
import type { Dashboard, Device, Seat } from './types';
import DeviceEnrollment from './DeviceEnrollment.vue';
const data=ref<Dashboard|null>(null),page=ref('live'),busy=ref(false),connected=ref(false),notice=ref('');
const loading=ref(true),password=ref(''),loginError=ref(''),selected=ref<Seat|null>(null);
const local=['127.0.0.1','localhost','[::1]'].includes(location.hostname),mode=ref('quiet');
let socket:WebSocket|null=null,retry:ReturnType<typeof setTimeout>|undefined,toastTimer:ReturnType<typeof setTimeout>|undefined;
const active=computed(()=>!!data.value?.session&&data.value.session.status!=='ended');
const running=computed(()=>data.value?.session?.status==='running');
const online=computed(()=>data.value?.devices.filter(d=>d.status!=='offline').length??0);
const average=computed(()=>{const seats=data.value?.seats.filter(s=>s.score!==null)??[];return seats.length?Math.round(seats.reduce((sum,s)=>sum+s.score!,0)/seats.length):null;});
const rows=computed(()=>[...new Set(data.value?.seats.map(s=>s.row)??[])]);
const columns=computed(()=>Math.max(...(data.value?.seats.map(s=>s.column)??[6])));
const alerts=computed(()=>data.value?.zones.filter(z=>z.alert).length??0);
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
async function start(){
 const devices=data.value?.devices.filter(d=>d.seatId&&d.status!=='offline')??[];
 if(!devices.length){page.value='devices';toast('请先接入学生机。');return;}
 const needsCalibration=devices.some(d=>d.baselineDbfs===null);
 const ok=await perform(async()=>{await api('/sessions','POST',{name:`机房监控 ${new Date().toLocaleString('zh-CN')}`,mode:mode.value,demo:false});await refresh();});
 if(ok&&needsCalibration)await calibrate();
}
async function transition(action:string){await perform(async()=>{await api('/sessions/current/'+action,'POST');await refresh();});}
async function bind(device:Device,event:Event){const seatId=(event.target as HTMLSelectElement).value;await perform(async()=>{await api(`/devices/${device.id}/seat`,'PUT',{seatId});await refresh();});}
async function remove(device:Device){await perform(async()=>{await api(`/devices/${device.id}`,'DELETE');await refresh();});}
function reload(){location.reload();}
onMounted(async()=>{try{await refresh();connect();}catch(e){loginError.value=local?'请重新启动教师端。':'';}finally{loading.value=false;}});
onUnmounted(()=>{clearTimeout(retry);clearTimeout(toastTimer);if(socket){socket.onclose=null;socket.close();}});
</script>

<template>
 <div v-if="loading" class="center-state">正在连接……</div>
 <div v-else-if="!data" class="center-state"><AudioLines :size="36"/><h1>机房音量管理</h1><template v-if="local"><p>{{loginError}}</p><button class="button primary" @click="reload">重新连接</button></template><form v-else @submit.prevent="login"><input v-model="password" type="password" placeholder="远程访问密码" required/><button class="button primary" :disabled="busy">进入</button></form></div>
 <template v-else>
  <header class="app-header"><div class="brand"><AudioLines :size="25"/><strong>机房音量管理</strong></div><nav aria-label="主导航"><button :class="{active:page==='live'}" @click="page='live'"><Volume2 :size="17"/>音量监控</button><button :class="{active:page==='devices'}" @click="page='devices'"><Monitor :size="17"/>学生机</button></nav><span class="connection"><component :is="connected?Wifi:WifiOff" :size="16"/>{{connected?'已连接':'重连中'}}</span><button v-if="!local" class="icon-button" aria-label="退出登录" @click="logout"><LogOut :size="17"/></button></header>
  <main>
   <template v-if="page==='live'">
    <section class="toolbar"><div class="monitor-state"><i :class="{on:running}"></i><strong>{{active?(running?'监测中':'已暂停'):'未开始监测'}}</strong><span v-if="calibrating">校准中，请保持安静</span></div><div class="actions"><select v-if="!active" v-model="mode" aria-label="课堂模式"><option value="quiet">安静上课</option><option value="discussion">小组讨论</option></select><button v-if="!active" class="button primary" @click="start" :disabled="busy||!connected"><Play :size="17"/>开始监测</button><template v-else><button class="button primary" @click="transition(running?'pause':'resume')" :disabled="busy||!connected"><component :is="running?Pause:Play" :size="17"/>{{running?'暂停':'继续'}}</button><button class="button" @click="transition('end')" :disabled="busy"><Square :size="16"/>结束</button><button class="button" @click="calibrate" :disabled="busy||!running||calibrating||data.session?.demo"><RefreshCw :size="16"/>校准</button></template></div></section>
    <div class="summary"><span>全室音量 <strong>{{score(average)}}</strong></span><button @click="page='devices'">学生机 <strong>{{online}}</strong> 台在线</button><span :class="{warning:alerts}">偏高区域 <strong>{{alerts}}</strong></span></div>
    <div v-if="active&&data.session?.demo" class="banner">旧版数据未结束。<button @click="transition('end')">结束旧版数据</button></div>
    <div class="monitor-grid"><section class="panel seat-panel"><div class="panel-heading"><h2>座位音量</h2><span>{{data.roomName}}</span></div><div class="classroom"><div class="teacher-desk">讲台</div><div class="seat-rows"><div v-for="row in rows" :key="row" class="seat-row" :style="{'--columns':columns}"><button v-for="seat in data.seats.filter(s=>s.row===row)" :key="seat.id" class="seat" :class="[tone(seat.score),{aisle:seat.column===columns/2}]" :aria-label="`座位 ${seat.label}，${statuses[seat.status]??seat.status}，音量 ${score(seat.score)}`" @click="selected=seat"><span>{{seat.label}}</span><strong>{{score(seat.score)}}</strong><small v-if="seat.score===null">{{statuses[seat.status]??seat.status}}</small></button></div></div></div><div class="map-legend"><i class="dot quiet"></i>低<i class="dot medium"></i>中<i class="dot loud"></i>高<i class="dot none"></i>无数据</div></section><aside class="panel zone-panel"><div class="panel-heading"><h2>区域音量</h2></div><div v-for="zone in data.zones" :key="zone.id" class="zone"><div><span>{{zone.name}}</span><strong :class="tone(zone.score)">{{score(zone.score)}}</strong></div><small>{{zone.valid}} / {{zone.expected}} 台有效<span v-if="zone.alert" class="warning">音量偏高</span></small></div></aside></div>
   </template>
   <template v-else><DeviceEnrollment :api="api" :seats="data.seats" :devices="data.devices" @notice="toast"/><section v-if="data.devices.length" class="panel"><div class="panel-heading"><h2>已接入 {{data.devices.length}} 台</h2><button class="icon-button" aria-label="刷新设备" @click="perform(refresh)"><RefreshCw :size="17"/></button></div><div class="table-scroll"><table><thead><tr><th>学生机</th><th>座位</th><th>状态</th><th></th></tr></thead><tbody><tr v-for="device in data.devices" :key="device.id"><td>{{device.name}}</td><td><select :value="device.seatId??''" :aria-label="`${device.name} 的座位`" @change="bind(device,$event)" :disabled="busy"><option value="" disabled>选择座位</option><option v-for="seat in data.seats" :key="seat.id" :value="seat.id" :disabled="data.devices.some(d=>d.id!==device.id&&d.seatId===seat.id)">{{seat.label}} 号</option></select></td><td><span class="device-status" :class="{offline:device.status==='offline'}">{{statuses[device.status]??device.status}}</span></td><td><button class="text-button danger" @click="remove(device)" :disabled="busy">移除</button></td></tr></tbody></table></div></section></template>
  </main>
 </template>
 <div v-if="notice" class="toast" role="status">{{notice}}<button aria-label="关闭提示" @click="notice=''"><X :size="16"/></button></div>
 <div v-if="selected" class="overlay" @click.self="selected=null"><section class="modal" role="dialog" :aria-label="`座位 ${selected.label}`"><button class="close icon-button" aria-label="关闭" @click="selected=null"><X :size="20"/></button><h2>{{selected.label}} 号座位</h2><strong class="detail-score" :class="tone(selected.score)">{{score(selected.score)}}</strong><p>{{selected.deviceName??'未接入设备'}}</p><p>{{statuses[selected.status]??selected.status}}</p></section></div>
</template>