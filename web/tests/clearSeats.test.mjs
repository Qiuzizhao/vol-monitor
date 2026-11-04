import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import {ref,computed,nextTick} from 'vue';
const source=fs.readFileSync(new URL('../src/App.vue',import.meta.url),'utf8').split('<script setup lang="ts">')[1].split('</script>')[0].replace(/^import .*;\r?\n/gm,'');
function setup(fail=false){
 const calls=[],fixture={sequentialMatching:true,devices:[{id:'a',name:'192.168.1.2',seatId:'A1',status:'ok'},{id:'b',name:'192.168.1.3',seatId:'A1',status:'offline'}],seats:[{id:'A1',label:'A1'}]};
 const context={ref,computed,nextTick,useGridFit:()=>ref({}),onMounted:()=>{},onUnmounted:()=>{},location:{hostname:'127.0.0.1'},Intl,AbortSignal,
  setTimeout:()=>0,clearTimeout:()=>{},fetch:async(path,options)=>{
   calls.push({path,method:options.method});let result=fixture;
   if(path.endsWith('/clear-seats')){
    if(fail)return {ok:false,status:500,json:async()=>({error:'清空失败'})};
    result={cleared:fixture.devices.filter(d=>d.seatId!=null).length};
    fixture.devices=fixture.devices.map(d=>({...d,seatId:null}));fixture.sequentialMatching=false;
   }
   return {ok:true,text:async()=>JSON.stringify(result)};
  }};
 vm.runInNewContext(ts.transpileModule(source+'\nglobalThis.controller={data,clearingSeats,assignedCount,clearSeats,cancelClearSeats,confirmClearSeats,deviceSearch,deviceFilter,visibleDevices};',{compilerOptions:{target:ts.ScriptTarget.ES2022}}).outputText,context);
 context.controller.data.value=fixture;return {controller:context.controller,calls};
}

test('clear button only opens confirmation and cancel leaves assignments intact',async()=>{
 const {controller,calls}=setup();await controller.clearSeats();
 assert.equal(controller.clearingSeats.value,true);assert.equal(controller.assignedCount.value,2);assert.equal(calls.length,0);
 controller.cancelClearSeats();await controller.confirmClearSeats();
 assert.equal(controller.clearingSeats.value,false);assert.equal(controller.assignedCount.value,2);assert.equal(calls.length,0);
});

test('confirmed clear applies to all students despite filters and refreshes once',async()=>{
 const {controller,calls}=setup();controller.deviceSearch.value='no match';controller.deviceFilter.value='offline';
 assert.equal(controller.visibleDevices.value.length,0);await controller.clearSeats();await controller.confirmClearSeats();
 assert.equal(calls.length,2);assert.ok(calls[0].path.endsWith('/clear-seats'));assert.equal(calls[0].method,'POST');assert.ok(calls[1].path.endsWith('/dashboard'));
 assert.equal(controller.assignedCount.value,0);assert.equal(controller.data.value.devices.length,2);assert.equal(controller.data.value.seats.length,1);
 assert.equal(controller.clearingSeats.value,false);await controller.clearSeats();assert.equal(controller.clearingSeats.value,false);
});

test('failed clear keeps confirmation open and preserves assignments for retry',async()=>{
 const {controller,calls}=setup(true);await controller.clearSeats();await controller.confirmClearSeats();
 assert.equal(calls.length,1);assert.equal(controller.clearingSeats.value,true);assert.equal(controller.assignedCount.value,2);
});
