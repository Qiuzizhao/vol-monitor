import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import {ref,computed,nextTick} from 'vue';
const helperSource=fs.readFileSync(new URL('../src/layoutFill.ts',import.meta.url),'utf8');
const js=ts.transpileModule(helperSource,{compilerOptions:{module:ts.ModuleKind.ESNext,target:ts.ScriptTarget.ES2022}}).outputText;
const {fillCells,fillRange,cellRange}=await import('data:text/javascript;base64,'+Buffer.from(js).toString('base64'));
const horizontal={top:0,left:0,bottom:0,right:1};

test('computer room 7 preset matches the reference and only saves after confirmation',()=>{
 const source=fs.readFileSync(new URL('../src/SeatLayout.vue',import.meta.url),'utf8').split('<script setup lang="ts">')[1].split('</script>')[0].replace(/^import .*;\r?\n/gm,'');
 const emitted=[];
 const context={ref,computed,nextTick,cellRange,fillRange,fillCells,useGridFit:()=>ref({}),defineProps:()=>({rows:1,columns:1,busy:false,seats:[]}),defineEmits:()=> (...args)=>emitted.push(args)};
 vm.runInNewContext(ts.transpileModule(source+'\nglobalThis.editor={presetName,loadPreset,cells,rows,columns,save};',{compilerOptions:{target:ts.ScriptTarget.ES2022}}).outputText,context);
 const editor=context.editor;editor.presetName.value='计算机7';editor.loadPreset();
 assert.equal(editor.rows.value,12);assert.equal(editor.columns.value,7);assert.equal(emitted.length,0);
 const cells=editor.cells.value;
 assert.deepEqual(Array.from(cells[0]),['A11','B11','','C11','D11','','']);
 assert.deepEqual(Array.from(cells[11]),['A1','B1','','C1','D1','','']);
 assert.ok(cells.every(row=>row[2]===''&&row[5]===''));
 assert.deepEqual(Array.from(cells[5]),['','','','','','','E3']);
 assert.deepEqual(Array.from(cells,row=>row[6]),['','','E6','E5','E4','E3','','E2','E1','','','']);
 const labels=cells.flat().filter(Boolean);assert.equal(labels.length,50);assert.equal(new Set(labels).size,50);
 editor.cells.value[0][0]='修改';editor.loadPreset();assert.equal(editor.cells.value[0][0],'A11');
 editor.save();assert.equal(emitted.length,1);assert.equal(emitted[0][0],'save');assert.equal(emitted[0][1].length,12);
});

test('A1 A2 fills right without changing source or unrelated rows',()=>{
 const cells=[['a1','a2','','','',''],['B1','','','','','']];
 assert.deepEqual(fillCells(cells,horizontal,{row:0,col:5}),[['a1','a2','a3','a4','a5','a6'],['B1','','','','','']]);
 assert.equal(cells[0][2],'');
});
test('vertical series preserves padded numbers and custom step',()=>{
 const cells=[['PC01'],['PC03'],[''],[''],['']];
 assert.deepEqual(fillCells(cells,{top:0,left:0,bottom:1,right:0},{row:4,col:0}),[['PC01'],['PC03'],['PC05'],['PC07'],['PC09']]);
});
test('supports reverse and descending numeric series',()=>{
 assert.deepEqual(fillCells([['','','A3','A4']],{top:0,left:2,bottom:0,right:3},{row:0,col:0}),[['A1','A2','A3','A4']]);
 assert.deepEqual(fillCells([['6','4','','']],horizontal,{row:0,col:3}),[['6','4','2','0']]);
});
test('fills each selected row independently and leaves blanks blank',()=>{
 assert.deepEqual(fillCells([['A1','A2','',''],['B1','B2','',''],['','','','']],{...horizontal,bottom:2},{row:2,col:3}),[['A1','A2','A3','A4'],['B1','B2','B3','B4'],['','','','']]);
});
test('non-series repeats its pattern, single numbered seed increments',()=>{
 assert.deepEqual(fillCells([['左','右','','']],horizontal,{row:0,col:3}),[['左','右','左','右']]);
 assert.deepEqual(fillCells([['A9','','']],{top:0,left:0,bottom:0,right:0},{row:0,col:2}),[['A9','A10','A11']]);
});
test('drag stays on dominant axis and inside the existing grid',()=>{
 assert.deepEqual(fillRange(horizontal,{row:1,col:5}),{top:0,left:0,bottom:0,right:5});
 assert.deepEqual(fillCells([['A1','A2','']],horizontal,{row:999,col:999}),[['A1','A2','A3']]);
 assert.deepEqual(cellRange({row:2,col:3},{row:0,col:0}),{top:0,left:0,bottom:2,right:3});
});
test('actual component pointer handlers select two cells, preview and commit fill; keyboard and clipboard work',async()=>{
 const source=fs.readFileSync(new URL('../src/SeatLayout.vue',import.meta.url),'utf8').split('<script setup lang="ts">')[1].split('</script>')[0].replace(/^import .*;\r?\n/gm,'');
 const context={ref,computed,nextTick,cellRange,fillRange,fillCells,useGridFit:()=>ref({}),defineProps:()=>({rows:2,columns:6,busy:false,seats:[{row:1,column:1,label:'a1'},{row:1,column:2,label:'a2'}]}),defineEmits:()=>()=>{},
  document:{querySelector:()=>null,elementFromPoint:(x,y)=>({closest:()=>({dataset:{layoutPosition:`${Math.floor(y/60)}:${Math.floor(x/100)}`}})})},assert};
 const verify=`
let captured=false;
sheet.value={getBoundingClientRect:()=>({left:0,top:0,right:600,bottom:120}),scrollBy:()=>{},contains:()=>true,setPointerCapture:()=>captured=true,hasPointerCapture:()=>captured,releasePointerCapture:()=>captured=false};
const event={button:0,pointerId:1,shiftKey:false,preventDefault:()=>{}};
select(event,0,0);pointerMove({clientX:150,clientY:30});finishPointer(event);
assert.equal(selection.value.right,1);assert.equal(captured,false);
startFill(event);pointerMove({clientX:550,clientY:30});assert.equal(preview.value.right,5);assert.equal(cells.value[0][5],'');finishPointer(event);
assert.equal(cells.value[0].join(','),'a1,a2,a3,a4,a5,a6');
let copied='';copy({preventDefault:()=>{},clipboardData:{setData:(_,text)=>copied=text}});assert.equal(copied,'a1\\ta2\\ta3\\ta4\\ta5\\ta6');
move({key:'Delete',preventDefault:()=>{}},0,0);assert.ok(cells.value[0].every(v=>v===''));
paste({clipboardData:{getData:()=> 'A01\\tA02\\nB01\\tB02'}},0,0);assert.equal(cells.value[1][1],'B02');assert.equal(selection.value.bottom,1);
startFill(event);pointerMove({clientX:550,clientY:30});finishPointer(event,true);assert.equal(cells.value[0][2],'');
`;
 vm.runInNewContext(ts.transpileModule(source+verify,{compilerOptions:{target:ts.ScriptTarget.ES2022}}).outputText,context);
 await nextTick();
});
