<script setup lang="ts">
import { computed,onMounted,onUnmounted,ref } from 'vue';
import { Check,Monitor,Plus,Square } from '@lucide/vue';
import type { Device,Seat } from './types';
interface Pending{id:string;name:string;suggestedSeatId:string|null;existingDeviceId:string|null;hardwareFingerprint:string;duplicateHardware:boolean}
const props=defineProps<{api:(path:string,method?:string,body?:unknown)=>Promise<any>;seats:Seat[];devices:Device[]}>();
const emit=defineEmits<{notice:[message:string]}>();
const overview=ref<{window:{open:boolean};requests:Pending[]}>({window:{open:false},requests:[]});
const busy=ref(false),selected=ref<string[]>([]),mapping=ref<Record<string,string>>({});
let poll:ReturnType<typeof setInterval>|undefined;
const requests=computed(()=>overview.value.requests.filter(r=>selected.value.includes(r.id)));
const conflict=computed(()=>{const seats=requests.value.map(r=>mapping.value[r.id]);if(seats.some(s=>!s))return '请先选择座位。';if(new Set(seats).size!==seats.length)return '座位不能重复。';if(new Set(requests.value.map(r=>r.hardwareFingerprint)).size!==requests.value.length)return '有重复设备，请核对。';return '';});
function occupied(seat:string,r:Pending){return props.devices.some(d=>d.seatId===seat&&d.id!==r.existingDeviceId);}
async function refresh(){const next=await props.api('/enrollment');const previous=new Set(overview.value.requests.map(r=>r.id));overview.value=next;selected.value=selected.value.filter(id=>next.requests.some((r:Pending)=>r.id===id));for(const r of next.requests){if(!previous.has(r.id))selected.value.push(r.id);if(mapping.value[r.id]===undefined)mapping.value[r.id]=r.suggestedSeatId??'';}}
async function action(fn:()=>Promise<void>){busy.value=true;try{await fn();await refresh();}catch(e){emit('notice',e instanceof Error?e.message:'操作失败。');}finally{busy.value=false;}}
async function toggle(){await action(async()=>{await props.api('/enrollment/'+(overview.value.window.open?'close':'open'),'POST');});}
async function approve(){await action(async()=>{await props.api('/enrollment/approve','POST',{items:requests.value.map(r=>({requestId:r.id,seatId:mapping.value[r.id]}))});selected.value=[];emit('notice','已接入。');});}
onMounted(async()=>{try{await refresh();}catch{}poll=setInterval(()=>{if(!busy.value)refresh().catch(()=>{});},3000);});
onUnmounted(()=>clearInterval(poll));
</script>
<template>
 <section class="panel enrollment-panel"><div class="panel-heading"><h2>新学生机<span v-if="overview.window.open" class="receiving">正在接收申请</span></h2><button class="button" :class="overview.window.open?'':'primary'" @click="toggle" :disabled="busy"><component :is="overview.window.open?Square:Plus" :size="17"/>{{overview.window.open?'关闭接入':'开启接入'}}</button></div>
 <template v-if="overview.requests.length"><div class="table-scroll"><table><thead><tr><th><input type="checkbox" aria-label="全选" :checked="selected.length===overview.requests.length" @change="selected=selected.length===overview.requests.length?[]:overview.requests.map(r=>r.id)"/></th><th>学生机</th><th>座位</th></tr></thead><tbody><tr v-for="r in overview.requests" :key="r.id"><td><input type="checkbox" v-model="selected" :value="r.id" :aria-label="`选择 ${r.name}`"/></td><td>{{r.name}}<small v-if="r.existingDeviceId" class="reinstall">重新接入</small></td><td><select v-model="mapping[r.id]" :aria-label="`${r.name} 的座位`"><option value="">选择座位</option><option v-for="s in seats" :key="s.id" :value="s.id" :disabled="occupied(s.id,r)">{{s.label}} 号</option></select></td></tr></tbody></table></div><div class="approval"><span>{{conflict||`已选 ${selected.length} 台`}}</span><button class="button primary" @click="approve" :disabled="busy||!requests.length||!!conflict||requests.length>64"><Check :size="17"/>批准接入</button></div></template>
 <div v-else class="empty"><Monitor :size="25"/><span>{{overview.window.open?'等待学生机连接……':'暂无新设备'}}</span></div></section>
</template>