<script setup lang="ts">
import { onMounted,ref } from 'vue';
import { Eye,EyeOff } from '@lucide/vue';
const props=defineProps<{mode:'package'|'password';api:(path:string,method?:string,body?:unknown)=>Promise<any>}>();
const emit=defineEmits<{close:[];notice:[message:string]}>();
const loading=ref(true),busy=ref(false),configured=ref(false),password=ref(''),confirmation=ref(''),error=ref('');
const showPassword=ref(false),showConfirmation=ref(false);
async function submit(){
 if(busy.value||loading.value)return;
 const needsPassword=props.mode==='password'||!configured.value;
 if(needsPassword&&(password.value.length<10||password.value.length>256)){error.value='密码需要 10～256 位。';return;}
 if(needsPassword&&password.value!==confirmation.value){error.value='两次输入的密码不一致。';return;}
 busy.value=true;error.value='';
 try{
  const body=needsPassword?{password:password.value,confirmation:confirmation.value}:{};
  if(props.mode==='password'){
   await props.api('/maintenance','PUT',body);
   emit('notice','密码已修改，对所有学生端立即生效。');
  }else{
   const response=await fetch('/api/teacher/student-package',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-RoomScope':'1'},body:JSON.stringify(body),signal:AbortSignal.timeout(120000)});
   if(!response.ok){let message=`生成失败（${response.status}）`;try{message=(await response.json()).error??message;}catch{}throw new Error(message);}
   const url=URL.createObjectURL(await response.blob());const link=document.createElement('a');link.href=url;link.download='学生端安装包.zip';link.click();setTimeout(()=>URL.revokeObjectURL(url),60000);
   emit('notice','学生端安装包已生成。');
  }
  password.value=confirmation.value='';emit('close');
 }catch(e){error.value=e instanceof Error?e.message:'保存失败，请重试。';}finally{busy.value=false;}
}
onMounted(async()=>{try{configured.value=(await props.api('/maintenance')).configured;}catch(e){error.value=e instanceof Error?e.message:'连接失败，请重试。';}finally{loading.value=false;}});
</script>
<template>
 <div class="layout-overlay"><form class="layout-editor password-dialog" role="dialog" aria-modal="true" aria-labelledby="student-setup-title" @submit.prevent="submit">
  <header class="layout-tools"><strong id="student-setup-title">{{mode==='package'?'生成学生端':'维护密码'}}</strong></header>
  <div class="password-fields">
   <p v-if="loading">正在连接……</p>
   <template v-else-if="mode==='password'||!configured">
    <div class="password-field"><label for="maintenance-new-password">新密码</label><div class="password-entry"><input id="maintenance-new-password" v-model="password" :type="showPassword?'text':'password'" autocomplete="new-password" minlength="10" maxlength="256" placeholder="至少 10 位" :disabled="busy" required/><button type="button" :aria-label="showPassword?'隐藏新密码':'显示新密码'" :title="showPassword?'隐藏新密码':'显示新密码'" :aria-pressed="showPassword" aria-controls="maintenance-new-password" @click="showPassword=!showPassword" :disabled="busy"><EyeOff v-if="showPassword" :size="18" aria-hidden="true"/><Eye v-else :size="18" aria-hidden="true"/></button></div></div>
    <div class="password-field"><label for="maintenance-confirm-password">确认密码</label><div class="password-entry"><input id="maintenance-confirm-password" v-model="confirmation" :type="showConfirmation?'text':'password'" autocomplete="new-password" minlength="10" maxlength="256" :disabled="busy" required/><button type="button" :aria-label="showConfirmation?'隐藏确认密码':'显示确认密码'" :title="showConfirmation?'隐藏确认密码':'显示确认密码'" :aria-pressed="showConfirmation" aria-controls="maintenance-confirm-password" @click="showConfirmation=!showConfirmation" :disabled="busy"><EyeOff v-if="showConfirmation" :size="18" aria-hidden="true"/><Eye v-else :size="18" aria-hidden="true"/></button></div></div>
    <p>用于退出学生端，对所有学生机生效。</p>
   </template>
   <p v-else>使用当前维护密码。</p>
   <p v-if="error" class="password-error" role="alert">{{error}}</p>
  </div>
  <footer class="layout-footer"><button type="button" class="button" @click="emit('close')" :disabled="busy">取消</button><button class="button primary" :disabled="busy||loading">{{busy?'处理中……':mode==='package'?'生成并下载':'保存'}}</button></footer>
 </form></div>
</template>
