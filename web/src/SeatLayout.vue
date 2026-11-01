<script setup lang="ts">
import { computed,nextTick,ref } from 'vue';
import type { Seat } from './types';
const props=defineProps<{seats:Seat[];rows:number;columns:number;busy:boolean}>();
const emit=defineEmits<{close:[];save:[cells:string[][]]}>();
const rows=ref(props.rows),columns=ref(props.columns);
const cells=ref(Array.from({length:props.rows},(_,r)=>Array.from({length:props.columns},(_,c)=>props.seats.find(s=>s.row===r+1&&s.column===c+1)?.label??'')));
const key=(label:string)=>{const text=label.trim();const n=Number(text);return /^\d+$/.test(text)&&Number.isSafeInteger(n)&&n<=2147483647?'N:'+n:'S:'+text.toUpperCase();};
const unbound=computed(()=>{const labels=new Set(cells.value.flat().filter(s=>s.trim()).map(key));return props.seats.filter(s=>s.deviceId&&!labels.has(key(s.label))).length;});
const error=ref('');
function resize(){rows.value=Math.max(1,Math.min(30,Math.floor(Number(rows.value)||1)));columns.value=Math.max(1,Math.min(30,Math.floor(Number(columns.value)||1)));cells.value=Array.from({length:rows.value},(_,r)=>Array.from({length:columns.value},(_,c)=>cells.value[r]?.[c]??''));}
function clear(){cells.value=cells.value.map(row=>row.map(()=>''));}
function columnName(index:number){let name='';for(let n=index+1;n>0;n=Math.floor((n-1)/26))name=String.fromCharCode(65+(n-1)%26)+name;return name;}
async function focus(row:number,col:number){await nextTick();const input=document.querySelector<HTMLInputElement>(`input[data-layout-cell="${row}:${col}"]`);input?.focus();input?.select();}
function move(event:KeyboardEvent,row:number,col:number){
 if(event.key==='Delete'){event.preventDefault();cells.value[row][col]='';return;}
 const shifts:Record<string,number[]>={ArrowUp:[-1,0],ArrowDown:[1,0],ArrowLeft:[0,-1],ArrowRight:[0,1],Enter:[1,0]};
 if(shifts[event.key]&&!event.ctrlKey&&!event.metaKey){event.preventDefault();const [r,c]=shifts[event.key];focus(Math.max(0,Math.min(rows.value-1,row+r)),Math.max(0,Math.min(columns.value-1,col+c)));}
}
function paste(event:ClipboardEvent,row:number,col:number){
 const text=event.clipboardData?.getData('text/plain');if(text===undefined)return;
 const lines=text.replace(/\r/g,'').split('\n');if(lines.length>1&&lines.at(-1)==='')lines.pop();
 const values=lines.map(line=>line.split('\t'));
 if(row+values.length>30||col+Math.max(...values.map(r=>r.length))>30){error.value='最多 30 行、30 列。';return;}
 rows.value=Math.max(rows.value,row+values.length);columns.value=Math.max(columns.value,col+Math.max(...values.map(r=>r.length)));resize();
 values.forEach((line,r)=>line.forEach((value,c)=>cells.value[row+r][col+c]=value.trim()));error.value='';
}
function save(){const labels=cells.value.flat().map(v=>v.trim()).filter(Boolean);if(!labels.length){error.value='请填写座位号。';return;}if(new Set(labels.map(key)).size!==labels.length){error.value='座位号不能重复。';return;}if(labels.some(s=>s.length>16)){error.value='座位号最多 16 个字符。';return;}error.value='';emit('save',cells.value.map(row=>row.map(v=>v.trim())));}
</script>
<template>
 <div class="layout-overlay"><section class="layout-editor" role="dialog" aria-modal="true" aria-label="座位布局"><header class="layout-tools"><strong>座位布局</strong><label>行<input v-model.number="rows" type="number" min="1" max="30" @change="resize" aria-label="布局行数"/></label><label>列<input v-model.number="columns" type="number" min="1" max="30" @change="resize" aria-label="布局列数"/></label><button class="text-button" @click="clear" :disabled="busy">清空</button><span>空白为走廊</span></header><div class="layout-sheet"><table><thead><tr><th></th><th v-for="(_,c) in cells[0]" :key="c">{{columnName(c)}}</th></tr></thead><tbody><tr v-for="(line,r) in cells" :key="r"><th>{{r+1}}</th><td v-for="(_,c) in line" :key="c"><input v-model="cells[r][c]" :data-layout-cell="`${r}:${c}`" :aria-label="`${columnName(c)}${r+1}`" maxlength="16" @keydown="move($event,r,c)" @paste.prevent="paste($event,r,c)" @focus="($event.target as HTMLInputElement).select()"/></td></tr></tbody></table></div><footer class="layout-footer"><span>{{error||(unbound?`需重新绑定 ${unbound} 台学生机`:'')}}</span><button class="button" @click="emit('close')" :disabled="busy">取消</button><button class="button primary" @click="save" :disabled="busy">保存</button></footer></section></div>
</template>
