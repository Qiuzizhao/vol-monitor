import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import {ref,computed,nextTick} from 'vue';
const source=fs.readFileSync(new URL('../src/App.vue',import.meta.url),'utf8').split('<script setup lang="ts">')[1].split('</script>')[0].replace(/^import .*;\r?\n/gm,'');
function setup({automatic=true,offer=true,canShift=true,shiftable=2,unchanged=0}={}){
 const calls=[],fixture={sequentialMatching:automatic,devices:[],seats:[]};
 const preview={from:'B4',to:'B5',followingCount:2,shiftableFollowingCount:shiftable,unchangedFollowingCount:unchanged,expectedState:'reviewed-state',offerShift:offer,canShift,error:canShift?null:'顺延后会与前面座位重复',examples:[{from:'B5',to:'B6'}]};
 const context={ref,computed,nextTick,useGridFit:()=>ref({}),onMounted:()=>{},onUnmounted:()=>{},location:{hostname:'127.0.0.1'},Intl,AbortSignal,
  setTimeout:()=>0,clearTimeout:()=>{},fetch:async(path,options)=>{
   calls.push({path,...options,body:options.body?JSON.parse(options.body):null});
   const result=path.endsWith('seat-shift-preview')?preview:path.endsWith('dashboard')?fixture:{};
   return {ok:true,text:async()=>JSON.stringify(result)};
  }};
 vm.runInNewContext(ts.transpileModule(source+'\nglobalThis.controller={data,seatShift,bind,cancelSeatShift,applySeatShift,notice};',{compilerOptions:{target:ts.ScriptTarget.ES2022}}).outputText,context);
 const controller=context.controller;controller.data.value=fixture;
 const device={id:'student',name:'192.168.1.4',seatId:'old-seat'},input={value:'new-seat'};
 return {controller,calls,input,choose:()=>controller.bind(device,{target:input})};
}
test('manual edit after matching previews before writing and cancel makes no changes',async()=>{
 const state=setup();await state.choose();
 assert.equal(state.input.value,'old-seat');assert.equal(state.controller.seatShift.value.from,'B4');
 assert.equal(state.calls.length,1);assert.ok(state.calls[0].path.endsWith('seat-shift-preview'));
 state.controller.cancelSeatShift();assert.equal(state.controller.seatShift.value,null);assert.equal(state.calls.length,1);
});
test('only this device explicitly uses single assignment with reviewed state',async()=>{
 const state=setup();await state.choose();await state.controller.applySeatShift(false);
 const write=state.calls.find(call=>call.method==='PUT');assert.ok(write.path.endsWith('/student/seat'));
 assert.equal(write.body.expectedState,'reviewed-state');assert.equal(state.controller.seatShift.value,null);
});
test('confirmed cascade calls batch endpoint with the exact reviewed state',async()=>{
 const state=setup();await state.choose();await state.controller.applySeatShift(true);
 const write=state.calls.find(call=>call.path.endsWith('/shift-seats'));
 assert.equal(write.method,'POST');assert.equal(write.body.expectedState,'reviewed-state');
 assert.equal(state.controller.seatShift.value,null);
});
test('conflict blocks cascade while single assignment remains available',async()=>{
 const state=setup({canShift:false});await state.choose();await state.controller.applySeatShift(true);
 assert.equal(state.calls.length,1);assert.notEqual(state.controller.seatShift.value,null);
 await state.controller.applySeatShift(false);assert.ok(state.calls.some(call=>call.method==='PUT'));
});

test('partial capacity can be confirmed and reports shifted and retained counts',async()=>{
 for(const shifted of [1,0]){
  const state=setup({shiftable:shifted,unchanged:2-shifted});await state.choose();
  assert.equal(state.controller.seatShift.value.canShift,true);await state.controller.applySeatShift(true);
  assert.ok(state.calls.some(call=>call.path.endsWith('/shift-seats')));
  assert.ok(state.controller.notice.value.includes(`顺延后续 ${shifted} 台`));
  assert.ok(state.controller.notice.value.includes(`${2-shifted} 台保留原座位`));
 }
});
test('outside sequential mode or with no affected suffix edits directly',async()=>{
 const direct=setup({automatic:false});await direct.choose();assert.equal(direct.calls[0].method,'PUT');
 assert.equal(direct.controller.seatShift.value,null);
 const backward=setup({offer:false});await backward.choose();
 assert.equal(backward.calls[1].method,'PUT');assert.equal(backward.controller.seatShift.value,null);
});
