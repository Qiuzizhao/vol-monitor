import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import {ref,computed,nextTick} from 'vue';
const source=fs.readFileSync(new URL('../src/App.vue',import.meta.url),'utf8').split('<script setup lang="ts">')[1].split('</script>')[0].replace(/^import .*;\r?\n/gm,'');
function setup(canMatch=true){
 const calls=[],fixture={sequentialMatching:false,devices:[],seats:[]};let generation=0;
 const context={ref,computed,nextTick,useGridFit:()=>ref({}),onMounted:()=>{},onUnmounted:()=>{},location:{hostname:'127.0.0.1'},Intl,AbortSignal,
  setTimeout:()=>0,clearTimeout:()=>{},fetch:async(path,options)=>{
   calls.push({path,method:options.method,body:options.body?JSON.parse(options.body):null});
   const result=path.endsWith('/match-preview')?{students:4,seats:5,expectedState:'review-'+(++generation),canMatch,gaps:[{before:'192.168.1.10',after:'192.168.1.12',missingFrom:'192.168.1.11',missingTo:'192.168.1.11',count:1}],offlineIps:[]}:
    path.endsWith('/match-seats')?{matched:4,changed:4}:fixture;
   return {ok:true,text:async()=>JSON.stringify(result)};
  }};
 vm.runInNewContext(ts.transpileModule(source+'\nglobalThis.controller={data,matchReview,matchSeats,cancelMatching,confirmMatching,ipGaps,missingIpCount,deviceSearch,deviceFilter,visibleDevices};',{compilerOptions:{target:ts.ScriptTarget.ES2022}}).outputText,context);
 context.controller.data.value=fixture;return {controller:context.controller,calls};
}
test('clicking sequential matching only checks IPs; cancel does not apply assignments',async()=>{
 const {controller,calls}=setup();await controller.matchSeats();
 assert.equal(calls.length,1);assert.ok(calls[0].path.endsWith('/match-preview'));
 assert.equal(controller.matchReview.value.gaps[0].missingFrom,'192.168.1.11');
 controller.cancelMatching();assert.equal(controller.matchReview.value,null);assert.equal(calls.length,1);
});
test('confirmation submits the exact reviewed state',async()=>{
 const {controller,calls}=setup();await controller.matchSeats();await controller.confirmMatching();
 const commit=calls.find(c=>c.path.endsWith('/match-seats'));assert.equal(commit.body.expectedState,'review-1');
 assert.equal(controller.matchReview.value,null);
});
test('recheck refreshes review state before confirmation',async()=>{
 const {controller,calls}=setup();await controller.matchSeats();await controller.matchSeats();await controller.confirmMatching();
 assert.equal(calls.find(c=>c.path.endsWith('/match-seats')).body.expectedState,'review-2');
});
test('insufficient capacity cannot be confirmed',async()=>{
 const {controller,calls}=setup(false);await controller.matchSeats();await controller.confirmMatching();
 assert.equal(calls.length,1);assert.notEqual(controller.matchReview.value,null);
});

test('persistent gap warning tracks live snapshots and clears when missing IPs arrive',()=>{
 const {controller,calls}=setup();assert.equal(controller.ipGaps.value.length,0);
 const gap={before:'192.168.1.10',after:'192.168.1.13',missingFrom:'192.168.1.11',missingTo:'192.168.1.12',count:2};
 controller.data.value={devices:[],seats:[],ipGaps:[gap]};
 assert.equal(controller.ipGaps.value[0].missingFrom,gap.missingFrom);assert.equal(controller.missingIpCount.value,2);
 controller.data.value={devices:[],seats:[],ipGaps:[]};
 assert.equal(controller.ipGaps.value.length,0);assert.equal(controller.missingIpCount.value,0);assert.equal(calls.length,0);
});

test('gap warning uses the whole classroom even when the student list is filtered',()=>{
 const {controller}=setup();
 controller.data.value={devices:[{name:'192.168.1.10',status:'ok',seatId:null}],seats:[],ipGaps:[
  {before:'192.168.1.10',after:'192.168.1.12',missingFrom:'192.168.1.11',missingTo:'192.168.1.11',count:1},
  {before:'192.168.1.12',after:'192.168.1.15',missingFrom:'192.168.1.13',missingTo:'192.168.1.14',count:2}]};
 controller.deviceSearch.value='no match';controller.deviceFilter.value='offline';
 assert.equal(controller.visibleDevices.value.length,0);assert.equal(controller.ipGaps.value.length,2);assert.equal(controller.missingIpCount.value,3);
});
