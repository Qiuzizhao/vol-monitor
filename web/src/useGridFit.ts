import { onBeforeUnmount,ref,watch,type Ref } from 'vue';
import {fitGrid} from './fitGrid';
export function useGridFit(element:Ref<HTMLElement|null>,rows:Ref<number>,columns:Ref<number>,gapLimit=10){
 const styles=ref<Record<string,string>>({});let observer:ResizeObserver|undefined;
 function measure(width:number,height:number){
  const fit=fitGrid(width,height,rows.value,columns.value,gapLimit);
  styles.value=Object.fromEntries(Object.entries(fit).map(([key,value])=>['--fit-'+key.replace(/[A-Z]/g,m=>'-'+m.toLowerCase()),value+'px']));
 }
 watch([element,rows,columns],()=>{
  observer?.disconnect();const target=element.value;if(!target)return;
  const padding=getComputedStyle(target);
  measure(target.clientWidth-parseFloat(padding.paddingLeft)-parseFloat(padding.paddingRight),target.clientHeight-parseFloat(padding.paddingTop)-parseFloat(padding.paddingBottom));
  observer=new ResizeObserver(entries=>{const box=entries[0]?.contentRect;if(box)measure(box.width,box.height);});observer.observe(target);
 },{flush:'post'});
 onBeforeUnmount(()=>observer?.disconnect());
 return styles;
}
