<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { Check, Download, Layers, RefreshCw, ShieldCheck, Square, X } from '@lucide/vue';
import type { Device, Seat } from './types';
interface Pending {id:string;name:string;hardwareFingerprint:string;createdAt:string;lastSeen:string;suggestedSeatId:string|null;existingDeviceId:string|null;duplicateHardware:boolean}
interface Overview {window:{open:boolean;closesAt:string|null};requests:Pending[]}
const props=defineProps<{api:(path:string,method?:string,body?:unknown)=>Promise<any>;seats:Seat[];devices:Device[]}>();
const emit=defineEmits<{notice:[message:string]}>();
const overview=ref<Overview>({window:{open:false,closesAt:null},requests:[]}),busy=ref(false),selected=ref<string[]>([]),mapping=ref<Record<string,string>>({});
const exporting=ref(false),profileServer=ref(location.protocol==='https:'?location.origin:''),identityDirectory=ref(''),error=ref('');
let poll:ReturnType<typeof setInterval>|undefined;
const selectedRequests=computed(()=>overview.value.requests.filter(r=>selected.value.includes(r.id)));
const conflict=computed(()=>{
 const values=selectedRequests.value.map(r=>mapping.value[r.id]);
 if(!values.length)return '';
 if(values.length>64)return '每批最多批准 64 台，请分批处理。';
 if(values.some(v=>!v))return '请为所有选中设备指定座位。';
 if(new Set(values).size!==values.length)return '选中的设备存在重复座位，请核对机器名并调整映射。';
 if(new Set(selectedRequests.value.map(r=>r.hardwareFingerprint)).size!==values.length)return '选中设备存在重复硬件标识，请只保留一项并核对硬件。';
 return '';
});
async function refresh(){
 const next=await props.api('/enrollment') as Overview;
 overview.value=next;
 selected.value=selected.value.filter(id=>next.requests.some(r=>r.id===id));
 for(const r of next.requests)if(mapping.value[r.id]===undefined)mapping.value[r.id]=r.suggestedSeatId??'';
 error.value='';
}
async function action(fn:()=>Promise<void>){busy.value=true;try{await fn();await refresh();}catch(e){error.value=e instanceof Error?e.message:'操作失败';emit('notice',error.value);}finally{busy.value=false;}}
async function toggleWindow(){await action(async()=>{await props.api('/enrollment/'+(overview.value.window.open?'close':'open'),'POST');emit('notice',overview.value.window.open?'批量接入窗口已关闭，现有申请仍可处理。':'已开启 10 分钟接入窗口，请启动镜像部署的学生端。');});}
async function approve(){await action(async()=>{const count=selectedRequests.value.length;await props.api('/enrollment/approve','POST',{items:selectedRequests.value.map(r=>({requestId:r.id,seatId:mapping.value[r.id]}))});selected.value=[];emit('notice',`已批准并绑定 ${count} 台设备，学生端将自动保存各自凭证。`);});}
async function reject(id:string){await action(async()=>{await props.api('/enrollment/'+id+'/reject','POST');});}
function toggleAll(event:Event){selected.value=(event.target as HTMLInputElement).checked?overview.value.requests.map(r=>r.id):[];}
function occupied(seatId:string,r:Pending){return props.devices.some(d=>d.seatId===seatId&&d.id!==r.existingDeviceId);}
function downloadProfile(){
 try{
  const url=new URL(profileServer.value.trim());
  if(url.pathname!=='/'||url.search||url.hash||url.username||url.password||!(url.protocol==='https:'||url.protocol==='http:'&&['127.0.0.1','localhost','[::1]'].includes(url.hostname)))throw new Error('请填写教师机 HTTPS 根地址，例如 https://192.168.1.10:5081。');
  const profile={Server:url.origin,AutoEnroll:true,IdentityDirectory:identityDirectory.value.trim()||null,ManagedMode:true,EnableLoginStartup:true};
  const objectUrl=URL.createObjectURL(new Blob([JSON.stringify(profile,null,2)+'\n'],{type:'application/json'}));
  const a=document.createElement('a');a.href=objectUrl;a.download='agent-deployment.json';a.click();setTimeout(()=>URL.revokeObjectURL(objectUrl),2000);
  emit('notice','通用配置已导出。请放到学生端程序旁，封装母盘前运行 prepare-image.ps1 清除身份。');
 }catch(e){error.value=e instanceof Error?e.message:'配置生成失败';}
}
onMounted(async()=>{try{const connection=await props.api('/connection');if(connection.server)profileServer.value=connection.server;await refresh();}catch(e){error.value=e instanceof Error?e.message:'申请列表加载失败';}poll=setInterval(()=>{if(!busy.value)refresh().catch(e=>error.value=e.message);},4000);});
onUnmounted(()=>clearInterval(poll));
</script>

<template>
 <section class="panel enrollment-panel">
  <div class="panel-heading"><div><h2><Layers :size="18"/>镜像批量接入<span v-if="overview.window.open" class="status-pill on"><i></i>接入中</span></h2><p>母盘共用地址配置；每台电脑首次启动生成独立凭证，等待教师确认。</p></div><div class="enrollment-actions"><button class="button secondary" @click="exporting=!exporting"><Download :size="14"/>导出通用配置</button><button class="button" :class="overview.window.open?'secondary':'primary'" @click="toggleWindow" :disabled="busy"><component :is="overview.window.open?Square:Layers" :size="14"/>{{overview.window.open?'关闭接入窗口':'开启接入（10分钟）'}}</button></div></div>
  <form v-if="exporting" class="profile-export" @submit.prevent="downloadProfile"><label>教师机地址<input v-model="profileServer" type="url" required placeholder="https://192.168.1.10:5081"/></label><label>身份保存目录（可选）<input v-model="identityDirectory" placeholder="例如 D:\RoomScopeState，留空使用本地用户目录"/></label><button class="button secondary"><Download :size="14"/>下载配置</button><p>配置不含设备令牌。整机还原时，可将身份目录设在不还原的本地分区；仍需同一 Windows 账户和可用的 DPAPI 密钥。</p></form>
  <div class="enrollment-info"><ShieldCheck :size="16"/><span v-if="overview.window.open">接入窗口将在 {{overview.window.closesAt?new Date(overview.window.closesAt).toLocaleTimeString('zh-CN',{hour12:false}):''}} 关闭。申请不会自动获得采集权限。</span><span v-else>当前不接受新申请。已提交的申请保留 24 小时，关闭窗口后仍可处理。</span><button class="text-button" @click="action(refresh)" :disabled="busy"><RefreshCw :size="13"/>刷新</button></div>
  <p v-if="error" class="enrollment-error" role="alert">{{error}}</p>
  <template v-if="overview.requests.length"><div class="table-scroll"><table class="pending-table"><thead><tr><th><input type="checkbox" aria-label="全选接入申请" :checked="selected.length===overview.requests.length" @change="toggleAll"/></th><th>机器名 / 硬件摘要</th><th>座位映射（可调整）</th><th>接入类型</th><th>操作</th></tr></thead><tbody><tr v-for="r in overview.requests" :key="r.id"><td><input type="checkbox" v-model="selected" :value="r.id" :aria-label="`选择 ${r.name}`"/></td><td><strong>{{r.name}}</strong><small class="table-sub">{{r.hardwareFingerprint.slice(0,12)}}<span v-if="r.duplicateHardware" class="duplicate-warning"> · 标识重复，请核对</span></small></td><td><select v-model="mapping[r.id]" :aria-label="`${r.name} 的座位`"><option value="">请选择座位</option><option v-for="seat in seats" :key="seat.id" :value="seat.id" :disabled="occupied(seat.id,r)">{{seat.label}} 号{{occupied(seat.id,r)?'（已占用）':''}}</option></select></td><td><span class="source-tag" :class="{simulated:r.existingDeviceId}">{{r.existingDeviceId?'重新接入 · 替换旧凭证':'首次接入'}}</span></td><td><button class="text-button" @click="reject(r.id)" :disabled="busy"><X :size="13"/>拒绝</button></td></tr></tbody></table></div><div class="enrollment-bottom"><div><strong>已选择 {{selectedRequests.length}} / {{overview.requests.length}} 台</strong><p>{{conflict||'按 PC01、STU-02、学生机03 等机器名建议座位；还原重装优先沿用原座位。请核对后批准。'}}</p><p v-if="selectedRequests.some(r=>r.existingDeviceId)" class="duplicate-warning">批准重新接入会立即撤销该硬件的旧凭证，新设备需要重新校准。</p></div><button class="button primary" @click="approve" :disabled="busy||!selectedRequests.length||!!conflict"><Check :size="15"/>批准并绑定（{{selectedRequests.length}} 台）</button></div></template>
  <div v-else class="enrollment-empty"><Layers :size="22"/><div><strong>{{overview.window.open?'等待学生机提交申请':'准备好母盘后，在这里开启整班接入'}}</strong><p>学生端旁放置 agent-deployment.json，并运行 prepare-image.ps1。克隆后客户端会读取本机硬件摘要，无需逐台输入配对码。</p></div></div>
 </section>
</template>

<style scoped>
.enrollment-panel{margin-bottom:22px}.enrollment-actions{display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end}.enrollment-actions .button{font-size:11px;padding:8px 10px}.enrollment-info{background:#f5f9f5;border-top:1px solid #e7eee5;border-bottom:1px solid #e7eee5;display:flex;align-items:center;gap:8px;padding:11px 21px;color:#718a71;font-size:10px;line-height:1.8}.enrollment-info .text-button{margin-left:auto;white-space:nowrap}.enrollment-empty{display:flex;align-items:center;gap:17px;padding:27px 23px;color:#94a58a}.enrollment-empty strong{font-size:12px;color:#5a7d59;font-weight:500}.enrollment-empty p{font-size:10px;margin-top:9px;line-height:1.9}.pending-table input[type=checkbox]{accent-color:#168675;width:15px;height:15px}.pending-table select{font-size:11px;padding:7px 25px 7px 9px}.pending-table th:first-child,.pending-table td:first-child{padding-right:0;width:45px}.duplicate-warning{color:#a78247}.enrollment-bottom{display:flex;align-items:center;justify-content:space-between;gap:15px;padding:18px 22px;border-top:1px solid #ecf1e7}.enrollment-bottom strong{font-size:11px;font-weight:500;color:#537650}.enrollment-bottom p{font-size:10px;line-height:1.8;color:#94a18a;margin-top:7px}.enrollment-bottom p.duplicate-warning{color:#a78247}.enrollment-bottom .button{font-size:11px}.enrollment-error{color:#af704d;padding:10px 22px;font-size:11px;line-height:1.8}.profile-export{display:flex;align-items:flex-end;gap:12px;padding:16px 22px;flex-wrap:wrap;background:#fcfdfb;border-top:1px solid #edf1e8}.profile-export label{font-size:10px;color:#748e69;flex:1;min-width:180px}.profile-export input{display:block;width:100%;margin-top:7px;font-size:11px}.profile-export>p{width:100%;font-size:10px;color:#93a285;line-height:1.8}@media(max-width:760px){.enrollment-panel .panel-heading{flex-wrap:wrap}.enrollment-actions{width:100%;justify-content:flex-start}.enrollment-info{padding:10px 14px;align-items:flex-start}.enrollment-empty{padding:20px 15px;align-items:flex-start}.enrollment-empty p{font-size:9px}.enrollment-bottom{flex-wrap:wrap;padding:15px}.enrollment-bottom .button{width:100%}.profile-export{padding:15px}.enrollment-info .text-button{font-size:0}.enrollment-actions .button{font-size:10px}}
</style>
