<script setup lang="ts">
import { computed,onMounted,onUnmounted,ref } from 'vue';
import type { Device,Seat } from './types';
interface Pending{id:string;name:string;suggestedSeatId:string|null;existingDeviceId:string|null;hardwareFingerprint:string;duplicateHardware:boolean;ipAddress?:string|null}
const props=defineProps<{api:(path:string,method?:string,body?:unknown)=>Promise<any>;seats:Seat[];devices:Device[]}>();
const emit=defineEmits<{notice:[message:string]}>();
const overview=ref<{window:{open:boolean};requests:Pending[]}>({window:{open:false},requests:[]});
const busy=ref(false),selected=ref<string[]>([]),mapping=ref<Record<string,string>>({});
let poll:ReturnType<typeof setInterval>|undefined;
const edited=new Set<string>();
const requests=computed(()=>overview.value.requests.filter(r=>selected.value.includes(r.id)));
const conflict=computed(()=>{const seats=requests.value.map(r=>mapping.value[r.id]);if(seats.some(s=>!s))return '请先选择座位。';if(new Set(seats).size!==seats.length)return '座位不能重复。';if(new Set(requests.value.map(r=>r.hardwareFingerprint)).size!==requests.value.length)return '有重复设备，请核对。';return '';});
function occupied(seat:string,r:Pending){return props.devices.some(d=>d.seatId===seat&&d.id!==r.existingDeviceId);}
async function refresh(){
 const next=await props.api('/enrollment');const previous=new Set(overview.value.requests.map(r=>r.id));overview.value=next;
 const ids=new Set<string>(next.requests.map((r:Pending)=>r.id));
 selected.value=selected.value.filter(id=>ids.has(id));for(const id of edited)if(!ids.has(id))edited.delete(id);
 const used=new Set<string>(next.requests.filter((r:Pending)=>edited.has(r.id)).map((r:Pending)=>mapping.value[r.id]).filter(Boolean));
 for(const r of next.requests){
  if(!previous.has(r.id)&&selected.value.length<64)selected.value.push(r.id);
  if(!edited.has(r.id)){
   const preferred=r.suggestedSeatId;
   const seat=preferred&&!used.has(preferred)&&!occupied(preferred,r)?preferred:'';
   mapping.value[r.id]=seat;if(seat)used.add(seat);
  }
 }
 // Reserve all remaining preferred seats before filling gaps caused by manual edits.
 for(const r of next.requests)if(!edited.has(r.id)&&!mapping.value[r.id]){
  const seat=props.seats.find(s=>!used.has(s.id)&&!occupied(s.id,r))?.id??'';
  mapping.value[r.id]=seat;if(seat)used.add(seat);
 }
}
async function action(fn:()=>Promise<void>){busy.value=true;try{await fn();await refresh();}catch(e){emit('notice',e instanceof Error?e.message:'操作失败。');}finally{busy.value=false;}}
async function toggle(){await action(async()=>{await props.api('/enrollment/'+(overview.value.window.open?'close':'open'),'POST');});}
async function approve(){await action(async()=>{await props.api('/enrollment/approve','POST',{items:requests.value.map(r=>({requestId:r.id,seatId:mapping.value[r.id]}))});selected.value=[];emit('notice','已接入。');});}
onMounted(async()=>{await action(async()=>{await refresh();if(!overview.value.window.open)await props.api('/enrollment/open','POST');});poll=setInterval(()=>{if(!busy.value)refresh().catch(()=>{});},1500);});
onUnmounted(()=>clearInterval(poll));
</script>
<template>
 <section class="panel enrollment-panel"><div class="panel-heading"><h2>新设备</h2><button class="button" :class="overview.window.open?'':'primary'" @click="toggle" :disabled="busy">{{overview.window.open?'关闭接入':'开启接入'}}</button></div>
 <template v-if="overview.requests.length"><div class="table-scroll"><table><thead><tr><th><input type="checkbox" aria-label="全选" :checked="selected.length===Math.min(overview.requests.length,64)" @change="selected=selected.length===Math.min(overview.requests.length,64)?[]:overview.requests.slice(0,64).map(r=>r.id)"/></th><th>IP</th><th>座位</th></tr></thead><tbody><tr v-for="r in overview.requests" :key="r.id"><td><input type="checkbox" v-model="selected" :value="r.id" :aria-label="`选择 ${r.ipAddress??'未连接'}`"/></td><td>{{r.ipAddress??'未连接'}}<small v-if="r.existingDeviceId" class="reinstall">重新接入</small></td><td><select v-model="mapping[r.id]" @change="edited.add(r.id)" :aria-label="`${r.ipAddress??'未连接'} 的座位`"><option value="">选择座位</option><option v-for="s in seats" :key="s.id" :value="s.id" :disabled="occupied(s.id,r)">{{s.label}} 号</option></select></td></tr></tbody></table></div><div class="approval"><span>{{conflict||`已选 ${selected.length} 台`}}</span><button class="button primary" @click="approve" :disabled="busy||!requests.length||!!conflict||requests.length>64">确认接入</button></div></template>
 <div v-else class="empty"><span>{{overview.window.open?'等待连接':'暂无设备'}}</span></div></section>
</template>
