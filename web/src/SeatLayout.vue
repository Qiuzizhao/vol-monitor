<script setup lang="ts">
import { computed,nextTick,ref } from 'vue';
import type { Seat } from './types';
import { cellRange,fillRange,fillCells,type CellPoint,type CellRange } from './layoutFill';
import {useGridFit} from './useGridFit';
const props=defineProps<{seats:Seat[];rows:number;columns:number;busy:boolean}>();
const emit=defineEmits<{close:[];save:[cells:string[][]]}>();
const rows=ref(props.rows),columns=ref(props.columns);
const cells=ref(Array.from({length:props.rows},(_,r)=>Array.from({length:props.columns},(_,c)=>props.seats.find(s=>s.row===r+1&&s.column===c+1)?.label??'')));
const key=(label:string)=>{const text=label.trim();const n=Number(text);return /^\d+$/.test(text)&&Number.isSafeInteger(n)&&n<=2147483647?'N:'+n:'S:'+text.toUpperCase();};
const unbound=computed(()=>{const labels=new Set(cells.value.flat().filter(s=>s.trim()).map(key));return props.seats.filter(s=>s.deviceId&&!labels.has(key(s.label))).length;});
const error=ref('');
const sheet=ref<HTMLDivElement|null>(null),anchor=ref<CellPoint>({row:0,col:0}),end=ref<CellPoint>({row:0,col:0});
const sheetFit=useGridFit(sheet,rows,columns,0);
const selection=computed(()=>cellRange(anchor.value,end.value));
const dragging=ref<'select'|'fill'|null>(null),fillSource=ref<CellRange|null>(null),fillTarget=ref<CellPoint|null>(null);
const preview=computed(()=>fillSource.value&&fillTarget.value?fillRange(fillSource.value,fillTarget.value):null);
function inside(range:CellRange,r:number,c:number){return r>=range.top&&r<=range.bottom&&c>=range.left&&c<=range.right;}
function cellClasses(r:number,c:number){const bounds=preview.value??selection.value;const active=inside(bounds,r,c);return {
 selected:inside(selection.value,r,c),'fill-preview':!!preview.value&&active&&!inside(selection.value,r,c),
 'range-top':active&&r===bounds.top,'range-bottom':active&&r===bounds.bottom,'range-left':active&&c===bounds.left,'range-right':active&&c===bounds.right
};}
function resize(){rows.value=Math.max(1,Math.min(30,Math.floor(Number(rows.value)||1)));columns.value=Math.max(1,Math.min(30,Math.floor(Number(columns.value)||1)));cells.value=Array.from({length:rows.value},(_,r)=>Array.from({length:columns.value},(_,c)=>cells.value[r]?.[c]??''));anchor.value={row:0,col:0};end.value={row:0,col:0};}
function capture(event:PointerEvent){sheet.value?.setPointerCapture(event.pointerId);}
function select(event:PointerEvent,row:number,col:number){
 if(event.button!==0||props.busy)return;event.preventDefault();dragging.value='select';
 if(!event.shiftKey)anchor.value={row,col};end.value={row,col};capture(event);focus(anchor.value.row,anchor.value.col);
}
function startFill(event:PointerEvent){if(event.button!==0||props.busy)return;dragging.value='fill';fillSource.value={...selection.value};fillTarget.value={...end.value};capture(event);}
function pointerMove(event:PointerEvent){
 if(!dragging.value||!sheet.value)return;
 const box=sheet.value.getBoundingClientRect();
 const scrollX=event.clientX<box.left+20?-14:event.clientX>box.right-20?14:0;
 const scrollY=event.clientY<box.top+20?-14:event.clientY>box.bottom-20?14:0;
 sheet.value.scrollBy(scrollX,scrollY);
 const element=document.elementFromPoint(Math.max(box.left+4,Math.min(box.right-4,event.clientX)),Math.max(box.top+4,Math.min(box.bottom-4,event.clientY)))?.closest<HTMLElement>('[data-layout-position]');
 if(!element||!sheet.value.contains(element))return;
 const [row,col]=element.dataset.layoutPosition!.split(':').map(Number),point={row,col};
 if(dragging.value==='fill')fillTarget.value=point;else end.value=point;
}
function finishPointer(event:PointerEvent,cancel=false){
 if(!cancel&&dragging.value==='fill'&&fillSource.value&&fillTarget.value){
  const bounds=fillRange(fillSource.value,fillTarget.value);cells.value=fillCells(cells.value,fillSource.value,fillTarget.value);
  anchor.value={row:bounds.top,col:bounds.left};end.value={row:bounds.bottom,col:bounds.right};error.value='';
 }
 dragging.value=null;fillSource.value=null;fillTarget.value=null;
 if(sheet.value?.hasPointerCapture(event.pointerId))sheet.value.releasePointerCapture(event.pointerId);
}
function inputFocus(event:FocusEvent,row:number,col:number){if(!dragging.value){anchor.value={row,col};end.value={row,col};}(event.target as HTMLInputElement).select();}
function typed(row:number,col:number){anchor.value={row,col};end.value={row,col};error.value='';}
function copy(event:ClipboardEvent){const s=selection.value;if(s.top===s.bottom&&s.left===s.right)return;event.preventDefault();event.clipboardData?.setData('text/plain',cells.value.slice(s.top,s.bottom+1).map(row=>row.slice(s.left,s.right+1).join('\t')).join('\n'));}
function clear(){cells.value=cells.value.map(row=>row.map(()=>''));}
async function focus(row:number,col:number){await nextTick();const input=document.querySelector<HTMLInputElement>(`input[data-layout-cell="${row}:${col}"]`);input?.focus();input?.select();}
function move(event:KeyboardEvent,row:number,col:number){
 if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='a'){event.preventDefault();anchor.value={row:0,col:0};end.value={row:rows.value-1,col:columns.value-1};return;}
 if(event.key==='Delete'||event.key==='Backspace'&&(selection.value.top!==selection.value.bottom||selection.value.left!==selection.value.right)){event.preventDefault();const s=selection.value;for(let r=s.top;r<=s.bottom;r++)for(let c=s.left;c<=s.right;c++)cells.value[r][c]='';return;}
 const shifts:Record<string,number[]>={ArrowUp:[-1,0],ArrowDown:[1,0],ArrowLeft:[0,-1],ArrowRight:[0,1],Enter:[1,0]};
 if(shifts[event.key]&&!event.ctrlKey&&!event.metaKey){event.preventDefault();const [r,c]=shifts[event.key];const from=event.shiftKey?end.value:{row,col};const point={row:Math.max(0,Math.min(rows.value-1,from.row+r)),col:Math.max(0,Math.min(columns.value-1,from.col+c))};end.value=point;if(!event.shiftKey){anchor.value=point;focus(point.row,point.col);}}
}
function paste(event:ClipboardEvent,row:number,col:number){
 row=selection.value.top;col=selection.value.left;
 const text=event.clipboardData?.getData('text/plain');if(text===undefined)return;
 const lines=text.replace(/\r/g,'').split('\n');if(lines.length>1&&lines.at(-1)==='')lines.pop();
 const values=lines.map(line=>line.split('\t'));
 if(row+values.length>30||col+Math.max(...values.map(r=>r.length))>30){error.value='最多 30 行、30 列。';return;}
 rows.value=Math.max(rows.value,row+values.length);columns.value=Math.max(columns.value,col+Math.max(...values.map(r=>r.length)));resize();
 values.forEach((line,r)=>line.forEach((value,c)=>cells.value[row+r][col+c]=value.trim()));anchor.value={row,col};end.value={row:row+values.length-1,col:col+Math.max(...values.map(r=>r.length))-1};error.value='';
}
function save(){const labels=cells.value.flat().map(v=>v.trim()).filter(Boolean);if(!labels.length){error.value='请填写座位号。';return;}if(new Set(labels.map(key)).size!==labels.length){error.value='座位号不能重复。';return;}if(labels.some(s=>s.length>16)){error.value='座位号最多 16 个字符。';return;}error.value='';emit('save',cells.value.map(row=>row.map(v=>v.trim())));}
</script>
<template>
 <div class="layout-overlay"><section class="layout-editor seat-layout-dialog" role="dialog" aria-modal="true" aria-label="座位布局"><header class="layout-tools"><strong>座位布局</strong><label>行<input v-model.number="rows" type="number" min="1" max="30" @change="resize" aria-label="布局行数"/></label><label>列<input v-model.number="columns" type="number" min="1" max="30" @change="resize" aria-label="布局列数"/></label><button class="text-button" @click="clear" :disabled="busy">清空</button><span>拖动右下角填充 · 空白为走廊</span></header><div ref="sheet" class="layout-sheet" :style="sheetFit" @pointermove="pointerMove" @pointerup="finishPointer($event)" @pointercancel="finishPointer($event,true)"><table role="grid" aria-multiselectable="true"><tbody><tr v-for="(line,r) in cells" :key="r"><td v-for="(_,c) in line" :key="c" :data-layout-position="`${r}:${c}`" :class="cellClasses(r,c)" :aria-selected="inside(selection,r,c)"><input v-model="cells[r][c]" :data-layout-cell="`${r}:${c}`" :aria-label="`第 ${r+1} 行第 ${c+1} 列座位号`" maxlength="16" @pointerdown="select($event,r,c)" @input="typed(r,c)" @copy="copy" @keydown="move($event,r,c)" @paste.prevent="paste($event,r,c)" @focus="inputFocus($event,r,c)"/><button v-if="r===selection.bottom&&c===selection.right&&!dragging" type="button" class="layout-fill-handle" aria-label="拖动填充座位号" title="拖动填充座位号" tabindex="-1" @pointerdown.stop.prevent="startFill"/></td></tr></tbody></table></div><footer class="layout-footer"><span>{{error||(unbound?`需重新绑定 ${unbound} 台学生机`:'')}}</span><button class="button" @click="emit('close')" :disabled="busy">取消</button><button class="button primary" @click="save" :disabled="busy">保存</button></footer></section></div>
</template>
